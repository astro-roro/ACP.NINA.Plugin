using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// A plan's ts_refs name the exact Target Scheduler rows it came from, and
    /// the push updates those rows by Id before it looks for a guid or a name.
    ///
    /// The case that needs it: projects made by hand in Target Scheduler and
    /// imported into ACP. Target Scheduler gives every row its own random guid,
    /// so the guid lookup never finds them and the name claim refuses a row
    /// that is already stamped. Without the refs, pushing an imported plan back
    /// writes a second project, target and exposure plan beside the original,
    /// starting from zero frames, which Target Scheduler then schedules as if
    /// the night's work had never happened.
    ///
    /// The fixture is three real projects cut from Voyager's database on
    /// 2026-09-24: Dolphin Nebula (71 accepted), Helix Nebula (56) and Sculptor
    /// Galaxy (54), with the plans the Python extension's import made of them.
    public class TsRefsPinTests {

        private const string ProfileId = "9d3a61fd-9f35-42ae-a911-bd6e4b0f76a7";
        private static readonly string[] Wheel = { "L", "R", "G", "B", "H", "O", "S" };
        private static readonly string[] Tables = { "project", "target", "exposuretemplate", "exposureplan" };

        // -- The round trip --------------------------------------------------

        [Fact]
        public async Task PushingImportedPlansBackAddsNoRowsAndKeepsEveryFrame() {
            using (var tmp = new TempDir()) {
                var path = SnapshotDb(tmp);
                var before = ReadAll(path);

                var result = await ServiceFor(path).PushAsync(Plans(), Gear(), ProfileId, default, Wheel);

                Assert.True(result.Success, result.Failure);
                var after = ReadAll(path);
                foreach (var table in Tables) {
                    Assert.True(
                        before[table].Keys.SequenceEqual(after[table].Keys),
                        $"{table}: rows before {string.Join(",", before[table].Keys)}, " +
                        $"after {string.Join(",", after[table].Keys)}");
                }
                foreach (var id in before["exposureplan"].Keys) {
                    Assert.Equal(before["exposureplan"][id]["acquired"], after["exposureplan"][id]["acquired"]);
                    Assert.Equal(before["exposureplan"][id]["accepted"], after["exposureplan"][id]["accepted"]);
                }
                Assert.Equal(71, AcceptedIn(path, 2));
                Assert.Equal(56, AcceptedIn(path, 10));
                Assert.Equal(54, AcceptedIn(path, 11));

                Assert.Equal(3, result.Outcome.Project.Pinned);
                Assert.Equal(3, result.Outcome.Target.Pinned);
                Assert.Equal(16, result.Outcome.ExposurePlan.Pinned);
                Assert.Equal(0, result.Outcome.ExposureTemplate.Inserted);
            }
        }

        [Fact]
        public async Task APinnedRowKeepsEverythingACPDoesNotEdit() {
            using (var tmp = new TempDir()) {
                var path = SnapshotDb(tmp);
                var before = ReadAll(path);

                await ServiceFor(path).PushAsync(Plans(), Gear(), ProfileId, default, Wheel);

                var after = ReadAll(path);
                // Sculptor is parked (state 2) with the grader on. ACP carries
                // neither, so writing its defaults would reactivate the project
                // and switch the grader off.
                foreach (var col in new[] { "state", "enablegrader", "description", "flatsHandling",
                                            "minimumtime", "smartexposureorder", "guid", "createdate" }) {
                    foreach (var id in before["project"].Keys) {
                        Assert.Equal(before["project"][id][col], after["project"][id][col]);
                    }
                }
                Assert.Equal(2L, after["project"][11]["state"]);
                foreach (var col in new[] { "epochcode", "active", "roi", "guid", "projectid" }) {
                    foreach (var id in before["target"].Keys) {
                        Assert.Equal(before["target"][id][col], after["target"][id][col]);
                    }
                }
                foreach (var col in new[] { "exposureTemplateId", "targetid", "enabled", "guid" }) {
                    foreach (var id in before["exposureplan"].Keys) {
                        Assert.Equal(before["exposureplan"][id][col], after["exposureplan"][id][col]);
                    }
                }
                // Templates are pointed at, never rewritten.
                foreach (var id in before["exposuretemplate"].Keys) {
                    Assert.Equal(
                        JsonConvert.SerializeObject(before["exposuretemplate"][id]),
                        JsonConvert.SerializeObject(after["exposuretemplate"][id]));
                }
            }
        }

        [Fact]
        public async Task ANightOfFramesSurvivesTheNextPushToo() {
            using (var tmp = new TempDir()) {
                var path = SnapshotDb(tmp);
                await ServiceFor(path).PushAsync(Plans(), Gear(), ProfileId, default, Wheel);
                Exec(path, "UPDATE exposureplan SET acquired = 40, accepted = 33 WHERE Id = 32");

                var second = await ServiceFor(path).PushAsync(Plans(), Gear(), ProfileId, default, Wheel);

                Assert.True(second.Success, second.Failure);
                var row = ReadAll(path)["exposureplan"][32];
                Assert.Equal(40L, row["acquired"]);
                Assert.Equal(33L, row["accepted"]);
                Assert.Equal(0, second.Outcome.ExposurePlan.Inserted);
            }
        }

        [Fact]
        public async Task WithoutRefsTheSamePlansComeBackAsDuplicates() {
            // The behaviour the refs fix, pinned down so the reason for them
            // stays visible: every hand-made row already has a guid, so
            // nothing is found and nothing can be claimed.
            using (var tmp = new TempDir()) {
                var path = SnapshotDb(tmp);
                var plans = Plans();
                foreach (var p in plans) p.TsRefs = null;

                var result = await ServiceFor(path).PushAsync(plans, Gear(), ProfileId, default, Wheel);

                Assert.True(result.Success, result.Failure);
                Assert.Equal(3, result.Outcome.Project.Inserted);
                Assert.Equal(0, result.Outcome.Project.Claimed);
                Assert.Equal(16, result.Outcome.ExposurePlan.Inserted);
            }
        }

        // -- When a pin does not hold ----------------------------------------

        [Fact]
        public void APinToARowThatIsGoneFallsBackToTheGuidPath() {
            using (var tmp = new TempDir()) {
                var path = SnapshotDb(tmp);
                Exec(path, "DELETE FROM exposureplan WHERE Id = 32");

                var outcome = Apply(path, Plans());

                Assert.Equal(15, outcome.ExposurePlan.Pinned);
                Assert.Equal(1, outcome.ExposurePlan.Inserted);
                Assert.Contains(outcome.Notes, n => n.Contains("exposure plan 32"));
                // The new row goes on the pinned target, under the template the
                // refs give for the filter, so no template is added for it.
                var added = ReadAll(path)["exposureplan"].Values.Single(r => (long)r["Id"] > 127);
                Assert.Equal(5L, added["targetid"]);
                Assert.Equal(0, outcome.ExposureTemplate.Inserted);
            }
        }

        [Fact]
        public void RefsTakenUnderAnotherProfileAreIgnored() {
            using (var tmp = new TempDir()) {
                var path = SnapshotDb(tmp);
                var plans = Plans();
                foreach (var p in plans) p.TsRefs["profile_id"] = "some-other-profile";

                var outcome = Apply(path, plans);

                Assert.Equal(0, outcome.Project.Pinned + outcome.Target.Pinned + outcome.ExposurePlan.Pinned);
                Assert.Equal(3, outcome.Project.Inserted);
            }
        }

        [Fact]
        public void TwoPlansPinnedToOneTargetDoNotCollapseIntoIt() {
            // A plan copied in ACP carries its original's refs. Only the first
            // gets the row; the copy is written as a row of its own.
            using (var tmp = new TempDir()) {
                var path = SnapshotDb(tmp);
                var plans = Plans();
                var copy = JsonConvert.DeserializeObject<Plan>(JsonConvert.SerializeObject(plans[0]));
                copy.Id = "copy-of-dolphin";
                copy.Target.Name = "Dolphin Nebula copy";
                plans.Add(copy);

                var outcome = Apply(path, plans);

                Assert.Equal(3, outcome.Target.Pinned);
                Assert.Equal(1, outcome.Target.Inserted);
                Assert.Contains(outcome.Notes, n => n.Contains("Dolphin Nebula copy"));
                var target5 = ReadAll(path)["target"][5];
                Assert.Equal("Dolphin Nebula", target5["name"]);
            }
        }

        [Fact]
        public void ATargetPinnedUnderAnotherProjectIsNotMovedIntoThisOne() {
            using (var tmp = new TempDir()) {
                var path = SnapshotDb(tmp);
                var plans = Plans();
                // Point Dolphin's panel at Helix's target.
                plans[0].TsRefs["target_ids_by_panel"]["1,1"] = 20;

                var outcome = Apply(path, plans);

                var target20 = ReadAll(path)["target"][20];
                Assert.Equal(10L, target20["projectid"]);
                Assert.Equal("Helix Nebula", target20["name"]);
                Assert.Equal(1, outcome.Target.Inserted);
            }
        }

        [Fact]
        public void ARefsBlockInAnOddShapeCostsThePinsNotThePlan() {
            var json = "{\"plans\":[{\"id\":\"p\",\"target\":{\"name\":\"X\"},\"ts_refs\":[1,2,3]}]}";
            var plans = JsonConvert.DeserializeObject<PlansResponse>(json).Plans;

            Assert.Single(plans);
            Assert.Null(TsConvert.RefsFor(plans[0], ProfileId));
        }

        // -- Fixture plumbing ------------------------------------------------

        private static JObject Fixture() {
            return JObject.Parse(TsFixtures.ReadFixture("ts-snapshot-frames-plans.json"));
        }

        private static List<Plan> Plans() {
            return Fixture()["plans"].ToObject<List<Plan>>();
        }

        private static GearResponse Gear() {
            return Fixture()["gear"].ToObject<GearResponse>();
        }

        private static string SnapshotDb(TempDir tmp) {
            var path = TsFixtures.MakeDb(23, tmp.File("schedulerdb.sqlite"));
            Exec(path, TsFixtures.ReadFixture("ts-snapshot-frames.sql"));
            return path;
        }

        private static TsPushService ServiceFor(string path) {
            return new TsPushService(
                new FakeContainerWatch(),
                p => TargetSchedulerDb.Open(path),
                () => TsTestPlans.FrozenNow
            ) {
                DbPathOverride = path,
                MakeBackup = false,
            };
        }

        private static TsSyncOutcome Apply(string path, List<Plan> plans) {
            var payload = TsConvert.BuildPayload(plans, Gear(), ProfileId, TsTestPlans.FrozenNow, Wheel);
            using (var db = TargetSchedulerDb.Open(path)) {
                return TsUpsert.Apply(db, payload);
            }
        }

        private static long AcceptedIn(string path, int projectId) {
            using (var conn = Open(path)) {
                using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText =
                        "SELECT COALESCE(SUM(e.accepted), 0) FROM exposureplan e " +
                        "JOIN target t ON t.Id = e.targetid WHERE t.projectid = $p";
                    cmd.Parameters.AddWithValue("$p", projectId);
                    return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
            }
        }

        private static Dictionary<string, SortedDictionary<long, Dictionary<string, object>>> ReadAll(string path) {
            var all = new Dictionary<string, SortedDictionary<long, Dictionary<string, object>>>();
            using (var conn = Open(path)) {
                foreach (var table in Tables) {
                    var rows = new SortedDictionary<long, Dictionary<string, object>>();
                    using (var cmd = conn.CreateCommand()) {
                        cmd.CommandText = $"SELECT * FROM {table}";
                        using (var reader = cmd.ExecuteReader()) {
                            while (reader.Read()) {
                                var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                                for (var i = 0; i < reader.FieldCount; i++) {
                                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                                }
                                rows[Convert.ToInt64(row["Id"], CultureInfo.InvariantCulture)] = row;
                            }
                        }
                    }
                    all[table] = rows;
                }
            }
            return all;
        }

        private static void Exec(string path, string sql) {
            using (var conn = Open(path)) {
                using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static SqliteConnection Open(string path) {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = path,
                Pooling = false,
            }.ToString());
            conn.Open();
            return conn;
        }
    }
}
