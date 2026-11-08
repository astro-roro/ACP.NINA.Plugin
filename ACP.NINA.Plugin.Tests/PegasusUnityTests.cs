using ACP.NINA.Plugin.Services.EquipmentRecovery;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The Pegasus Unity replies, read from canned JSON in the shape the
    /// server sends. The unique keys are made up.
    public class PegasusUnityTests {

        private const string Key = "00000000-0000-4000-8000-0000000000aa";

        private const string Report =
            "{\"status\":\"success\",\"code\":200,\"message\":\"Readings report.\",\"data\":{\"uniqueKey\":\"" + Key + "\",\"name\":\"PPBAdvance\","
            + "\"message\":{\"voltage\":13.8,\"current\":0.08,\"quadCurrent\":0.3,\"power\":1,\"temperature\":20.9,\"humidity\":31,\"dewPoint\":3.1,"
            + "\"isOverCurrent\":false,\"averageAmps\":0.54,\"ampsPerHour\":4.98,\"wattPerHour\":68.51,\"upTime\":\"09:15:59.2800000\","
            + "\"powerHubStatus\":{\"state\":\"ON\",\"messageType\":\"SwitchState\"},\"messageType\":\"AggregateReportPPB\"}}}";

        private const string Connected =
            "{\"status\":\"success\",\"code\":200,\"data\":["
            + "{\"uniqueKey\":\"" + Key + "\",\"name\":\"PPBAdvance\",\"fullName\":\"Pocket Powerbox Advance\",\"deviceID\":\"PPBA\",\"firmware\":\"2.0\"},"
            + "{\"uniqueKey\":\"00000000-0000-4000-8000-0000000000bb\",\"name\":\"FocusCube\",\"fullName\":\"Focus Cube\",\"deviceID\":\"FC\",\"firmware\":\"1.0\"}"
            + "]}";

        [Fact]
        public void Reads_the_connected_devices() {
            var devices = PegasusUnityJson.ParseConnected(Connected);
            Assert.Equal(2, devices.Count);
            Assert.Equal(("PPBAdvance", Key), devices[0]);
            Assert.Equal("FocusCube", devices[1].Name);
        }

        [Fact]
        public void Reads_the_power_box_report() {
            var r = PegasusUnityJson.ParseReport(Report, "PPBAdvance");
            Assert.Equal("PPBAdvance", r.DeviceName);
            Assert.Equal(13.8, r.Voltage);
            Assert.Equal(0.08, r.Current);
            Assert.Equal(1.0, r.Power);
            Assert.Equal(4.98, r.AmpHours);
            Assert.False(r.OverCurrent);
            Assert.Equal(new TimeSpan(0, 9, 15, 59, 280), r.UpTime);
        }

        [Fact]
        public void A_reply_with_no_message_is_no_report() {
            Assert.Null(PegasusUnityJson.ParseReport("{\"status\":\"error\",\"code\":404,\"data\":null}"));
            Assert.Empty(PegasusUnityJson.ParseConnected("{\"status\":\"success\",\"data\":[]}"));
        }

        [Theory]
        [InlineData("09:15:59.2800000", 9 * 3600 + 15 * 60 + 59.28)]
        [InlineData("27:03:12", 27 * 3600 + 3 * 60 + 12.0)]
        [InlineData("1.03:03:12.5000000", 27 * 3600 + 3 * 60 + 12.5)]
        [InlineData("00:00:04.1000000", 4.1)]
        public void Uptime_reads_past_twenty_four_hours_in_either_form(string text, double seconds) {
            Assert.Equal(seconds, PegasusUnityJson.ParseUpTime(text).Value.TotalSeconds, 3);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("soon")]
        [InlineData("10:75:00")]
        public void Uptime_that_does_not_parse_is_null(string text) {
            Assert.Null(PegasusUnityJson.ParseUpTime(text));
        }

        // -- Restarted or not -----------------------------------------------------

        private static readonly DateTimeOffset Drop = new DateTimeOffset(2026, 11, 1, 2, 15, 32, TimeSpan.Zero);

        [Fact]
        public void A_box_powered_on_after_the_drop_restarted() {
            var now = Drop.AddMinutes(1);
            var line = PowerBoxRestart.Describe(now, TimeSpan.FromSeconds(50), Drop, TimeZoneInfo.Utc);
            Assert.Contains("powered on at 2026-11-01 02:15:42 local time", line);
            Assert.Contains("after the drop started at 02:15:32", line);
            Assert.Contains("lost power or reset", line);
            Assert.Contains("3 minutes for clock drift", line);
        }

        [Fact]
        public void A_box_powered_on_hours_before_the_drop_kept_running() {
            var now = Drop.AddMinutes(1);
            var line = PowerBoxRestart.Describe(now, TimeSpan.FromHours(9), Drop, TimeZoneInfo.Utc);
            Assert.Contains("before the drop", line);
            Assert.Contains("only USB dropped", line);
        }

        [Fact]
        public void A_power_on_inside_the_slack_counts_as_a_restart() {
            Assert.True(PowerBoxRestart.RestartedSince(Drop - TimeSpan.FromMinutes(2), Drop));
            Assert.False(PowerBoxRestart.RestartedSince(Drop - TimeSpan.FromMinutes(4), Drop));
        }

        // -- In the watcher ------------------------------------------------------

        private sealed class FakeDevice : IRecoveryDevice {
            public FakeDevice(string kind) { Kind = kind; }
            public string Kind { get; }
            public bool Connected = true;
            public bool IsConnected() => Connected;
            public double? ReadPosition() => null;
            public Task<bool> ReconnectAsync(CancellationToken ct) { Connected = true; return Task.FromResult(true); }
        }

        private sealed class FakePowerBox : IPowerBoxSource {
            public TimeSpan? UpTime;
            public int Reads;
            public Task<IReadOnlyList<PowerBoxReport>> ReadAllAsync(CancellationToken ct) {
                Reads++;
                IReadOnlyList<PowerBoxReport> list = UpTime.HasValue
                    ? new List<PowerBoxReport> { new PowerBoxReport { DeviceName = "PPBAdvance", UpTime = UpTime } }
                    : new List<PowerBoxReport>();
                return Task.FromResult(list);
            }
        }

        private sealed class ListLog : IRecoveryLog {
            public readonly List<string> Info = new List<string>();
            void IRecoveryLog.Info(string m) => Info.Add(m);
            void IRecoveryLog.Warning(string m) { }
            void IRecoveryLog.Error(string m) { }
        }

        [Fact]
        public async Task After_a_drop_the_power_box_line_is_logged_once() {
            var now = Drop;
            var sw = new FakeDevice("switch");
            var cam = new FakeDevice("camera");
            var box = new FakePowerBox { UpTime = TimeSpan.FromSeconds(5) };
            var log = new ListLog();
            var watcher = new EquipmentRecoveryWatcher(
                new IRecoveryDevice[] { cam, sw }, null, log, () => true, () => true, () => now,
                null, box, TimeZoneInfo.Utc);

            async Task Run(int seconds) {
                for (var i = 0; i < seconds / 2; i++) {
                    now += EquipmentRecoveryWatcher.PollInterval;
                    await watcher.TickAsync();
                }
            }

            await Run(4);
            cam.Connected = false;
            sw.Connected = false;
            await Run(30);

            Assert.Single(log.Info, l => l.Contains("power box was powered on") && l.Contains("lost power or reset"));
            await Run(60);
            Assert.Single(log.Info, l => l.Contains("power box was powered on"));
        }

        [Fact]
        public async Task With_unity_unreachable_recovery_carries_on_without_the_line() {
            var now = Drop;
            var cam = new FakeDevice("camera");
            var box = new FakePowerBox { UpTime = null };
            var log = new ListLog();
            var watcher = new EquipmentRecoveryWatcher(
                new IRecoveryDevice[] { cam }, null, log, () => true, () => true, () => now,
                null, box, TimeZoneInfo.Utc);

            for (var i = 0; i < 2; i++) { now += EquipmentRecoveryWatcher.PollInterval; await watcher.TickAsync(); }
            cam.Connected = false;
            for (var i = 0; i < 15; i++) { now += EquipmentRecoveryWatcher.PollInterval; await watcher.TickAsync(); }

            Assert.True(cam.Connected);
            Assert.True(box.Reads > 0);
            Assert.DoesNotContain(log.Info, l => l.Contains("power box was powered on"));
            Assert.Contains(log.Info, l => l.Contains("equipment recovered"));
        }

        // -- In the health log ---------------------------------------------------

        [Fact]
        public void One_power_box_fills_the_plain_unity_columns() {
            var r = PegasusUnityJson.ParseReport(Report, "PPBAdvance");
            var s = new HealthSnapshot {
                Utc = Drop,
                Devices = new List<(string, bool)> { ("switch", true) },
                PowerBoxes = new[] { r },
                PowerBoxNames = new[] { "PPBAdvance" },
            };
            var cols = HealthCsv.Columns(s).ToList();
            var vals = HealthCsv.Values(s).ToList();
            Assert.Equal("13.8", vals[cols.IndexOf("unity_voltage")]);
            Assert.Equal("0.08", vals[cols.IndexOf("unity_current")]);
            Assert.Equal("1", vals[cols.IndexOf("unity_power")]);
            Assert.Equal("33359.28", vals[cols.IndexOf("unity_uptime_s")]);
            Assert.Equal("4.98", vals[cols.IndexOf("unity_amp_hours")]);
            Assert.Equal("0", vals[cols.IndexOf("unity_overcurrent")]);
        }

        [Fact]
        public void Several_devices_get_columns_named_after_each() {
            var cols = HealthCsv.PowerBoxColumns(new[] { "PPBAdvance", "FocusCube" });
            Assert.Contains("unity_PPBAdvance_voltage", cols);
            Assert.Contains("unity_FocusCube_uptime_s", cols);
            Assert.Equal(12, cols.Count);
        }

        [Fact]
        public void Unreachable_unity_keeps_the_last_columns_with_empty_cells() {
            var names = HealthCsv.KeepPowerBoxNames(new List<PowerBoxReport>(), new[] { "PPBAdvance", "FocusCube" });
            var s = new HealthSnapshot { Utc = Drop, PowerBoxNames = names };
            Assert.Equal(12, HealthCsv.PowerBoxColumns(names).Count);
            Assert.All(HealthCsv.PowerBoxValues(s), v => Assert.Equal("", v));
        }
    }
}
