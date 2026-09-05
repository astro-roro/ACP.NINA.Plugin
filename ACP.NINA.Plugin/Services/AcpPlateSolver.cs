using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services {

    /// Captures a frame and solves it, the same way NINA's own Center and
    /// Solve and sync instructions do, and hands back the small snapshot the
    /// fingerprint wants.
    public interface IAcpPlateSolver {

        /// Capture one frame at the given exposure and solve it. Returns null
        /// when the solve failed, which is an ordinary outcome on a cloudy
        /// night and not an exception.
        Task<SolveSnapshot> SolveAsync(
            double exposureSeconds, IProgress<ApplicationStatus> progress, CancellationToken token
        );
    }

    /// Where the most recent solve lives, so the dock button can reuse a solve
    /// the sequencer instruction took an hour ago rather than taking another
    /// one. Deliberately process wide and deliberately not persisted: the spec
    /// says the fingerprint is never cached across sessions, and a solve from
    /// last night tells you nothing about tonight's rig.
    public static class LastSolve {

        private static readonly object gate = new object();
        private static SolveSnapshot current;

        public static SolveSnapshot Get() {
            lock (gate) { return current; }
        }

        public static void Set(SolveSnapshot snapshot) {
            lock (gate) { current = snapshot; }
        }

        /// The solve to reuse, or null when there is nothing fresh enough.
        /// One hour, per decision 1 in the v3 spec.
        public static SolveSnapshot GetIfFresh() {
            var snapshot = Get();
            return snapshot != null && snapshot.IsFreshEnoughToReuse ? snapshot : null;
        }
    }

    [Export(typeof(IAcpPlateSolver))]
    public class AcpPlateSolver : IAcpPlateSolver {

        private readonly IProfileService profileService;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly ICameraMediator cameraMediator;
        private readonly IImagingMediator imagingMediator;
        private readonly IFilterWheelMediator filterWheelMediator;
        private readonly IPlateSolverFactory plateSolverFactory;

        [ImportingConstructor]
        public AcpPlateSolver(
            IProfileService profileService,
            ITelescopeMediator telescopeMediator,
            ICameraMediator cameraMediator,
            IImagingMediator imagingMediator,
            IFilterWheelMediator filterWheelMediator,
            IPlateSolverFactory plateSolverFactory
        ) {
            this.profileService = profileService;
            this.telescopeMediator = telescopeMediator;
            this.cameraMediator = cameraMediator;
            this.imagingMediator = imagingMediator;
            this.filterWheelMediator = filterWheelMediator;
            this.plateSolverFactory = plateSolverFactory;
        }

        public async Task<SolveSnapshot> SolveAsync(
            double exposureSeconds, IProgress<ApplicationStatus> progress, CancellationToken token
        ) {
            // NINA throws CameraConnectionLostException from deep inside the
            // capture when there is no camera, and that name is all the user
            // would see. Say the plain thing first.
            if (cameraMediator?.GetInfo()?.Connected != true) {
                throw new InvalidOperationException(
                    "No camera is connected. Connect the camera, then run Sync for tonight."
                );
            }
            var profile = profileService.ActiveProfile;
            var plateSolveSettings = profile.PlateSolveSettings;

            var plateSolver = plateSolverFactory.GetPlateSolver(plateSolveSettings);
            var blindSolver = plateSolverFactory.GetBlindSolver(plateSolveSettings);
            var solver = plateSolverFactory.GetCaptureSolver(
                plateSolver, blindSolver, imagingMediator, filterWheelMediator
            );

            // Every value here comes from the profile's own plate solve
            // settings, so the solve behaves exactly as NINA's does. The one
            // thing the instruction overrides is the exposure time, because
            // the ACP instruction runs at the start of the night when the
            // profile's centring exposure may not be what you want.
            var binning = plateSolveSettings.Binning;
            var parameter = new CaptureSolverParameter {
                Attempts = plateSolveSettings.NumberOfAttempts,
                Binning = binning,
                Coordinates = telescopeMediator.GetCurrentPosition(),
                DownSampleFactor = plateSolveSettings.DownSampleFactor,
                FocalLength = profile.TelescopeSettings.FocalLength,
                MaxObjects = plateSolveSettings.MaxObjects,
                PixelSize = profile.CameraSettings.PixelSize,
                ReattemptDelay = TimeSpan.FromMinutes(plateSolveSettings.ReattemptDelay),
                Regions = plateSolveSettings.Regions,
                SearchRadius = plateSolveSettings.SearchRadius,
                BlindFailoverEnabled = plateSolveSettings.BlindFailoverEnabled,
            };

            var sequence = new CaptureSequence(
                exposureSeconds > 0 ? exposureSeconds : plateSolveSettings.ExposureTime,
                CaptureSequence.ImageTypes.SNAPSHOT,
                plateSolveSettings.Filter,
                new BinningMode(binning, binning),
                1
            );

            // No window service and no status view model. The ACP instruction
            // reports through the sequencer's own progress, and the dock button
            // through the dock, so a plate solve window popping up over either
            // would be noise.
            var solveProgress = new Progress<PlateSolveProgress>();

            var result = await solver.Solve(sequence, parameter, solveProgress, progress, token);

            if (result == null || !result.Success) {
                Logger.Warning("ACP: the plate solve did not succeed, so no fingerprint focal length is available.");
                return null;
            }

            var snapshot = new SolveSnapshot {
                PixScaleArcsec = result.Pixscale,
                PositionAngleDeg = result.PositionAngle,
                Binning = Math.Max(1, (int)binning),
                SolvedAtUtc = DateTime.UtcNow,
            };
            LastSolve.Set(snapshot);

            Logger.Info(
                $"ACP: solved at {snapshot.PixScaleArcsec:F3} arcsec per pixel, " +
                $"angle {snapshot.PositionAngleDeg:F1} degrees, bin {snapshot.Binning}."
            );
            return snapshot;
        }
    }
}
