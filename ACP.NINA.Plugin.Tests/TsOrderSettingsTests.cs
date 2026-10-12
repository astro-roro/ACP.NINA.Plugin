using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// Switch frequency and smart order, written only when ACP changed them.
    /// docs/specs/goal-panel-and-ts-order.md section 4. The golden rows for
    /// F1 and F2 (test F3) are in TsPushGoldenTests; these cover the rest of
    /// the rule, as tests/test_order_settings.py does in the extension.
    public class TsOrderSettingsTests {

        private const string Profile = TsTestPlans.ProfileId;

        private static TsSyncPayload Payload(params Plan[] plans) {
            return TsConvert.BuildPayload(plans.ToList(), TsRowScenarios.Gear(), Profile, TsTestPlans.FrozenNow);
        }

        private static long ProjectColumn(SqliteConnection conn, string column) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = $"SELECT {column} FROM project WHERE name = 'M42'";
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        private static long Cadence(SqliteConnection conn) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "SELECT COUNT(*) FROM filtercadenceitem";
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        private static void Exec(SqliteConnection conn, string sql) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        [Fact]
        public void BothColumnsAreConditionalNotInsertOnly() {
            var cols = TsSchema.ColumnsForUpdate("project", new[] { "filterswitchfrequency", "smartexposureorder", "ditherevery" });
            Assert.Equal(new[] { "filterswitchfrequency", "smartexposureorder" }, cols);
            Assert.Contains("filterswitchfrequency", TsSchema.ConditionalProjectColumns);
            Assert.Contains("smartexposureorder", TsSchema.ConditionalProjectColumns);
        }

        [Fact]
        public void F2TheBaseKeepsZeroWhenThePushLeftTsAlone() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("f2-base.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var plans = TsRowScenarios.FState(db, 0);
                    Exec(db.Connection, "UPDATE project SET filterswitchfrequency = 3");
                    var payload = Payload(plans);
                    var outcome = TsUpsert.Apply(db, payload);
                    Assert.Equal(3, ProjectColumn(db.Connection, "filterswitchfrequency"));
                    Assert.Equal(6, Cadence(db.Connection));
                    Assert.Empty(outcome.CadenceResetProjects);

                    var snap = db.ReadAll(Profile);
                    var withPush = TsState.BuildBaseSnapshot(snap, plans[0], Profile, payload.Projects[0], outcome);
                    Assert.Equal(0, (int)withPush["project"]["filterswitchfrequency"]);
                    var fromTs = TsState.BuildBaseSnapshot(snap, plans[0], Profile);
                    Assert.Equal(3, (int)fromTs["project"]["filterswitchfrequency"]);
                }
            }
        }

        [Fact]
        public void AWrittenSettingMovesTheBase() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("f1-base.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var plans = TsRowScenarios.FState(db, 0);
                    foreach (var plan in plans) plan.FilterSwitchFrequency = 3;
                    var payload = Payload(plans);
                    var outcome = TsUpsert.Apply(db, payload);
                    Assert.Equal(new List<string> { "M42" }, outcome.CadenceResetProjects);
                    var snap = db.ReadAll(Profile);
                    var b = TsState.BuildBaseSnapshot(snap, plans[0], Profile, payload.Projects[0], outcome);
                    Assert.Equal(3, (int)b["project"]["filterswitchfrequency"]);
                    Assert.Equal(0, (int)b["project"]["ditherevery"]);
                }
            }
        }

        [Fact]
        public void AnUnknownSettingIsNeverWrittenOnUpdate() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("unknown.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var plans = TsRowScenarios.FState(db, 0);
                    Exec(db.Connection, "UPDATE project SET filterswitchfrequency = 7, smartexposureorder = 1");
                    foreach (var plan in plans) plan.FilterSwitchFrequency = null;
                    var outcome = TsUpsert.Apply(db, Payload(plans));
                    Assert.Equal(7, ProjectColumn(db.Connection, "filterswitchfrequency"));
                    Assert.Equal(1, ProjectColumn(db.Connection, "smartexposureorder"));
                    Assert.Equal(6, Cadence(db.Connection));
                    Assert.Empty(outcome.CadenceResetProjects);
                }
            }
        }

        [Fact]
        public void InsertTakesAcpsValuesOrZeroAndOff() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("insert.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var plan = TsRowScenarios.M42Plan();
                    plan.FilterSwitchFrequency = 4;
                    plan.SmartExposureOrder = true;
                    TsUpsert.Apply(db, Payload(plan));
                    Assert.Equal(4, ProjectColumn(db.Connection, "filterswitchfrequency"));
                    Assert.Equal(1, ProjectColumn(db.Connection, "smartexposureorder"));
                }
            }
            var none = Payload(TsRowScenarios.M42Plan());
            Assert.Equal(0, none.Projects[0].FilterSwitchFrequency);
            Assert.Equal(0, none.Projects[0].SmartExposureOrder);
        }

        [Fact]
        public void PlansThatDisagreeTakeTheFirstAndSaySo() {
            var a = TsRowScenarios.M42Plan();
            var b = TsTestPlans.Plan("m43", projectName: "M42", targetName: "M43", filterRows: new List<FilterRow>());
            a.FilterSwitchFrequency = 2;
            b.FilterSwitchFrequency = 6;
            var payload = Payload(a, b);
            Assert.Equal(2, payload.Projects[0].FilterSwitchFrequency);
            Assert.Contains(payload.Notes, n => n.Contains("disagree on the filter switch frequency") && n.Contains("m42"));
        }

        [Fact]
        public void ABaseFromBeforeTheFieldsReadsAsZeroAndOff() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("old-base.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var plans = TsRowScenarios.FState(db, 0);
                    foreach (var plan in plans) {
                        var proj = (Newtonsoft.Json.Linq.JObject)plan.TsLinks[Profile]["base_snapshot"]["project"];
                        proj.Remove("filterswitchfrequency");
                        proj.Remove("smartexposureorder");
                    }
                    Assert.DoesNotContain("filterswitchfrequency", Payload(plans).Projects[0].ConditionalColumnsToWrite);
                    plans[0].FilterSwitchFrequency = 3;
                    Assert.Contains("filterswitchfrequency", Payload(plans).Projects[0].ConditionalColumnsToWrite);
                }
            }
        }
    }
}
