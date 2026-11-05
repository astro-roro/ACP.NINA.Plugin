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
using System.Linq;
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

    /// The settings of the focal length solve frame once the ACP options, the
    /// instruction and NINA's plate solve settings have been reconciled.
    public class SolveFrameSpec {
        public double ExposureSeconds { get; set; }
        public int Binning { get; set; }

        /// Null means keep the plate solve setting's filter.
        public string FilterName { get; set; }

        /// -1 means leave it to the camera, which is what NINA's own
        /// CaptureSequence uses for an unset value.
        public int Gain { get; set; } = -1;
        public int Offset { get; set; } = -1;
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

        /// Exposure: the instruction's own value when more than 0, else the ACP
        /// option, else the plate solve setting. Filter and binning: the ACP
        /// option, else the plate solve setting. Gain and offset: the ACP
        /// option, else the camera's defaults.
        internal static SolveFrameSpec ResolveSolveFrame(
            double instructionExposure, double profileExposure, int profileBinning, AcpSettings settings
        ) {
            settings = settings ?? new AcpSettings();
            double exposure;
            if (instructionExposure > 0) exposure = instructionExposure;
            else if (settings.SolveExposureSeconds > 0) exposure = settings.SolveExposureSeconds.Value;
            else exposure = profileExposure;
            return new SolveFrameSpec {
                ExposureSeconds = exposure,
                Binning = settings.SolveBinning > 0
                    ? AcpSettings.ClampBinning(settings.SolveBinning.Value)
                    : Math.Max(1, profileBinning),
                FilterName = string.IsNullOrWhiteSpace(settings.SolveFilter) ? null : settings.SolveFilter.Trim(),
                Gain = settings.SolveGain >= 0 ? settings.SolveGain.Value : -1,
                Offset = settings.SolveOffset >= 0 ? settings.SolveOffset.Value : -1,
            };
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
            // settings, so the solve behaves as NINA's does, except where the
            // ACP options say otherwise: the frame (exposure, filter, binning,
            // gain, offset, see ResolveSolveFrame) and the number of attempts
            // (AcpSettings.SolveAttempts). The instruction's own exposure
            // still wins, because the ACP instruction runs at the start of the
            // night when the profile's centring exposure may not be what you
            // want.
            var acpSettings = AcpSettings.Current ?? AcpSettings.Load();
            var frame = ResolveSolveFrame(
                exposureSeconds, plateSolveSettings.ExposureTime, (int)plateSolveSettings.Binning, acpSettings);
            var binning = frame.Binning;
            var parameter = new CaptureSolverParameter {
                Attempts = AcpSettings.ClampSolveAttempts(acpSettings.SolveAttempts),
                Binning = (short)binning,
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
                frame.ExposureSeconds,
                CaptureSequence.ImageTypes.SNAPSHOT,
                ResolveFilter(profile, plateSolveSettings.Filter, frame.FilterName),
                new BinningMode((short)binning, (short)binning),
                1
            );
            sequence.Gain = frame.Gain;
            sequence.Offset = frame.Offset;
            Logger.Info(
                $"ACP: solve frame {frame.ExposureSeconds:F1} s, bin {binning}, " +
                $"filter {sequence.FilterType?.Name ?? "none"}, " +
                $"gain {(frame.Gain >= 0 ? frame.Gain.ToString() : "camera default")}, " +
                $"offset {(frame.Offset >= 0 ? frame.Offset.ToString() : "camera default")}."
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
                Binning = Math.Max(1, binning),
                SolvedAtUtc = DateTime.UtcNow,
            };
            LastSolve.Set(snapshot);

            Logger.Info(
                $"ACP: solved at {snapshot.PixScaleArcsec:F3} arcsec per pixel, " +
                $"angle {snapshot.PositionAngleDeg:F1} degrees, bin {snapshot.Binning}."
            );
            return snapshot;
        }

        /// The wheel filter with the configured name, or the plate solve
        /// setting's filter when none is configured or the name matches
        /// nothing (logged, because the solve then runs on a filter the
        /// user did not ask for).
        private static FilterInfo ResolveFilter(IProfile profile, FilterInfo fallback, string name) {
            if (name == null) return fallback;
            var filters = profile?.FilterWheelSettings?.FilterWheelFilters;
            var match = filters?.FirstOrDefault(f =>
                string.Equals(f?.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match == null) {
                Logger.Warning(
                    $"ACP: the solve frame filter \"{name}\" is not in the profile's filter wheel, " +
                    "so the plate solve setting's filter is used instead."
                );
                return fallback;
            }
            return match;
        }
    }
}
