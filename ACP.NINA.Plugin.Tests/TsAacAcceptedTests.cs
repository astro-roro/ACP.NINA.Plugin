using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// AAC's accepted counts reach Target Scheduler, tests A3 to A6 in
    /// docs/specs/goal-panel-and-ts-order.md section 7. Every case in
    /// Fixtures/aac_accepted_cases.json runs here exactly as
    /// tests/test_aac_accepted.py runs it in the extension: seed the exposure
    /// plans AAC would have found, push the plan with its AAC figures, then
    /// check accepted, desired, enablegrader and the push result's notes.
    /// acquired must never move. The file is copied from
    /// acp-nina-ts-sync/tests/fixtures; never edit this copy.
    public class TsAacAcceptedTests {

        private const string Profile = TsTestPlans.ProfileId;

        private static JObject Doc() {
            return JObject.Parse(TsFixtures.ReadFixture("aac_accepted_cases.json"));
        }

        public static IEnumerable<object[]> CaseNames() {
            foreach (var c in (JArray)Doc()["cases"]) yield return new object[] { (string)c["name"] };
        }

        private static JObject CaseNamed(string name) {
            return (JObject)((JArray)Doc()["cases"]).First(c => (string)c["name"] == name);
        }

        private static List<FilterRow> AcpRows(JObject c, bool withAac) {
            var rows = new List<FilterRow>();
            var i = 0;
            foreach (JObject r in (JArray)c["rows"]) {
                var row = new FilterRow {
                    Id = "row" + i++,
                    Filter = (string)r["filter"],
                    Template = (string)r["template"],
                    TemplateSubS = (double)r["template_sub_s"],
                    Unit = (string)r["unit"],
                };
                if (r["target_frames"] != null) row.TargetFrames = (double)r["target_frames"];
                if (r["target_hours"] != null) row.TargetHours = (double)r["target_hours"];
                if (withAac && r["aac"] != null && r["aac"].Type == JTokenType.Object) row.Aac = r["aac"].DeepClone();
                rows.Add(row);
            }
            return rows;
        }

        private static Plan PlanFor(JObject c, bool withAac) {
            var mosaic = (bool)c["plan"]["mosaic"];
            var plan = TsTestPlans.Plan(
                "m42", projectName: "M42", targetName: "M42", rows: 1, cols: mosaic ? 2 : 1,
                filterRows: AcpRows(c, withAac));
            if (withAac) {
                plan.Aac = new JObject {
                    { "filed_through_night", c["plan"]["filed_through_night"] },
                    { "min_total_frames", c["plan"]["min_total_frames"] },
                    { "received_at", "2026-10-12T10:05:00+00:00" },
                };
            }
            return plan;
        }

        private static void Exec(SqliteConnection conn, string sql, params object[] args) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$p" + i, args[i]);
                cmd.ExecuteNonQuery();
            }
        }

        private static long Scalar(SqliteConnection conn, string sql) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        private static Dictionary<long, long> AcquiredById(SqliteConnection conn) {
            var map = new Dictionary<long, long>();
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "SELECT Id, acquired FROM exposureplan";
                using (var r = cmd.ExecuteReader()) {
                    while (r.Read()) map[r.GetInt64(0)] = r.GetInt64(1);
                }
            }
            return map;
        }

        private static TsSyncOutcome Run(JObject c, TargetSchedulerDb db, out Dictionary<long, long> acquiredBefore) {
            var conn = db.Connection;
            // The profile's grading settings: all 0 unless the case says otherwise.
            var prefs = new Dictionary<string, double>();
            foreach (JProperty p in ((JObject)Doc()["default_profile"]).Properties()) prefs[p.Name] = (double)p.Value;
            foreach (JProperty p in ((JObject)c["profile"]).Properties()) prefs[p.Name] = (double)p.Value;
            var cols = string.Join(", ", prefs.Keys);
            var marks = string.Join(", ", prefs.Keys.Select((k, i) => "$v" + i));
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = $"INSERT INTO profilepreference (profileId, {cols}) VALUES ($pid, {marks})";
                cmd.Parameters.AddWithValue("$pid", Profile);
                var n = 0;
                foreach (var v in prefs.Values) cmd.Parameters.AddWithValue("$v" + n++, v);
                cmd.ExecuteNonQuery();
            }

            // What TS holds before: a push with no AAC figures, then the counts the case names.
            TsRowScenarios.Push(db, PlanFor(c, false));
            foreach (JObject r in (JArray)c["rows"]) {
                Exec(conn,
                    "UPDATE exposureplan SET desired = $p0, acquired = $p1, accepted = $p2 WHERE exposureTemplateId IN " +
                    "(SELECT Id FROM exposuretemplate WHERE name = $p3)",
                    (long)r["ts"]["desired"], (long)r["ts"]["acquired"], (long)r["ts"]["accepted"], (string)r["template"]);
            }
            Exec(conn, "UPDATE project SET enablegrader = $p0", (long)c["ts"]["enablegrader"]);
            var projectId = Scalar(conn, "SELECT Id FROM project");
            var targetId = Scalar(conn, "SELECT Id FROM target ORDER BY Id LIMIT 1");
            foreach (var stamp in c["ts"]["acquired_images_utc"]) {
                var when = DateTimeOffset.Parse((string)stamp, CultureInfo.InvariantCulture).ToUnixTimeSeconds();
                Exec(conn,
                    "INSERT INTO acquiredimage (projectId, targetId, acquireddate, filtername, gradingStatus, metadata) " +
                    "VALUES ($p0, $p1, $p2, 'Ha', 1, '{}')", projectId, targetId, when);
            }
            acquiredBefore = AcquiredById(conn);

            var now = DateTimeOffset.Parse((string)c["now"], CultureInfo.InvariantCulture);
            var payload = TsConvert.BuildPayload(
                new List<Plan> { PlanFor(c, true) }, TsRowScenarios.Gear(), Profile, TsTestPlans.FrozenNow, null, now);
            return TsUpsert.Apply(db, payload);
        }

        [Theory]
        [MemberData(nameof(CaseNames))]
        public void A6TheSharedCasesWriteTheExpectedRows(string name) {
            var c = CaseNamed(name);
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("aac.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    Dictionary<long, long> acquiredBefore;
                    var outcome = Run(c, db, out acquiredBefore);
                    var conn = db.Connection;
                    foreach (JObject r in (JArray)c["rows"]) {
                        var got = new List<Tuple<long, long>>();
                        using (var cmd = conn.CreateCommand()) {
                            cmd.CommandText =
                                "SELECT e.desired, e.accepted FROM exposureplan e " +
                                "JOIN exposuretemplate x ON x.Id = e.exposureTemplateId WHERE x.name = $n";
                            cmd.Parameters.AddWithValue("$n", (string)r["template"]);
                            using (var rd = cmd.ExecuteReader()) {
                                while (rd.Read()) got.Add(Tuple.Create(rd.GetInt64(0), rd.GetInt64(1)));
                            }
                        }
                        Assert.NotEmpty(got);
                        foreach (var g in got) {
                            Assert.Equal((long)r["expected"]["desired"], g.Item1);
                            Assert.Equal((long)r["expected"]["accepted"], g.Item2);
                        }
                    }
                    Assert.Equal((long)c["expected"]["enablegrader"], Scalar(conn, "SELECT enablegrader FROM project"));
                    Assert.Equal(acquiredBefore, AcquiredById(conn));

                    var warning = c["expected"]["warning"];
                    if (warning != null && warning.Type == JTokenType.String) {
                        Assert.Contains(outcome.Notes, n => n.Contains((string)warning));
                    } else {
                        Assert.DoesNotContain(outcome.Notes, n => n.Contains("AAC"));
                    }
                    Assert.Equal((bool)c["expected"]["written"], outcome.AacWrittenProjects.Count > 0);
                }
            }
        }

        [Fact]
        public void A6TheFixtureHasTheT8Cases() {
            var names = ((JArray)Doc()["cases"]).Select(c => (string)c["name"]).ToList();
            foreach (var wanted in new[] {
                "ha_shortfall_grader_on", "dolphin_oiii_grader_switches_on", "mosaic_writes_nothing",
                "rerun_at_23_00_after_tonights_frames", "new_sii_row_with_zero_figures" }) {
                Assert.Contains(wanted, names);
            }
        }

        [Fact]
        public void A6DesiredFollowsTheSharedFloorRule() {
            foreach (JObject c in (JArray)Doc()["cases"]) {
                var plan = PlanFor(c, true);
                var rows = TsRows.RowsOf(plan, null);
                var cases = ((JArray)c["rows"]).Cast<JObject>().ToList();
                for (var i = 0; i < rows.Count; i++) {
                    Assert.Equal((int)cases[i]["expected"]["desired"], TsRows.DesiredFor(rows[i], plan));
                }
            }
        }

        [Fact]
        public void A4AMalformedPlanBlockOpensNoWrite() {
            var plan = PlanFor(CaseNamed("ha_shortfall_grader_on"), true);
            Assert.NotNull(TsRows.PlanAac(plan));
            plan.Aac = new JObject { { "filed_through_night", "2026-10-11" }, { "min_total_frames", 10.0 } };
            Assert.Null(TsRows.PlanAac(plan));
            plan.Aac = new JObject { { "filed_through_night", "yesterday" }, { "min_total_frames", 10 } };
            Assert.Null(TsRows.PlanAac(plan));
        }
    }
}
