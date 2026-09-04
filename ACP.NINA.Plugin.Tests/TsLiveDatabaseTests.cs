using ACP.NINA.Plugin.Services.TargetScheduler;
using NINA.Core.Utility;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// A read-only smoke test against a real Target Scheduler database.
    ///
    /// The fixtures are reconstructed schemas, and a reconstruction can be
    /// right about every column and still miss something a live install does.
    /// Point ACP_TS_LIVE_DB at a copy of a real schedulerdb.sqlite and this
    /// checks the plugin can open it, agrees with its version, and can read
    /// every table it will later write to.
    ///
    /// It reads and never writes, but point it at a copy anyway. When the
    /// variable is unset the test passes without doing anything, which is what
    /// happens on CI and on the maintainer's Mac.
    public class TsLiveDatabaseTests {

        private const string EnvLiveDb = "ACP_TS_LIVE_DB";

        [Fact]
        public void ARealDatabaseOpensAndEveryTableThePushWritesCanBeRead() {
            var path = Environment.GetEnvironmentVariable(EnvLiveDb);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) {
                // Nothing to check on a machine with no NINA install.
                return;
            }

            using (var db = TargetSchedulerDb.Open(path, readOnly: true)) {
                Assert.True(
                    TsSchema.IsSupported(db.UserVersion),
                    $"the live database is at user_version {db.UserVersion}, outside 23 to 28");

                // Every table the push writes has to be there with the columns
                // the entity classes name, or the first real sync fails on a
                // machine no test ever ran on.
                foreach (var table in new[] { "exposuretemplate", "project", "target", "exposureplan" }) {
                    Assert.True(db.ColumnsByTable.ContainsKey(table), $"no {table} table");
                }

                AssertHasColumns(db, "project", new TsProject().Columns);
                AssertHasColumns(db, "exposuretemplate", new TsExposureTemplate().Columns);
                AssertHasColumns(db, "exposureplan", new TsExposurePlan().Columns);
                AssertHasColumns(
                    db, "target",
                    new TsTarget().Columns.Where(c => TsSchema.ColumnExistsAt("target", c, db.UserVersion)));

                // And the reads run against real rows rather than an empty
                // fixture, which is where a column type surprise would show.
                foreach (var profileId in ProfileIds(db)) {
                    var snapshot = db.ReadAll(profileId);
                    var acquired = db.ReadAcquired(profileId);
                    Logger.Info(
                        $"ACP: live database check, profile {profileId}: " +
                        $"{snapshot.ProjectsById.Count} projects, {snapshot.TargetsById.Count} targets, " +
                        $"{snapshot.PlansById.Count} exposure plans, {acquired.Count} with a stamp.");
                }
            }
        }

        private static void AssertHasColumns(
            TargetSchedulerDb db, string table, System.Collections.Generic.IEnumerable<string> expected
        ) {
            var actual = db.ColumnsByTable[table];
            foreach (var column in expected) {
                Assert.True(actual.Contains(column), $"{table} has no column {column}");
            }
        }

        private static System.Collections.Generic.List<string> ProfileIds(TargetSchedulerDb db) {
            var ids = new System.Collections.Generic.List<string>();
            using (var cmd = db.Connection.CreateCommand()) {
                cmd.CommandText = "SELECT DISTINCT profileId FROM project WHERE profileId IS NOT NULL";
                using (var reader = cmd.ExecuteReader()) {
                    while (reader.Read()) ids.Add(reader.GetString(0));
                }
            }
            return ids;
        }
    }
}
