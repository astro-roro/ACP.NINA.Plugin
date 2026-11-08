using ACP.NINA.Plugin.Services.EquipmentRecovery;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The decision logic behind equipment recovery, driven tick by tick with
    /// a fake clock. No NINA, no USB, no waiting.
    public class EquipmentRecoveryTests {

        // -- Fakes --------------------------------------------------------------

        private sealed class FakeDevice : IRecoveryDevice {
            public FakeDevice(string kind) { Kind = kind; }
            public string Kind { get; }
            public bool Connected = true;
            /// When false, ReconnectAsync leaves the device disconnected.
            public bool UsbPresent = true;
            public bool ThrowOnReconnect;
            public int Reconnects;
            public double? Position;
            public double? PositionAfterReconnect;

            public bool IsConnected() => Connected;
            public double? ReadPosition() => Position;

            public Task<bool> ReconnectAsync(CancellationToken ct) {
                Reconnects++;
                if (ThrowOnReconnect) throw new InvalidOperationException("driver said no");
                Connected = UsbPresent;
                if (Connected && PositionAfterReconnect.HasValue) Position = PositionAfterReconnect;
                return Task.FromResult(Connected);
            }
        }

        private sealed class FakeCooler : IRecoveryCooler {
            public double? Active = -10;
            public bool ThrowOnRead;
            public double? Profile = -5;
            public List<double> Restored = new List<double>();

            public double? ActiveSetPoint() {
                if (ThrowOnRead) throw new InvalidOperationException("no reading");
                return Active;
            }
            public double? Temperature = -10;
            public double? Power = 40;
            public (double? Temperature, double? CoolerPower) ReadThermals() => (Temperature, Power);
            public double? ProfileSetPoint() => Profile;
            public Task RestoreAsync(double setPoint, CancellationToken ct) {
                Restored.Add(setPoint);
                return Task.CompletedTask;
            }
        }

        private sealed class FakeLog : IRecoveryLog {
            public List<(string Level, string Text)> Lines = new List<(string, string)>();
            public void Info(string m) => Lines.Add(("INFO", m));
            public void Warning(string m) => Lines.Add(("WARN", m));
            public void Error(string m) => Lines.Add(("ERROR", m));
            public IEnumerable<string> Of(string level) => Lines.Where(l => l.Level == level).Select(l => l.Text);
        }

        private sealed class Rig {
            public DateTimeOffset Now = new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.Zero);
            public bool Enabled = true;
            public bool SequenceRunning = true;
            public readonly FakeDevice Camera = new FakeDevice("camera");
            public readonly FakeDevice Wheel = new FakeDevice("filter wheel");
            public readonly FakeDevice Focuser = new FakeDevice("focuser");
            public readonly FakeCooler Cooler = new FakeCooler();
            public readonly FakeLog Log = new FakeLog();
            public readonly EquipmentRecoveryWatcher Watcher;

            public Rig() {
                Watcher = new EquipmentRecoveryWatcher(
                    new IRecoveryDevice[] { Camera, Wheel, Focuser },
                    Cooler, Log, () => Enabled, () => SequenceRunning, () => Now, Camera);
            }

            /// Advance the clock by one poll interval and tick, n times.
            public async Task Run(TimeSpan duration) {
                var end = Now + duration;
                while (Now < end) {
                    Now += EquipmentRecoveryWatcher.PollInterval;
                    await Watcher.TickAsync();
                }
            }

            public void DropAll(bool usbBack = false) {
                foreach (var d in new[] { Camera, Wheel, Focuser }) {
                    d.Connected = false;
                    d.UsbPresent = usbBack;
                }
            }

            public void UsbBack() {
                foreach (var d in new[] { Camera, Wheel, Focuser }) d.UsbPresent = true;
            }
        }

        private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

        // -- What counts as unexpected -----------------------------------------

        [Fact]
        public async Task A_drop_with_no_sequence_running_is_left_alone() {
            var rig = new Rig { SequenceRunning = false };
            await rig.Run(10 * Second);
            rig.DropAll(usbBack: true);
            await rig.Run(TimeSpan.FromMinutes(2));

            Assert.Equal(0, rig.Camera.Reconnects);
            Assert.False(rig.Camera.Connected);
            Assert.Empty(rig.Log.Lines);
        }

        [Fact]
        public async Task A_device_disconnected_before_the_sequence_starts_is_not_reconnected() {
            var rig = new Rig { SequenceRunning = false };
            await rig.Run(10 * Second);
            rig.Focuser.Connected = false; // the user's choice
            await rig.Run(10 * Second);
            rig.SequenceRunning = true;
            await rig.Run(TimeSpan.FromMinutes(2));

            Assert.Equal(0, rig.Focuser.Reconnects);
            Assert.Equal(EquipmentRecoveryWatcher.State.Watching, rig.Watcher.Current);
            Assert.Empty(rig.Log.Lines);
        }

        [Fact]
        public async Task With_the_option_off_nothing_happens() {
            var rig = new Rig { Enabled = false };
            await rig.Run(10 * Second);
            rig.DropAll(usbBack: true);
            await rig.Run(TimeSpan.FromMinutes(2));

            Assert.Equal(0, rig.Camera.Reconnects);
            Assert.Empty(rig.Log.Lines);
        }

        [Fact]
        public async Task Devices_that_drop_a_moment_apart_are_named_in_one_line() {
            var rig = new Rig();
            await rig.Run(10 * Second);
            rig.Camera.Connected = false;
            rig.Wheel.Connected = false;
            await rig.Run(2 * Second);
            rig.Focuser.Connected = false; // the hub takes a second or two
            await rig.Run(20 * Second);

            var drop = Assert.Single(rig.Log.Of("INFO"), l => l.Contains("dropped"));
            Assert.Contains("camera", drop);
            Assert.Contains("filter wheel", drop);
            Assert.Contains("focuser", drop);
        }

        // -- Retry timing --------------------------------------------------------

        [Fact]
        public async Task Waits_for_the_settle_delay_before_the_first_reconnect() {
            var rig = new Rig();
            await rig.Run(10 * Second);
            rig.DropAll(usbBack: true);
            await rig.Run(EquipmentRecoveryWatcher.SettleDelay - 2 * Second);
            Assert.Equal(0, rig.Camera.Reconnects);

            await rig.Run(4 * Second);
            Assert.Equal(1, rig.Camera.Reconnects);
        }

        [Fact]
        public async Task Retries_every_thirty_seconds_while_the_devices_are_missing() {
            var rig = new Rig();
            await rig.Run(10 * Second);
            rig.DropAll(usbBack: false);
            // Drop seen at +2 s, so attempts at +12, +42, +72 and +102 s.
            await rig.Run(TimeSpan.FromSeconds(105));

            Assert.Equal(4, rig.Camera.Reconnects);
            Assert.Equal(4, rig.Focuser.Reconnects);
            Assert.Empty(rig.Log.Of("ERROR"));
        }

        [Fact]
        public async Task Reconnects_once_the_usb_devices_are_back_and_logs_each_and_a_summary() {
            var rig = new Rig();
            await rig.Run(10 * Second);
            rig.DropAll(usbBack: false);
            await rig.Run(20 * Second);
            rig.UsbBack();
            await rig.Run(40 * Second);

            Assert.True(rig.Camera.Connected && rig.Wheel.Connected && rig.Focuser.Connected);
            Assert.Equal(EquipmentRecoveryWatcher.State.Watching, rig.Watcher.Current);
            var info = rig.Log.Of("INFO").ToList();
            Assert.Contains(info, l => l.Contains("the camera is connected again"));
            Assert.Contains(info, l => l.Contains("the filter wheel is connected again"));
            Assert.Contains(info, l => l.Contains("the focuser is connected again"));
            Assert.Single(info, l => l.Contains("equipment recovered") && l.Contains("back after"));
            Assert.Empty(rig.Log.Of("ERROR"));
        }

        [Fact]
        public async Task A_device_nina_reconnects_by_itself_is_not_touched() {
            var rig = new Rig();
            await rig.Run(10 * Second);
            rig.Focuser.Connected = false;
            await rig.Run(4 * Second);
            rig.Focuser.Connected = true; // NINA's own ASCOM retry worked
            await rig.Run(30 * Second);

            Assert.Equal(0, rig.Focuser.Reconnects);
            Assert.Contains(rig.Log.Of("INFO"), l => l.Contains("the focuser is connected again"));
            Assert.Equal(EquipmentRecoveryWatcher.State.Watching, rig.Watcher.Current);
        }

        [Fact]
        public async Task A_failed_reconnect_is_a_warning_and_never_an_error() {
            var rig = new Rig();
            await rig.Run(10 * Second);
            rig.Camera.Connected = false;
            rig.Camera.ThrowOnReconnect = true;
            await rig.Run(TimeSpan.FromMinutes(3));

            Assert.NotEmpty(rig.Log.Of("WARN"));
            Assert.Empty(rig.Log.Of("ERROR"));
        }

        // -- Giving up -----------------------------------------------------------

        [Fact]
        public async Task Gives_up_after_ten_minutes_with_exactly_one_error_and_stops_retrying() {
            var rig = new Rig();
            await rig.Run(10 * Second);
            rig.DropAll(usbBack: false);
            await rig.Run(EquipmentRecoveryWatcher.GiveUpAfter + 10 * Second);

            Assert.Equal(EquipmentRecoveryWatcher.State.GaveUp, rig.Watcher.Current);
            var error = Assert.Single(rig.Log.Of("ERROR"));
            Assert.Contains("10 minutes", error);
            Assert.Contains("camera", error);

            var attempts = rig.Camera.Reconnects;
            Assert.True(attempts >= 19 && attempts <= 21, $"attempts {attempts}");
            await rig.Run(TimeSpan.FromMinutes(30));
            Assert.Equal(attempts, rig.Camera.Reconnects);
            Assert.Single(rig.Log.Of("ERROR"));
        }

        [Fact]
        public async Task After_giving_up_it_rearms_once_someone_reconnects_by_hand() {
            var rig = new Rig();
            await rig.Run(10 * Second);
            rig.DropAll(usbBack: false);
            await rig.Run(EquipmentRecoveryWatcher.GiveUpAfter + 10 * Second);
            rig.UsbBack();
            rig.Camera.Connected = rig.Wheel.Connected = rig.Focuser.Connected = true;
            await rig.Run(4 * Second);
            Assert.Equal(EquipmentRecoveryWatcher.State.Watching, rig.Watcher.Current);

            // A second drop later in the night is handled again.
            rig.Focuser.Connected = false;
            await rig.Run(20 * Second);
            Assert.True(rig.Focuser.Reconnects > 0);
            Assert.Single(rig.Log.Of("ERROR"));
        }

        [Fact]
        public async Task Turning_the_option_off_mid_recovery_stops_without_an_error() {
            var rig = new Rig();
            await rig.Run(10 * Second);
            rig.DropAll(usbBack: false);
            await rig.Run(20 * Second);
            rig.Enabled = false;
            await rig.Run(2 * Second);
            var attempts = rig.Camera.Reconnects;
            await rig.Run(TimeSpan.FromMinutes(15));

            Assert.Equal(attempts, rig.Camera.Reconnects);
            Assert.Equal(EquipmentRecoveryWatcher.State.Watching, rig.Watcher.Current);
            Assert.Contains(rig.Log.Of("INFO"), l => l.Contains("option was turned off"));
            Assert.Empty(rig.Log.Of("ERROR"));
        }

        // -- Cooler set point ----------------------------------------------------

        [Fact]
        public async Task The_cooler_goes_back_to_the_set_point_it_had_before_the_drop() {
            var rig = new Rig();
            rig.Cooler.Active = -15;
            await rig.Run(10 * Second);
            rig.DropAll(usbBack: true);
            rig.Cooler.Active = null; // what a freshly reconnected camera reports
            await rig.Run(20 * Second);

            Assert.Equal(new[] { -15.0 }, rig.Cooler.Restored);
        }

        [Fact]
        public async Task The_camera_temperature_is_logged_before_the_cooler_is_turned_on() {
            var rig = new Rig();
            rig.Cooler.Temperature = -14.6;
            await rig.Run(10 * Second);
            rig.DropAll(usbBack: true);
            rig.Cooler.Temperature = 18.2; // the hub lost 12 V, so the camera warmed
            rig.Cooler.Power = 0;
            await rig.Run(20 * Second);

            var line = Assert.Single(rig.Log.Of("INFO"), l => l.Contains("sensor at"));
            Assert.Contains("18.2 °C", line);
            Assert.Contains("cooler power 0 %", line);
            Assert.Contains("-14.6 °C just before the drop", line);
            var all = rig.Log.Lines.Select(l => l.Text).ToList();
            Assert.True(all.IndexOf(line) < all.FindIndex(l => l.Contains("cooler is on again")));
        }

        [Fact]
        public void DescribeThermals_says_when_a_reading_is_missing() {
            var text = EquipmentRecoveryWatcher.DescribeThermals(null, null, null);
            Assert.Contains("unknown temperature", text);
            Assert.Contains("no reading from before the drop", text);
        }

        [Fact]
        public async Task A_cooler_that_was_off_is_left_off() {
            var rig = new Rig();
            rig.Cooler.Active = null;
            await rig.Run(10 * Second);
            rig.DropAll(usbBack: true);
            await rig.Run(20 * Second);

            Assert.Empty(rig.Cooler.Restored);
        }

        [Fact]
        public async Task A_cooler_never_read_falls_back_to_the_profile_set_point() {
            var rig = new Rig();
            rig.Cooler.ThrowOnRead = true;
            rig.Cooler.Profile = -5;
            await rig.Run(10 * Second);
            rig.DropAll(usbBack: true);
            await rig.Run(20 * Second);

            Assert.Equal(new[] { -5.0 }, rig.Cooler.Restored);
        }

        [Fact]
        public async Task The_cooler_is_not_touched_when_only_the_focuser_drops() {
            var rig = new Rig();
            await rig.Run(10 * Second);
            rig.Focuser.Connected = false;
            await rig.Run(20 * Second);

            Assert.Empty(rig.Cooler.Restored);
        }

        [Theory]
        [InlineData(true, -10.0, -5.0, -10.0)]
        [InlineData(true, null, -5.0, null)]
        [InlineData(false, null, -5.0, -5.0)]
        [InlineData(false, null, null, null)]
        public void ChooseSetPoint_prefers_the_reading_from_before_the_drop(
            bool read, double? before, double? profile, double? expected) {
            Assert.Equal(expected, EquipmentRecoveryWatcher.ChooseSetPoint(read, before, profile));
        }

        // -- Focus ---------------------------------------------------------------

        [Fact]
        public async Task A_focuser_back_at_a_different_position_gets_a_warning_and_is_not_moved() {
            var rig = new Rig();
            rig.Focuser.Position = 12000;
            rig.Focuser.PositionAfterReconnect = 0;
            await rig.Run(10 * Second);
            rig.Focuser.Connected = false;
            await rig.Run(20 * Second);

            Assert.Contains(rig.Log.Of("WARN"), l => l.Contains("position 0") && l.Contains("12000"));
            Assert.Equal(0.0, rig.Focuser.Position.Value);
        }

        [Theory]
        [InlineData(5, "5 s")]
        [InlineData(75, "1 min 15 s")]
        [InlineData(600, "10 min 0 s")]
        public void FormatDuration_reads_plainly(int seconds, string expected) {
            Assert.Equal(expected, EquipmentRecoveryWatcher.FormatDuration(TimeSpan.FromSeconds(seconds)));
        }
    }
}
