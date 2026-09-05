using System;

namespace ACP.NINA.Plugin.Services {

    /// What the plugin keeps from a plate solve. Deliberately not NINA's
    /// PlateSolveResult: the fingerprint builder and the dock's one hour reuse
    /// rule both want a small value with a timestamp on it, and keeping NINA's
    /// type out of them keeps them testable.
    public class SolveSnapshot {

        /// Arcseconds per pixel of the frame that was solved, so at the binning
        /// the frame was taken at.
        public double PixScaleArcsec { get; set; }

        /// The solved camera angle.
        public double PositionAngleDeg { get; set; }

        /// The bin factor of the frame that was solved. Kept with the solve
        /// rather than read from the camera later, because the camera's binning
        /// can change between the solve and the fingerprint being built.
        public int Binning { get; set; } = 1;

        public DateTime SolvedAtUtc { get; set; } = DateTime.UtcNow;

        public TimeSpan Age => DateTime.UtcNow - SolvedAtUtc;

        /// The dock's Sync for tonight button reuses a solve this fresh rather
        /// than taking another one. Decision 1 in the v3 spec.
        public static readonly TimeSpan ReuseWindow = TimeSpan.FromHours(1);

        public bool IsFreshEnoughToReuse => Age < ReuseWindow && PixScaleArcsec > 0;
    }
}
