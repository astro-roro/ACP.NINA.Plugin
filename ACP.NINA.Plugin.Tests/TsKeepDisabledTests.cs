using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using System.Collections.Generic;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// A plan the user turned off in TS stays off, as in the Python sync
    /// (test_a_plan_turned_off_in_ts_stays_off and its siblings).
    public class TsKeepDisabledTests {

        private static Plan TwoRows() {
            return TsRowScenarios.M42Plan(
                TsTestPlans.Row("row-ha", "Ha", "Ha", 300, frames: 4),
                TsTestPlans.Row("row-oiii", "OIII", "OIII", 300, frames: 4));
        }

        private static List<string> Enabled(TargetSchedulerDb db) {
            var list = new List<string>();
            using (var cmd = db.Connection.CreateCommand()) {
                cmd.CommandText = "SELECT enabled FROM exposureplan ORDER BY Id";
                using (var r = cmd.ExecuteReader()) {
                    while (r.Read()) list.Add(r.GetValue(0).ToString());
                }
            }
            return list;
        }

        private static void TurnOffPlanOne(TargetSchedulerDb db) {
            using (var cmd = db.Connection.CreateCommand()) {
                cmd.CommandText = "UPDATE exposureplan SET enabled = 0 WHERE Id = 1";
                cmd.ExecuteNonQuery();
            }
        }

        [Fact]
        public void APlanTurnedOffInTsStaysOff() {
            using (var tmp = new TempDir())
            using (var db = TargetSchedulerDb.Open(TsFixtures.MakeDb(28, tmp.File("s.sqlite")))) {
                var p = TwoRows();
                TsRowScenarios.Link(db, TsRowScenarios.Push(db, p), p);
                TurnOffPlanOne(db);
                var outcome = TsRowScenarios.Push(db, p);
                Assert.Equal(new[] { "0", "1" }, Enabled(db));
                var kept = Assert.Single(outcome.KeptDisabled);
                Assert.Contains("Ha", kept);
                Assert.Contains(outcome.Notes, n => n.EndsWith("turned off in TS, so the sync left it off."));
            }
        }

        [Fact]
        public void APlanOffInTsWithNoBaseIsEnabled() {
            using (var tmp = new TempDir())
            using (var db = TargetSchedulerDb.Open(TsFixtures.MakeDb(28, tmp.File("s.sqlite")))) {
                var p = TwoRows();
                TsRowScenarios.Push(db, p);
                TurnOffPlanOne(db);
                var outcome = TsRowScenarios.Push(db, p);
                Assert.Equal(new[] { "1", "1" }, Enabled(db));
                Assert.Empty(outcome.KeptDisabled);
            }
        }

        [Fact]
        public void AGoalOfZeroDisablesThePlanAndARiseEnablesItAgain() {
            using (var tmp = new TempDir())
            using (var db = TargetSchedulerDb.Open(TsFixtures.MakeDb(28, tmp.File("s.sqlite")))) {
                var p = TwoRows();
                TsRowScenarios.Link(db, TsRowScenarios.Push(db, p), p);
                p.FilterRows[1].TargetFrames = 0;
                var zero = TsRowScenarios.Push(db, p);
                Assert.Equal(new[] { "1", "0" }, Enabled(db));
                Assert.Equal(1, zero.DisabledPlans);
                TsRowScenarios.Link(db, zero, p);
                p.FilterRows[1].TargetFrames = 6;
                var back = TsRowScenarios.Push(db, p);
                Assert.Equal(new[] { "1", "1" }, Enabled(db));
                Assert.Empty(back.KeptDisabled);
            }
        }

        [Fact]
        public void APlanTurnedOffInTsStaysOffAtEveryPush() {
            using (var tmp = new TempDir())
            using (var db = TargetSchedulerDb.Open(TsFixtures.MakeDb(28, tmp.File("s.sqlite")))) {
                var p = TwoRows();
                TsRowScenarios.Link(db, TsRowScenarios.Push(db, p), p);
                TurnOffPlanOne(db);
                for (var i = 0; i < 3; i++) {
                    var outcome = TsRowScenarios.Push(db, p);
                    TsRowScenarios.Link(db, outcome, p);
                    Assert.Equal(new[] { "0", "1" }, Enabled(db));
                    Assert.Single(outcome.KeptDisabled);
                }
                using (var cmd = db.Connection.CreateCommand()) {
                    cmd.CommandText = "UPDATE exposureplan SET enabled = 1 WHERE Id = 1";
                    cmd.ExecuteNonQuery();
                }
                var again = TsRowScenarios.Push(db, p);
                Assert.Equal(new[] { "1", "1" }, Enabled(db));
                Assert.Empty(again.KeptDisabled);
            }
        }
    }
}
