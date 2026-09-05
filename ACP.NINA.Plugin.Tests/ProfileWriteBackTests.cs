using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using System.Collections.Generic;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The aperture choice behind the focal ratio write-back, and the wording
    /// the user sees. The write itself needs a live NINA profile, so it is on
    /// tomorrow's checklist rather than in here.
    public class ProfileWriteBackTests {

        private static List<Telescope> Fleet() {
            return new List<Telescope> {
                new Telescope { Id = "t1", Name = "RedCat 51", FocalLengthMm = 250, ApertureMm = 51 },
                new Telescope { Id = "t2", Name = "Esprit 100", FocalLengthMm = 550, ApertureMm = 100 },
                new Telescope { Id = "t3", Name = "EdgeHD 8", FocalLengthMm = 2032, ApertureMm = 203 },
                new Telescope { Id = "t4", Name = "Unmeasured lens", FocalLengthMm = 540, ApertureMm = null },
            };
        }

        [Fact]
        public void The_nearest_telescope_by_focal_length_lends_its_aperture() {
            // 540 solved is 1.8 percent from the Esprit's 550, and the only
            // other candidate in range has no aperture recorded.
            Assert.Equal(100, ProfileWriteBack.ChooseApertureMm(540, Fleet()));
        }

        [Fact]
        public void A_telescope_with_no_aperture_recorded_is_skipped_rather_than_winning() {
            // The unmeasured lens is an exact focal length match at 540 but has
            // no aperture, so it cannot lend one.
            var onlyUnmeasured = new List<Telescope> {
                new Telescope { Id = "t4", Name = "Unmeasured lens", FocalLengthMm = 540, ApertureMm = null },
            };
            Assert.Null(ProfileWriteBack.ChooseApertureMm(540, onlyUnmeasured));
        }

        [Fact]
        public void Nothing_within_fifteen_percent_means_no_focal_ratio_is_written() {
            // 1200 mm is nowhere near anything in the fleet. Better to leave
            // the profile's focal ratio alone than invent one from the wrong
            // telescope.
            Assert.Null(ProfileWriteBack.ChooseApertureMm(1200, Fleet()));
        }

        [Fact]
        public void Exactly_fifteen_percent_out_still_counts() {
            // 250 profile against a solve at 287.5 is 15 percent, the edge of
            // the tolerance ACP uses for pixel scale.
            var scopes = new List<Telescope> {
                new Telescope { Id = "t1", FocalLengthMm = 287.5, ApertureMm = 51 },
            };
            Assert.Equal(51, ProfileWriteBack.ChooseApertureMm(250, scopes));
        }

        [Fact]
        public void An_empty_or_missing_fleet_is_not_an_error() {
            Assert.Null(ProfileWriteBack.ChooseApertureMm(540, null));
            Assert.Null(ProfileWriteBack.ChooseApertureMm(540, new List<Telescope>()));
        }

        // -- The one line everything reports ----------------------------------

        [Fact]
        public void A_write_reads_as_old_value_to_new_value() {
            var result = new WriteBackResult {
                Written = true,
                OldFocalLengthMm = 250,
                NewFocalLengthMm = 540.4,
                OldFocalRatio = 4.9,
                NewFocalRatio = 5.4,
            };
            Assert.Equal(
                "Profile focal length 250.0 mm to 540.4 mm, focal ratio f/4.9 to f/5.4.",
                result.Summary
            );
        }

        [Fact]
        public void A_write_with_no_aperture_reports_the_focal_length_alone() {
            var result = new WriteBackResult {
                Written = true,
                OldFocalLengthMm = 250,
                NewFocalLengthMm = 540.4,
            };
            Assert.Equal("Profile focal length 250.0 mm to 540.4 mm.", result.Summary);
        }

        [Fact]
        public void A_skipped_write_says_why_it_was_skipped() {
            var result = new WriteBackResult {
                Written = false,
                OldFocalLengthMm = 250,
                NewFocalLengthMm = 250,
                Reason = "the solve says 248.0 mm, within 5 percent of the profile",
            };
            Assert.Equal(
                "Profile focal length left at 250.0 mm (the solve says 248.0 mm, within 5 percent of the profile).",
                result.Summary
            );
        }
    }
}
