using ACP.NINA.Plugin.Services.EquipmentRecovery;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The health log's rows, file names and retention, built from made-up
    /// readings. The switch gauge names are generic ones a driver might use.
    public class HealthLogTests {

        private static HealthSnapshot Sample(bool cameraOn = true, bool switchOn = true) => new HealthSnapshot {
            Utc = new DateTimeOffset(2026, 11, 1, 2, 15, 30, TimeSpan.Zero),
            Devices = new List<(string, bool)> {
                ("camera", cameraOn), ("filter wheel", true), ("focuser", false), ("switch", switchOn),
            },
            CameraTemperature = -14.6,
            CoolerPower = 42.25,
            CoolerOn = true,
            Gauges = new List<(string, double)> {
                ("Input Voltage", 12.31), ("Total Current", 3.5), ("Dew A", 40), ("Dew A", 0),
            },
        };

        [Fact]
        public void Columns_are_the_fixed_set_then_devices_then_switch_gauges() {
            Assert.Equal(new[] {
                "utc", "camera_temp_c", "cooler_power_pct", "cooler_on",
                "camera_connected", "filter_wheel_connected", "focuser_connected", "switch_connected",
                "unity_voltage", "unity_current", "unity_power", "unity_uptime_s", "unity_amp_hours", "unity_overcurrent",
                "switch:Input Voltage", "switch:Total Current", "switch:Dew A", "switch:Dew A#2",
            }, HealthCsv.Columns(Sample()));
        }

        [Fact]
        public void Values_line_up_with_the_columns() {
            var s = Sample();
            Assert.Equal(new[] {
                "2026-11-01T02:15:30Z", "-14.6", "42.25", "1",
                "1", "1", "0", "1",
                "", "", "", "", "", "",
                "12.31", "3.5", "40", "0",
            }, HealthCsv.Values(s));
            Assert.Equal(HealthCsv.Columns(s).Count, HealthCsv.Values(s).Count);
        }

        [Fact]
        public void Missing_camera_readings_are_empty_cells() {
            var s = Sample();
            s.CameraTemperature = null;
            s.CoolerPower = double.NaN;
            s.CoolerOn = null;
            var v = HealthCsv.Values(s);
            Assert.Equal("", v[1]);
            Assert.Equal("", v[2]);
            Assert.Equal("", v[3]);
        }

        [Fact]
        public void Only_logs_while_the_switch_or_camera_is_connected() {
            Assert.True(Sample(cameraOn: true, switchOn: false).ShouldLog);
            Assert.True(Sample(cameraOn: false, switchOn: true).ShouldLog);
            Assert.False(Sample(cameraOn: false, switchOn: false).ShouldLog);
        }

        [Fact]
        public void Switch_columns_stay_while_the_switch_is_away() {
            var kept = HealthCsv.KeepGaugeColumns(new List<(string, double)>(), new[] { "Input Voltage", "Dew A" });
            Assert.Equal(new[] { "Input Voltage", "Dew A" }, kept.Select(g => g.Name));
            Assert.All(kept, g => Assert.Equal("", HealthCsv.Number(g.Value)));
        }

        [Fact]
        public void Cells_with_commas_or_quotes_are_quoted() {
            Assert.Equal("a,\"b,c\",\"say \"\"hi\"\"\"", HealthCsv.Line(new[] { "a", "b,c", "say \"hi\"" }));
        }

        [Fact]
        public void Files_older_than_fourteen_days_are_chosen_for_deletion_and_nothing_else() {
            var now = new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);
            var names = new[] {
                "health-2026-11-05.csv",          // 15 days old
                "health-2026-11-05-021530.csv",   // same day, started mid-day
                "health-2026-11-06.csv",          // exactly 14 days, kept
                "health-2026-11-20.csv",
                "settings.json",
                "health-notes.csv",
            };
            Assert.Equal(
                new[] { "health-2026-11-05.csv", "health-2026-11-05-021530.csv" },
                HealthCsv.FilesToDelete(names, now));
        }

        [Fact]
        public void Writer_appends_rows_under_one_header_and_starts_a_new_file_when_gauges_change() {
            var dir = Path.Combine(Path.GetTempPath(), "acp-health-" + Guid.NewGuid().ToString("N"));
            try {
                var writer = new HealthLogWriter(dir);
                Assert.True(writer.TryWrite(Sample()));
                Assert.True(writer.TryWrite(Sample()));
                var first = writer.CurrentPath;
                var lines = File.ReadAllLines(first);
                Assert.Equal(3, lines.Length);
                Assert.StartsWith("utc,", lines[0]);

                var changed = Sample();
                changed.Gauges = new List<(string, double)> { ("Input Voltage", 12.0) };
                Assert.True(writer.TryWrite(changed));
                Assert.NotEqual(first, writer.CurrentPath);
                Assert.Equal(0, writer.ErrorCount);
            } finally {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void A_settings_file_from_before_these_options_loads_both_as_on() {
            var path = Path.Combine(Path.GetTempPath(), "acp-settings-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, "{\"ServerUrl\":\"http://127.0.0.1:5555\",\"ReportProgressToAcp\":false}");
            try {
                var loaded = ACP.NINA.Plugin.Services.AcpSettings.LoadFromDisk(path, null);
                Assert.True(loaded.EquipmentRecoveryEnabled);
                Assert.True(loaded.HealthLogEnabled);
                Assert.False(loaded.ReportProgressToAcp);
            } finally {
                File.Delete(path);
            }
        }

        [Fact]
        public void A_failed_write_is_counted_and_does_not_throw() {
            // A file where the folder should be makes every write fail.
            var blocker = Path.Combine(Path.GetTempPath(), "acp-health-block-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(blocker, "");
            try {
                var writer = new HealthLogWriter(blocker);
                Assert.False(writer.TryWrite(Sample()));
                Assert.False(writer.TryWrite(Sample()));
                Assert.Equal(2, writer.ErrorCount);
            } finally {
                File.Delete(blocker);
            }
        }
    }
}
