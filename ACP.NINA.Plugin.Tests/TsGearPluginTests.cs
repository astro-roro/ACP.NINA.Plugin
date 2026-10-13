using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// What the plugin sends and how it behaves against today's ACP
    /// (docs/specs/gear-templates.md section 9, L1 to L6).
    public class TsGearPluginTests {

        private const string Base = "http://acp.test";
        private const string GearProfile = TsRowScenarios.GearProfile;

        private static TsPushService ServiceFor(string dbPath, long? now = null) {
            return new TsPushService(
                new FakeContainerWatch(),
                path => TargetSchedulerDb.Open(dbPath),
                () => now ?? TsTestPlans.FrozenNow
            ) { DbPathOverride = dbPath, MakeBackup = false };
        }

        private static ScriptedAcp OkAcp() {
            return new ScriptedAcp(_ => ScriptedAcp.Json(HttpStatusCode.OK, "{}"));
        }

        private static string Dump(string dbPath) {
            using (var db = TargetSchedulerDb.Open(dbPath)) {
                var all = new Dictionary<string, object>();
                foreach (var table in new[] { "exposuretemplate", "project", "target", "exposureplan", "ruleweight" }) {
                    var rows = new List<Dictionary<string, object>>();
                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText = $"SELECT * FROM {table} ORDER BY Id";
                        using (var r = cmd.ExecuteReader()) {
                            while (r.Read()) {
                                var row = new Dictionary<string, object>();
                                for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                                rows.Add(row);
                            }
                        }
                    }
                    all[table] = rows;
                }
                return JsonConvert.SerializeObject(all);
            }
        }

        private static long Count(string dbPath, string table) {
            using (var db = TargetSchedulerDb.Open(dbPath)) {
                return Convert.ToInt64(TsRowScenarios.Cell(db.Connection, $"SELECT COUNT(*) FROM {table}"));
            }
        }

        // -- L1 -------------------------------------------------------------

        [Fact]
        public void L1_TheFingerprintCarriesTheProfileIdAndTheSlotsInSlotOrderWithTheWheelDisconnected() {
            // The wheel is disconnected, so Filters is empty. The slots come
            // from the profile, positions 0 to 6, handed over out of order.
            var fingerprint = new Fingerprint { Filters = new List<string>() };
            FingerprintMath.ApplyProfileWheel(fingerprint, GearProfile, new[] {
                Tuple.Create("H", 4), Tuple.Create("O", 5), Tuple.Create("S", 6), Tuple.Create("L", 0),
                Tuple.Create("R", 1), Tuple.Create("G", 2), Tuple.Create("B", 3),
            });

            Assert.Equal(GearProfile, fingerprint.ProfileId);
            Assert.Equal(new[] { "L", "R", "G", "B", "H", "O", "S" }, fingerprint.WheelSlots);
            Assert.Empty(fingerprint.Filters);

            var wire = JObject.Parse(JsonConvert.SerializeObject(fingerprint));
            Assert.Equal(GearProfile, (string)wire["profile_id"]);
            Assert.Equal(new[] { "L", "R", "G", "B", "H", "O", "S" }, wire["wheel_slots"].Select(t => (string)t).ToArray());
            Assert.Empty((JArray)wire["filters"]);
        }

        [Fact]
        public void L1_AProfileWithNoSlotsSendsAnEmptyListAndNoIdWhenThereIsNone() {
            var fingerprint = new Fingerprint();
            FingerprintMath.ApplyProfileWheel(fingerprint, null, null);
            Assert.Empty(fingerprint.WheelSlots);
            var wire = JObject.Parse(JsonConvert.SerializeObject(fingerprint));
            Assert.Null(wire["profile_id"]);
            Assert.Empty((JArray)wire["wheel_slots"]);
        }

        // -- L2 -------------------------------------------------------------

        [Fact]
        public async Task L2_TheTemplateListCarriesIdsGuidsTidyColumnsAndPlans() {
            using (var tmp = new TempDir()) {
                var path = TsRowScenarios.MakeTemplateDb(tmp.File("schedulerdb.sqlite"));
                var templates = await ServiceFor(path).ReadTemplatesAsync(GearProfile);
                Assert.NotNull(templates);

                var acp = OkAcp();
                var sent = await TsTemplatesReporter.PostAsync(new AcpApiClient(Base, () => "tok", acp), templates, GearProfile);
                Assert.True(sent);

                var post = Assert.Single(acp.Requests);
                Assert.Equal("/api/ts-templates", post.Path);
                var body = JObject.Parse(post.Body);
                var list = (JArray)body["templates"];
                Assert.Equal(20, list.Count);
                Assert.All(list, t => {
                    Assert.True((int)t["id"] > 0);
                    Assert.False(string.IsNullOrEmpty((string)t["guid"]));
                    foreach (var key in new[] {
                        "moonavoidanceenabled", "moonavoidanceseparation", "moonavoidancewidth",
                        "twilightlevel", "maximumhumidity", "ditherevery", "plans" }) {
                        Assert.NotNull(t[key]);
                    }
                });

                JToken ById(int id) { return list.Single(t => (int)t["id"] == id); }
                Assert.Equal(55, ((JArray)ById(11)["plans"]).Count);
                var plans14 = (JArray)ById(14)["plans"];
                Assert.Equal(26, plans14.Count);
                Assert.Equal(3, plans14.Count(p => (double)p["exposure"] == 15.0));
                Assert.Equal(244, list.Sum(t => ((JArray)t["plans"]).Count));

                // Rohan's original and ACP's copy differ in the columns Tidy shows.
                var one = ById(1);
                Assert.Equal(1, (int)one["moonavoidanceenabled"]);
                Assert.Equal(120.0, (double)one["moonavoidanceseparation"]);
                Assert.Equal(14, (int)one["moonavoidancewidth"]);
                Assert.Equal(0, (int)one["twilightlevel"]);
                Assert.Equal(2, (int)one["ditherevery"]);
                var fourteen = ById(14);
                Assert.Equal(0, (int)fourteen["moonavoidanceenabled"]);
                Assert.Equal(1, (int)fourteen["twilightlevel"]);
                Assert.Equal(100.0, (double)fourteen["maximumhumidity"]);
                Assert.Equal(-1, (int)fourteen["ditherevery"]);
                // The fields today's ACP reads are still there.
                Assert.Equal("L", (string)one["name"]);
                Assert.Equal(180.0, (double)one["default_exposure_s"]);
            }
        }

        // -- L3 -------------------------------------------------------------

        private class FakeFingerprints : IGearFingerprintService {
            public Fingerprint Build(SolveSnapshot solve) {
                var fp = new Fingerprint {
                    Camera = new FingerprintCamera { Name = "ZWO ASI6200MM Pro", SensorPx = new[] { 9576, 6388 }, PixelSizeUm = 3.76 },
                    ProfileName = "Foo",
                };
                FingerprintMath.ApplyProfileWheel(fp, GearProfile, new[] { Tuple.Create("L", 0), Tuple.Create("R", 1) });
                return fp;
            }
        }

        private class FakeTsPush : ITsPushService {
            private readonly List<string> events;
            public FakeTsPush(List<string> events) { this.events = events; }

            public Task<TsPushResult> PushAsync(
                IReadOnlyList<Plan> plans, GearResponse gear, string profileId, CancellationToken token = default,
                IReadOnlyList<string> wheelFilters = null, string profileName = null
            ) {
                events.Add("push");
                var result = new TsPushResult { Success = true, Outcome = new TsSyncOutcome() };
                result.Templates.Add(new TsExposureTemplate { Id = 1, Name = "L", FilterName = "L", Guid = "g" });
                return Task.FromResult(result);
            }

            public Task<IReadOnlyList<TsExposureTemplate>> ReadTemplatesAsync(string profileId, CancellationToken token = default) {
                events.Add("read-templates");
                IReadOnlyList<TsExposureTemplate> list = new List<TsExposureTemplate> {
                    new TsExposureTemplate { Id = 1, Name = "L", FilterName = "L", Guid = "g" },
                };
                return Task.FromResult(list);
            }

            public Task<TsHeldStates.Outcome> WriteHeldStatesAsync(
                IReadOnlyList<Plan> heldPlans, string profileId, CancellationToken token = default
            ) {
                return Task.FromResult<TsHeldStates.Outcome>(null);
            }
        }

        [Fact]
        public async Task L3_SyncAllPostsTheFingerprintThenTheTemplatesThenPushes() {
            var events = new List<string>();
            var bodies = new Dictionary<string, string>();
            var acp = new ScriptedAcp(seen => {
                events.Add(seen.Method.Method + " " + seen.Path);
                bodies[seen.Method.Method + " " + seen.Path] = seen.Body;
                switch (seen.Path) {
                    case "/api/plans/match": return ScriptedAcp.Json(HttpStatusCode.OK, "{\"plans\": []}");
                    case "/api/plans": return ScriptedAcp.Json(HttpStatusCode.OK, "{\"version\": 2, \"plans\": []}");
                    case "/api/gear": return ScriptedAcp.Json(HttpStatusCode.OK, "{\"version\": 3, \"telescopes\": [], \"cameras\": []}");
                    default: return ScriptedAcp.Json(HttpStatusCode.OK, "{}");
                }
            });
            var client = new AcpApiClient(Base, () => "tok", acp);

            var run = await TsSyncAllRunner.RunAsync(
                client, new FakeFingerprints(), new FakeTsPush(events), GearProfile, "Foo",
                TsRowScenarios.WheelSlots, SyncMode.Everything);

            Assert.True(run.Push.Success);
            var fingerprintAt = events.IndexOf("POST /api/plans/match");
            var templatesAt = events.IndexOf("POST /api/ts-templates");
            var pushAt = events.IndexOf("push");
            Assert.True(fingerprintAt >= 0, string.Join(" | ", events));
            Assert.True(fingerprintAt < templatesAt, string.Join(" | ", events));
            Assert.True(templatesAt < pushAt, string.Join(" | ", events));
            // Gear is read after the template post, so the pull has run.
            Assert.True(templatesAt < events.IndexOf("GET /api/gear"), string.Join(" | ", events));

            var fingerprint = JObject.Parse(bodies["POST /api/plans/match"]);
            Assert.Equal(GearProfile, (string)fingerprint["profile_id"]);
            Assert.Equal(new[] { "L", "R" }, fingerprint["wheel_slots"].Select(t => (string)t).ToArray());
        }

        [Fact]
        public async Task L3_AFingerprintThatCannotBeSentDoesNotStopTheSync() {
            var events = new List<string>();
            var acp = new ScriptedAcp(seen => {
                events.Add(seen.Method.Method + " " + seen.Path);
                if (seen.Path == "/api/plans/match") return ScriptedAcp.Json(HttpStatusCode.InternalServerError, "{\"error\": \"boom\"}");
                if (seen.Path == "/api/plans") return ScriptedAcp.Json(HttpStatusCode.OK, "{\"version\": 2, \"plans\": []}");
                return ScriptedAcp.Json(HttpStatusCode.OK, "{}");
            });
            var run = await TsSyncAllRunner.RunAsync(
                new AcpApiClient(Base, () => "tok", acp), new FakeFingerprints(), new FakeTsPush(events),
                GearProfile, "Foo", TsRowScenarios.WheelSlots, SyncMode.Everything);
            Assert.True(run.Push.Success);
            Assert.Contains("push", events);
        }

        // -- L4 -------------------------------------------------------------

        [Fact]
        public async Task L4_ARenameGoesToTheLinksPostWithItsTemplateResultAndTheCapabilities() {
            using (var tmp = new TempDir()) {
                var path = TsRowScenarios.MakeTemplateDb(tmp.File("schedulerdb.sqlite"));
                var gear = TsRowScenarios.GearV3();
                var ha = TsRowScenarios.GearTemplateNamed(gear, "Ha");
                ha.Name = "Ha 300";
                ha.Aliases.Insert(0, "Ha");
                var plan = TsRowScenarios.GearPlan(
                    "gear-ha", "M42", TsRowScenarios.Camera6200, TsRowScenarios.GearRowFor(gear, "row-ha", ha));

                var push = await ServiceFor(path).PushAsync(
                    new List<Plan> { plan }, gear, GearProfile, default, TsRowScenarios.WheelSlots, "Foo");
                Assert.True(push.Success, push.Failure);

                var acp = OkAcp();
                var warning = await TsLinksReporter.PostAsync(new AcpApiClient(Base, () => "tok", acp), push, GearProfile);
                Assert.Null(warning);

                var post = Assert.Single(acp.Requests);
                Assert.Equal("/api/ext/nina-ts-sync/links", post.Path);
                var body = JObject.Parse(post.Body);
                Assert.Equal(new[] { "templates", "repoint" }, body["capabilities"].Select(t => (string)t).ToArray());
                var entry = ((JArray)body["templates"]).Single(t => (string)t["template_id"] == ha.Id);
                Assert.Equal(12, (int)entry["ts_id"]);
                Assert.Equal("updated", (string)entry["action"]);
                Assert.Equal(new[] { "name" }, entry["fields_written"].Select(t => (string)t).ToArray());
                Assert.Equal("Ha 300", (string)entry["base"]["name"]);
                Assert.Equal(10, ((JArray)body["templates"]).Count);
                Assert.Equal(1, ((JArray)body["states"]).Count);
                Assert.Equal(20, Count(path, "exposuretemplate"));
            }
        }

        // -- L5 and L6: today's ACP ------------------------------------------

        private static List<Plan> LegacyPlans() {
            return TsTestPlans.ThreePlans();
        }

        [Fact]
        public async Task L5_AGearWithNoTemplatesPushesExactlyTodaysRowsAndLinksNoTemplates() {
            string withNone, withEmptyList;
            TsPushResult first;
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("a.sqlite"));
                first = await ServiceFor(path).PushAsync(
                    LegacyPlans(), TsTestPlans.Gear(), TsTestPlans.ProfileId, default, TsRowScenarios.WheelSlots, "Foo");
                Assert.True(first.Success, first.Failure);
                withNone = Dump(path);

                // Every filter is on a known slot, so the row names follow the wheel as before.
                using (var db = TargetSchedulerDb.Open(path)) {
                    Assert.Equal("O", TsRowScenarios.Cell(db.Connection, "SELECT filtername FROM exposuretemplate WHERE name = 'OIII 3nm'"));
                    Assert.Equal("H", TsRowScenarios.Cell(db.Connection, "SELECT filtername FROM exposuretemplate WHERE name = 'Ha 3nm'"));
                }
            }
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("b.sqlite"));
                var gear = TsTestPlans.Gear();
                foreach (var filter in gear.Cameras[0].Filters.Values) filter.Templates = new List<GearTemplate>();
                var second = await ServiceFor(path).PushAsync(
                    LegacyPlans(), gear, TsTestPlans.ProfileId, default, TsRowScenarios.WheelSlots, "Foo");
                Assert.True(second.Success, second.Failure);
                withEmptyList = Dump(path);
            }
            Assert.Equal(withNone, withEmptyList);

            Assert.Empty(first.GearResults);
            var acp = OkAcp();
            var client = new AcpApiClient(Base, () => "tok", acp);
            await TsLinksReporter.PostAsync(client, first, TsTestPlans.ProfileId);
            await TsTemplatesReporter.PostAsync(client, first, TsTestPlans.ProfileId);

            var links = JObject.Parse(acp.Requests.Single(r => r.Path == "/api/ext/nina-ts-sync/links").Body);
            Assert.Null(links["templates"]);
            Assert.Equal(new[] { "templates", "repoint" }, links["capabilities"].Select(t => (string)t).ToArray());
            Assert.NotEmpty((JArray)links["states"]);

            // The template post carries the new fields, which today's ACP ignores.
            var list = (JArray)JObject.Parse(acp.Requests.Single(r => r.Path == "/api/ts-templates").Body)["templates"];
            Assert.All(list, t => {
                Assert.NotNull(t["id"]);
                Assert.NotNull(t["guid"]);
                Assert.NotNull(t["plans"]);
            });
        }

        private static GearResponse LegacyGearWithSii() {
            var gear = TsTestPlans.Gear();
            gear.Cameras[0].Filters["SII"] = new CameraFilter {
                TsTemplateName = "SII 3nm", DefaultSubS = 300, Gain = 100, Offset = 50, Bin = 1,
            };
            return gear;
        }

        [Fact]
        public async Task L6_AFilterOffTheWheelIsSkippedAndSaidSo() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var plan = TsTestPlans.Plan("p", projectName: "NGC 7000", targetName: "NGC 7000", filterRows: new List<FilterRow> {
                    TsTestPlans.Row("row-ha", "Ha", "Ha 3nm", 600, hours: 2.0),
                    TsTestPlans.Row("row-sii", "SII", "SII 3nm", 300, hours: 2.0),
                });

                var push = await ServiceFor(path).PushAsync(
                    new List<Plan> { plan }, LegacyGearWithSii(), TsTestPlans.ProfileId, default,
                    new[] { "L", "R", "G", "B", "H", "O" }, "Foo");

                Assert.True(push.Success, push.Failure);
                // Only Ha was written. Nothing at all for SII.
                Assert.Equal(1, Count(path, "exposuretemplate"));
                Assert.Equal(1, Count(path, "exposureplan"));
                Assert.Contains(
                    "Test IMX571: SII is not on the wheel of Foo yet, so 1 row was skipped: NGC 7000 SII.",
                    push.SyncNotes);
                Assert.Contains("SII is not on the wheel of Foo yet", push.Summary());
            }
        }

        [Fact]
        public async Task L6_AnUnknownWheelSkipsTheRowsOfATemplateTsDoesNotHave() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var plan = TsTestPlans.Plan("p", projectName: "NGC 7000", targetName: "NGC 7000", filterRows: new List<FilterRow> {
                    TsTestPlans.Row("row-ha", "Ha", "Ha 3nm", 600, hours: 2.0),
                });

                var push = await ServiceFor(path).PushAsync(
                    new List<Plan> { plan }, TsTestPlans.Gear(), TsTestPlans.ProfileId, default, new string[0], "Foo");

                Assert.True(push.Success, push.Failure);
                Assert.Equal(0, Count(path, "exposuretemplate"));
                Assert.Equal(0, Count(path, "exposureplan"));
                Assert.Contains("Test IMX571: the wheel of Foo is unknown, so 1 row was skipped.", push.SyncNotes);
            }
        }

        [Fact]
        public async Task L6_AnUnknownWheelStillWritesRowsOnATemplateTsAlreadyHas() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                using (var holder = TargetSchedulerDb.Open(path)) {
                    TsRowScenarios.AddTemplate(holder.Connection, "Ha 3nm", "Ha", 600, "nina-guid-ha");
                }
                var plan = TsTestPlans.Plan("p", projectName: "NGC 7000", targetName: "NGC 7000", filterRows: new List<FilterRow> {
                    TsTestPlans.Row("row-ha", "Ha", "Ha 3nm", 600, hours: 2.0),
                });

                var push = await ServiceFor(path).PushAsync(
                    new List<Plan> { plan }, TsTestPlans.Gear(), TsTestPlans.ProfileId, default, new string[0], "Foo");

                Assert.True(push.Success, push.Failure);
                Assert.Equal(1, Count(path, "exposuretemplate"));
                Assert.Equal(1, Count(path, "exposureplan"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    Assert.Equal("Ha", TsRowScenarios.Cell(db.Connection, "SELECT filtername FROM exposuretemplate"));
                }
            }
        }
    }
}
