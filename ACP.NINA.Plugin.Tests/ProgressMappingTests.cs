using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The join from a Target Scheduler target back to an ACP plan id, through
    /// the refs a sync stamped onto each plan. This is the part that decides
    /// which plan a night's work is credited to, so getting it wrong is worse
    /// than reporting nothing.
    public class ProgressMappingTests {

        private static List<TsPlanRefs> ThreePlans() {
            return new List<TsPlanRefs> {
                Fixtures.SingleTargetPlan("acp-rosette", tsTargetId: 41),
                Fixtures.SingleTargetPlan("acp-horsehead", tsTargetId: 42),
                Fixtures.MosaicPlan("acp-veil", rows: 2, cols: 3, firstTargetId: 50),
            };
        }

        [Fact]
        public void A_single_target_maps_to_its_plan() {
            var found = ProgressMapper.FindPlanForTarget(ThreePlans(), 42, "profile-a");
            Assert.Equal("acp-horsehead", found.AcpPlanId);
        }

        [Fact]
        public void Any_panel_of_a_mosaic_maps_to_the_one_plan() {
            // TS raises TargetStart for whichever panel it is about to shoot.
            // Panel 5 is still news about the Veil plan.
            var plans = ThreePlans();
            foreach (var tid in new[] { 50, 51, 52, 53, 54, 55 }) {
                var found = ProgressMapper.FindPlanForTarget(plans, tid, "profile-a");
                Assert.Equal("acp-veil", found.AcpPlanId);
            }
        }

        [Fact]
        public void A_target_no_plan_claims_maps_to_nothing() {
            // Someone adding their own project in TS is normal, not an error.
            Assert.Null(ProgressMapper.FindPlanForTarget(ThreePlans(), 999, "profile-a"));
        }

        [Fact]
        public void Refs_from_another_profile_are_not_joined_against() {
            // TS row ids are only unique within a profile, so target 41 in
            // profile B is a different target entirely.
            var plans = new List<TsPlanRefs> {
                Fixtures.SingleTargetPlan("acp-rosette", tsTargetId: 41, profileId: "profile-b"),
            };
            Assert.Null(ProgressMapper.FindPlanForTarget(plans, 41, "profile-a"));
        }

        [Fact]
        public void Refs_with_no_profile_recorded_are_accepted() {
            // Blocks written before the profile was stamped. Refusing them
            // would make the first upgrade look like a total failure.
            var plans = new List<TsPlanRefs> {
                Fixtures.SingleTargetPlan("acp-rosette", tsTargetId: 41, profileId: null),
            };
            Assert.Equal("acp-rosette", ProgressMapper.FindPlanForTarget(plans, 41, "profile-a").AcpPlanId);
        }

        [Fact]
        public void The_anchor_of_a_mosaic_is_panel_one_one() {
            // ACP stores a mosaic's goals per panel, not summed, so exactly one
            // panel's counts stand for the plan. Panel 1,1 is the one the
            // Python extension picks, so both paths land on the same number.
            var veil = Fixtures.MosaicPlan("acp-veil", rows: 2, cols: 3, firstTargetId: 50);
            Assert.Equal(50, ProgressMapper.AnchorTargetId(veil));
        }

        [Fact]
        public void The_anchor_of_a_single_target_plan_is_its_only_target() {
            Assert.Equal(41, ProgressMapper.AnchorTargetId(Fixtures.SingleTargetPlan("acp-rosette", 41)));
        }

        [Fact]
        public void A_mosaic_missing_panel_one_one_falls_back_to_the_lowest_panel() {
            // An interrupted sync can leave a gap. The fallback is by row then
            // column so the choice is the same on every run rather than
            // whatever the dictionary happens to iterate first.
            var refs = new TsPlanRefs {
                AcpPlanId = "acp-partial",
                TargetIdsByPanel = new Dictionary<string, int> {
                    { "2,2", 77 }, { "1,3", 66 }, { "2,1", 88 },
                },
            };
            Assert.Equal(66, ProgressMapper.AnchorTargetId(refs));
        }

        [Fact]
        public void A_plan_with_no_targets_has_no_anchor() {
            Assert.Null(ProgressMapper.AnchorTargetId(new TsPlanRefs { AcpPlanId = "acp-empty" }));
            Assert.Null(ProgressMapper.AnchorTargetId(null));
        }

        [Fact]
        public void Reportable_plans_skip_the_ones_with_nothing_to_report_on() {
            var plans = ThreePlans();
            plans.Add(new TsPlanRefs { AcpPlanId = "acp-never-synced" });
            plans.Add(new TsPlanRefs { AcpPlanId = "", TargetIdsByPanel = { { "1,1", 60 } } });

            var reportable = ProgressMapper.ReportablePlans(plans, "profile-a");
            Assert.Equal(
                new[] { "acp-horsehead", "acp-rosette", "acp-veil" },
                reportable.Select(r => r.AcpPlanId).ToArray()
            );
        }

        [Fact]
        public void Reportable_plans_come_back_in_a_stable_order() {
            // The fallback timer walks this list every five minutes. A stable
            // order keeps the NINA log readable across a night.
            var first = ProgressMapper.ReportablePlans(ThreePlans(), "profile-a");
            var second = ProgressMapper.ReportablePlans(ThreePlans(), "profile-a");
            Assert.Equal(first.Select(r => r.AcpPlanId), second.Select(r => r.AcpPlanId));
        }
    }
}
