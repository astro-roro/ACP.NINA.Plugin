using ACP.NINA.Plugin.Services.TargetScheduler;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// Rows written under the old identity recipe are restamped, not orphaned.
    ///
    /// The fixture is built by pushing with the current code and then winding
    /// every guid back to the pre-length-prefix recipe (TsScenarios), which
    /// spells the old recipe out rather than calling TsGuid.Legacy*, so the
    /// test states the old shape rather than trusting the code under test to
    /// describe it. The Python extension's tests/test_legacy_guid_migration.py
    /// asserts the same things against the same fixture.
    public class TsLegacyGuidMigrationTests {

        private static readonly string[] Tables =
            { "exposuretemplate", "project", "target", "exposureplan" };

        [Fact]
        public void TheFirstPushRestampsEveryAcpRow() {
            using (var tmp = new TempDir())
            using (var db = OldRecipeDb(tmp, "legacy.sqlite")) {
                var before = Counts(db.Connection);
                var expected = Tables.ToDictionary(t => t, t => Guids(db.Connection, t));

                var outcome = TsUpsert.Apply(db, Payload());

                // 2 templates + 3 projects + 6 targets + 7 plans.
                Assert.Equal(18, outcome.MigratedGuids);
                Assert.Empty(outcome.Notes);
                // Nothing new was inserted, so no second tree beside the old one.
                Assert.Equal(before, Counts(db.Connection));
                Assert.Equal(0, outcome.ExposureTemplate.Inserted);
                Assert.Equal(0, outcome.Project.Inserted);
                Assert.Equal(0, outcome.Target.Inserted);
                Assert.Equal(0, outcome.ExposurePlan.Inserted);

                foreach (var table in Tables) {
                    var now = Guids(db.Connection, table);
                    Assert.Equal(expected[table].Keys.OrderBy(k => k), now.Keys.OrderBy(k => k));
                    foreach (var row in now) {
                        Assert.True(
                            row.Value != expected[table][row.Key],
                            $"{table} {row.Key} was not restamped");
                    }
                }
            }
        }

        [Fact]
        public void TheRestampedGuidsAreTheOnesThePushWouldWrite() {
            using (var tmp = new TempDir())
            using (var db = OldRecipeDb(tmp, "match.sqlite")) {
                TsUpsert.Apply(db, Payload());

                var payload = Payload();
                var wanted = new HashSet<string>(
                    payload.Templates.Select(t => t.Guid)
                        .Concat(payload.Projects.Select(p => p.Guid))
                        .Concat(payload.TargetsByProjectGuid.Values.SelectMany(v => v)
                            .Select(t => t.Guid))
                        .Concat(payload.PlansByTargetGuid.Values.SelectMany(v => v)
                            .Select(p => p.Guid)),
                    StringComparer.Ordinal);

                var onDisk = new HashSet<string>(
                    Tables.SelectMany(t => Guids(db.Connection, t).Values), StringComparer.Ordinal);
                Assert.Equal(wanted.OrderBy(g => g), onDisk.OrderBy(g => g));
            }
        }

        [Fact]
        public void NoRowIsLeftPointingAtAParentThatMoved() {
            using (var tmp = new TempDir())
            using (var db = OldRecipeDb(tmp, "orphans.sqlite")) {
                TsUpsert.Apply(db, Payload());

                Assert.Equal(0, Count(db.Connection,
                    "SELECT COUNT(*) FROM target t " +
                    "LEFT JOIN project p ON p.Id = t.projectid WHERE p.Id IS NULL"));
                Assert.Equal(0, Count(db.Connection,
                    "SELECT COUNT(*) FROM exposureplan e " +
                    "LEFT JOIN target t ON t.Id = e.targetid " +
                    "LEFT JOIN exposuretemplate x ON x.Id = e.exposureTemplateId " +
                    "WHERE t.Id IS NULL OR x.Id IS NULL"));
                Assert.Equal(0, Count(db.Connection,
                    "SELECT COUNT(*) FROM project p WHERE NOT EXISTS " +
                    "(SELECT 1 FROM target t WHERE t.projectid = p.Id)"));
            }
        }

        [Fact]
        public void ARowThatIsNotOursIsLeftAlone() {
            using (var tmp = new TempDir())
            using (var db = OldRecipeDb(tmp, "foreign.sqlite")) {
                var stranger = Guid.NewGuid().ToString();
                using (var cmd = db.Connection.CreateCommand()) {
                    cmd.CommandText =
                        "INSERT INTO project (profileId, name, guid, state) " +
                        "VALUES ($p, 'Made in Target Scheduler', $g, 1)";
                    cmd.Parameters.AddWithValue("$p", TsTestPlans.ProfileId);
                    cmd.Parameters.AddWithValue("$g", stranger);
                    cmd.ExecuteNonQuery();
                }

                TsUpsert.Apply(db, Payload());

                using (var cmd = db.Connection.CreateCommand()) {
                    cmd.CommandText =
                        "SELECT guid FROM project WHERE name = 'Made in Target Scheduler'";
                    Assert.Equal(stranger, cmd.ExecuteScalar()?.ToString());
                }
            }
        }

        [Fact]
        public void ASecondPushMigratesNothingAndChangesNothing() {
            using (var tmp = new TempDir())
            using (var db = OldRecipeDb(tmp, "idem.sqlite")) {
                TsUpsert.Apply(db, Payload());
                var afterFirst = Tables.ToDictionary(t => t, t => Guids(db.Connection, t));
                var counts = Counts(db.Connection);

                var outcome = TsUpsert.Apply(db, Payload());
                Assert.Equal(0, outcome.MigratedGuids);
                Assert.Equal(counts, Counts(db.Connection));
                foreach (var table in Tables) {
                    Assert.Equal(afterFirst[table], Guids(db.Connection, table));
                }
            }
        }

        [Fact]
        public void MigrationRunsInsideThePushTransaction() {
            // A failure after the restamping takes the restamping back with it.
            using (var tmp = new TempDir())
            using (var db = OldRecipeDb(tmp, "rollback.sqlite")) {
                var before = Tables.ToDictionary(t => t, t => Guids(db.Connection, t));

                var payload = Payload();
                // A NOT NULL violation on the first project, after the two
                // templates and the whole migration have already run.
                payload.Projects[0].Name = null;

                Assert.ThrowsAny<Exception>(() =>
                    db.RunWriteAsync(
                        conn => TsUpsert.Apply(conn, db.UserVersion, db.ColumnsByTable, payload)
                    ).GetAwaiter().GetResult());

                foreach (var table in Tables) {
                    Assert.Equal(before[table], Guids(db.Connection, table));
                }
            }
        }

        // -- Helpers ---------------------------------------------------------

        private static TsSyncPayload Payload() {
            return TsConvert.BuildPayload(
                TsTestPlans.ThreePlans(), TsTestPlans.Gear(),
                TsTestPlans.ProfileId, TsTestPlans.FrozenNow);
        }

        /// A database exactly as the pre-fix code would have left it.
        private static TargetSchedulerDb OldRecipeDb(TempDir tmp, string name) {
            var path = TsFixtures.MakeDb(28, tmp.File(name));
            var db = TargetSchedulerDb.Open(path);
            try {
                TsUpsert.Apply(db, Payload());
                TsScenarios.WindBackToTheOldRecipe(db.Connection, TsTestPlans.ProfileId);
                return db;
            } catch {
                db.Dispose();
                throw;
            }
        }

        private static Dictionary<long, string> Guids(SqliteConnection conn, string table) {
            var map = new Dictionary<long, string>();
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = $"SELECT Id, guid FROM {table} ORDER BY Id";
                using (var reader = cmd.ExecuteReader()) {
                    while (reader.Read()) {
                        map[reader.GetInt64(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
                    }
                }
            }
            return map;
        }

        private static Dictionary<string, long> Counts(SqliteConnection conn) {
            return Tables.ToDictionary(t => t, t => Count(conn, $"SELECT COUNT(*) FROM {t}"));
        }

        private static long Count(SqliteConnection conn, string sql) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
    }
}
