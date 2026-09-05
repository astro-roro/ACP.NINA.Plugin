using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using ACP.NINA.Plugin.Services.TargetScheduler;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The seam between the mode switch and the push: what the Everything and
    /// Only what fits modes actually put into Target Scheduler.
    ///
    /// The runner itself needs a camera, a mount and a sky, so what is checked
    /// here is the handoff, which is the part that decides what the user ends
    /// up imaging. MatchSelection picks the plans and TsPushService writes
    /// exactly what it was handed.
    public class TsModeWiringTests {

        private static MatchedPlan Matched(string id, string projectName, string verdict, string reason = null) {
            var plan = TsTestPlans.Plan(id, projectName: projectName, targetName: "T" + id);
            return new MatchedPlan {
                Id = plan.Id,
                Guid = plan.Guid,
                ProjectName = plan.ProjectName,
                State = plan.State,
                Priority = plan.Priority,
                MinAltitudeDeg = plan.MinAltitudeDeg,
                MeridianWindowMin = plan.MeridianWindowMin,
                TelescopeId = plan.TelescopeId,
                CameraId = plan.CameraId,
                Target = plan.Target,
                FilterGoals = plan.FilterGoals,
                Match = new PlanMatch {
                    Verdict = verdict,
                    Reasons = reason == null ? new List<string>() : new List<string> { reason },
                },
            };
        }

        private static MatchResponse Response() {
            return new MatchResponse {
                Plans = new List<MatchedPlan> {
                    Matched("fits", "Fits", MatchVerdict.Fit),
                    Matched("nogear", "No Gear", MatchVerdict.Unconstrained),
                    Matched("wrong", "Wrong Scope", MatchVerdict.NoFit, "pixel scale is 2.3 times out"),
                    Matched("warn", "Warned", MatchVerdict.FitWithWarnings, "no Ha filter"),
                },
            };
        }

        private static async Task<TsPushResult> PushSelection(
            string dbPath, List<MatchedPlan> selected
        ) {
            var service = new TsPushService(
                new FakeContainerWatch(),
                path => TargetSchedulerDb.Open(dbPath),
                () => TsTestPlans.FrozenNow
            ) { DbPathOverride = dbPath };

            return await service.PushAsync(
                selected.Cast<Plan>().ToList(), TsTestPlans.Gear(), TsTestPlans.ProfileId);
        }

        [Fact]
        public async Task EverythingModeLoadsEveryPlanIncludingTheOnesThatDoNotSuit() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("everything.sqlite"));
                var selected = MatchSelection.SelectForMode(Response(), SyncMode.Everything);

                Assert.Equal(4, selected.Count);
                var result = await PushSelection(path, selected);

                Assert.True(result.Success, result.Failure);
                using (var db = TargetSchedulerDb.Open(path)) {
                    var snap = db.ReadAll(TsTestPlans.ProfileId);
                    Assert.Equal(4, snap.ProjectsById.Count);
                    Assert.Contains("Wrong Scope", snap.ProjectsById.Values.Select(p => p.Name));
                }
            }
        }

        [Fact]
        public async Task OnlyWhatFitsLoadsTheFitAndTheUnconstrainedAndNothingElse() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("fit.sqlite"));
                var selected = MatchSelection.SelectForMode(Response(), SyncMode.OnlyWhatFits);

                // A plan with no gear set is synced in both modes, so the
                // unconstrained one comes along; the warned one does not.
                Assert.Equal(new[] { "Fits", "No Gear" }, selected.Select(p => p.ProjectName).ToArray());

                var result = await PushSelection(path, selected);
                Assert.True(result.Success, result.Failure);

                using (var db = TargetSchedulerDb.Open(path)) {
                    var names = TargetSchedulerDbNames(db);
                    Assert.Equal(new[] { "Fits", "No Gear" }, names);
                }
            }
        }

        [Fact]
        public void TheMisfitLineNamesWhatWasLeftOutAndWhy() {
            var line = MatchSelection.Summarise(Response(), SyncMode.OnlyWhatFits);

            Assert.Contains("2 of 4 plans fit tonight's gear", line);
            Assert.Contains("Left out", line);
            Assert.Contains("pixel scale is 2.3 times out", line);
            Assert.Contains("no Ha filter", line);
        }

        [Fact]
        public void InEverythingModeTheMisfitsAreLoadedAnywayAndStillNamed() {
            var line = MatchSelection.Summarise(Response(), SyncMode.Everything);

            Assert.Contains("4 plans to load", line);
            Assert.Contains("Loaded anyway but not suited to tonight", line);
        }

        [Fact]
        public async Task ThePushLineReportsWhatWentInAndWhatDidNot() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("report.sqlite"));
                var selected = MatchSelection.SelectForMode(Response(), SyncMode.Everything);
                selected.Add(Matched("empty", "Nothing", MatchVerdict.Fit));
                selected.Last().FilterGoals = new Dictionary<string, FilterGoal>();

                var result = await PushSelection(path, selected);

                Assert.True(result.Success, result.Failure);
                var summary = result.Summary();
                Assert.Contains("4 plans loaded into Target Scheduler", summary);
                Assert.Contains("1 left out", summary);
                Assert.Contains("no filter goals", summary);
            }
        }

        private static string[] TargetSchedulerDbNames(TargetSchedulerDb db) {
            return db.ReadAll(TsTestPlans.ProfileId).ProjectsById.Values
                .Select(p => p.Name).OrderBy(n => n).ToArray();
        }
    }
}
