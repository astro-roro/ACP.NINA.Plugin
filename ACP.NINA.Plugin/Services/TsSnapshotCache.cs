using ACP.NINA.Plugin.Services.TargetScheduler;
using NINA.Core.Utility;
using System;

namespace ACP.NINA.Plugin.Services {

    /// One read of the Target Scheduler database, shared by everything in a
    /// reporting pass and thrown away shortly after.
    ///
    /// The reason this exists: a reporting pass asks about one target at a
    /// time, while the underlying read is whole database. Without the cache,
    /// reporting eight plans opens and reads the database eight times for one
    /// unchanged answer, on a file another plugin is using to run the night.
    ///
    /// The window is deliberately much shorter than the five minute fallback
    /// and shorter than any sub exposure, so the next event still sees fresh
    /// counts. This is a way of not asking the same question eight times in a
    /// row, not a way of remembering anything.
    public class TsSnapshotCache {

        public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

        private readonly Func<string> dbPathProvider;
        private readonly Func<DateTimeOffset> clock;
        private readonly object gate = new object();

        private TsSnapshot cached;
        private string cachedProfileId;
        private DateTimeOffset cachedAt;

        public TsSnapshotCache(
            Func<string> dbPathProvider = null,
            Func<DateTimeOffset> clock = null
        ) {
            this.dbPathProvider = dbPathProvider ?? (() => null);
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        /// The snapshot for a profile, reading the database only when the last
        /// one is stale or was for a different profile. Null when no profile is
        /// active, because Target Scheduler row ids only mean anything inside
        /// one.
        ///
        /// Opened read only, so this can never take a write lock on a database
        /// Target Scheduler is running a container against.
        public TsSnapshot Get(string profileId) {
            if (string.IsNullOrWhiteSpace(profileId)) return null;

            lock (gate) {
                if (cached != null
                    && string.Equals(cachedProfileId, profileId, StringComparison.OrdinalIgnoreCase)
                    && clock() - cachedAt < Window) {
                    return cached;
                }
            }

            TsSnapshot fresh;
            using (var db = TargetSchedulerDb.Open(dbPathProvider(), readOnly: true)) {
                fresh = db.ReadAll(profileId);
            }

            lock (gate) {
                cached = fresh;
                cachedProfileId = profileId;
                cachedAt = clock();
            }
            Logger.Debug(
                $"ACP: read Target Scheduler for progress, {fresh.PlansById.Count} exposure plans "
                + $"across {fresh.TargetsById.Count} targets."
            );
            return fresh;
        }

        /// Drop the cache. Used after a push, when the rows the reporter joins
        /// against have just changed underneath it.
        public void Invalidate() {
            lock (gate) {
                cached = null;
                cachedProfileId = null;
            }
        }
    }
}
