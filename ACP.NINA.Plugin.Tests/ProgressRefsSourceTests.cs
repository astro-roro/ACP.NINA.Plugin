using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using ACP.NINA.Plugin.Services.TargetScheduler;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The mapping from an ACP plan onto Target Scheduler rows, against a real
    /// database rather than a mock of one.
    ///
    /// This is the test for the load bearing decision in Part F: the mapping is
    /// recomputed from the deterministic guids rather than read from anything
    /// the push stored. These push three plans and then check that deriving
    /// finds exactly the rows the push wrote, which is the only thing that
    /// makes recomputation safe to rely on.
    public class ProgressRefsSourceTests {

        private static TsPushService PushServiceFor(string dbPath) {
            return new TsPushService(
                new FakeContainerWatch(),
                path => TargetSchedulerDb.Open(dbPath),
                () => TsTestPlans.FrozenNow
            ) {
                DbPathOverride = dbPath,
            };
        }

        private static TsPlanRefsSource RefsSourceFor(string dbPath, IReadOnlyList<Plan> plans) {
            return new TsPlanRefsSource(
                new TsSnapshotCache(() => dbPath),
                () => TsTestPlans.ProfileId,
                ct => Task.FromResult(plans)
            );
        }

        [Fact]
        public async Task Deriving_finds_the_same_rows_the_push_wrote() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var plans = TsTestPlans.ThreePlans();

                var pushed = await PushServiceFor(path)
                    .PushAsync(plans, TsTestPlans.Gear(), TsTestPlans.ProfileId);
                Assert.True(pushed.Success, pushed.Failure);

                var derived = await RefsSourceFor(path, plans).ReadPlanRefsAsync(CancellationToken.None);

                Assert.Equal(pushed.PlanStates.Count, derived.Count);
                foreach (var state in pushed.PlanStates) {
                    var mine = derived.Single(r => r.AcpPlanId == state.PlanId);

                    // The panel to target id map is the thing the whole join
                    // hangs on, so it is compared entry by entry.
                    var expected = state.Refs["target_ids_by_panel"]
                        .ToObject<Dictionary<string, int>>();
                    Assert.Equal(expected, mine.TargetIdsByPanel);

                    var expectedPlans = state.Refs["exposure_plan_ids"]
                        .ToObject<Dictionary<string, int>>();
                    Assert.Equal(expectedPlans, mine.ExposurePlanIds);
                }
            }
        }

        [Fact]
        public async Task A_mosaic_derives_every_panel_and_anchors_on_one_one() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var plans = TsTestPlans.ThreePlans();
                await PushServiceFor(path).PushAsync(plans, TsTestPlans.Gear(), TsTestPlans.ProfileId);

                var derived = await RefsSourceFor(path, plans).ReadPlanRefsAsync(CancellationToken.None);

                var mosaic = derived.Single(r => r.AcpPlanId == "mosaic");
                Assert.Equal(4, mosaic.TargetIdsByPanel.Count);
                Assert.Equal(
                    mosaic.TargetIdsByPanel["1,1"],
                    ProgressMapper.AnchorTargetId(mosaic)
                );
            }
        }

        [Fact]
        public async Task Every_panel_of_a_mosaic_joins_back_to_its_plan() {
            // This is what makes a TargetStart for panel 3 report the Veil
            // rather than nothing.
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var plans = TsTestPlans.ThreePlans();
                await PushServiceFor(path).PushAsync(plans, TsTestPlans.Gear(), TsTestPlans.ProfileId);

                var derived = await RefsSourceFor(path, plans).ReadPlanRefsAsync(CancellationToken.None);
                var mosaic = derived.Single(r => r.AcpPlanId == "mosaic");

                foreach (var targetId in mosaic.TargetIdsByPanel.Values) {
                    var found = ProgressMapper.FindPlanForTarget(
                        derived, targetId, TsTestPlans.ProfileId);
                    Assert.Equal("mosaic", found?.AcpPlanId);
                }
            }
        }

        [Fact]
        public async Task A_plan_that_was_never_synced_is_left_out() {
            // Nothing in Target Scheduler belongs to it, so there is nothing to
            // report and no reason to POST about it.
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var pushedPlans = TsTestPlans.ThreePlans();
                await PushServiceFor(path).PushAsync(
                    pushedPlans, TsTestPlans.Gear(), TsTestPlans.ProfileId);

                var withExtra = pushedPlans.ToList();
                withExtra.Add(TsTestPlans.Plan(id: "never-synced", targetName: "NGC 6960"));

                var derived = await RefsSourceFor(path, withExtra)
                    .ReadPlanRefsAsync(CancellationToken.None);

                Assert.DoesNotContain(derived, r => r.AcpPlanId == "never-synced");
                Assert.Equal(3, derived.Count);
            }
        }

        [Fact]
        public async Task An_empty_database_maps_nothing_rather_than_throwing() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));

                var derived = await RefsSourceFor(path, TsTestPlans.ThreePlans())
                    .ReadPlanRefsAsync(CancellationToken.None);

                Assert.Empty(derived);
            }
        }

        [Fact]
        public async Task Rows_from_another_profile_are_not_claimed() {
            // Target Scheduler row ids only mean anything inside one profile.
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var plans = TsTestPlans.ThreePlans();
                await PushServiceFor(path).PushAsync(plans, TsTestPlans.Gear(), TsTestPlans.ProfileId);

                var otherProfile = new TsPlanRefsSource(
                    new TsSnapshotCache(() => path),
                    () => "a-different-profile",
                    ct => Task.FromResult((IReadOnlyList<Plan>)plans)
                );

                Assert.Empty(await otherProfile.ReadPlanRefsAsync(CancellationToken.None));
            }
        }

        [Fact]
        public async Task The_derived_rows_feed_a_real_payload() {
            // The whole chain in one test: push, derive the mapping, read the
            // counts back out of the same database, and build what would go to
            // ACP. Nothing here is stubbed except the sink.
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var plans = TsTestPlans.ThreePlans();
                await PushServiceFor(path).PushAsync(plans, TsTestPlans.Gear(), TsTestPlans.ProfileId);

                var cache = new TsSnapshotCache(() => path);
                var refsSource = new TsPlanRefsSource(
                    cache, () => TsTestPlans.ProfileId, ct => Task.FromResult((IReadOnlyList<Plan>)plans));
                var sink = new FakeProgressSink();

                var reporter = new ProgressReporter(
                    broker: null,
                    tsSource: new TsDatabaseProgressSource(cache, () => TsTestPlans.ProfileId),
                    refsSource: refsSource,
                    sink: sink,
                    containerWatch: null,
                    isEnabled: () => true,
                    profileId: () => TsTestPlans.ProfileId
                );

                var sent = await reporter.ReportAllAsync(CancellationToken.None);

                Assert.Equal(3, sent);
                Assert.Equal(3, sink.Sent.Count);
                foreach (var report in sink.Sent) {
                    Assert.Equal("ts", report.Item2.Source);
                    Assert.NotEmpty(report.Item2.Filters);
                    Assert.Null(report.Item2.Force);
                }
            }
        }
    }
}
