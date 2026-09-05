using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services {

    /// Acquired hours flowing back to ACP while imaging. Part F of the v3 spec.
    ///
    /// Target Scheduler says it started or finished a target, we read that
    /// target's acquired counts out of the Target Scheduler database, turn
    /// counts into hours, and POST them to the plan's progress endpoint. ACP
    /// raises actual_hours and never lowers it, so a report that arrives late
    /// or twice costs nothing.
    ///
    /// This is the other half of the v3.1 push. That push writes ACP's view of
    /// acquired counts into Target Scheduler, so a stale actual_hours walks the
    /// counts backwards; this is what keeps actual_hours from going stale in
    /// the first place.
    ///
    /// Three things make it safe to leave running all night.
    ///
    /// The database is only ever read, and opened read only. Nothing here takes
    /// a write lock on the file Target Scheduler is using to run the night,
    /// which is why this can run mid session when the push deliberately cannot.
    ///
    /// Every failure is swallowed into the status line. An exception escaping a
    /// pub/sub callback is our bug landing in somebody else's imaging run, and
    /// a night's imaging is worth more than a progress number.
    ///
    /// One report at a time. The event path and the timer share a gate, so a
    /// slow ACP cannot pile up overlapping posts.
    public class ProgressReporter : ISubscriber, IDisposable {

        /// How often to report anyway while a container is running, in case an
        /// event was missed. Five minutes per the spec: often enough that the
        /// dock is never far out of date, rare enough to be invisible.
        public static readonly TimeSpan FallbackInterval = TimeSpan.FromMinutes(5);

        /// The topics worth a report. Taken from TsContainerWatch rather than
        /// spelled again here, so there is one list of Target Scheduler topic
        /// names in the plugin and it cannot drift.
        ///
        /// There is no ContainerStarted topic; Target Scheduler does not
        /// publish one. Knowing whether a container is running is
        /// TsContainerWatch's job and this defers to it rather than keeping a
        /// second, disagreeing opinion.
        public static readonly string[] ProgressTopics = {
            TsContainerWatch.TopicTargetStart,
            TsContainerWatch.TopicNewTargetStart,
            TsContainerWatch.TopicTargetComplete,
        };

        /// Subscribed to as well, so the night's totals are not left one event
        /// short when the container stops.
        public const string TopicContainerStopped = TsContainerWatch.TopicContainerStopped;

        private readonly IMessageBroker broker;
        private readonly ITsProgressSource tsSource;
        private readonly IPlanRefsSource refsSource;
        private readonly IProgressSink sink;
        private readonly ITsContainerWatch containerWatch;
        private readonly Func<bool> isEnabled;
        private readonly Func<string> profileId;
        private readonly Func<DateTimeOffset> clock;

        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource timerCts = new CancellationTokenSource();
        private readonly object stateLock = new object();

        private bool started;
        private bool disposed;
        private DateTimeOffset? lastSentUtc;
        private string lastError;

        public ProgressReporter(
            IMessageBroker broker,
            ITsProgressSource tsSource,
            IPlanRefsSource refsSource,
            IProgressSink sink,
            ITsContainerWatch containerWatch,
            Func<bool> isEnabled,
            Func<string> profileId,
            Func<DateTimeOffset> clock = null
        ) {
            this.broker = broker;
            this.tsSource = tsSource;
            this.refsSource = refsSource;
            this.sink = sink;
            this.containerWatch = containerWatch;
            this.isEnabled = isEnabled ?? (() => true);
            this.profileId = profileId ?? (() => null);
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        // -- Status ------------------------------------------------------------

        /// Raised after every attempt, so the dock can refresh its footer
        /// without polling.
        public event EventHandler StatusChanged;

        public DateTimeOffset? LastSentUtc {
            get { lock (stateLock) { return lastSentUtc; } }
        }

        public string LastError {
            get { lock (stateLock) { return lastError; } }
        }

        /// Whether the timer should be reporting right now.
        ///
        /// With no container watch there is no broker either, so no events will
        /// ever arrive and the timer is the only path there is. Running it
        /// unconditionally in that case is the difference between the feature
        /// degrading and the feature silently not existing, which is the same
        /// call v3.1 made about its own missing broker.
        public bool ShouldReportOnTimer =>
            containerWatch == null || containerWatch.IsRunning;

        public string StatusLine {
            get {
                DateTimeOffset? sent;
                string error;
                lock (stateLock) {
                    sent = lastSentUtc;
                    error = lastError;
                }
                return ProgressStatus.Describe(isEnabled(), sent, error, clock());
            }
        }

        // -- Lifecycle ---------------------------------------------------------

        /// Subscribes to the progress topics and starts the fallback timer.
        ///
        /// Subscribing is unconditional rather than gated on the Options
        /// toggle, and the toggle is checked when an event arrives instead.
        /// Otherwise switching it on mid session would do nothing until NINA
        /// restarted, which is exactly when somebody would want to switch it on.
        public void Start() {
            if (started || disposed) return;
            started = true;

            if (broker == null) {
                Logger.Warning(
                    "ACP: no message broker, progress will be reported on the five minute timer only."
                );
            } else {
                foreach (var topic in AllTopics()) {
                    try {
                        broker.Subscribe(topic, this);
                    } catch (Exception ex) {
                        Logger.Debug($"ACP: could not subscribe to {topic}: {ex.Message}");
                    }
                }
                Logger.Info("ACP: subscribed to Target Scheduler progress topics.");
            }

            _ = FallbackLoopAsync(timerCts.Token);
        }

        private static IEnumerable<string> AllTopics() {
            foreach (var t in ProgressTopics) yield return t;
            yield return TopicContainerStopped;
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;

            if (broker != null && started) {
                foreach (var topic in AllTopics()) {
                    try {
                        broker.Unsubscribe(topic, this);
                    } catch (Exception ex) {
                        Logger.Debug($"ACP: could not unsubscribe from {topic}: {ex.Message}");
                    }
                }
            }
            try {
                timerCts.Cancel();
                timerCts.Dispose();
            } catch (Exception) {
                // Already gone, nothing useful to do.
            }
            gate.Dispose();
        }

        // -- Events ------------------------------------------------------------

        /// The pub/sub callback. Never throws.
        public async Task OnMessageReceived(IMessage message) {
            try {
                await HandleAsync(message, timerCts.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // NINA is shutting down, or the plugin was disposed mid report.
            } catch (Exception ex) {
                SetError($"Progress report failed: {ex.Message}");
                Logger.Error($"ACP: progress reporter threw handling a Target Scheduler event: {ex}");
            }
        }

        private async Task HandleAsync(IMessage message, CancellationToken ct) {
            if (message == null) return;

            // Same check the container watch makes: another plugin publishing
            // on a topic of the same name does not get to move ACP's numbers.
            if (message.SenderId != TsContainerWatch.TargetSchedulerSenderId) return;

            var topic = message.Topic ?? string.Empty;

            if (string.Equals(topic, TopicContainerStopped, StringComparison.OrdinalIgnoreCase)) {
                // One last report on the way out, so ACP and the dock agree on
                // the night's totals rather than being one event short.
                Logger.Info("ACP: Target Scheduler container stopped, sending a final progress report.");
                await ReportAllAsync(ct).ConfigureAwait(false);
                return;
            }

            if (!IsProgressTopic(topic)) return;

            var found = TsEventReader.Read(message);
            if (found.HasTargetId) {
                Logger.Debug($"ACP: {topic} for Target Scheduler target {found.TargetId} (read from {found.Source}).");
                await ReportForTargetAsync(found.TargetId.Value, ct).ConfigureAwait(false);
                return;
            }

            Logger.Info(
                $"ACP: {topic} carried no readable target id ({found.Source}"
                + (string.IsNullOrWhiteSpace(found.TargetName) ? "" : $", name '{found.TargetName}'")
                + "), reporting every synced plan instead."
            );
            await ReportAllAsync(ct).ConfigureAwait(false);
        }

        private static bool IsProgressTopic(string topic) {
            return Array.IndexOf(ProgressTopics, topic) >= 0;
        }

        // -- Fallback timer ----------------------------------------------------

        /// Reports every plan every five minutes while a container is running.
        ///
        /// This is the belt to the events' braces. If Target Scheduler changes
        /// a payload shape, renames a topic, or simply does not fire, the dock
        /// is still never more than five minutes out of date.
        private async Task FallbackLoopAsync(CancellationToken ct) {
            while (!ct.IsCancellationRequested) {
                try {
                    await Task.Delay(FallbackInterval, ct).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    return;
                }
                if (ct.IsCancellationRequested) return;
                if (!ShouldReportOnTimer) continue;

                try {
                    await ReportAllAsync(ct).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    return;
                } catch (Exception ex) {
                    SetError($"Progress report failed: {ex.Message}");
                    Logger.Warning($"ACP: fallback progress report failed: {ex.Message}");
                }
            }
        }

        // -- Reporting ---------------------------------------------------------

        /// Report the plan that owns one Target Scheduler target.
        /// Returns the number of plans reported, so 0 or 1 here.
        public async Task<int> ReportForTargetAsync(int tsTargetId, CancellationToken ct = default) {
            if (!isEnabled()) return 0;

            var all = await SafeReadRefsAsync(ct).ConfigureAwait(false);
            var refs = ProgressMapper.FindPlanForTarget(all, tsTargetId, profileId());
            if (refs == null) {
                // A target Target Scheduler is imaging that no ACP plan claims.
                // Normal: people are free to add their own projects.
                Logger.Debug($"ACP: Target Scheduler target {tsTargetId} belongs to no ACP plan, nothing to report.");
                return 0;
            }
            return await ReportPlanAsync(refs, ct).ConfigureAwait(false) ? 1 : 0;
        }

        /// Report every plan that maps onto Target Scheduler rows.
        /// Returns how many were reported successfully.
        public async Task<int> ReportAllAsync(CancellationToken ct = default) {
            if (!isEnabled()) return 0;

            var all = await SafeReadRefsAsync(ct).ConfigureAwait(false);
            var plans = ProgressMapper.ReportablePlans(all, profileId());
            var sent = 0;
            foreach (var refs in plans) {
                if (ct.IsCancellationRequested) break;
                if (await ReportPlanAsync(refs, ct).ConfigureAwait(false)) sent++;
            }
            return sent;
        }

        /// Build and send one plan's report. Returns false when there was
        /// nothing worth saying as well as when the send failed; only an actual
        /// failure touches the status line.
        private async Task<bool> ReportPlanAsync(TsPlanRefs refs, CancellationToken ct) {
            if (refs == null || string.IsNullOrWhiteSpace(refs.AcpPlanId)) return false;

            var anchor = ProgressMapper.AnchorTargetId(refs);
            if (!anchor.HasValue) return false;

            var body = BuildPayload(anchor.Value);
            if (body == null) return false;

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try {
                var resp = await sink.ReportProgressAsync(refs.AcpPlanId, body, ct)
                    .ConfigureAwait(false);
                MarkSent();
                Logger.Info($"ACP: progress for plan {refs.AcpPlanId}: {resp?.ToShortString() ?? "sent"}");
                return true;
            } catch (AcpUnauthorizedException ex) {
                SetError(ex.Message);
                Logger.Warning($"ACP: progress rejected for plan {refs.AcpPlanId}: {ex.Message}");
                return false;
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                SetError($"Progress report failed: {ex.Message}");
                Logger.Warning($"ACP: progress failed for plan {refs.AcpPlanId}: {ex.Message}");
                return false;
            } finally {
                gate.Release();
            }
        }

        /// The conversion from one Target Scheduler target to a request body.
        /// Public so the tests can check the payload with no broker, no
        /// database and no server anywhere in the picture.
        ///
        /// Returns null when there is nothing worth sending. An empty filters
        /// block would be a POST that says "no news", and ACP has enough to do.
        public ProgressRequest BuildPayload(int anchorTargetId) {
            IReadOnlyList<TsProgressRow> rows;
            try {
                rows = tsSource.ReadRowsForTarget(anchorTargetId);
            } catch (Exception ex) {
                SetError($"Could not read Target Scheduler: {ex.Message}");
                Logger.Warning($"ACP: reading Target Scheduler target {anchorTargetId} failed: {ex.Message}");
                return null;
            }
            if (rows == null || rows.Count == 0) return null;

            var filters = ProgressMath.BuildFilters(rows);
            if (filters.Count == 0) return null;

            return new ProgressRequest {
                Filters = filters,
                Source = "ts",
                At = clock().ToUniversalTime()
                    .ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            };
        }

        private async Task<IReadOnlyList<TsPlanRefs>> SafeReadRefsAsync(CancellationToken ct) {
            if (refsSource == null) return new List<TsPlanRefs>();
            try {
                return await refsSource.ReadPlanRefsAsync(ct).ConfigureAwait(false)
                    ?? (IReadOnlyList<TsPlanRefs>)new List<TsPlanRefs>();
            } catch (OperationCanceledException) {
                throw;
            } catch (AcpUnauthorizedException ex) {
                SetError(ex.Message);
                return new List<TsPlanRefs>();
            } catch (Exception ex) {
                SetError($"Could not work out which plans to report: {ex.Message}");
                Logger.Warning($"ACP: reading plan refs failed: {ex.Message}");
                return new List<TsPlanRefs>();
            }
        }

        private void MarkSent() {
            lock (stateLock) {
                lastSentUtc = clock();
                lastError = null;
            }
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SetError(string message) {
            lock (stateLock) {
                lastError = message;
            }
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
