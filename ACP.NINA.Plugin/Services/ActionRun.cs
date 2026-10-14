using System;
using System.Threading;

namespace ACP.NINA.Plugin.Services {

    /// The stop button and the time limit for one run of an ACP action.
    ///
    /// The token fires on Stop, on the time limit, or when the outer token
    /// (NINA's own, for the sequence instruction) fires. Why() tells the three
    /// apart, so the dock and the sequence can say what happened. The limit
    /// comes from AcpSettings.ActionTimeoutMinutes unless one is given.
    public sealed class ActionRun : IDisposable {
        private readonly CancellationTokenSource cts;
        private readonly CancellationToken outer;

        public CancellationToken Token { get; }
        public TimeSpan Limit { get; }
        public bool StopRequested { get; private set; }

        public ActionRun(CancellationToken outer = default, TimeSpan? limit = null) {
            this.outer = outer;
            cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            Token = cts.Token;
            Limit = limit ?? TimeSpan.FromMinutes(
                AcpSettings.ClampTimeoutMinutes((AcpSettings.Current ?? AcpSettings.Load()).ActionTimeoutMinutes));
            if (Limit > TimeSpan.Zero) cts.CancelAfter(Limit);
        }

        /// Ask the run to stop. False when a stop was already asked for.
        public bool Stop() {
            if (StopRequested) return false;
            StopRequested = true;
            try {
                cts.Cancel();
            } catch (ObjectDisposedException) {
                // The run finished between the click and the cancel.
            }
            return true;
        }

        /// The token fired because the time limit ran out, not Stop or NINA.
        public bool TimedOut => Token.IsCancellationRequested && !StopRequested && !outer.IsCancellationRequested;

        /// "Sync for tonight stopped." or "... stopped after its 10 minute limit."
        public string Why(string action) {
            if (!TimedOut) return $"{action} stopped.";
            var minutes = Math.Round(Limit.TotalMinutes, 1);
            return $"{action} stopped after its {minutes:0.#} minute limit.";
        }

        public void Dispose() => cts.Dispose();
    }
}
