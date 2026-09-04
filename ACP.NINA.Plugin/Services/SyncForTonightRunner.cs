using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services {

    /// What a Sync for tonight run did, in a shape both the sequencer log and
    /// the dock can render without recomputing anything.
    public class SyncOutcome {

        public bool Success { get; set; }

        /// Set when the run could not finish, in words a user can act on.
        public string Failure { get; set; }

        public SolveSnapshot Solve { get; set; }
        public Fingerprint Fingerprint { get; set; }
        public WriteBackResult WriteBack { get; set; }
        public MatchResponse Match { get; set; }
        public List<MatchedPlan> Selected { get; set; } = new List<MatchedPlan>();

        /// What the Target Scheduler push did. Null when the run stopped before
        /// getting that far.
        public TsPushResult TsPush { get; set; }

        /// Whether the solve was taken now or reused from an earlier one, which
        /// the spec says the dock has to say out loud.
        public bool SolveWasReused { get; set; }

        /// The lines to report, in order. The sequencer log writes them one per
        /// line; the dock joins them.
        public List<string> Lines { get; set; } = new List<string>();

        /// A single line for the dock's result text: how many plans reached
        /// Target Scheduler and what the focal length change was.
        public string ShortResult {
            get {
                if (!Success) return Failure ?? "Sync for tonight did not finish.";

                var loaded = TsPush != null && TsPush.Success
                    ? $"{TsPush.Pushed.Count} {(TsPush.Pushed.Count == 1 ? "plan" : "plans")} " +
                      "loaded into Target Scheduler"
                    : $"{Selected.Count} {(Selected.Count == 1 ? "plan" : "plans")} fit tonight's gear";

                var focal = WriteBack == null || !WriteBack.Written
                    ? "profile focal length unchanged"
                    : $"focal length {WriteBack.OldFocalLengthMm:F1} to {WriteBack.NewFocalLengthMm:F1} mm";

                var line = $"{loaded}, {focal}.";
                // A push that did not happen is the thing the user most needs
                // to see, so it goes on the end rather than into the log alone.
                if (TsPush != null && !TsPush.Success) {
                    line += " " + TsPush.Failure;
                }
                return line;
            }
        }
    }

    /// Everything Sync for tonight does after the solve. One implementation,
    /// two callers: the sequencer instruction and the dock button. Keeping it
    /// here is what makes the spec's rule enforceable, that the profile write
    /// back happens in exactly those two places and nowhere else.
    public interface ISyncForTonightRunner {

        /// Build the fingerprint from the solve, correct the profile focal
        /// length if it is far enough out and write-back is on, ask ACP which
        /// plans fit, and load them into Target Scheduler.
        Task<SyncOutcome> RunAsync(SolveSnapshot solve, bool writeBackEnabled, CancellationToken token);
    }

    [Export(typeof(ISyncForTonightRunner))]
    public class SyncForTonightRunner : ISyncForTonightRunner {

        private readonly IGearFingerprintService fingerprintService;
        private readonly IProfileWriteBack writeBack;
        private readonly ITsPushService tsPush;
        private readonly IProfileService profileService;

        [ImportingConstructor]
        public SyncForTonightRunner(
            IGearFingerprintService fingerprintService,
            IProfileWriteBack writeBack,
            ITsPushService tsPush,
            IProfileService profileService
        ) {
            this.fingerprintService = fingerprintService;
            this.writeBack = writeBack;
            this.tsPush = tsPush;
            this.profileService = profileService;
        }

        public async Task<SyncOutcome> RunAsync(
            SolveSnapshot solve, bool writeBackEnabled, CancellationToken token
        ) {
            var settings = AcpSettings.Load();
            var outcome = new SyncOutcome { Solve = solve };
            var client = new AcpApiClient(settings.ServerUrl);

            outcome.Fingerprint = fingerprintService.Build(solve);
            outcome.Lines.Add(DescribeFingerprint(outcome.Fingerprint));

            // Gear is fetched before the match because the focal ratio write
            // back needs an aperture, and the write-back runs before the match
            // call. A server that cannot answer is not a reason to abandon the
            // run: the focal length is still written, just without a ratio.
            // The gear is wanted twice: for the aperture behind the focal ratio,
            // and for the focal lengths, sensor sizes and per filter settings
            // the Target Scheduler push turns into mosaic panels and exposure
            // templates. Fetch it once and keep it.
            GearResponse gear = null;
            try {
                gear = await client.GetGearAsync(token).ConfigureAwait(false);
            } catch (AcpUnauthorizedException) {
                throw;
            } catch (Exception ex) {
                Logger.Warning($"ACP: could not read gear from ACP, so no focal ratio will be written: {ex.Message}");
            }

            outcome.WriteBack = writeBack.Apply(outcome.Fingerprint, gear?.Telescopes, writeBackEnabled);
            outcome.Lines.Add(outcome.WriteBack.Summary);

            try {
                outcome.Match = await client
                    .MatchPlansAsync(outcome.Fingerprint, settings.SyncMode, token)
                    .ConfigureAwait(false);
            } catch (AcpUnauthorizedException ex) {
                outcome.Failure = ex.Message;
                outcome.Lines.Add(ex.Message);
                return outcome;
            } catch (Exception ex) {
                outcome.Failure = $"ACP could not match the plans: {ex.Message}";
                outcome.Lines.Add(outcome.Failure);
                return outcome;
            }

            outcome.Selected = MatchSelection.SelectForMode(outcome.Match, settings.SyncMode);
            outcome.Lines.Add(MatchSelection.Summarise(outcome.Match, settings.SyncMode));

            // The mode has already decided what Selected holds: everything, or
            // only the plans that fit plus the ones with no gear set. The push
            // takes that list as given.
            if (outcome.Selected.Count == 0) {
                outcome.Lines.Add("Target Scheduler was left as it was, because nothing was selected to load.");
                outcome.Success = true;
                LogLines(outcome);
                return outcome;
            }

            var profileId = profileService?.ActiveProfile?.Id.ToString();
            outcome.TsPush = await tsPush
                .PushAsync(outcome.Selected.Cast<Plan>().ToList(), gear, profileId, token)
                .ConfigureAwait(false);
            outcome.Lines.Add(outcome.TsPush.Summary());

            if (outcome.TsPush.Success && !string.IsNullOrEmpty(outcome.TsPush.BackupPath)) {
                outcome.Lines.Add($"The database was copied to {outcome.TsPush.BackupPath} first.");
            }

            // A push that did not run is not a failed night: the fingerprint is
            // built, the profile is corrected and the user has been told why
            // Target Scheduler is untouched. The run reports itself as done and
            // the reason is in the log and in the dock.
            outcome.Success = true;
            LogLines(outcome);
            return outcome;
        }

        private static void LogLines(SyncOutcome outcome) {
            foreach (var line in outcome.Lines) {
                Logger.Info("ACP: " + line);
            }
        }

        private static string DescribeFingerprint(Fingerprint fingerprint) {
            var camera = fingerprint?.Camera;
            var filters = fingerprint?.Filters != null && fingerprint.Filters.Count > 0
                ? string.Join(", ", fingerprint.Filters)
                : "no filter wheel";
            var focal = fingerprint?.FocalLengthMm;
            var focalText = focal == null
                ? "no focal length"
                : focal.Solved.HasValue
                    ? $"{focal.Solved.Value:F0} mm from the solve"
                    : $"{focal.Profile:F0} mm from the profile, because nothing was solved";
            return
                $"Gear: {camera?.Name ?? "no camera"} " +
                $"({(camera != null && camera.Colour ? "colour" : "mono")}, bin {camera?.Binning ?? 1}), " +
                $"{filters}, {fingerprint?.Mount?.Name ?? "no mount"}, {focalText}.";
        }
    }
}
