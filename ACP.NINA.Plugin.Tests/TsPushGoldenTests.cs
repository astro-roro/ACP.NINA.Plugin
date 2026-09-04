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

    /// The acceptance test the v3 spec asks for: push the same three plans into
    /// a 23 and a 28 database and compare the rows to what the Python extension
    /// writes for the same plans, field by field.
    ///
    /// The expected rows in Fixtures/golden-rows.json were produced by running
    /// nina_ts_sync's own convert and upsert over tests/plans.py's three plans
    /// under a frozen clock, and dumping every row of every table it writes.
    /// If this test fails, the two tools have stopped agreeing about what they
    /// wrote, and a user who pushes from one and then the other will get
    /// duplicated or contradictory rows in Target Scheduler.
    ///
    /// Rows are matched by guid rather than by Id. The guid is the identity
    /// both tools actually use to find their own rows again, so matching on it
    /// tests the contract that matters and does not fail over a row ordering
    /// that neither tool promises. Foreign keys are compared as the guid of the
    /// row they point at, which checks the wiring rather than the numbering.
    public class TsPushGoldenTests {

        private static readonly string[] Tables =
            { "exposuretemplate", "project", "target", "exposureplan", "ruleweight" };

        /// Foreign key columns, and the table each one points into.
        private static readonly Dictionary<string, string> ForeignKeys =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "target.projectid", "project" },
                { "ruleweight.projectid", "project" },
                { "exposureplan.targetid", "target" },
                { "exposureplan.exposureTemplateId", "exposuretemplate" },
            };

        // -- The comparison --------------------------------------------------

        [Theory]
        [InlineData(23)]
        [InlineData(28)]
        public void PushWritesTheSameRowsAsThePythonExtension(int version) {
            using (var tmp = new TempDir()) {
                var actual = Push(version, tmp.File($"v{version}.sqlite"));
                var expected = Golden(version);

                foreach (var table in Tables) {
                    var expectedRows = Normalise(table, (JArray)expected.Tables[table], expected.IdToGuid);
                    var actualRows = Normalise(table, (JArray)actual.Tables[table], actual.IdToGuid);

                    Assert.True(
                        expectedRows.Count == actualRows.Count,
                        $"{table}: expected {expectedRows.Count} rows, got {actualRows.Count}");

                    foreach (var key in expectedRows.Keys) {
                        Assert.True(actualRows.ContainsKey(key), $"{table}: no row for {key}");
                        AssertRowsMatch(table, key, expectedRows[key], actualRows[key]);
                    }
                }
            }
        }

        [Theory]
        [InlineData(23)]
        [InlineData(28)]
        public void PushReportsTheSameCountsAsThePythonExtension(int version) {
            using (var tmp = new TempDir()) {
                var actual = Push(version, tmp.File($"v{version}.sqlite"));
                var expected = Golden(version).Report;

                foreach (var table in new[] { "exposuretemplate", "project", "target", "exposureplan" }) {
                    var counts = CountsFor(actual.Outcome, table);
                    Assert.Equal((int)expected[table]["inserted"], counts.Inserted);
                    Assert.Equal((int)expected[table]["updated"], counts.Updated);
                    Assert.Equal((int)expected[table]["claimed"], counts.Claimed);
                }
                Assert.Equal(
                    (int)expected["ruleweight_seeded_projects"],
                    actual.Outcome.RuleWeightSeededProjects);
                Assert.Empty(actual.Outcome.Notes);
            }
        }

        /// Only one column may differ between the two schemas, and it is the
        /// one Migrate/24.sql added. Anything else differing means the version
        /// aware write path has quietly changed behaviour on the databases that
        /// already worked.
        [Fact]
        public void TheOnlyDifferenceBetween23And28IsTargetPriority() {
            using (var tmp = new TempDir()) {
                var at23 = Push(23, tmp.File("a23.sqlite"));
                var at28 = Push(28, tmp.File("a28.sqlite"));

                foreach (var table in Tables) {
                    var rows23 = Normalise(table, (JArray)at23.Tables[table], at23.IdToGuid);
                    var rows28 = Normalise(table, (JArray)at28.Tables[table], at28.IdToGuid);
                    Assert.Equal(rows23.Count, rows28.Count);

                    foreach (var key in rows23.Keys) {
                        var a = rows23[key];
                        var b = rows28[key];
                        var allowed = table == "target"
                            ? new HashSet<string>(new[] { "priority" }, StringComparer.OrdinalIgnoreCase)
                            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        Assert.All(a.Keys, k => Assert.True(
                            b.ContainsKey(k), $"{table}.{k} is present at 23 and missing at 28"));
                        Assert.Equal(allowed, new HashSet<string>(
                            b.Keys.Where(k => !a.ContainsKey(k)), StringComparer.OrdinalIgnoreCase));

                        foreach (var column in a.Keys.Where(k => !allowed.Contains(k))) {
                            AssertValuesMatch($"{table}.{column} for {key}", a[column], b[column]);
                        }
                    }
                }
            }
        }

        [Fact]
        public void TargetPriorityIsWrittenAtTheTargetSchedulerDefaultFrom24() {
            using (var tmp = new TempDir()) {
                var pushed = Push(28, tmp.File("v28.sqlite"));
                var targets = (JArray)pushed.Tables["target"];
                Assert.NotEmpty(targets);
                // Migrate/24.sql declares priority INTEGER DEFAULT -1. ACP has
                // no target level priority to map, so the default is written
                // explicitly rather than left to the schema.
                Assert.All(targets, t => Assert.Equal(-1, (int)t["priority"]));
            }
        }

        [Fact]
        public void TargetPriorityIsNotWrittenAt23() {
            using (var tmp = new TempDir()) {
                var path = tmp.File("v23.sqlite");
                Push(23, path);
                Assert.DoesNotContain("priority", TsFixtures.TableColumns(path, "target"));
            }
        }

        // -- Shape -----------------------------------------------------------

        [Fact]
        public void ThreePlansBecomeThreeProjectsSixTargetsTwoTemplatesSevenPlans() {
            using (var tmp = new TempDir()) {
                var pushed = Push(28, tmp.File("shape.sqlite"));

                Assert.Equal(
                    new[] { "Mosaic", "Single Target", "Two Filters" },
                    ((JArray)pushed.Tables["project"]).Select(p => (string)p["name"]).OrderBy(n => n).ToArray());

                var targets = (JArray)pushed.Tables["target"];
                Assert.Equal(6, targets.Count);
                Assert.Equal(4, targets.Count(t => ((string)t["name"]).StartsWith("M31 Panel ")));

                // One template per filter and camera, which is the dedup Target
                // Scheduler's own Import Profile does not do: OIII appears on
                // two plans and still produces one template.
                var templates = (JArray)pushed.Tables["exposuretemplate"];
                Assert.Equal(2, templates.Count);
                Assert.Equal(
                    new[] { "Ha", "OIII" },
                    templates.Select(t => (string)t["filtername"]).OrderBy(n => n).ToArray());

                // One exposure plan per target and filter: 1 + 4 + 2.
                Assert.Equal(7, ((JArray)pushed.Tables["exposureplan"]).Count);
                Assert.Equal(7, pushed.Outcome.ExposurePlan.Inserted);
            }
        }

        [Theory]
        [InlineData(23)]
        [InlineData(24)]
        [InlineData(25)]
        [InlineData(26)]
        [InlineData(27)]
        [InlineData(28)]
        public void PushingTwiceUpdatesInPlaceRatherThanDuplicating(int version) {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(version, tmp.File($"idem{version}.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var first = TsUpsert.Apply(db, BuildPayload());
                    var second = TsUpsert.Apply(db, BuildPayload());

                    Assert.Equal(6, first.Target.Inserted);
                    Assert.Equal(7, first.ExposurePlan.Inserted);
                    Assert.Equal(0, second.Target.Inserted);
                    Assert.Equal(0, second.ExposurePlan.Inserted);
                    Assert.Equal(6, second.Target.Updated);
                    Assert.Equal(7, second.ExposurePlan.Updated);

                    Assert.Equal(6, CountRows(db.Connection, "target"));
                    Assert.Equal(7, CountRows(db.Connection, "exposureplan"));
                    Assert.Equal(3, CountRows(db.Connection, "project"));
                    // Rule weights are seeded once, on the insert, and never
                    // touched again, because they are the user's to tune.
                    Assert.Equal(15, CountRows(db.Connection, "ruleweight"));
                }
            }
        }

        // -- Helpers ---------------------------------------------------------

        private static TsSyncPayload BuildPayload() {
            return TsConvert.BuildPayload(
                TsTestPlans.ThreePlans(), TsTestPlans.Gear(),
                TsTestPlans.ProfileId, TsTestPlans.FrozenNow);
        }

        private class PushResult {
            public JObject Tables { get; set; }
            public Dictionary<string, Dictionary<long, string>> IdToGuid { get; set; }
            public TsSyncOutcome Outcome { get; set; }
        }

        private static PushResult Push(int version, string path) {
            TsFixtures.MakeDb(version, path);
            using (var db = TargetSchedulerDb.Open(path)) {
                var outcome = TsUpsert.Apply(db, BuildPayload());
                var tables = Dump(db.Connection);
                return new PushResult {
                    Tables = tables,
                    IdToGuid = IdToGuidMaps(tables),
                    Outcome = outcome,
                };
            }
        }

        private class GoldenResult {
            public JObject Tables { get; set; }
            public Dictionary<string, Dictionary<long, string>> IdToGuid { get; set; }
            public JObject Report { get; set; }
        }

        private static GoldenResult Golden(int version) {
            var root = JObject.Parse(TsFixtures.ReadFixture("golden-rows.json"));
            Assert.Equal(TsTestPlans.ProfileId, (string)root["profile_id"]);
            Assert.Equal(TsTestPlans.FrozenNow, (long)root["frozen_now"]);

            var slice = (JObject)root["versions"][version.ToString(CultureInfo.InvariantCulture)];
            var tables = (JObject)slice["tables"];
            return new GoldenResult {
                Tables = tables,
                IdToGuid = IdToGuidMaps(tables),
                Report = (JObject)slice["report"],
            };
        }

        /// Every row of every table the push writes, ordered by Id, as JSON so
        /// the two sides have the same shape to compare.
        private static JObject Dump(SqliteConnection conn) {
            var result = new JObject();
            foreach (var table in Tables) {
                var rows = new JArray();
                using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText = $"SELECT * FROM {table} ORDER BY Id";
                    using (var reader = cmd.ExecuteReader()) {
                        while (reader.Read()) {
                            var row = new JObject();
                            for (var i = 0; i < reader.FieldCount; i++) {
                                row[reader.GetName(i)] = reader.IsDBNull(i)
                                    ? JValue.CreateNull()
                                    : JToken.FromObject(reader.GetValue(i));
                            }
                            rows.Add(row);
                        }
                    }
                }
                result[table] = rows;
            }
            return result;
        }

        private static Dictionary<string, Dictionary<long, string>> IdToGuidMaps(JObject tables) {
            var maps = new Dictionary<string, Dictionary<long, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in new[] { "exposuretemplate", "project", "target", "exposureplan" }) {
                var map = new Dictionary<long, string>();
                foreach (var row in (JArray)tables[table]) {
                    map[(long)row["Id"]] = (string)row["guid"];
                }
                maps[table] = map;
            }
            return maps;
        }

        /// Rows keyed by the identity that matters, with Id dropped and every
        /// foreign key swapped for the guid of the row it points at.
        ///
        /// ruleweight has no guid of its own, so it is keyed by its project's
        /// guid and its rule name, which is what makes a row unique there.
        private static Dictionary<string, Dictionary<string, JToken>> Normalise(
            string table, JArray rows, Dictionary<string, Dictionary<long, string>> idToGuid
        ) {
            var result = new Dictionary<string, Dictionary<string, JToken>>(StringComparer.Ordinal);
            foreach (var row in rows.Cast<JObject>()) {
                var normalised = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in row.Properties()) {
                    if (string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase)) continue;

                    string referencedTable;
                    if (ForeignKeys.TryGetValue($"{table}.{property.Name}", out referencedTable)) {
                        var value = property.Value;
                        normalised[property.Name] = value.Type == JTokenType.Null
                            ? JValue.CreateNull()
                            : JToken.FromObject(Referenced(idToGuid, referencedTable, (long)value));
                        continue;
                    }
                    normalised[property.Name] = property.Value;
                }

                var key = table == "ruleweight"
                    ? $"{normalised["projectid"]}/{normalised["name"]}"
                    : (string)row["guid"];
                Assert.False(
                    result.ContainsKey(key),
                    $"{table}: two rows share the key {key}, so the guid is not unique");
                result[key] = normalised;
            }
            return result;
        }

        private static string Referenced(
            Dictionary<string, Dictionary<long, string>> idToGuid, string table, long id
        ) {
            string guid;
            return idToGuid[table].TryGetValue(id, out guid) && guid != null
                ? guid
                : $"<dangling {table} id {id}>";
        }

        private static void AssertRowsMatch(
            string table, string key,
            Dictionary<string, JToken> expected, Dictionary<string, JToken> actual
        ) {
            Assert.Equal(
                expected.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray(),
                actual.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray());

            foreach (var column in expected.Keys) {
                AssertValuesMatch($"{table}.{column} for {key}", expected[column], actual[column]);
            }
        }

        /// Numbers are compared with a tolerance because the panel centres go
        /// through trigonometry on both sides. Everything else is exact.
        private static void AssertValuesMatch(string what, JToken expected, JToken actual) {
            if (expected.Type == JTokenType.Null || actual.Type == JTokenType.Null) {
                Assert.True(
                    expected.Type == JTokenType.Null && actual.Type == JTokenType.Null,
                    $"{what}: expected {expected}, got {actual}");
                return;
            }

            if (IsNumeric(expected) && IsNumeric(actual)) {
                var a = expected.Value<double>();
                var b = actual.Value<double>();
                var tolerance = Math.Max(1e-9, Math.Abs(a) * 1e-12);
                Assert.True(Math.Abs(a - b) <= tolerance, $"{what}: expected {a}, got {b}");
                return;
            }

            Assert.True(
                string.Equals(expected.ToString(), actual.ToString(), StringComparison.Ordinal),
                $"{what}: expected {expected}, got {actual}");
        }

        private static bool IsNumeric(JToken token) {
            return token.Type == JTokenType.Integer || token.Type == JTokenType.Float;
        }

        private static TsTableCounts CountsFor(TsSyncOutcome outcome, string table) {
            switch (table) {
                case "exposuretemplate": return outcome.ExposureTemplate;
                case "project": return outcome.Project;
                case "target": return outcome.Target;
                case "exposureplan": return outcome.ExposurePlan;
                default: throw new ArgumentOutOfRangeException(nameof(table), table, null);
            }
        }

        private static int CountRows(SqliteConnection conn, string table) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
    }
}
