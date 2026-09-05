using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// A push must not overwrite what the camera and the grader recorded.
    ///
    /// exposureplan.acquired is how many subs were taken and accepted is the
    /// grader's verdict on them. ACP has no business stating either on a row
    /// that already exists, so both are written when a row is created and never
    /// on an update. project.createdate goes the same way: a creation date that
    /// tracks the last push is wrong on its face.
    ///
    /// The Python extension's tests/test_frame_counts.py asserts the same
    /// things against the same fixtures.
    public class TsFrameCountsTests {

        private const long Later = 1800000000L;

        [Fact]
        public void ANightOfImagingSurvivesTheNextPush() {
            // The scenario the audit ran by hand: push, image, push the same plans.
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("counts.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    TsUpsert.Apply(db, Payload());

                    Exec(db.Connection,
                        "UPDATE exposureplan SET acquired = 137, accepted = 120 WHERE Id = 1");
                    Exec(db.Connection,
                        "UPDATE exposureplan SET acquired = 40, accepted = 0 WHERE Id = 2");

                    var outcome = TsUpsert.Apply(db, Payload());
                    Assert.Equal(7, outcome.ExposurePlan.Updated);
                    Assert.Equal(0, outcome.ExposurePlan.Inserted);

                    Assert.Equal(137, Scalar(db.Connection, "acquired", "exposureplan", 1));
                    Assert.Equal(120, Scalar(db.Connection, "accepted", "exposureplan", 1));
                    Assert.Equal(40, Scalar(db.Connection, "acquired", "exposureplan", 2));
                    Assert.Equal(0, Scalar(db.Connection, "accepted", "exposureplan", 2));
                }
            }
        }

        [Fact]
        public void ABrandNewRowStillGetsItsInitialCounts() {
            // Insert is the one place ACP does state the counts.
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("fresh.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var plans = TsTestPlans.ThreePlans();
                    // Four hours already in the bag on the two-filter plan's Ha
                    // goal, at 600 s a sub, so ACP computes 24 acquired on a row
                    // it is creating.
                    plans[2].FilterGoals["Ha"].ActualHours = 4.0;
                    TsUpsert.Apply(db, TsConvert.BuildPayload(
                        plans, TsTestPlans.Gear(), TsTestPlans.ProfileId, TsTestPlans.FrozenNow));

                    var acquired = Column(db.Connection, "acquired", "exposureplan");
                    Assert.Contains(24, acquired);
                    Assert.Equal(24, acquired.Sum());
                }
            }
        }

        [Fact]
        public void ThePushStillUpdatesEverythingItIsMeantTo() {
            // Only the insert-only columns are held back, not the whole row.
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("settings.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    TsUpsert.Apply(db, Payload());
                    Exec(db.Connection,
                        "UPDATE exposureplan SET desired = 1, exposure = 5 WHERE Id = 1");
                    Exec(db.Connection, "UPDATE project SET minimumaltitude = 0 WHERE Id = 1");

                    TsUpsert.Apply(db, Payload());
                    Assert.Equal(12, Scalar(db.Connection, "desired", "exposureplan", 1));
                    Assert.Equal(300, Scalar(db.Connection, "exposure", "exposureplan", 1));
                    Assert.Equal(30, Scalar(db.Connection, "minimumaltitude", "project", 1));
                }
            }
        }

        [Fact]
        public void CreateDateIsTheCreationDateNotTheLastPush() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("created.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    TsUpsert.Apply(db, Payload(TsTestPlans.FrozenNow));
                    Assert.All(
                        Column(db.Connection, "createdate", "project"),
                        v => Assert.Equal(TsTestPlans.FrozenNow, v));

                    TsUpsert.Apply(db, Payload(Later));
                    Assert.All(
                        Column(db.Connection, "createdate", "project"),
                        v => Assert.Equal(TsTestPlans.FrozenNow, v));
                }
            }
        }

        [Fact]
        public void ClaimingAHandMadeRowLeavesItsCountsAlone() {
            // A claim is an update of a row that already exists, so same rule.
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("claim.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    TsUpsert.Apply(db, Payload());
                    // Strip the guid off one plan row and give it a night's
                    // work, which is the shape of a row Target Scheduler left
                    // unstamped.
                    Exec(db.Connection,
                        "UPDATE exposureplan SET guid = '', acquired = 55, accepted = 50 " +
                        "WHERE Id = 1");

                    var outcome = TsUpsert.Apply(db, Payload());
                    Assert.Equal(1, outcome.ExposurePlan.Claimed);
                    Assert.Equal(55, Scalar(db.Connection, "acquired", "exposureplan", 1));
                    Assert.Equal(50, Scalar(db.Connection, "accepted", "exposureplan", 1));
                    Assert.NotEqual(
                        string.Empty, Text(db.Connection, "guid", "exposureplan", 1));
                }
            }
        }

        // -- Helpers ---------------------------------------------------------

        private static TsSyncPayload Payload(long now = TsTestPlans.FrozenNow) {
            return TsConvert.BuildPayload(
                TsTestPlans.ThreePlans(), TsTestPlans.Gear(), TsTestPlans.ProfileId, now);
        }

        private static void Exec(SqliteConnection conn, string sql) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        private static long Scalar(SqliteConnection conn, string column, string table, int id) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = $"SELECT \"{column}\" FROM {table} WHERE Id = {id}";
                return System.Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        private static string Text(SqliteConnection conn, string column, string table, int id) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = $"SELECT \"{column}\" FROM {table} WHERE Id = {id}";
                var value = cmd.ExecuteScalar();
                return value == null ? null : value.ToString();
            }
        }

        private static List<long> Column(SqliteConnection conn, string column, string table) {
            var values = new List<long>();
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = $"SELECT \"{column}\" FROM {table} ORDER BY Id";
                using (var reader = cmd.ExecuteReader()) {
                    while (reader.Read()) {
                        values.Add(System.Convert.ToInt64(
                            reader.GetValue(0), CultureInfo.InvariantCulture));
                    }
                }
            }
            return values;
        }
    }
}
