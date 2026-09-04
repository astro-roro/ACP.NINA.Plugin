using ACP.NINA.Plugin.Models;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;

namespace ACP.NINA.Plugin.Services {

    /// Builds the gear fingerprint. Behind an interface so the sequencer
    /// instruction and the dock button can be reasoned about without a camera,
    /// a mount and a sky attached.
    public interface IGearFingerprintService {

        /// Build a fingerprint from whatever is connected right now. Pass the
        /// most recent solve to get a solved focal length, or null to fall back
        /// to the profile value.
        Fingerprint Build(SolveSnapshot solve);
    }

    /// The real implementation. Everything here is a read: it never writes to
    /// the profile, which is the point of keeping the write-back in its own
    /// service that only two callers can reach.
    [Export(typeof(IGearFingerprintService))]
    public class GearFingerprintService : IGearFingerprintService {

        private readonly IProfileService profileService;
        private readonly ICameraMediator cameraMediator;
        private readonly IFilterWheelMediator filterWheelMediator;
        private readonly ITelescopeMediator telescopeMediator;

        [ImportingConstructor]
        public GearFingerprintService(
            IProfileService profileService,
            ICameraMediator cameraMediator,
            IFilterWheelMediator filterWheelMediator,
            ITelescopeMediator telescopeMediator
        ) {
            this.profileService = profileService;
            this.cameraMediator = cameraMediator;
            this.filterWheelMediator = filterWheelMediator;
            this.telescopeMediator = telescopeMediator;
        }

        public Fingerprint Build(SolveSnapshot solve) {
            var profile = profileService?.ActiveProfile;
            var camInfo = cameraMediator?.GetInfo();
            var wheelInfo = filterWheelMediator?.GetInfo();
            var scopeInfo = telescopeMediator?.GetInfo();

            var profileFocalLength = profile?.TelescopeSettings?.FocalLength ?? 0;
            var binning = solve?.Binning > 0
                ? solve.Binning
                : Math.Max(1, (int)(camInfo?.BinX ?? 1));
            var pixelSize = camInfo?.PixelSize ?? 0;

            var solvedFocalLength = solve == null
                ? null
                : FingerprintMath.SolvedFocalLengthMm(pixelSize, binning, solve.PixScaleArcsec);

            var focalLength = new FingerprintFocalLength {
                Profile = profileFocalLength,
                Solved = solvedFocalLength,
                Source = solvedFocalLength.HasValue ? "solved" : "profile",
            };

            // With a solve, the pixel scale is measured. Without one it is
            // derived from the profile focal length, which is the number the
            // spec expects to be wrong, so ACP is told the source either way.
            var pixelScale = solve != null && solve.PixScaleArcsec > 0
                ? solve.PixScaleArcsec
                : FingerprintMath.PixelScaleArcsec(pixelSize, binning, profileFocalLength);

            var fingerprint = new Fingerprint {
                Camera = new FingerprintCamera {
                    Name = camInfo?.Name,
                    // Unbinned sensor size, with the bin factor sent alongside.
                    SensorPx = new[] { camInfo?.XSize ?? 0, camInfo?.YSize ?? 0 },
                    PixelSizeUm = pixelSize,
                    Colour = FingerprintMath.IsColourSensor(camInfo?.SensorType.ToString()),
                    Binning = binning,
                },
                Filters = ReadFilters(profile, wheelInfo),
                Mount = new FingerprintMount { Name = scopeInfo?.Name },
                Site = ReadSite(profile, scopeInfo),
                FocalLengthMm = focalLength,
                PixelScaleArcsec = pixelScale,
                RotationDeg = solve?.PositionAngleDeg,
                ProfileName = profile?.Name,
                NinaVersion = ReadNinaVersion(),
            };

            Logger.Info(
                $"ACP: fingerprint built - camera {fingerprint.Camera.Name} " +
                $"{fingerprint.Camera.SensorPx[0]}x{fingerprint.Camera.SensorPx[1]} " +
                $"at {fingerprint.Camera.PixelSizeUm} um bin {binning}, " +
                $"{(fingerprint.Camera.Colour ? "colour" : "mono")}, " +
                $"filters [{string.Join(", ", fingerprint.Filters)}], " +
                $"mount {fingerprint.Mount.Name}, " +
                $"focal length {focalLength.Effective:F1} mm from {focalLength.Source}"
            );

            return fingerprint;
        }

        /// Slot names come from the profile rather than the wheel, because the
        /// wheel only reports the filter it currently has in the beam. An
        /// empty list is a real answer: no wheel connected, which ACP reads as
        /// OSC when the camera is a colour one.
        private static List<string> ReadFilters(IProfile profile, FilterWheelInfo wheelInfo) {
            if (wheelInfo == null || !wheelInfo.Connected) return new List<string>();
            var filters = profile?.FilterWheelSettings?.FilterWheelFilters;
            if (filters == null) return new List<string>();
            return FingerprintMath.FilterNamesInSlotOrder(
                filters.Select(f => Tuple.Create(f?.Name, (int)(f?.Position ?? 0)))
            );
        }

        /// A connected mount is the better source, since it knows where it
        /// actually is. Fall back to the profile when nothing is connected or
        /// the mount is reporting the null island, which is what an
        /// uninitialised mount looks like.
        private static FingerprintSite ReadSite(IProfile profile, TelescopeInfo scopeInfo) {
            var astrometry = profile?.AstrometrySettings;
            var site = new FingerprintSite {
                Lat = astrometry?.Latitude ?? 0,
                Lon = astrometry?.Longitude ?? 0,
                ElevM = astrometry?.Elevation ?? 0,
            };
            if (scopeInfo != null && scopeInfo.Connected) {
                var lat = scopeInfo.SiteLatitude;
                var lon = scopeInfo.SiteLongitude;
                if (Math.Abs(lat) > 0.0001 || Math.Abs(lon) > 0.0001) {
                    site.Lat = lat;
                    site.Lon = lon;
                    site.ElevM = scopeInfo.SiteElevation;
                }
            }
            return site;
        }

        /// NINA is the entry assembly, so its version is the one to report.
        /// Read reflectively rather than through a constant, which would go
        /// stale the moment NINA updates.
        private static string ReadNinaVersion() {
            try {
                return Assembly.GetEntryAssembly()?.GetName()?.Version?.ToString() ?? "unknown";
            } catch (Exception) {
                return "unknown";
            }
        }
    }
}
