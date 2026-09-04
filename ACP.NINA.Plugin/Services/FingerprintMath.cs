using System;
using System.Collections.Generic;
using System.Linq;

namespace ACP.NINA.Plugin.Services {

    /// The arithmetic and the decisions behind the gear fingerprint, with no
    /// NINA types anywhere in the signatures.
    ///
    /// Everything a plate solve or a profile write-back turns on lives here so
    /// it can be tested without NINA running, which matters because the rest of
    /// the fingerprint path cannot be exercised without a camera, a mount and a
    /// sky. Callers do the plumbing; this file does the sums.
    public static class FingerprintMath {

        /// Arcseconds in a radian, divided by a thousand. The constant that
        /// turns a pixel size in microns and a focal length in millimetres into
        /// an angle: 206265 arcsec per radian, with the micron to millimetre
        /// factor of 1000 folded in.
        public const double ArcsecPerRadianScaled = 206.265;

        /// The whole point of the fingerprint. NINA's profile focal length is
        /// the field users forget to change when they swap a reducer or a
        /// telescope; the plate solve knows the truth.
        ///
        /// pixelSizeUm is the unbinned sensor pixel size, binning the current
        /// bin factor, and pixScaleArcsec the solver's reported arcseconds per
        /// pixel for the frame it just solved, which is a binned pixel. So the
        /// binning multiplies the effective pixel size.
        ///
        /// Returns null rather than throwing when the inputs cannot produce an
        /// answer, because a failed or absent solve is an ordinary state the
        /// caller falls back from, not an error.
        public static double? SolvedFocalLengthMm(double pixelSizeUm, int binning, double pixScaleArcsec) {
            if (pixelSizeUm <= 0 || binning <= 0 || pixScaleArcsec <= 0) return null;
            if (double.IsNaN(pixScaleArcsec) || double.IsInfinity(pixScaleArcsec)) return null;
            return pixelSizeUm * binning * ArcsecPerRadianScaled / pixScaleArcsec;
        }

        /// The same relation the other way, for the pixel scale to send to ACP
        /// when nothing has been solved and the profile focal length is all
        /// there is.
        public static double? PixelScaleArcsec(double pixelSizeUm, int binning, double focalLengthMm) {
            if (pixelSizeUm <= 0 || binning <= 0 || focalLengthMm <= 0) return null;
            return pixelSizeUm * binning * ArcsecPerRadianScaled / focalLengthMm;
        }

        /// Anything other than a monochrome sensor has a Bayer matrix, which is
        /// what the fingerprint's "colour" flag means.
        ///
        /// Takes the enum's name rather than the enum itself so the decision
        /// stays testable without a reference to NINA's equipment assembly.
        /// An unknown or missing sensor type reads as monochrome, which is the
        /// safer default: a mono fingerprint fails a plan that needs filters
        /// the wheel does not have, rather than quietly matching an OSC plan.
        public static bool IsColourSensor(string sensorTypeName) {
            if (string.IsNullOrWhiteSpace(sensorTypeName)) return false;
            return !string.Equals(sensorTypeName.Trim(), "Monochrome", StringComparison.OrdinalIgnoreCase);
        }

        /// Filter names in slot order. ACP matches a plan's filter goals against
        /// this list, and the order is part of the fingerprint's identity, so a
        /// wheel that reports its slots out of order must not produce a
        /// different fingerprint from one night to the next.
        ///
        /// Blank names are dropped: an unpopulated slot is not a filter.
        public static List<string> FilterNamesInSlotOrder(IEnumerable<Tuple<string, int>> nameAndPosition) {
            if (nameAndPosition == null) return new List<string>();
            return nameAndPosition
                .Where(f => f != null && !string.IsNullOrWhiteSpace(f.Item1))
                .OrderBy(f => f.Item2)
                .Select(f => f.Item1.Trim())
                .ToList();
        }

        /// The 5 percent rule from the spec. A RedCat solving at 248 mm against
        /// a profile of 250 mm is left alone; a forgotten reducer at 20 to 30
        /// percent out, or a Hyperstar at several times the native length, is
        /// corrected.
        ///
        /// The comparison is relative to the profile value, since that is the
        /// number the user typed and the one the threshold is a tolerance on.
        /// A profile focal length of zero or less means the profile has nothing
        /// worth keeping, so any solve wins.
        public const double WriteBackThreshold = 0.05;

        public static bool ShouldWriteBackFocalLength(
            double profileFocalLengthMm,
            double solvedFocalLengthMm,
            double threshold = WriteBackThreshold
        ) {
            if (solvedFocalLengthMm <= 0) return false;
            if (double.IsNaN(solvedFocalLengthMm) || double.IsInfinity(solvedFocalLengthMm)) return false;
            if (profileFocalLengthMm <= 0) return true;
            var difference = Math.Abs(solvedFocalLengthMm - profileFocalLengthMm) / profileFocalLengthMm;
            return difference > threshold;
        }

        /// Focal ratio for the write-back, when ACP knows the aperture of the
        /// telescope it matched. Null when it does not, and nothing is written.
        public static double? FocalRatio(double focalLengthMm, double? apertureMm) {
            if (apertureMm == null || apertureMm.Value <= 0 || focalLengthMm <= 0) return null;
            return focalLengthMm / apertureMm.Value;
        }
    }
}
