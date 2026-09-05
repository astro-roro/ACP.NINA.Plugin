using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// Strictest wins when several plans collapse into one project. Mirrors
    /// tests/test_strictest_wins.py in the Python extension.
    public class TsStrictestWinsTests {

        private static List<Plan> Group(params Plan[] plans) {
            return plans.ToList();
        }

        private static Plan InProject(string id, double minAlt = 30, int? meridian = null, string priority = "normal") {
            return TsTestPlans.Plan(
                id, projectName: "Shared Project", targetName: "T" + id,
                minAltitudeDeg: minAlt, meridianWindowMin: meridian, priority: priority);
        }

        private static TsSyncPayload Build(List<Plan> plans) {
            return TsConvert.BuildPayload(
                plans, TsTestPlans.Gear(), TsTestPlans.ProfileId, TsTestPlans.FrozenNow);
        }

        [Fact]
        public void MinimumAltitudeTakesTheHighest() {
            var payload = Build(Group(
                InProject("a", minAlt: 20), InProject("b", minAlt: 45), InProject("c", minAlt: 30)));

            Assert.Single(payload.Projects);
            Assert.Equal(45.0, payload.Projects[0].MinimumAltitude);
        }

        [Fact]
        public void MeridianWindowTakesTheTightestNonZero() {
            var payload = Build(Group(
                InProject("a", meridian: 0), InProject("b", meridian: 90), InProject("c", meridian: 60)));

            Assert.Equal(60, payload.Projects[0].MeridianWindow);
        }

        [Fact]
        public void MeridianWindowStaysZeroWhenNoPlanSetsOne() {
            var payload = Build(Group(InProject("a", meridian: 0), InProject("b")));
            Assert.Equal(0, payload.Projects[0].MeridianWindow);
        }

        [Fact]
        public void PriorityTakesTheHighest() {
            var payload = Build(Group(
                InProject("a", priority: "low"),
                InProject("b", priority: "high"),
                InProject("c", priority: "normal")));

            Assert.Equal(TsConvert.PriorityRank["high"], payload.Projects[0].Priority);
        }

        [Fact]
        public void AnUnknownPriorityCountsAsNormal() {
            Assert.Equal(1, TsConvert.RankOf("whatever"));
            Assert.Equal(1, TsConvert.RankOf(null));
        }

        [Theory]
        [InlineData(23)]
        [InlineData(28)]
        public void TheCollapsedValuesReachTheDatabaseAtBothEnds(int version) {
            var plans = Group(
                InProject("a", minAlt: 20, meridian: 0, priority: "low"),
                InProject("b", minAlt: 45, meridian: 90, priority: "high"),
                InProject("c", minAlt: 30, meridian: 60, priority: "normal"));

            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(version, tmp.File($"v{version}.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    TsUpsert.Apply(db, Build(plans));

                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText = "SELECT minimumaltitude, meridianwindow, priority FROM project";
                        using (var reader = cmd.ExecuteReader()) {
                            Assert.True(reader.Read());
                            Assert.Equal(45.0, reader.GetDouble(0));
                            Assert.Equal(60, reader.GetInt32(1));
                            Assert.Equal(TsConvert.PriorityRank["high"], reader.GetInt32(2));
                            Assert.False(reader.Read());
                        }
                    }

                    // All three plans still land as their own targets.
                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText = "SELECT COUNT(*) FROM target";
                        Assert.Equal(3L, Convert.ToInt64(cmd.ExecuteScalar()));
                    }
                }
            }
        }
    }

    /// Claiming, and what happens to rows somebody else stamped. This is the
    /// behaviour that decides whether a push tramples the projects a user set
    /// up by hand in Target Scheduler's own UI.
    public class TsClaimTests {

        private static TsSyncPayload OneProject(string name) {
            return TsConvert.BuildPayload(
                new List<Plan> { TsTestPlans.Plan("p", projectName: name, targetName: "T") },
                TsTestPlans.Gear(), TsTestPlans.ProfileId, TsTestPlans.FrozenNow);
        }

        [Fact]
        public void AProjectWithNoGuidIsClaimedRatherThanDuplicated() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("claim.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    Exec(db.Connection,
                        "INSERT INTO project (profileId, name, state, priority, minimumaltitude) " +
                        $"VALUES ('{TsTestPlans.ProfileId}', 'Hand Made', 1, 1, 10.0)");

                    var outcome = TsUpsert.Apply(db, OneProject("Hand Made"));

                    Assert.Equal(1, outcome.Project.Claimed);
                    Assert.Equal(0, outcome.Project.Inserted);
                    Assert.Equal(1L, Scalar(db.Connection, "SELECT COUNT(*) FROM project"));
                    // The claimed row now carries our stamp, so the next push
                    // finds it by guid rather than claiming it again.
                    Assert.Equal(
                        TsGuid.Project(TsTestPlans.ProfileId, "Hand Made"),
                        Scalar(db.Connection, "SELECT guid FROM project"));
                }
            }
        }

        [Fact]
        public void RuleWeightsAreNotSeededOntoAClaimedProject() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("claim-rules.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    Exec(db.Connection,
                        "INSERT INTO project (profileId, name, state, priority) " +
                        $"VALUES ('{TsTestPlans.ProfileId}', 'Hand Made', 1, 1)");

                    var outcome = TsUpsert.Apply(db, OneProject("Hand Made"));

                    Assert.Equal(0, outcome.RuleWeightSeededProjects);
                    Assert.Equal(0L, Scalar(db.Connection, "SELECT COUNT(*) FROM ruleweight"));
                }
            }
        }

        [Fact]
        public void ARowStampedBySomebodyElseIsLeftAloneAndANewRowGoesInBesideIt() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("foreign.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    Exec(db.Connection,
                        "INSERT INTO project (profileId, name, state, priority, guid) " +
                        $"VALUES ('{TsTestPlans.ProfileId}', 'Theirs', 1, 1, 'not-our-stamp')");

                    var outcome = TsUpsert.Apply(db, OneProject("Theirs"));

                    Assert.Equal(1, outcome.Project.Inserted);
                    Assert.Equal(0, outcome.Project.Claimed);
                    Assert.Equal(2L, Scalar(db.Connection, "SELECT COUNT(*) FROM project"));
                    // Their stamp survives untouched. Fighting over another
                    // tool's identity is worse than a duplicate the user can see.
                    Assert.Equal(1L, Scalar(db.Connection,
                        "SELECT COUNT(*) FROM project WHERE guid = 'not-our-stamp'"));
                }
            }
        }

        [Fact]
        public void RuleWeightsAreSeededOnceOnANewProjectAndNotTouchedAgain() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("rules.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var first = TsUpsert.Apply(db, OneProject("Fresh"));
                    Assert.Equal(1, first.RuleWeightSeededProjects);
                    Assert.Equal(5L, Scalar(db.Connection, "SELECT COUNT(*) FROM ruleweight"));

                    Exec(db.Connection, "UPDATE ruleweight SET weight = 99 WHERE name = 'PercentComplete'");
                    var second = TsUpsert.Apply(db, OneProject("Fresh"));

                    Assert.Equal(0, second.RuleWeightSeededProjects);
                    Assert.Equal(5L, Scalar(db.Connection, "SELECT COUNT(*) FROM ruleweight"));
                    // The user's tuning survives a re-sync.
                    Assert.Equal(99.0, Convert.ToDouble(Scalar(db.Connection,
                        "SELECT weight FROM ruleweight WHERE name = 'PercentComplete'")));
                }
            }
        }

        [Fact]
        public void PreviewCountsWithoutWriting() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("preview.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var payload = TsConvert.BuildPayload(
                        TsTestPlans.ThreePlans(), TsTestPlans.Gear(),
                        TsTestPlans.ProfileId, TsTestPlans.FrozenNow);

                    var preview = TsUpsert.Preview(db, payload);

                    Assert.Equal(3, preview.Project.Inserted);
                    Assert.Equal(6, preview.Target.Inserted);
                    Assert.Equal(7, preview.ExposurePlan.Inserted);
                    Assert.Equal(2, preview.ExposureTemplate.Inserted);
                    Assert.Equal(0L, Scalar(db.Connection, "SELECT COUNT(*) FROM project"));
                }
            }
        }

        private static void Exec(SqliteConnection conn, string sql) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        private static object Scalar(SqliteConnection conn, string sql) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                return cmd.ExecuteScalar();
            }
        }
    }

    /// Reading acquired counts back out. Mirrors tests/test_read_acquired.py.
    public class TsReadAcquiredTests {

        private static string Seed(TempDir tmp, int version) {
            var path = TsFixtures.MakeDb(version, tmp.File($"seeded{version}.sqlite"));
            using (var db = TargetSchedulerDb.Open(path)) {
                TsUpsert.Apply(db, TsConvert.BuildPayload(
                    TsTestPlans.ThreePlans(), TsTestPlans.Gear(),
                    TsTestPlans.ProfileId, TsTestPlans.FrozenNow));

                // Counts the way Target Scheduler would leave them after a few
                // nights: acquired is everything shot, accepted is what
                // survived grading.
                var ids = new List<long>();
                using (var cmd = db.Connection.CreateCommand()) {
                    cmd.CommandText = "SELECT Id FROM exposureplan ORDER BY Id";
                    using (var reader = cmd.ExecuteReader()) {
                        while (reader.Read()) ids.Add(reader.GetInt64(0));
                    }
                }
                for (var i = 0; i < ids.Count; i++) {
                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText =
                            "UPDATE exposureplan SET acquired = $a, accepted = $b WHERE Id = $id";
                        cmd.Parameters.AddWithValue("$a", 10 + i);
                        cmd.Parameters.AddWithValue("$b", 8 + i);
                        cmd.Parameters.AddWithValue("$id", ids[i]);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            return path;
        }

        [Fact]
        public void ACountAndAnExposurePerPlan() {
            using (var tmp = new TempDir()) {
                var path = Seed(tmp, 28);
                using (var db = TargetSchedulerDb.Open(path)) {
                    var acquired = db.ReadAcquired(TsTestPlans.ProfileId);
                    Assert.Equal(7, acquired.Count);
                    Assert.All(acquired.Values, a => Assert.InRange(a.Count, 10, 16));
                    Assert.All(acquired.Values, a => Assert.True(a.ExposureSeconds > 0));
                }
            }
        }

        [Fact]
        public void AcceptedWinsWhenTheGraderIsOn() {
            using (var tmp = new TempDir()) {
                var path = Seed(tmp, 28);
                using (var db = TargetSchedulerDb.Open(path)) {
                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText = "UPDATE project SET enablegrader = 1 WHERE name = 'Two Filters'";
                        cmd.ExecuteNonQuery();
                    }

                    var acquired = db.ReadAcquired(TsTestPlans.ProfileId);
                    var graded = new List<string>();
                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText =
                            "SELECT ep.guid FROM exposureplan ep " +
                            "JOIN target t ON ep.targetid = t.Id " +
                            "JOIN project p ON t.projectid = p.Id WHERE p.name = 'Two Filters'";
                        using (var reader = cmd.ExecuteReader()) {
                            while (reader.Read()) graded.Add(reader.GetString(0));
                        }
                    }

                    Assert.Equal(2, graded.Count);
                    foreach (var guid in graded) {
                        using (var cmd = db.Connection.CreateCommand()) {
                            cmd.CommandText =
                                "SELECT acquired, accepted FROM exposureplan WHERE guid = $g";
                            cmd.Parameters.AddWithValue("$g", guid);
                            using (var reader = cmd.ExecuteReader()) {
                                Assert.True(reader.Read());
                                var acq = reader.GetInt32(0);
                                var acc = reader.GetInt32(1);
                                Assert.Equal(acc, acquired[guid].Count);
                                Assert.NotEqual(acq, acquired[guid].Count);
                            }
                        }
                    }
                }
            }
        }

        [Fact]
        public void PlansWithNoStampAreSkipped() {
            using (var tmp = new TempDir()) {
                var path = Seed(tmp, 28);
                using (var db = TargetSchedulerDb.Open(path)) {
                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText =
                            "UPDATE exposureplan SET guid = '' " +
                            "WHERE Id = (SELECT MIN(Id) FROM exposureplan)";
                        cmd.ExecuteNonQuery();
                    }
                    Assert.Equal(6, db.ReadAcquired(TsTestPlans.ProfileId).Count);
                }
            }
        }

        [Fact]
        public void ReadsAreScopedToTheProfile() {
            using (var tmp = new TempDir()) {
                using (var db = TargetSchedulerDb.Open(Seed(tmp, 28))) {
                    Assert.Empty(db.ReadAcquired("some-other-profile"));
                }
            }
        }

        [Fact]
        public void ReadAllRoundTripsThePushedRows() {
            using (var tmp = new TempDir()) {
                using (var db = TargetSchedulerDb.Open(Seed(tmp, 28))) {
                    var snap = db.ReadAll(TsTestPlans.ProfileId);
                    Assert.Equal(3, snap.ProjectsById.Count);
                    Assert.Equal(6, snap.TargetsById.Count);
                    Assert.Equal(7, snap.PlansById.Count);
                    Assert.Equal(2, snap.TemplatesById.Count);
                    Assert.All(snap.TargetsById.Values, t => Assert.Equal(-1, t.Priority));
                }
            }
        }

        [Fact]
        public void ReadAllToleratesA23DatabaseWhereTargetPriorityDoesNotExist() {
            using (var tmp = new TempDir()) {
                using (var db = TargetSchedulerDb.Open(Seed(tmp, 23))) {
                    var snap = db.ReadAll(TsTestPlans.ProfileId);
                    Assert.Equal(6, snap.TargetsById.Count);
                    // The column is absent, so the Target Scheduler default
                    // stands in rather than the read failing.
                    Assert.All(snap.TargetsById.Values, t => Assert.Equal(-1, t.Priority));
                    Assert.Equal(7, db.ReadAcquired(TsTestPlans.ProfileId).Count);
                }
            }
        }
    }

    /// The mosaic geometry and the conversions around it, which decide the
    /// target names and therefore the guids.
    public class TsConvertGeometryTests {

        [Fact]
        public void ASinglePanelPlanGetsNoPanelSuffix() {
            var payload = TsConvert.BuildPayload(
                new List<Plan> { TsTestPlans.Plan("s", projectName: "P", targetName: "NGC 253") },
                TsTestPlans.Gear(), TsTestPlans.ProfileId, TsTestPlans.FrozenNow);

            var target = payload.TargetsByProjectGuid.Values.Single().Single();
            Assert.Equal("NGC 253", target.Name);
        }

        [Fact]
        public void AMosaicIsNamedRowMajorAndOneIndexed() {
            var payload = TsConvert.BuildPayload(
                new List<Plan> {
                    TsTestPlans.Plan("m", projectName: "P", targetName: "M31",
                                     rows: 2, cols: 2, ra: 10.68, dec: 41.27),
                },
                TsTestPlans.Gear(), TsTestPlans.ProfileId, TsTestPlans.FrozenNow);

            Assert.Equal(
                new[] {
                    "M31 Panel 1 (R1C1)", "M31 Panel 2 (R1C2)",
                    "M31 Panel 3 (R2C1)", "M31 Panel 4 (R2C2)",
                },
                payload.TargetsByProjectGuid.Values.Single().Select(t => t.Name).ToArray());
        }

        [Fact]
        public void AMosaicWithNoUsableFieldOfViewStaysASinglePanel() {
            // No focal length means no field of view, so the panels cannot be
            // placed and one target is the honest answer.
            var gear = TsTestPlans.Gear();
            gear.Telescopes[0].FocalLengthMm = 0;

            var payload = TsConvert.BuildPayload(
                new List<Plan> {
                    TsTestPlans.Plan("m", projectName: "P", targetName: "M31", rows: 2, cols: 2),
                },
                gear, TsTestPlans.ProfileId, TsTestPlans.FrozenNow);

            var target = payload.TargetsByProjectGuid.Values.Single().Single();
            Assert.Equal("M31", target.Name);
        }

        [Fact]
        public void RightAscensionIsStoredInHours() {
            var payload = TsConvert.BuildPayload(
                new List<Plan> { TsTestPlans.Plan("s", projectName: "P", targetName: "T", ra: 180.0) },
                TsTestPlans.Gear(), TsTestPlans.ProfileId, TsTestPlans.FrozenNow);

            Assert.Equal(12.0, payload.TargetsByProjectGuid.Values.Single().Single().Ra, 9);
        }

        [Fact]
        public void FieldOfViewMatchesTheArcsecondFormula() {
            double width, height;
            TsConvert.FovArcmin(TsTestPlans.Gear().Telescopes[0], TsTestPlans.Gear().Cameras[0],
                                out width, out height);

            // 206.265 * 3.76 / 600 arcsec per pixel, over 6248 by 4176 pixels.
            var arcsecPerPx = 206.265 * 3.76 / 600.0;
            Assert.Equal(Math.Round(6248 * arcsecPerPx / 60.0, 2), width, 6);
            Assert.Equal(Math.Round(4176 * arcsecPerPx / 60.0, 2), height, 6);
        }

        [Fact]
        public void MissingGearMeansNoFieldOfView() {
            double width, height;
            TsConvert.FovArcmin(null, TsTestPlans.Gear().Cameras[0], out width, out height);
            Assert.Equal(0.0, width);
            Assert.Equal(0.0, height);
        }

        [Fact]
        public void DesiredSubsRoundUpAndAcquiredSubsRoundHalfToEven() {
            var plans = new List<Plan> {
                TsTestPlans.Plan("p", projectName: "P", targetName: "T",
                    filterGoals: new Dictionary<string, FilterGoal> {
                        // 1.5 hours at 400 s is 13.5 subs to shoot, and 0.5
                        // hours already shot is 4.5 subs.
                        { "Ha", new FilterGoal {
                            TargetHours = 1.5, SubExposureS = 400, ActualHours = 0.5 } },
                    }),
            };

            var payload = TsConvert.BuildPayload(
                plans, TsTestPlans.Gear(), TsTestPlans.ProfileId, TsTestPlans.FrozenNow);
            var row = payload.PlansByTargetGuid.Values.Single().Single();

            // Desired ceilings, because 13 subs would never reach the goal.
            Assert.Equal(14, row.Desired);
            // Acquired takes the nearest, and Python's round() breaks a tie to
            // the even number, so 4.5 is 4 rather than 5. Matched here on
            // purpose: it is what the Python extension writes for the same
            // plan, and a half sub either way is noise next to the two tools
            // disagreeing about the count.
            Assert.Equal(4, row.Acquired);
            Assert.Equal(4, row.Accepted);
            Assert.Equal(400.0, row.Exposure);
        }

        [Fact]
        public void AGoalWithNoSubLengthFallsBackTo300Seconds() {
            var plans = new List<Plan> {
                TsTestPlans.Plan("p", projectName: "P", targetName: "T",
                    filterGoals: new Dictionary<string, FilterGoal> {
                        { "Ha", new FilterGoal { TargetHours = 1.0, SubExposureS = null } },
                    }),
            };

            var payload = TsConvert.BuildPayload(
                plans, TsTestPlans.Gear(), TsTestPlans.ProfileId, TsTestPlans.FrozenNow);
            var row = payload.PlansByTargetGuid.Values.Single().Single();

            Assert.Equal(300.0, row.Exposure);
            Assert.Equal(12, row.Desired);
        }

        [Fact]
        public void AGoalWithNoHoursIsSkippedEntirely() {
            var plans = new List<Plan> {
                TsTestPlans.Plan("p", projectName: "P", targetName: "T",
                    filterGoals: new Dictionary<string, FilterGoal> {
                        { "Ha", new FilterGoal { TargetHours = 0, SubExposureS = 300 } },
                        { "OIII", new FilterGoal { TargetHours = 2, SubExposureS = 300 } },
                    }),
            };

            var payload = TsConvert.BuildPayload(
                plans, TsTestPlans.Gear(), TsTestPlans.ProfileId, TsTestPlans.FrozenNow);

            Assert.Single(payload.Templates);
            Assert.Equal("OIII", payload.Templates[0].FilterName);
            Assert.Single(payload.PlansByTargetGuid.Values.Single());
        }

        [Fact]
        public void ATemplateWithNoNameSetIsNamedFromTheFilterAndCamera() {
            var gear = TsTestPlans.Gear();
            gear.Cameras[0].Filters["OIII"].TsTemplateName = null;

            var payload = TsConvert.BuildPayload(
                new List<Plan> { TsTestPlans.Plan("p", projectName: "P", targetName: "T") },
                gear, TsTestPlans.ProfileId, TsTestPlans.FrozenNow);

            Assert.Equal("OIII (Test IMX571)", payload.Templates[0].Name);
        }

        [Fact]
        public void ProjectNameFallsBackToTheTargetThenThePlanId() {
            Assert.Equal("Explicit", TsConvert.ProjectNameOf(
                TsTestPlans.Plan("id", projectName: "Explicit", targetName: "T")));
            Assert.Equal("T", TsConvert.ProjectNameOf(
                TsTestPlans.Plan("id", projectName: "", targetName: "T")));
            Assert.Equal("id", TsConvert.ProjectNameOf(
                TsTestPlans.Plan("id", projectName: "", targetName: "")));
        }
    }
}
