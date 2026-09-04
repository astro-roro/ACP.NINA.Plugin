using ACP.NINA.Plugin.Services;
using System;
using System.Collections.Generic;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The four decisions the v3 spec turns on, tested without NINA.
    public class FingerprintMathTests {

        // -- Focal length from pixel scale ------------------------------------

        [Fact]
        public void SolvedFocalLength_matches_the_worked_example_in_the_spec() {
            // The spec's own fingerprint: a QHY268M at 3.76 um solving at
            // 1.436 arcsec per pixel unbinned gives about 540 mm, which is the
            // travel rig with its reducer on.
            var mm = FingerprintMath.SolvedFocalLengthMm(3.76, 1, 1.436);
            Assert.NotNull(mm);
            Assert.Equal(540.1, mm.Value, 1);
        }

        [Fact]
        public void SolvedFocalLength_scales_with_the_bin_factor() {
            // A frame solved at bin 2 reports twice the arcseconds per pixel
            // for the same optics, so the bin factor has to come back in or the
            // focal length lands at half its real value.
            var unbinned = FingerprintMath.SolvedFocalLengthMm(3.76, 1, 1.436);
            var binned = FingerprintMath.SolvedFocalLengthMm(3.76, 2, 2.872);
            Assert.Equal(unbinned.Value, binned.Value, 6);
        }

        [Fact]
        public void SolvedFocalLength_round_trips_through_the_pixel_scale() {
            var scale = FingerprintMath.PixelScaleArcsec(3.76, 1, 540.0);
            var mm = FingerprintMath.SolvedFocalLengthMm(3.76, 1, scale.Value);
            Assert.Equal(540.0, mm.Value, 6);
        }

        [Theory]
        [InlineData(0, 1, 1.436)]
        [InlineData(3.76, 0, 1.436)]
        [InlineData(3.76, 1, 0)]
        [InlineData(3.76, 1, -1)]
        [InlineData(3.76, 1, double.NaN)]
        public void SolvedFocalLength_is_null_when_the_inputs_cannot_produce_an_answer(
            double pixelSize, int binning, double pixScale
        ) {
            // A failed solve is an ordinary state, not an exception. The caller
            // falls back to the profile value.
            Assert.Null(FingerprintMath.SolvedFocalLengthMm(pixelSize, binning, pixScale));
        }

        // -- Mono versus colour -----------------------------------------------

        [Fact]
        public void Monochrome_sensor_type_is_not_colour() {
            Assert.False(FingerprintMath.IsColourSensor("Monochrome"));
        }

        [Theory]
        [InlineData("RGGB")]
        [InlineData("BGGR")]
        [InlineData("GRBG")]
        [InlineData("GBRG")]
        [InlineData("LRGB")]
        public void Any_bayer_pattern_is_colour(string sensorType) {
            Assert.True(FingerprintMath.IsColourSensor(sensorType));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void An_unknown_sensor_type_reads_as_mono(string sensorType) {
            // The safer default. A mono fingerprint fails a plan that wants
            // filters the wheel does not have, rather than quietly matching an
            // OSC plan it cannot shoot.
            Assert.False(FingerprintMath.IsColourSensor(sensorType));
        }

        // -- Filter list order -------------------------------------------------

        [Fact]
        public void Filters_come_back_in_slot_order_whatever_order_they_arrive_in() {
            var wheel = new List<Tuple<string, int>> {
                Tuple.Create("Ha", 4),
                Tuple.Create("L", 0),
                Tuple.Create("B", 3),
                Tuple.Create("R", 1),
                Tuple.Create("G", 2),
            };
            Assert.Equal(
                new[] { "L", "R", "G", "B", "Ha" },
                FingerprintMath.FilterNamesInSlotOrder(wheel)
            );
        }

        [Fact]
        public void Empty_slots_are_dropped_and_names_are_trimmed() {
            var wheel = new List<Tuple<string, int>> {
                Tuple.Create(" L ", 0),
                Tuple.Create("", 1),
                Tuple.Create((string)null, 2),
                Tuple.Create("   ", 3),
                Tuple.Create("SII", 4),
            };
            Assert.Equal(new[] { "L", "SII" }, FingerprintMath.FilterNamesInSlotOrder(wheel));
        }

        [Fact]
        public void No_wheel_gives_an_empty_list_rather_than_null() {
            // ACP reads an empty list plus a colour camera as OSC, so the empty
            // list has to actually arrive.
            Assert.Empty(FingerprintMath.FilterNamesInSlotOrder(null));
        }

        // -- The 5 percent write-back threshold --------------------------------

        [Fact]
        public void A_redcat_solving_two_millimetres_short_is_left_alone() {
            // 248 against a profile of 250 is 0.8 percent out. The spec names
            // this case: do not touch it.
            Assert.False(FingerprintMath.ShouldWriteBackFocalLength(250.0, 248.0));
        }

        [Fact]
        public void A_forgotten_reducer_is_corrected() {
            // 250 in the profile, 540 on the sky. Well past the threshold.
            Assert.True(FingerprintMath.ShouldWriteBackFocalLength(250.0, 540.4));
        }

        [Fact]
        public void Exactly_five_percent_out_is_left_alone() {
            // The spec says more than 5 percent, so the boundary itself does
            // not trigger a write.
            Assert.False(FingerprintMath.ShouldWriteBackFocalLength(1000.0, 1050.0));
        }

        [Fact]
        public void Just_past_five_percent_is_corrected() {
            Assert.True(FingerprintMath.ShouldWriteBackFocalLength(1000.0, 1050.1));
        }

        [Fact]
        public void The_threshold_is_symmetric() {
            // A Hyperstar is shorter than the profile, not longer, and it still
            // needs correcting.
            Assert.True(FingerprintMath.ShouldWriteBackFocalLength(2032.0, 675.0));
        }

        [Fact]
        public void An_empty_profile_focal_length_takes_any_solve() {
            // Nothing worth keeping, so the sky wins.
            Assert.True(FingerprintMath.ShouldWriteBackFocalLength(0.0, 540.0));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        public void A_solve_that_produced_nothing_never_writes_back(double solved) {
            Assert.False(FingerprintMath.ShouldWriteBackFocalLength(250.0, solved));
        }

        // -- Focal ratio -------------------------------------------------------

        [Fact]
        public void Focal_ratio_is_focal_length_over_aperture() {
            var f = FingerprintMath.FocalRatio(540.0, 108.0);
            Assert.NotNull(f);
            Assert.Equal(5.0, f.Value, 6);
        }

        [Fact]
        public void No_aperture_means_no_focal_ratio_and_nothing_is_written() {
            Assert.Null(FingerprintMath.FocalRatio(540.0, null));
            Assert.Null(FingerprintMath.FocalRatio(540.0, 0));
        }
    }
}
