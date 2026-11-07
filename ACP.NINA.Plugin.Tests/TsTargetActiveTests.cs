using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// A plan's own scheduling switch (target_active) and its per-panel
    /// switches (panel_active) reach Target Scheduler as each target's
    /// `active` column, as in ACP's nina_ts_sync
    /// (tests/ts_sync/test_target_active.py). Both mean on when absent.
    public class TsTargetActiveTests {

        private static Plan Single(string id, string project, string target, double dec = -30.0, bool? targetActive = null) {
            var p = TsTestPlans.Plan(id, projectName: project, targetName: target, dec: dec);
            p.TargetActive = targetActive;
            return p;
        }

        private static Plan Mosaic(string target, int rows, int cols, bool? targetActive = null,
                                   Dictionary<string, bool> panelActive = null, string telescopeId = "tel-1") {
            var p = TsTestPlans.Plan("m", targetName: target, rows: rows, cols: cols, telescopeId: telescopeId);
            p.TargetActive = targetActive;
            p.PanelActive = panelActive;
            return p;
        }

        private static void Push(TargetSchedulerDb db, params Plan[] plans) {
            TsUpsert.Apply(db, TsConvert.BuildPayload(
                plans.ToList(), TsTestPlans.Gear(), TsTestPlans.ProfileId, TsTestPlans.FrozenNow));
        }

        private static Dictionary<string, long> Active(TargetSchedulerDb db) {
            var all = new Dictionary<string, long>();
            using (var cmd = db.Connection.CreateCommand()) {
                cmd.CommandText = "SELECT name, active FROM target";
                using (var r = cmd.ExecuteReader()) {
                    while (r.Read()) all[r.GetString(0)] = r.GetInt64(1);
                }
            }
            return all;
        }

        private static Plan[] Halves(bool? southActive = null) {
            return new[] {
                Single("north", "Split Nebula", "Split Nebula North"),
                Single("south", "Split Nebula", "Split Nebula South", dec: -32.0, targetActive: southActive),
            };
        }

        [Fact]
        public void PlanOffWritesTheTargetInactiveAndLeavesTheProjectActive() {
            using (var tmp = new TempDir())
            using (var db = TargetSchedulerDb.Open(TsFixtures.MakeDb(28, tmp.File("s.sqlite")))) {
                Push(db, Halves(southActive: false));
                var active = Active(db);
                Assert.Equal(1, active["Split Nebula North"]);
                Assert.Equal(0, active["Split Nebula South"]);
                using (var cmd = db.Connection.CreateCommand()) {
                    cmd.CommandText = "SELECT state FROM project WHERE name = 'Split Nebula'";
                    Assert.Equal(1L, (long)cmd.ExecuteScalar());
                }
            }
        }

        [Fact]
        public void MosaicWithOnePanelOff() {
            using (var tmp = new TempDir())
            using (var db = TargetSchedulerDb.Open(TsFixtures.MakeDb(28, tmp.File("s.sqlite")))) {
                Push(db, Mosaic("Wide Field", 2, 1, panelActive: new Dictionary<string, bool> { { "2,1", false } }));
                var active = Active(db);
                Assert.Equal(1, active["Wide Field Panel 1 (R1C1)"]);
                Assert.Equal(0, active["Wide Field Panel 2 (R2C1)"]);
            }
        }

        [Fact]
        public void PlanOffTurnsEveryPanelOff() {
            using (var tmp = new TempDir())
            using (var db = TargetSchedulerDb.Open(TsFixtures.MakeDb(28, tmp.File("s.sqlite")))) {
                Push(db, Mosaic("Wide Field", 1, 2, targetActive: false));
                var active = Active(db);
                Assert.Equal(2, active.Count);
                Assert.All(active.Values, v => Assert.Equal(0, v));
            }
        }

        [Fact]
        public void SwitchesAreWrittenOnUpdateBothWays() {
            using (var tmp = new TempDir())
            using (var db = TargetSchedulerDb.Open(TsFixtures.MakeDb(28, tmp.File("s.sqlite")))) {
                Push(db, Halves());
                Push(db, Halves(southActive: false));
                Assert.Equal(0, Active(db)["Split Nebula South"]);
                Push(db, Halves());
                Assert.Equal(1, Active(db)["Split Nebula South"]);

                var m = Mosaic("Wide Field", 2, 1);
                Push(db, m);
                m.PanelActive = new Dictionary<string, bool> { { "1,1", false } };
                Push(db, m);
                var active = Active(db);
                Assert.Equal(0, active["Wide Field Panel 1 (R1C1)"]);
                Assert.Equal(1, active["Wide Field Panel 2 (R2C1)"]);
            }
        }

        [Fact]
        public void APlanWithoutTheFieldsPushesAsBefore() {
            using (var tmp = new TempDir())
            using (var db = TargetSchedulerDb.Open(TsFixtures.MakeDb(28, tmp.File("s.sqlite")))) {
                Push(db, Single("a", "Project a", "Ta"), Mosaic("Wide Field", 2, 2));
                var active = Active(db);
                Assert.Equal(5, active.Count);
                Assert.All(active.Values, v => Assert.Equal(1, v));
            }
        }

        /// With no telescope the mosaic is one target, so a panel switch has
        /// nothing to apply to.
        [Fact]
        public void ACollapsedMosaicFollowsThePlanSwitchOnly() {
            using (var tmp = new TempDir())
            using (var db = TargetSchedulerDb.Open(TsFixtures.MakeDb(28, tmp.File("s.sqlite")))) {
                Push(db, Mosaic("No Fov", 2, 1, telescopeId: "",
                    panelActive: new Dictionary<string, bool> { { "1,1", false } }));
                Assert.Equal(1, Active(db)["No Fov"]);
            }
        }

        [Fact]
        public void ATargetWithAForeignGuidIsLeftAlone() {
            using (var tmp = new TempDir())
            using (var db = TargetSchedulerDb.Open(TsFixtures.MakeDb(28, tmp.File("s.sqlite")))) {
                Push(db, Halves());
                using (var cmd = db.Connection.CreateCommand()) {
                    cmd.CommandText =
                        "UPDATE target SET guid = 'someone-else', active = 1 WHERE name = 'Split Nebula South'";
                    cmd.ExecuteNonQuery();
                }
                Push(db, Halves(southActive: false));
                using (var cmd = db.Connection.CreateCommand()) {
                    cmd.CommandText = "SELECT active FROM target WHERE guid = 'someone-else'";
                    Assert.Equal(1L, (long)cmd.ExecuteScalar());
                }
            }
        }

        /// A target found through the plan's ts_refs is updated by its pinned
        /// columns only, and active is one of them.
        [Fact]
        public void APinnedTargetFollowsTheSwitch() {
            using (var tmp = new TempDir())
            using (var db = TargetSchedulerDb.Open(TsFixtures.MakeDb(28, tmp.File("s.sqlite")))) {
                var plan = TsRowScenarios.M42Plan(TsTestPlans.Row("row-ha", "Ha", "Ha", 300, frames: 4));
                TsRowScenarios.Link(db, TsRowScenarios.Push(db, plan), plan);
                Assert.Equal(1, Active(db)["M42"]);

                plan.TargetActive = false;
                var off = TsRowScenarios.Push(db, plan);
                Assert.Equal(1, off.Target.Pinned);
                Assert.Equal(0, Active(db)["M42"]);

                plan.TargetActive = null;
                var on = TsRowScenarios.Push(db, plan);
                Assert.Equal(1, on.Target.Pinned);
                Assert.Equal(1, Active(db)["M42"]);
            }
        }

        [Fact]
        public void TheBaseSnapshotRecordsActive() {
            using (var tmp = new TempDir())
            using (var db = TargetSchedulerDb.Open(TsFixtures.MakeDb(28, tmp.File("s.sqlite")))) {
                var plan = Mosaic("Wide Field", 2, 1, panelActive: new Dictionary<string, bool> { { "2,1", false } });
                Push(db, plan);
                var snap = db.ReadAll(TsTestPlans.ProfileId);
                var baseSnapshot = TsState.BuildBaseSnapshot(snap, plan, TsTestPlans.ProfileId);
                var byPanel = (JObject)baseSnapshot["targets_by_panel"];
                Assert.Equal(1, (int)byPanel["1,1"]["active"]);
                Assert.Equal(0, (int)byPanel["2,1"]["active"]);
            }
        }

        [Fact]
        public void PlansReadTheFieldsFromJson() {
            var plan = JsonConvert.DeserializeObject<Plan>(
                "{\"id\":\"p\",\"target_active\":false,\"panel_active\":{\"2,1\":false}}");
            Assert.False(plan.TargetActive);
            Assert.False(plan.PanelActive["2,1"]);
            var bare = JsonConvert.DeserializeObject<Plan>("{\"id\":\"p\"}");
            Assert.Null(bare.TargetActive);
            Assert.Null(bare.PanelActive);
        }

        [Fact]
        public void TargetIsActiveHelper() {
            Assert.True(TsConvert.TargetIsActive(new Plan()));
            Assert.False(TsConvert.TargetIsActive(new Plan { TargetActive = false }));
            var p = new Plan { PanelActive = new Dictionary<string, bool> { { "1,2", false } } };
            Assert.False(TsConvert.TargetIsActive(p, "1,2"));
            Assert.True(TsConvert.TargetIsActive(p, "1,1"));
            Assert.True(TsConvert.TargetIsActive(p));
        }
    }
}
