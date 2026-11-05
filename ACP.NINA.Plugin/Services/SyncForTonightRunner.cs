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

        /// What writing the paused, draft and closed projects' state did, if
        /// anything was held. Null when nothing was held or the write could
        /// not run; see docs/specs/ts-project-settings.md section 6.
        public TsHeldStates.Outcome HeldStates { get; set; }

        /// Whether the solve was taken now or reused from an earlier one, which
        /// the spec says the dock has to say out loud.
        public bool SolveWasReused { get; set; }

        /// Set when the push worked but ACP could not be told about it.
        public string LinksWarning { get; set; }

        /// The lines to report, in order. The sequencer log writes them one per
        /// line; the dock joins them.
        public List<string> Lines { get; set; } = new List<string>();

        /// A single line for the dock's result text: how many plans reached
        /// Target Scheduler and what the focal length change was.
        /// The push was declined for a stated reason, most often a running
        /// Target Scheduler container. The run itself finished, but the dock
        /// should show this as the headline and not as a footnote to a tick.
        public bool PushRefused => TsPush != null && !TsPush.Success;

        public string ShortResult {
            get {
                if (!Success) return Failure ?? "Sync for tonight did not finish.";
                if (PushRefused) return TsPush.Failure;

                var loaded = TsPush != null && TsPush.Success
                    ? $"{TsPush.Pushed.Count} {(TsPush.Pushed.Count == 1 ? "plan" : "plans")} " +
                      "loaded into Target Scheduler"
                    : $"{Selected.Count} {(Selected.Count == 1 ? "plan" : "plans")} fit tonight's gear";

                var focal = WriteBack == null || !WriteBack.Written
                    ? "profile focal length unchanged"
                    : $"focal length {WriteBack.OldFocalLengthMm:F1} to {WriteBack.NewFocalLengthMm:F1} mm";

                var line = $"{loaded}, {focal}.";
                return LinksWarning == null ? line : $"{line} {LinksWarning}";
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
        private readonly Func<ActiveProfileView> profileReader;
        private readonly Func<AcpSettings> settingsReader;
        private readonly Func<string, AcpApiClient> clientFactory;
        private readonly Action<ActivityEvent> activity;

        /// The parts of the active NINA profile the run reads.
        public class ActiveProfileView {
            public string Id { get; set; }
            public string Name { get; set; }
            public List<string> WheelFilters { get; set; }
        }

        [ImportingConstructor]
        public SyncForTonightRunner(
            IGearFingerprintService fingerprintService,
            IProfileWriteBack writeBack,
            ITsPushService tsPush,
            IProfileService profileService
        ) : this(
            fingerprintService, writeBack, tsPush,
            () => {
                var profile = profileService?.ActiveProfile;
                return new ActiveProfileView {
                    Id = profile?.Id.ToString(),
                    Name = profile?.Name,
                    // Slot names from the profile rather than the wheel, so a
                    // daytime sync with nothing connected still writes names
                    // Target Scheduler can match.
                    WheelFilters = profile?.FilterWheelSettings?.FilterWheelFilters?
                        .Select(f => f?.Name)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .ToList(),
                };
            },
            AcpSettings.Load,
            url => new AcpApiClient(url)) { }

        /// The seam the tests use: the profile, the settings and the ACP
        /// client come from delegates, so the run can be driven without NINA.
        public SyncForTonightRunner(
            IGearFingerprintService fingerprintService,
            IProfileWriteBack writeBack,
            ITsPushService tsPush,
            Func<ActiveProfileView> profileReader,
            Func<AcpSettings> settingsReader,
            Func<string, AcpApiClient> clientFactory,
            Action<ActivityEvent> activity = null
        ) {
            this.activity = activity ?? ActivityHub.Post;
            this.fingerprintService = fingerprintService;
            this.writeBack = writeBack;
            this.tsPush = tsPush;
            this.profileReader = profileReader;
            this.settingsReader = settingsReader;
            this.clientFactory = clientFactory;
        }

        public async Task<SyncOutcome> RunAsync(
            SolveSnapshot solve, bool writeBackEnabled, CancellationToken token
        ) {
            var outcome = await RunCoreAsync(solve, writeBackEnabled, token).ConfigureAwait(false);
            PostActivity(outcome);
            return outcome;
        }

        /// One sync_for_tonight event per run that got an outcome, whether it
        /// finished, was refused or failed. Posting is a courtesy to ACP's
        /// Activity tab and never changes how the run went.
        private void PostActivity(SyncOutcome outcome) {
            try {
                var profile = profileReader();
                activity(ActivityEvents.SyncForTonight(outcome, DateTimeOffset.Now, profile?.Id, profile?.Name));
            } catch (Exception ex) {
                Logger.Debug($"ACP: could not record the sync as an activity event: {ex.Message}");
            }
        }

        private async Task<SyncOutcome> RunCoreAsync(
            SolveSnapshot solve, bool writeBackEnabled, CancellationToken token
        ) {
            var settings = settingsReader();
            var outcome = new SyncOutcome { Solve = solve };
            var client = clientFactory(settings.ServerUrl);

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
            } catch (Exception ex) when (!token.IsCancellationRequested) {
                Logger.Warning($"ACP: could not read gear from ACP, so no focal ratio will be written: {ex.Message}");
            }

            // A Stop means the profile is left alone too.
            token.ThrowIfCancellationRequested();
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
            } catch (Exception ex) when (settings.SyncMode == SyncMode.Everything && !token.IsCancellationRequested) {
                // Everything mode loads the lot whether or not ACP can judge
                // the fit, so a match that cannot run, most often because no
                // camera is connected and the fingerprint has no sensor, is
                // not a reason to stop. The plan list is fetched plain and
                // every plan is treated as unconstrained.
                Logger.Warning($"ACP: could not match the plans, loading everything unjudged: {ex.Message}");
                try {
                    var plans = await client.GetPlansAsync(token).ConfigureAwait(false);
                    outcome.Match = MatchSelection.Unjudged(plans?.Plans);
                    outcome.Lines.Add("ACP could not judge the fit, so every plan is loaded without a verdict.");
                } catch (AcpUnauthorizedException ex2) {
                    outcome.Failure = ex2.Message;
                    outcome.Lines.Add(ex2.Message);
                    return outcome;
                } catch (Exception ex2) when (!token.IsCancellationRequested) {
                    outcome.Failure = $"ACP could not list the plans: {ex2.Message}";
                    outcome.Lines.Add(outcome.Failure);
                    return outcome;
                }
            } catch (Exception ex) when (!token.IsCancellationRequested) {
                outcome.Failure = $"ACP could not match the plans: {ex.Message}";
                outcome.Lines.Add(outcome.Failure);
                return outcome;
            }

            outcome.Selected = MatchSelection.SelectForMode(outcome.Match, settings.SyncMode);
            outcome.Lines.Add(MatchSelection.Summarise(outcome.Match, settings.SyncMode));

            var profile = profileReader();
            var profileId = profile?.Id;

            // A paused plan is not loaded, so the push below never touches its
            // row and Target Scheduler would keep imaging it. This writes the
            // state column, and only that column, to the row it already has.
            // Runs whether or not anything was selected to load tonight, and
            // never counts as a reason the run failed: it is a courtesy on
            // top of the real push. See docs/specs/ts-project-settings.md
            // section 6.
            var held = outcome.Match?.Plans?
                .Where(p => p?.Match?.Verdict == MatchVerdict.Held)
                .Cast<Plan>()
                .ToList();
            if (held != null && held.Count > 0 && !string.IsNullOrWhiteSpace(profileId)) {
                var heldOutcome = await tsPush.WriteHeldStatesAsync(held, profileId, token).ConfigureAwait(false);
                var heldLine = heldOutcome?.Summary();
                if (heldLine != null) {
                    outcome.HeldStates = heldOutcome;
                    outcome.Lines.Add(heldLine);
                }
            }

            // The mode has already decided what Selected holds: everything, or
            // only the plans that fit plus the ones with no gear set. The push
            // takes that list as given.
            if (outcome.Selected.Count == 0) {
                outcome.Lines.Add("Target Scheduler was left as it was, because nothing was selected to load.");
                outcome.Success = true;
                LogLines(outcome);
                return outcome;
            }

            var wheelFilters = profile?.WheelFilters;

            // Send the template list as the database holds it, then read gear
            // again, so ACP's pull of edits made in Target Scheduler runs
            // before the push and the push sees the gear it produced
            // (docs/specs/gear-templates.md section 9). The writers do not
            // rely on the order, so a failure here only costs freshness.
            try {
                var current = await tsPush.ReadTemplatesAsync(profileId, token).ConfigureAwait(false);
                if (current != null &&
                    await TsTemplatesReporter.PostAsync(client, current, profileId, token).ConfigureAwait(false)) {
                    gear = await client.GetGearAsync(token).ConfigureAwait(false) ?? gear;
                }
            } catch (AcpUnauthorizedException) {
                throw;
            } catch (Exception ex) when (!token.IsCancellationRequested) {
                Logger.Warning($"ACP: could not refresh the template list and gear before the push: {ex.Message}");
            }

            // A Stop pressed from here on is honoured only before the write
            // takes its lock: the write is one transaction, and once it has
            // committed the reports below always go out, so ACP's record of
            // what Target Scheduler holds never falls behind it.
            outcome.TsPush = await tsPush
                .PushAsync(outcome.Selected.Cast<Plan>().ToList(), gear, profileId, token, wheelFilters,
                    profile?.Name)
                .ConfigureAwait(false);
            outcome.Lines.Add(outcome.TsPush.Summary());

            if (outcome.TsPush.Success && !string.IsNullOrEmpty(outcome.TsPush.BackupPath)) {
                outcome.Lines.Add($"The database was copied to {outcome.TsPush.BackupPath} first.");
            }

            // Tell ACP what was written, so its record of this rig's last sync
            // is current. A failure is a warning on the result, never a failed
            // run.
            if (outcome.TsPush.Success) {
                outcome.LinksWarning = await TsLinksReporter
                    .PostAsync(client, outcome.TsPush, profileId, CancellationToken.None)
                    .ConfigureAwait(false);
                if (outcome.LinksWarning != null) outcome.Lines.Add(outcome.LinksWarning);

                // The template list for ACP's drop-down. Best effort, logged
                // on failure, never a reason to warn the user.
                await TsTemplatesReporter.PostAsync(client, outcome.TsPush, profileId, CancellationToken.None)
                    .ConfigureAwait(false);
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
            var imaging = camera?.Binning ?? 1;
            var binText = fingerprint?.SolveBinning is int solveBin && solveBin != imaging
                ? $"imaging bin {imaging}, solve bin {solveBin}"
                : $"bin {imaging}";
            return
                $"Gear: {camera?.Name ?? "no camera"} " +
                $"({(camera != null && camera.Colour ? "colour" : "mono")}, {binText}), " +
                $"{filters}, {fingerprint?.Mount?.Name ?? "no mount"}, {focalText}.";
        }
    }
}
