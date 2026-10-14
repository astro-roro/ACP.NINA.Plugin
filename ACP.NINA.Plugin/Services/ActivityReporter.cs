using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services {

    /// Where a batch of events goes. The real one is AcpApiClient; the tests
    /// use a fake ACP behind a real client.
    public interface IActivitySink {

        Task<ActivityPostResult> PostActivityEventsAsync(
            IReadOnlyList<ActivityEvent> events, CancellationToken ct
        );
    }

    /// Posts to whatever server URL is configured at the moment of the call, so
    /// a URL changed on the Options page takes effect without a NINA restart.
    public class UrlActivitySink : IActivitySink {

        private readonly Func<string> serverUrl;

        public UrlActivitySink(Func<string> serverUrl) {
            this.serverUrl = serverUrl;
        }

        public Task<ActivityPostResult> PostActivityEventsAsync(
            IReadOnlyList<ActivityEvent> events, CancellationToken ct
        ) {
            return new AcpApiClient(serverUrl()).PostActivityEventsAsync(events, ct);
        }
    }

    /// The events waiting to go to ACP, held in memory.
    ///
    /// Bounded: an ACP that stays unreachable for a night must not grow the
    /// NINA process without limit. When the queue is over its cap the oldest
    /// plain information event goes first, then the oldest of anything, so
    /// warnings, errors and needs-you events outlive progress chatter.
    ///
    /// An event queued under a key that is already waiting replaces it when its
    /// seq is not lower. That is what keeps a busy imaging target to one
    /// pending event with the latest totals instead of one per frame.
    public class ActivityQueue {

        /// 500 events is a few hours of ordinary activity and well under a
        /// megabyte, so the cap is generous for a night and still a hard stop.
        public const int DefaultCapacity = 500;

        private readonly int capacity;
        private readonly object gate = new object();
        private readonly List<ActivityEvent> items = new List<ActivityEvent>();

        public ActivityQueue(int capacity = DefaultCapacity) {
            this.capacity = Math.Max(1, capacity);
        }

        /// How many events the cap has pushed out so far.
        public int DroppedCount { get; private set; }

        public int Count {
            get { lock (gate) { return items.Count; } }
        }

        public void Enqueue(ActivityEvent e) {
            if (e == null) return;
            lock (gate) {
                var at = items.FindIndex(x => x.Key == e.Key);
                if (at >= 0) {
                    var old = items[at];
                    if (old.Seq.HasValue && e.Seq.HasValue && e.Seq.Value < old.Seq.Value) return;
                    items[at] = e;
                    return;
                }
                items.Add(e);
                Trim();
            }
        }

        /// Up to `max` events from the front, removed from the queue.
        public List<ActivityEvent> TakeBatch(int max) {
            lock (gate) {
                var n = Math.Min(Math.Max(0, max), items.Count);
                var batch = items.GetRange(0, n);
                items.RemoveRange(0, n);
                return batch;
            }
        }

        /// Put a failed batch back at the front, in its original order. An event
        /// whose key has been queued again meanwhile is left out, because what
        /// is waiting is newer.
        public void Requeue(IEnumerable<ActivityEvent> batch) {
            if (batch == null) return;
            lock (gate) {
                foreach (var e in batch.Reverse()) {
                    if (items.Any(x => x.Key == e.Key)) continue;
                    items.Insert(0, e);
                }
                Trim();
            }
        }

        public void Clear() {
            lock (gate) { items.Clear(); }
        }

        public List<ActivityEvent> Snapshot() {
            lock (gate) { return items.ToList(); }
        }

        private void Trim() {
            while (items.Count > capacity) {
                var at = items.FindIndex(x => x.IsDroppable);
                if (at < 0) at = 0;
                items.RemoveAt(at);
                DroppedCount++;
            }
        }
    }

    /// Sends the plugin's events to ACP's Activity tab (phase 2 of
    /// docs/specs/activity-tab.md in ACP).
    ///
    /// Events are queued in memory and sent in batches of at most 100: on a 60
    /// second tick, and when the Target Scheduler container stops. A batch that
    /// fails for a reason that may pass (ACP unreachable, a 5xx, a 401 while the
    /// token is being fixed) goes back on the queue and is tried again on the
    /// next tick with the same keys. ACP stores by (source, key), so a batch
    /// that reached it but whose answer was lost is harmless to send twice.
    ///
    /// Events ACP rejected, and batches it refused outright, are logged and
    /// never retried: sending the same bad event again cannot succeed.
    ///
    /// After the first 404 or 403 the reporter stops for the rest of the NINA
    /// session and says so once. A 404 means this ACP has no Activity tab or has
    /// it switched off, and a 403 means it has no token set; neither fixes
    /// itself while NINA runs.
    ///
    /// Nothing here throws into NINA. A night's imaging is worth more than an
    /// activity row.
    public class ActivityReporter : ISubscriber, IDisposable {

        /// The batch limit ACP enforces. A bigger batch is refused with 413.
        public const int MaxBatch = 100;

        public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);

        private readonly IActivitySink sink;
        private readonly Func<bool> isEnabled;
        private readonly IMessageBroker broker;
        private readonly Action<string> log;
        private readonly TimeSpan interval;
        private readonly ActivityQueue queue;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        private bool started;
        private bool disposed;
        private volatile bool disabled;

        public ActivityReporter(
            IActivitySink sink,
            Func<bool> isEnabled,
            IMessageBroker broker = null,
            Action<string> log = null,
            TimeSpan? interval = null,
            int capacity = ActivityQueue.DefaultCapacity
        ) {
            this.sink = sink;
            this.isEnabled = isEnabled ?? (() => true);
            this.broker = broker;
            this.log = log ?? (m => Logger.Warning(m));
            this.interval = interval ?? FlushInterval;
            this.queue = new ActivityQueue(capacity);
        }

        /// True once ACP has answered 404 or 403. Stays true until NINA restarts.
        public bool IsDisabled => disabled;

        public int QueuedCount => queue.Count;

        public int DroppedCount => queue.DroppedCount;

        /// Queue an event. Does nothing while the option is off or after ACP has
        /// said it does not want events.
        public void Post(ActivityEvent e) {
            if (e == null || disposed || disabled) return;
            bool enabled;
            try { enabled = isEnabled(); } catch (Exception) { enabled = false; }
            if (!enabled) return;
            queue.Enqueue(e);
        }

        // -- Lifecycle ---------------------------------------------------------

        public void Start() {
            if (started || disposed) return;
            started = true;

            if (broker != null) {
                try {
                    broker.Subscribe(TsContainerWatch.TopicContainerStopped, this);
                } catch (Exception ex) {
                    Logger.Debug($"ACP: could not subscribe the activity reporter to the container topic: {ex.Message}");
                }
            }
            _ = TickLoopAsync(cts.Token);
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            if (broker != null && started) {
                try {
                    broker.Unsubscribe(TsContainerWatch.TopicContainerStopped, this);
                } catch (Exception) { }
            }
            try {
                cts.Cancel();
                cts.Dispose();
            } catch (Exception) { }
        }

        /// The Target Scheduler container has stopped: send what is waiting now
        /// rather than at the next tick, so the night's last frames are not
        /// stranded if NINA is closed straight afterwards.
        public async Task OnMessageReceived(IMessage message) {
            try {
                if (message == null) return;
                if (message.SenderId != TsContainerWatch.TargetSchedulerSenderId) return;
                await FlushAsync(cts.Token).ConfigureAwait(false);
            } catch (Exception ex) {
                Logger.Debug($"ACP: activity flush on container stop failed: {ex.Message}");
            }
        }

        private async Task TickLoopAsync(CancellationToken ct) {
            while (!ct.IsCancellationRequested) {
                try {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                    await FlushAsync(ct).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    return;
                } catch (Exception ex) {
                    Logger.Debug($"ACP: activity tick failed: {ex.Message}");
                }
            }
        }

        // -- Sending -----------------------------------------------------------

        /// Send everything waiting, 100 at a time, stopping at the first batch
        /// that fails. Returns how many events ACP received (accepted or
        /// rejected), which is 0 when nothing could be sent.
        public async Task<int> FlushAsync(CancellationToken ct = default) {
            if (disabled) {
                queue.Clear();
                return 0;
            }

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try {
                var delivered = 0;
                while (!disabled) {
                    if (!SafeEnabled()) {
                        queue.Clear();
                        break;
                    }
                    var batch = queue.TakeBatch(MaxBatch);
                    if (batch.Count == 0) break;

                    ActivityPostResult result;
                    try {
                        result = await sink.PostActivityEventsAsync(batch, ct).ConfigureAwait(false);
                    } catch (OperationCanceledException) {
                        queue.Requeue(batch);
                        throw;
                    } catch (AcpHttpException ex) when (
                        ex.Status == HttpStatusCode.NotFound || ex.Status == HttpStatusCode.Forbidden
                    ) {
                        disabled = true;
                        queue.Clear();
                        log($"ACP: activity events turned off for this NINA session, ACP answered {(int)ex.Status}. "
                            + (ex.Status == HttpStatusCode.NotFound
                                ? "This ACP has no Activity tab, or it is switched off."
                                : "ACP has no access token set, so it will not take events."));
                        break;
                    } catch (AcpHttpException ex) when (IsFinalRefusal(ex.Status)) {
                        log($"ACP: activity batch of {batch.Count} refused with {(int)ex.Status} and dropped: {ex.ServerMessage ?? ex.Message}");
                        continue;
                    } catch (Exception ex) {
                        // ACP unreachable, a 5xx, a timeout or a 401: back on
                        // the queue for the next tick.
                        queue.Requeue(batch);
                        Logger.Debug($"ACP: activity batch of {batch.Count} will be retried: {ex.Message}");
                        break;
                    }

                    delivered += batch.Count;
                    if (result?.Rejected != null) {
                        foreach (var r in result.Rejected) {
                            log($"ACP: activity event '{r?.Key}' rejected by ACP and not retried: {r?.Error}");
                        }
                    }
                }
                return delivered;
            } finally {
                gate.Release();
            }
        }

        /// A status that means this exact batch can never be accepted, so
        /// sending it again would only repeat the refusal.
        private static bool IsFinalRefusal(HttpStatusCode status) {
            return status == HttpStatusCode.RequestEntityTooLarge
                || status == HttpStatusCode.BadRequest
                || status == HttpStatusCode.UnprocessableEntity;
        }

        private bool SafeEnabled() {
            try { return isEnabled(); } catch (Exception) { return false; }
        }
    }

    /// The one place the rest of the plugin hands events to, so code that is
    /// not part of the dock (the Sync for tonight runner, called from the
    /// sequencer too) can post without holding a reference to the reporter.
    /// Does nothing until the dock has attached its reporter.
    public static class ActivityHub {

        private static volatile ActivityReporter current;

        public static void Attach(ActivityReporter reporter) {
            current = reporter;
        }

        public static void Detach(ActivityReporter reporter) {
            if (ReferenceEquals(current, reporter)) current = null;
        }

        public static void Post(ActivityEvent e) {
            try {
                current?.Post(e);
            } catch (Exception ex) {
                Logger.Debug($"ACP: could not queue an activity event: {ex.Message}");
            }
        }
    }
}
