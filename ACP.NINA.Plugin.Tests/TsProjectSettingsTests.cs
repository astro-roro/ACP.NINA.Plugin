using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// State, priority and minimum time: ACP's project settings that sync both
    /// ways with Target Scheduler. Mirrors
    /// docs/specs/ts-project-settings.md sections 1, 4, 5 and 6.
    public class TsGroupResolutionTests {

        [Fact]
        public void GroupStateIsTheMostRunningValue() {
            // active beats inactive, draft and closed.
            Assert.Equal("active", TsConvert.GroupState(new List<Plan> {
                TsTestPlans.Plan("a", state: "closed"),
                TsTestPlans.Plan("b", state: "active"),
            }));
            // inactive beats draft and closed.
            Assert.Equal("inactive", TsConvert.GroupState(new List<Plan> {
                TsTestPlans.Plan("a", state: "draft"),
                TsTestPlans.Plan("b", state: "inactive"),
                TsTestPlans.Plan("c", state: "closed"),
            }));
            // draft beats closed.
            Assert.Equal("draft", TsConvert.GroupState(new List<Plan> {
                TsTestPlans.Plan("a", state: "closed"),
                TsTestPlans.Plan("b", state: "draft"),
            }));
        }

        [Fact]
        public void AMissingStateReadsAsActive() {
            var plan = TsTestPlans.Plan("a", state: null);
            Assert.Equal("active", TsConvert.GroupState(new List<Plan> { plan }));
        }

        [Fact]
        public void MinimumTimeTakesTheLargest() {
            var payload = TsConvert.BuildPayload(
                new List<Plan> {
                    TsTestPlans.Plan("a", projectName: "P", targetName: "Ta", minimumTimeMin: 20),
                    TsTestPlans.Plan("b", projectName: "P", targetName: "Tb", minimumTimeMin: 45),
                    TsTestPlans.Plan("c", projectName: "P", targetName: "Tc", minimumTimeMin: null),
                },
                TsTestPlans.Gear(), TsTestPlans.ProfileId, TsTestPlans.FrozenNow);

            Assert.Equal(45, payload.Projects[0].MinimumTime);
        }

        [Fact]
        public void StateNoLongerHardcodesActive() {
            var payload = TsConvert.BuildPayload(
                new List<Plan> { TsTestPlans.Plan("a", projectName: "P", targetName: "T", state: "inactive") },
                TsTestPlans.Gear(), TsTestPlans.ProfileId, TsTestPlans.FrozenNow);

            Assert.Equal(2, payload.Projects[0].State);
            Assert.Equal(TsTestPlans.FrozenNow, payload.Projects[0].InactiveDate);
            Assert.Null(payload.Projects[0].ActiveDate);
        }
    }

    /// The conflict rule: write ACP's value for state, priority and minimum
    /// time only when ACP changed it since the last sync on this rig.
    public class TsConditionalColumnTests {

        private static TsSyncPayload OnePlan(Action<Plan> configure = null) {
            var plan = TsTestPlans.Plan("p", projectName: "P", targetName: "T");
            configure?.Invoke(plan);
            return TsConvert.BuildPayload(
                new List<Plan> { plan }, TsTestPlans.Gear(), TsTestPlans.ProfileId, TsTestPlans.FrozenNow);
        }

        [Fact]
        public void NoBaseAtAllWritesNoneOfTheThreeOnUpdate() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("nobase.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    TsUpsert.Apply(db, OnePlan()); // insert

                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText = "UPDATE project SET state = 2, priority = 2, minimumtime = 99";
                        cmd.ExecuteNonQuery();
                    }

                    // Same plan, still with no ts_links base at all: the
                    // second push must leave the rig's values alone.
                    TsUpsert.Apply(db, OnePlan());

                    AssertProject(db.Connection, state: 2, priority: 2, minimumtime: 99);
                }
            }
        }

        [Fact]
        public void ABaseThatMatchesAcpWritesNothing() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("matching-base.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    TsUpsert.Apply(db, OnePlan()); // insert, state active(1), priority normal(1), minimumtime 0

                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText = "UPDATE project SET state = 2"; // TS pauses it
                        cmd.ExecuteNonQuery();
                    }

                    // ACP has not changed anything since the base (active/normal/0),
                    // so the push must not undo the pause made in TS.
                    var payload = OnePlan(p => TsTestPlans.WithBase(
                        p, TsTestPlans.ProfileId, state: 1, priority: 1, minimumTime: 0));
                    TsUpsert.Apply(db, payload);

                    AssertProject(db.Connection, state: 2, priority: 1, minimumtime: 0);
                }
            }
        }

        [Fact]
        public void AcpChangingStateSinceTheBaseWinsOverTs() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("acp-wins.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    TsUpsert.Apply(db, OnePlan());

                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText = "UPDATE project SET state = 1"; // resumed in TS
                        cmd.ExecuteNonQuery();
                    }

                    // Base says active, ACP now says inactive: ACP changed it,
                    // so the push writes inactive even though TS currently
                    // disagrees (having been resumed there).
                    var payload = OnePlan(p => {
                        p.State = "inactive";
                        TsTestPlans.WithBase(p, TsTestPlans.ProfileId, state: 1, priority: 1, minimumTime: 0);
                    });
                    var outcome = TsUpsert.Apply(db, payload);

                    AssertProject(db.Connection, state: 2, priority: 1, minimumtime: 0);
                    Assert.Contains(outcome.ConflictLines, l => l.Contains("undid a change made in TS"));
                }
            }
        }

        [Fact]
        public void AFreshInsertWritesEveryColumnRegardless() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("fresh.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var payload = OnePlan(p => { p.State = "inactive"; p.MinimumTimeMin = 15; });
                    TsUpsert.Apply(db, payload);

                    AssertProject(db.Connection, state: 2, priority: 1, minimumtime: 15);
                }
            }
        }

        [Fact]
        public void TsOwnedColumnsSurviveAnUpdateEvenWithoutABase() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("ts-owned.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    TsUpsert.Apply(db, OnePlan());
                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText =
                            "UPDATE project SET ditherevery = 3, enablegrader = 1, usecustomhorizon = 1";
                        cmd.ExecuteNonQuery();
                    }

                    TsUpsert.Apply(db, OnePlan());

                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText = "SELECT ditherevery, enablegrader, usecustomhorizon FROM project";
                        using (var reader = cmd.ExecuteReader()) {
                            Assert.True(reader.Read());
                            Assert.Equal(3L, reader.GetInt64(0));
                            Assert.Equal(1L, reader.GetInt64(1));
                            Assert.Equal(1L, reader.GetInt64(2));
                        }
                    }
                }
            }
        }

        [Fact]
        public void APinnedProjectUpdateNeverRewritesTheGuid() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("pinned-guid.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    TsUpsert.Apply(db, OnePlan());
                    int projectId;
                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText = "SELECT Id FROM project";
                        projectId = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                    // Simulate the row being one Target Scheduler made by hand
                    // and imported into ACP: a foreign guid, found again only
                    // through ts_refs.
                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText = "UPDATE project SET guid = 'a-hand-made-guid'";
                        cmd.ExecuteNonQuery();
                    }

                    var plan = TsTestPlans.Plan("p", projectName: "P", targetName: "T");
                    plan.TsRefs = new Newtonsoft.Json.Linq.JObject {
                        { "profile_id", TsTestPlans.ProfileId },
                        { "project_id", projectId },
                    };
                    var payload = TsConvert.BuildPayload(
                        new List<Plan> { plan }, TsTestPlans.Gear(), TsTestPlans.ProfileId, TsTestPlans.FrozenNow);

                    var outcome = TsUpsert.Apply(db, payload);
                    Assert.Equal(1, outcome.Project.Pinned);

                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText = "SELECT guid FROM project WHERE Id = $id";
                        cmd.Parameters.AddWithValue("$id", projectId);
                        Assert.Equal("a-hand-made-guid", (string)cmd.ExecuteScalar());
                    }
                }
            }
        }

        private static void AssertProject(SqliteConnection conn, int state, int priority, int minimumtime) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "SELECT state, priority, minimumtime FROM project";
                using (var reader = cmd.ExecuteReader()) {
                    Assert.True(reader.Read());
                    Assert.Equal((long)state, reader.GetInt64(0));
                    Assert.Equal((long)priority, reader.GetInt64(1));
                    Assert.Equal((long)minimumtime, reader.GetInt64(2));
                }
            }
        }
    }

    /// Sync for tonight's state-only write for a plan that is not being
    /// loaded: docs/specs/ts-project-settings.md section 6.
    public class TsHeldStatesTests {

        [Fact]
        public void WritesStateOnlyToAnExistingRowAndInsertsNothing() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("held.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var payload = TsConvert.BuildPayload(
                        new List<Plan> { TsTestPlans.Plan("p", projectName: "Held", targetName: "T") },
                        TsTestPlans.Gear(), TsTestPlans.ProfileId, TsTestPlans.FrozenNow);
                    TsUpsert.Apply(db, payload);
                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText = "UPDATE project SET ditherevery = 7";
                        cmd.ExecuteNonQuery();
                    }

                    var held = new List<Plan> {
                        TsTestPlans.Plan("p", projectName: "Held", targetName: "T", state: "inactive"),
                    };
                    var outcome = TsHeldStates.Apply(
                        db.Connection, db.UserVersion, db.ColumnsByTable, held,
                        TsTestPlans.ProfileId, TsTestPlans.FrozenNow);

                    Assert.Equal(1, outcome.InactiveCount);
                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText = "SELECT state, ditherevery, inactivedate FROM project";
                        using (var reader = cmd.ExecuteReader()) {
                            Assert.True(reader.Read());
                            Assert.Equal(2L, reader.GetInt64(0));
                            Assert.Equal(7L, reader.GetInt64(1));
                            Assert.Equal(TsTestPlans.FrozenNow, reader.GetInt64(2));
                        }
                    }
                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText = "SELECT COUNT(*) FROM project";
                        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
                    }
                }
            }
        }

        [Fact]
        public void AHeldPlanWithNoExistingRowInsertsNothing() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("held-norow.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var held = new List<Plan> {
                        TsTestPlans.Plan("p", projectName: "Never Pushed", targetName: "T", state: "closed"),
                    };
                    var outcome = TsHeldStates.Apply(
                        db.Connection, db.UserVersion, db.ColumnsByTable, held,
                        TsTestPlans.ProfileId, TsTestPlans.FrozenNow);

                    Assert.Equal(0, outcome.ClosedCount);
                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText = "SELECT COUNT(*) FROM project";
                        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
                    }
                }
            }
        }
    }
}
