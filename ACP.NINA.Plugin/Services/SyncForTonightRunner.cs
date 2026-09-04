using ACP.NINA.Plugin.Models;
using NINA.Core.Utility;
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

        /// Whether the solve was taken now or reused from an earlier one, which
        /// the spec says the dock has to say out loud.
        public bool SolveWasReused { get; set; }

        /// The lines to report, in order. The sequencer log writes them one per
        /// line; the dock joins them.
        public List<string> Lines { get; set; } = new List<string>();

        /// A single line for the dock's result text: how many plans fit and
        /// what the focal length change was.
        public string ShortResult {
            get {
                if (!Success) return Failure ?? "Sync for tonight did not finish.";
                var plans = $"{Selected.Count} {(Selected.Count == 1 ? "plan" : "plans")} fit tonight's gear";
                var focal = WriteBack == null || !WriteBack.Written
                    ? "profile focal length unchanged"
                    : $"focal length {WriteBack.OldFocalLengthMm:F1} to {WriteBack.NewFocalLengthMm:F1} mm";
                return $"{plans}, {focal}.";
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
        /// plans fit, and report.
        ///
        /// Loading the chosen plans into Target Scheduler is v3.1. This version
        /// reports what would be loaded and leaves TS alone.
        Task<SyncOutcome> RunAsync(SolveSnapshot solve, bool writeBackEnabled, CancellationToken token);
    }

    [Export(typeof(ISyncForTonightRunner))]
    public class SyncForTonightRunner : ISyncForTonightRunner {

        private readonly IGearFingerprintService fingerprintService;
        private readonly IProfileWriteBack writeBack;

        [ImportingConstructor]
        public SyncForTonightRunner(
            IGearFingerprintService fingerprintService,
            IProfileWriteBack writeBack
        ) {
            this.fingerprintService = fingerprintService;
            this.writeBack = writeBack;
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
            List<Telescope> telescopes = null;
            try {
                var gear = await client.GetGearAsync(token).ConfigureAwait(false);
                telescopes = gear?.Telescopes;
            } catch (AcpUnauthorizedException) {
                throw;
            } catch (Exception ex) {
                Logger.Warning($"ACP: could not read gear from ACP, so no focal ratio will be written: {ex.Message}");
            }

            outcome.WriteBack = writeBack.Apply(outcome.Fingerprint, telescopes, writeBackEnabled);
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

            // v3.1 is where these plans reach Target Scheduler. Say so, rather
            // than letting someone think the sync happened.
            outcome.Lines.Add(
                "Target Scheduler was not touched. Loading these plans into it arrives in the next version."
            );

            outcome.Success = true;
            foreach (var line in outcome.Lines) {
                Logger.Info("ACP: " + line);
            }
            return outcome;
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
