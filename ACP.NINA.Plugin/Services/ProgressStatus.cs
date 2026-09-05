using System;

namespace ACP.NINA.Plugin.Services {

    /// The one line the dock footer shows about progress reporting.
    ///
    /// Split out from the reporter so the wording can be tested without
    /// standing up a subscriber, and so the reporter has one less reason to
    /// know about the UI.
    public static class ProgressStatus {

        public const string Off = "Progress reporting is off.";
        public const string NothingYet = "No progress sent yet.";

        /// "Progress sent 22 s ago", or the error if the last attempt failed.
        ///
        /// An error wins over a success time on purpose. Once something is
        /// broken, how long ago the last good report was is not the thing the
        /// user needs off the dock at 2 am.
        public static string Describe(
            bool enabled,
            DateTimeOffset? lastSentUtc,
            string lastError,
            DateTimeOffset nowUtc
        ) {
            if (!enabled) return Off;
            if (!string.IsNullOrWhiteSpace(lastError)) return lastError;
            if (!lastSentUtc.HasValue) return NothingYet;
            return $"Progress sent {Ago(nowUtc - lastSentUtc.Value)} ago";
        }

        /// Coarse on purpose. A footer that ticks every second is a footer
        /// that pulls your eye all night, and nobody needs to know a report
        /// landed 97 seconds ago rather than 2 minutes ago.
        public static string Ago(TimeSpan span) {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            var seconds = (long)span.TotalSeconds;
            if (seconds < 90) return $"{seconds} s";
            var minutes = (long)Math.Round(span.TotalMinutes);
            if (minutes < 90) return $"{minutes} min";
            var hours = span.TotalHours;
            return $"{hours:0.#} h";
        }
    }
}
