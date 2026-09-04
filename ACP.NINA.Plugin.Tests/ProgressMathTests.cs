using ACP.NINA.Plugin.Services;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The count to hours conversion. These numbers are the whole point of
    /// Part F, so they are pinned against the Python extension's rules rather
    /// than against whatever the C# happens to do.
    public class ProgressMathTests {

        [Fact]
        public void Eighteen_three_hundred_second_subs_is_one_and_a_half_hours() {
            // The example in the spec and in docs/api.md.
            var row = Fixtures.Row("Ha", acquired: 18, exposureSeconds: 300);
            Assert.Equal(1.5, ProgressMath.AcquiredHours(row), 4);
        }

        [Fact]
        public void Hours_are_rounded_to_four_places_like_ACP_stores_them() {
            // 7 subs at 47 s is 0.091388... hours.
            var row = Fixtures.Row("L", acquired: 7, exposureSeconds: 47);
            Assert.Equal(0.0914, ProgressMath.AcquiredHours(row), 4);
        }

        [Fact]
        public void With_the_grader_off_every_captured_sub_counts() {
            var row = Fixtures.Row("Ha", acquired: 20, accepted: 12, graderEnabled: false, exposureSeconds: 300);
            Assert.Equal(20, ProgressMath.GoodCount(row));
            Assert.Equal(20 * 300 / 3600.0, ProgressMath.AcquiredHours(row), 4);
        }

        [Fact]
        public void With_the_grader_on_only_accepted_subs_count() {
            // The whole reason the grader flag has to be read: counting the
            // eight rejected frames would tell ACP the target is further along
            // than it is, and ACP never lowers a number once it has it.
            var row = Fixtures.Row("Ha", acquired: 20, accepted: 12, graderEnabled: true, exposureSeconds: 300);
            Assert.Equal(12, ProgressMath.GoodCount(row));
            Assert.Equal(1.0, ProgressMath.AcquiredHours(row), 4);
        }

        [Fact]
        public void A_row_with_no_exposure_falls_back_to_its_template_default() {
            var row = Fixtures.Row("OIII", acquired: 10, exposureSeconds: 0, templateDefaultSeconds: 600);
            Assert.Equal(600.0, ProgressMath.SubExposureSeconds(row));
            Assert.Equal(10 * 600 / 3600.0, ProgressMath.AcquiredHours(row), 4);
        }

        [Fact]
        public void An_exposure_on_the_row_beats_the_template_default() {
            var row = Fixtures.Row("OIII", acquired: 10, exposureSeconds: 120, templateDefaultSeconds: 600);
            Assert.Equal(120.0, ProgressMath.SubExposureSeconds(row));
        }

        [Fact]
        public void A_row_with_no_usable_exposure_is_left_out_rather_than_sent_as_zero() {
            // Sending zero would be a claim that nothing has been shot. Saying
            // nothing about the filter is the truth.
            var rows = new[] {
                Fixtures.Row("Ha", acquired: 18, exposureSeconds: 300),
                Fixtures.Row("SII", acquired: 4, exposureSeconds: 0, templateDefaultSeconds: 0),
            };
            var filters = ProgressMath.BuildFilters(rows);
            Assert.True(filters.ContainsKey("Ha"));
            Assert.False(filters.ContainsKey("SII"));
        }

        [Fact]
        public void Nothing_acquired_yet_still_reports_the_filter_at_zero_hours() {
            // A target that started but has no subs yet is a real state, and
            // ACP takes the max, so zero is harmless and keeps the filter list
            // honest about what is being shot tonight.
            var filters = ProgressMath.BuildFilters(new[] {
                Fixtures.Row("Ha", acquired: 0, exposureSeconds: 300),
            });
            Assert.Equal(0.0, filters["Ha"].AcquiredHours);
            Assert.Equal(0, filters["Ha"].AcquiredCount);
        }

        [Fact]
        public void Several_filters_on_one_target_become_several_entries() {
            var filters = ProgressMath.BuildFilters(new[] {
                Fixtures.Row("Ha", acquired: 18, exposureSeconds: 300),
                Fixtures.Row("OIII", acquired: 12, exposureSeconds: 300),
                Fixtures.Row("SII", acquired: 6, exposureSeconds: 600),
            });
            Assert.Equal(3, filters.Count);
            Assert.Equal(1.5, filters["Ha"].AcquiredHours, 4);
            Assert.Equal(18, filters["Ha"].AcquiredCount);
            Assert.Equal(1.0, filters["OIII"].AcquiredHours, 4);
            Assert.Equal(1.0, filters["SII"].AcquiredHours, 4);
        }

        [Fact]
        public void Two_rows_for_one_filter_are_summed_rather_than_one_winning() {
            var filters = ProgressMath.BuildFilters(new[] {
                Fixtures.Row("Ha", acquired: 18, exposureSeconds: 300, exposurePlanId: 1),
                Fixtures.Row("Ha", acquired: 6, exposureSeconds: 600, exposurePlanId: 2),
            });
            Assert.Single(filters);
            Assert.Equal(24, filters["Ha"].AcquiredCount);
            Assert.Equal(1.5 + 1.0, filters["Ha"].AcquiredHours, 4);
        }

        [Fact]
        public void Filter_names_are_sent_as_TS_spells_them() {
            // ACP canonicalises its end, so "Antlia Ha" lands on the Ha goal
            // without the plugin carrying the alias table.
            var filters = ProgressMath.BuildFilters(new[] {
                Fixtures.Row("Antlia Ha", acquired: 18, exposureSeconds: 300),
            });
            Assert.Equal("Antlia Ha", filters.Keys.Single());
        }

        [Fact]
        public void Null_and_nameless_rows_are_ignored_rather_than_throwing() {
            var filters = ProgressMath.BuildFilters(new[] {
                null,
                Fixtures.Row("  ", acquired: 5, exposureSeconds: 300),
                Fixtures.Row("L", acquired: 12, exposureSeconds: 300),
            });
            Assert.Single(filters);
            Assert.True(filters.ContainsKey("L"));
        }

        [Fact]
        public void A_negative_count_is_treated_as_none() {
            var row = Fixtures.Row("L", acquired: -3, exposureSeconds: 300);
            Assert.Equal(0, ProgressMath.GoodCount(row));
            Assert.Equal(0.0, ProgressMath.AcquiredHours(row));
        }
    }
}
