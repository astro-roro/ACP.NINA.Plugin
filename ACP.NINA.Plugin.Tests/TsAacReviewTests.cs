using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// Review fixes for the AAC link, phase 6
    /// (docs/specs/goal-panel-and-ts-order.md section 6), mirroring
    /// tests/test_aac_review_fixes.py in the extension: the base records the
    /// row's goal and not AAC's floor, a goal of 0 disables the plan like a
    /// removed row, a push says why it skipped AAC's counts, a complete flag
    /// is judged against the goal AAC saw, and ACP's JSON dates stay strings.
    public class TsAacReviewTests {

        private const string Profile = TsTestPlans.ProfileId;
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
            "2026-10-12T15:00:00+11:00", CultureInfo.InvariantCulture);

        private static JObject Block() {
            return new JObject {
                { "accepted_seconds", 60.0 }, { "accepted_frames", 4 },
                { "seen_seconds", 60.0 }, { "seen_frames", 4 },
                { "complete", false },
                { "estimated_hours", JValue.CreateNull() }, { "measurement", JValue.CreateNull() },
            };
        }

        private static void SetPlanAac(Plan plan, int floor) {
            plan.Aac = new JObject {
                { "filed_through_night", "2026-10-11" },
                { "min_total_frames", floor },
                { "received_at", "x" },
            };
        }

        private static TargetSchedulerDb OpenDb(TempDir tmp) {
            var path = TsFixtures.MakeDb(28, tmp.File("review.sqlite"));
            var db = TargetSchedulerDb.Open(path);
            using (var cmd = db.Connection.CreateCommand()) {
                cmd.CommandText =
                    "INSERT INTO profilepreference (profileId, delayGrading, enableGradeRMS, enableGradeStars, " +
                    "enableGradeHFR, enableGradeFWHM, enableGradeEccentricity) VALUES ($p, 0, 0, 0, 0, 0, 0)";
                cmd.Parameters.AddWithValue("$p", Profile);
                cmd.ExecuteNonQuery();
            }
            return db;
        }

        /// A push judged at the fixed moment, then the link a real sync leaves.
        private static TsSyncOutcome PushAndLink(TargetSchedulerDb db, params Plan[] plans) {
            var outcome = TsUpsert.Apply(db, TsConvert.BuildPayload(
                plans.ToList(), TsRowScenarios.Gear(), Profile, TsTestPlans.FrozenNow, null, Now));
            TsRowScenarios.Link(db, outcome, plans);
            return outcome;
        }

        private static long Scalar(SqliteConnection conn, string sql) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        private static void Exec(SqliteConnection conn, string sql) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        private static long Enabled(SqliteConnection conn, string template) {
            return Scalar(conn,
                "SELECT e.enabled FROM exposureplan e JOIN exposuretemplate x ON x.Id = e.exposureTemplateId " +
                $"WHERE x.name = '{template}'");
        }

        private static long Desired(SqliteConnection conn, string template) {
            return Scalar(conn,
                "SELECT e.desired FROM exposureplan e JOIN exposuretemplate x ON x.Id = e.exposureTemplateId " +
                $"WHERE x.name = '{template}'");
        }

        private static Plan TwoRows(int oiiiFrames) {
            return TsRowScenarios.M42Plan(
                TsTestPlans.Row("ha", "Ha", "Ha 300", 300, frames: 4),
                TsTestPlans.Row("o", "OIII", "OIII 300", 300, frames: oiiiFrames));
        }

        // -- Decision 1: the floor never reaches ACP's goal -------------------

        [Fact]
        public void TheBaseRecordsTheGoalNotTheFlooredDesired() {
            using (var tmp = new TempDir())
            using (var db = OpenDb(tmp)) {
                var plan = TsRowScenarios.M42Plan(TsTestPlans.Row("rs", "R", "R Stars", 15, frames: 8));
                plan.FilterRows[0].Aac = Block();
                SetPlanAac(plan, 10);
                PushAndLink(db, plan);

                Assert.Equal(10, Desired(db.Connection, "R Stars"));
                var entry = (JObject)plan.TsLinks[Profile]["base_snapshot"]["exposure_plans_by_panel_filter"]["1,1_rs"];
                Assert.Equal(8, (int)entry["desired"]);
            }
        }

        [Fact]
        public void ADesiredThatIsNotTheFloorStaysAsTsHasIt() {
            var row = TsTestPlans.Row("rs", "R", "R Stars", 15, frames: 8);
            row.Aac = Block();
            var plan = TsRowScenarios.M42Plan(row);
            SetPlanAac(plan, 10);
            Assert.Equal(8, TsRows.UnflooredGoal(row, plan, 10));
            Assert.Equal(14, TsRows.UnflooredGoal(row, plan, 14));
            Assert.Equal(9, TsRows.UnflooredGoal(row, plan, 9));
            row.Aac = null;
            Assert.Equal(10, TsRows.UnflooredGoal(row, plan, 10));
        }

        // -- Decision 3: a goal of 0 is a removed row -------------------------

        [Fact]
        public void GoalZeroDisablesThePlanAndARiseEnablesItAgain() {
            using (var tmp = new TempDir())
            using (var db = OpenDb(tmp)) {
                var plan = TwoRows(4);
                PushAndLink(db, plan);
                plan.FilterRows[1].TargetFrames = 0;
                var outcome = PushAndLink(db, plan);
                Assert.Equal(0, Enabled(db.Connection, "OIII 300"));
                Assert.Equal(1, Enabled(db.Connection, "Ha 300"));
                Assert.Equal(1, outcome.DisabledPlans);

                plan.FilterRows[1].TargetFrames = 6;
                PushAndLink(db, plan);
                Assert.Equal(1, Enabled(db.Connection, "OIII 300"));
                Assert.Equal(6, Desired(db.Connection, "OIII 300"));
            }
        }

        [Fact]
        public void ADisabledZeroGoalPlanNoLongerBlocksAac() {
            using (var tmp = new TempDir())
            using (var db = OpenDb(tmp)) {
                var plan = TwoRows(4);
                PushAndLink(db, plan);
                plan.FilterRows[1].TargetFrames = 0;
                foreach (var row in plan.FilterRows) row.Aac = Block();
                SetPlanAac(plan, 3);
                var outcome = PushAndLink(db, plan);
                Assert.Equal(new[] { "M42" }, outcome.AacWrittenProjects.ToArray());
            }
        }

        // -- Decision 3: a skipped project says why ---------------------------

        [Fact]
        public void ASkippedProjectSaysWhy() {
            using (var tmp = new TempDir())
            using (var db = OpenDb(tmp)) {
                var plan = TwoRows(4);
                plan.FilterRows[0].Aac = Block();
                SetPlanAac(plan, 3);
                var outcome = PushAndLink(db, plan);
                Assert.Empty(outcome.AacWrittenProjects);
                Assert.Contains("M42: AAC counts not written, OIII 300 has no AAC figures yet.", outcome.Notes);
            }
        }

        [Fact]
        public void AProjectAacNeverTouchedGetsNoNote() {
            using (var tmp = new TempDir())
            using (var db = OpenDb(tmp)) {
                var outcome = PushAndLink(db, TwoRows(4));
                Assert.DoesNotContain(outcome.Notes, n => n.Contains("AAC"));
            }
        }

        [Fact]
        public void APlanTsHoldsThatAcpDoesNotNamesItself() {
            using (var tmp = new TempDir())
            using (var db = OpenDb(tmp)) {
                var plan = TwoRows(4);
                PushAndLink(db, plan);
                // Drop the OIII row in ACP but leave its plan enabled, as a hand edit in TS would.
                plan.FilterRows = plan.FilterRows.Take(1).ToList();
                foreach (var row in plan.FilterRows) row.Aac = Block();
                SetPlanAac(plan, 3);
                Exec(db.Connection, "UPDATE exposureplan SET guid = 'hand' WHERE Id = (SELECT MAX(Id) FROM exposureplan)");
                plan.TsLinks = null;
                var outcome = PushAndLink(db, plan);
                Assert.Empty(outcome.AacWrittenProjects);
                Assert.Contains(outcome.Notes,
                    n => n.Contains("is enabled in Target Scheduler but is not a row in ACP"));
            }
        }

        // -- Decision 4: complete is judged against the goal AAC saw ----------

        private static FilterRow CompleteRow(string unit, double goal, double? goalAtSend) {
            var aac = new JObject { { "complete", true } };
            if (goalAtSend.HasValue) aac["goal_at_send"] = goalAtSend.Value;
            var row = new FilterRow { Id = "r", Filter = "Ha", Template = "Ha", TemplateSubS = 300, Unit = unit, Aac = aac };
            if (unit == "fr") row.TargetFrames = goal; else row.TargetHours = goal;
            return row;
        }

        [Fact]
        public void CompleteCountsOnlyWhileTheGoalIsNotAboveTheOneSent() {
            Assert.False(TsRows.CompleteCounts(CompleteRow("h", 2.0, 0.5)));
            Assert.True(TsRows.CompleteCounts(CompleteRow("h", 0.5, 0.5)));
            Assert.True(TsRows.CompleteCounts(CompleteRow("h", 0.25, 0.5)));
            Assert.False(TsRows.CompleteCounts(CompleteRow("fr", 49, 48)));
        }

        [Fact]
        public void ABlockWithoutGoalAtSendKeepsItsCompleteFlag() {
            Assert.True(TsRows.CompleteCounts(CompleteRow("h", 9.0, null)));
            var notComplete = CompleteRow("h", 9.0, null);
            notComplete.Aac["complete"] = false;
            Assert.False(TsRows.CompleteCounts(notComplete));
        }

        // -- Decision 6: Json.NET dates ----------------------------------------

        private const string PlansJson = @"{
          ""version"": 1,
          ""plans"": [
            {""id"": ""m42"", ""project_name"": ""M42"", ""target"": {""name"": ""M42""},
             ""aac"": {""filed_through_night"": ""2026-10-11"", ""min_total_frames"": 10,
                       ""at"": ""2026-10-12T10:05:00Z"", ""received_at"": ""2026-10-12T10:05:00+00:00""},
             ""filter_rows"": [
               {""id"": ""rs"", ""filter"": ""R"", ""template"": ""R Stars"", ""template_sub_s"": 15,
                ""unit"": ""fr"", ""target_frames"": 8,
                ""aac"": {""accepted_seconds"": 60.0, ""accepted_frames"": 4, ""complete"": false,
                          ""at"": ""2026-10-12T10:05:00Z""}}
             ]}
          ]
        }";

        [Fact]
        public async Task RawPlansJsonKeepsTheAacBlockReadable() {
            var fake = new FakeAcpServer().Route("/api/plans", PlansJson);
            var client = new AcpApiClient("http://acp.local:5555", () => null, fake);

            var response = await client.GetPlansAsync();

            var plan = Assert.Single(response.Plans);
            var block = Assert.IsType<JObject>(plan.Aac);
            Assert.Equal(JTokenType.String, block["at"].Type);
            Assert.Equal("2026-10-12T10:05:00Z", (string)block["at"]);
            Assert.Equal("2026-10-12T10:05:00+00:00", (string)block["received_at"]);
            Assert.Equal("2026-10-11", (string)block["filed_through_night"]);
            Assert.NotNull(TsRows.PlanAac(plan));

            var row = plan.FilterRows[0];
            Assert.Equal(JTokenType.String, ((JObject)row.Aac)["at"].Type);
            Assert.Equal(10, TsRows.DesiredFor(row, plan));
            Assert.Equal(4, TsRows.AacAccepted(row, plan, 10));
        }
    }
}
