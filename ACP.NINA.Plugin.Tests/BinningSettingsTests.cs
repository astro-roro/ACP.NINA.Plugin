using ACP.NINA.Plugin.Services;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using System;
using System.IO;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// Two bin factors matter and they must not be mixed up: the solve
    /// frame's turns the solved pixel scale into a focal length, and the
    /// imaging one sets the pixel scale plans are judged against. The camera's
    /// current binning is neither.
    public class BinningSettingsTests : IDisposable {

        private readonly string root = Path.Combine(
            Path.GetTempPath(), "acp-binning-test-" + Guid.NewGuid().ToString("N"));

        public BinningSettingsTests() {
            Directory.CreateDirectory(root);
        }

        public void Dispose() {
            try { Directory.Delete(root, true); } catch { }
        }

        // 3.76 um pixels at bin 2 and 0.5 arcsec per pixel: the focal length
        // is 206.265 * 3.76 * 2 / 0.5, about 3102 mm.
        private static SolveSnapshot SolveAtBin(int bin, double pixScale) =>
            new SolveSnapshot { Binning = bin, PixScaleArcsec = pixScale };

        [Fact]
        public void NoSolveJudgesPlansAtTheImagingBinning() {
            var result = GearFingerprintService.ResolveBinning(3.76, 1000, null, 1);

            Assert.Equal(1, result.ImagingBinning);
            Assert.Null(result.SolveBinning);
            Assert.Null(result.SolvedFocalLengthMm);
            Assert.Equal(FingerprintMath.PixelScaleArcsec(3.76, 1, 1000), result.PixelScaleArcsec);
        }

        [Fact]
        public void ASolveUsesItsOwnBinningForFocalLengthAndImagingBinningForTheJudgedScale() {
            var solve = SolveAtBin(2, 0.5);

            var result = GearFingerprintService.ResolveBinning(3.76, 1000, solve, 1);

            Assert.Equal(FingerprintMath.SolvedFocalLengthMm(3.76, 2, 0.5), result.SolvedFocalLengthMm);
            Assert.Equal(2, result.SolveBinning);
            Assert.Equal(1, result.ImagingBinning);
            // Half the solved scale, because the imaging pixels are half as big.
            Assert.Equal(0.25, result.PixelScaleArcsec.Value, 6);
        }

        [Fact]
        public void TheJudgedScaleAgreesWithTheSolvedFocalLength() {
            var solve = SolveAtBin(2, 0.5);

            var result = GearFingerprintService.ResolveBinning(3.76, 1000, solve, 1);

            var fromFocalLength = FingerprintMath.PixelScaleArcsec(3.76, 1, result.SolvedFocalLengthMm.Value);
            Assert.Equal(fromFocalLength.Value, result.PixelScaleArcsec.Value, 6);
        }

        [Fact]
        public void MatchingBinningsLeaveTheMeasuredScaleAlone() {
            var result = GearFingerprintService.ResolveBinning(3.76, 1000, SolveAtBin(1, 0.7), 1);

            Assert.Equal(0.7, result.PixelScaleArcsec.Value, 9);
        }

        [Fact]
        public void ACameraAtBin2WithImagingBinning1GivesBin1() {
            // The camera reports the bin of the last test frame. Build must
            // not read it.
            var camera = (ICameraMediator)FakeProxy.Make(typeof(ICameraMediator), m => {
                if (m.Name != "GetInfo") return null;
                var info = Activator.CreateInstance(m.ReturnType);
                var binX = m.ReturnType.GetProperty("BinX");
                binX.SetValue(info, Convert.ChangeType(2, binX.PropertyType));
                var pixel = m.ReturnType.GetProperty("PixelSize");
                pixel.SetValue(info, Convert.ChangeType(3.76, pixel.PropertyType));
                return info;
            });
            var service = new GearFingerprintService(
                (IProfileService)FakeProxy.Make(typeof(IProfileService), m => null),
                camera,
                (IFilterWheelMediator)FakeProxy.Make(typeof(IFilterWheelMediator), m => null),
                (ITelescopeMediator)FakeProxy.Make(typeof(ITelescopeMediator), m => null),
                () => 1);

            var fingerprint = service.Build(null);

            Assert.Equal(1, fingerprint.Camera.Binning);
            Assert.Null(fingerprint.SolveBinning);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        [InlineData(4, 4)]
        [InlineData(20, 8)]
        public void ImagingBinningIsClamped(int given, int expected) {
            Assert.Equal(expected, GearFingerprintService.ResolveBinning(3.76, 1000, null, given).ImagingBinning);
        }

        // -- settings ----------------------------------------------------------

        [Fact]
        public void AnOldSettingsFileWithoutTheNewKeysLoadsWithDefaults() {
            var path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, "{\"ServerUrl\":\"http://10.0.0.9:5555\",\"SolveAttempts\":5}");

            var loaded = AcpSettings.LoadFromDisk(path, null);

            Assert.Equal("http://10.0.0.9:5555", loaded.ServerUrl);
            Assert.Equal(5, loaded.SolveAttempts);
            Assert.Equal(1, loaded.ImagingBinning);
            Assert.Null(loaded.SolveExposureSeconds);
            Assert.Null(loaded.SolveFilter);
            Assert.Null(loaded.SolveBinning);
            Assert.Null(loaded.SolveGain);
            Assert.Null(loaded.SolveOffset);
        }

        [Fact]
        public void TheNewKeysRoundTripThroughTheFile() {
            var path = Path.Combine(root, "settings.json");
            File.WriteAllText(path,
                "{\"ImagingBinning\":2,\"SolveExposureSeconds\":4.5,\"SolveFilter\":\"L\"," +
                "\"SolveBinning\":3,\"SolveGain\":100,\"SolveOffset\":30}");

            var loaded = AcpSettings.LoadFromDisk(path, null);

            Assert.Equal(2, loaded.ImagingBinning);
            Assert.Equal(4.5, loaded.SolveExposureSeconds);
            Assert.Equal("L", loaded.SolveFilter);
            Assert.Equal(3, loaded.SolveBinning);
            Assert.Equal(100, loaded.SolveGain);
            Assert.Equal(30, loaded.SolveOffset);
        }

        // -- the solve frame ---------------------------------------------------

        [Fact]
        public void UnsetSolveSettingsFallBackToThePlateSolveSettings() {
            var frame = AcpPlateSolver.ResolveSolveFrame(0, 3.0, 2, new AcpSettings());

            Assert.Equal(3.0, frame.ExposureSeconds);
            Assert.Equal(2, frame.Binning);
            Assert.Null(frame.FilterName);
            Assert.Equal(-1, frame.Gain);
            Assert.Equal(-1, frame.Offset);
        }

        [Fact]
        public void SetSolveSettingsAreUsed() {
            var settings = new AcpSettings {
                SolveExposureSeconds = 5,
                SolveFilter = "L",
                SolveBinning = 1,
                SolveGain = 120,
                SolveOffset = 0,
            };

            var frame = AcpPlateSolver.ResolveSolveFrame(0, 3.0, 2, settings);

            Assert.Equal(5, frame.ExposureSeconds);
            Assert.Equal(1, frame.Binning);
            Assert.Equal("L", frame.FilterName);
            Assert.Equal(120, frame.Gain);
            // Zero is a real offset, not unset.
            Assert.Equal(0, frame.Offset);
        }

        [Fact]
        public void TheInstructionsOwnExposureStillWins() {
            var settings = new AcpSettings { SolveExposureSeconds = 5 };

            Assert.Equal(8, AcpPlateSolver.ResolveSolveFrame(8, 3.0, 2, settings).ExposureSeconds);
            Assert.Equal(5, AcpPlateSolver.ResolveSolveFrame(0, 3.0, 2, settings).ExposureSeconds);
        }

        [Fact]
        public void ABlankFilterNameIsUnset() {
            var frame = AcpPlateSolver.ResolveSolveFrame(0, 3.0, 1, new AcpSettings { SolveFilter = "  " });

            Assert.Null(frame.FilterName);
        }
    }
}
