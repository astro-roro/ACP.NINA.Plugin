using ACP.NINA.Plugin.Services;
using ACP.NINA.Plugin.Services.TargetScheduler;
using NINA.Core.Utility;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The progress read path against a real Target Scheduler database.
    ///
    /// The same reasoning as TsLiveDatabaseTests, pointed at the other
    /// direction: that one checks the push can write every table, this one
    /// checks the reporter can read real acquired counts and turn them into
    /// hours that are not absurd. A reconstructed fixture can be right about
    /// every column and still not tell you what a database with a season of
    /// imaging in it looks like.
    ///
    /// Read only throughout, and gated on ACP_TS_LIVE_DB, so it does nothing
    /// on CI or on the maintainer's Mac. Point it at a copy regardless.
    public class ProgressLiveDatabaseTests {

        private const string EnvLiveDb = "ACP_TS_LIVE_DB";

        [Fact]
        public void RealAcquiredCountsReadBackAsPlausibleHours() {
            var path = Environment.GetEnvironmentVariable(EnvLiveDb);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            // Profile ids live on the rows rather than in a table of their own,
            // and a live database can hold several profiles where the empty
            // ones would prove nothing. Take the one with the most rows.
            string profileId;
            using (var db = TargetSchedulerDb.Open(path, readOnly: true)) {
                profileId = FindBusiestProfile(db);
            }
            if (profileId == null) return;

            var cache = new TsSnapshotCache(() => path);
            var source = new TsDatabaseProgressSource(cache, () => profileId);
            var snapshot = cache.Get(profileId);
            Assert.NotNull(snapshot);

            var targetsWithRows = 0;
            var filtersSeen = 0;

            foreach (var targetId in snapshot.TargetsById.Keys) {
                var rows = source.ReadRowsForTarget(targetId);
                if (rows.Count == 0) continue;
                targetsWithRows++;

                foreach (var row in rows) {
                    Assert.False(
                        string.IsNullOrWhiteSpace(row.FilterName),
                        $"exposure plan {row.ExposurePlanId} came back with no filter name");
                    Assert.Equal(targetId, row.TargetId);

                    var hours = ProgressMath.AcquiredHours(row);
                    Assert.True(hours >= 0, $"negative hours for exposure plan {row.ExposurePlanId}");
                    // A single exposure plan holding more than a year of
                    // integration means the sub length or the count has been
                    // read out of the wrong column.
                    Assert.True(
                        hours < 8760,
                        $"exposure plan {row.ExposurePlanId} came back as {hours} hours, "
                        + $"from {ProgressMath.GoodCount(row)} subs of "
                        + $"{ProgressMath.SubExposureSeconds(row)} s");
                }

                filtersSeen += ProgressMath.BuildFilters(rows).Count;
            }

            Logger.Info(
                $"ACP live progress read: profile {profileId}, {targetsWithRows} targets with "
                + $"exposure plans, {filtersSeen} filter entries built.");
            Assert.True(targetsWithRows > 0, "no target in the live database had any exposure plan");
        }

        /// The profile id that owns the most exposure plans, which is the one
        /// worth reading. Returns null when the database has none at all.
        private static string FindBusiestProfile(TargetSchedulerDb db) {
            var counts = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var row in ReadProfileIds(db)) {
                if (string.IsNullOrWhiteSpace(row)) continue;
                counts.TryGetValue(row, out var n);
                counts[row] = n + 1;
            }
            if (counts.Count == 0) return null;
            return counts.OrderByDescending(kv => kv.Value).First().Key;
        }

        private static System.Collections.Generic.List<string> ReadProfileIds(TargetSchedulerDb db) {
            var ids = new System.Collections.Generic.List<string>();
            using (var cmd = db.Connection.CreateCommand()) {
                cmd.CommandText = "SELECT profileId FROM exposureplan";
                using (var reader = cmd.ExecuteReader()) {
                    while (reader.Read()) {
                        ids.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
                    }
                }
            }
            return ids;
        }
    }
}
