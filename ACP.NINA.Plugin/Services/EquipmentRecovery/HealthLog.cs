using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ACP.NINA.Plugin.Services.EquipmentRecovery {

    /// One reading of the rig's power and health, taken every few seconds.
    /// Plain data, so the tests can build rows without NINA.
    public sealed class HealthSnapshot {
        public DateTimeOffset Utc { get; set; }

        /// Every watched device by kind, in a fixed order, and whether NINA
        /// shows it connected.
        public IReadOnlyList<(string Kind, bool Connected)> Devices { get; set; } = new List<(string, bool)>();

        public double? CameraTemperature { get; set; }
        public double? CoolerPower { get; set; }
        public bool? CoolerOn { get; set; }

        /// Every gauge and output the switch reports, under the names the
        /// switch driver gives them: input voltage, current, dew outputs and
        /// so on. Empty when the switch is not connected.
        public IReadOnlyList<(string Name, double Value)> Gauges { get; set; } = new List<(string, double)>();

        /// Rows are only worth writing while the power box or camera is there.
        public bool ShouldLog => Devices.Any(d => d.Connected && (d.Kind == "camera" || d.Kind == "switch"));
    }

    /// Turns snapshots into CSV and decides file names and retention.
    public static class HealthCsv {

        public const int KeepDays = 14;
        public const string FilePrefix = "health-";

        public static readonly string[] FixedColumns = {
            "utc", "camera_temp_c", "cooler_power_pct", "cooler_on",
        };

        public static IReadOnlyList<string> Columns(HealthSnapshot s) {
            var cols = new List<string>(FixedColumns);
            cols.AddRange(s.Devices.Select(d => d.Kind.Replace(' ', '_') + "_connected"));
            cols.AddRange(GaugeColumns(s.Gauges));
            return cols;
        }

        public static IReadOnlyList<string> Values(HealthSnapshot s) {
            var vals = new List<string> {
                s.Utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Number(s.CameraTemperature),
                Number(s.CoolerPower),
                s.CoolerOn.HasValue ? (s.CoolerOn.Value ? "1" : "0") : "",
            };
            vals.AddRange(s.Devices.Select(d => d.Connected ? "1" : "0"));
            vals.AddRange(s.Gauges.Select(g => Number(g.Value)));
            return vals;
        }

        /// "switch:" plus the driver's name. Two gauges with the same name get
        /// "#2", "#3" so every column stays distinct.
        public static IReadOnlyList<string> GaugeColumns(IEnumerable<(string Name, double Value)> gauges) {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var cols = new List<string>();
            foreach (var g in gauges) {
                var name = "switch:" + (string.IsNullOrWhiteSpace(g.Name) ? "unnamed" : g.Name.Trim());
                if (seen.TryGetValue(name, out var n)) {
                    seen[name] = n + 1;
                    name = $"{name}#{n + 1}";
                } else {
                    seen[name] = 1;
                }
                cols.Add(name);
            }
            return cols;
        }

        /// While the switch is away its columns stay, with empty values, so a
        /// USB drop does not change the header and start a new file.
        public static IReadOnlyList<(string Name, double Value)> KeepGaugeColumns(
            IReadOnlyList<(string Name, double Value)> current, IReadOnlyList<string> lastNames) {
            if (current != null && current.Count > 0) return current;
            return (lastNames ?? new List<string>()).Select(n => (n, double.NaN)).ToList();
        }

        public static string Number(double? v) {
            if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return "";
            return v.Value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static string Line(IEnumerable<string> cells) =>
            string.Join(",", cells.Select(Escape));

        public static string Escape(string cell) {
            cell = cell ?? "";
            if (cell.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return cell;
            return "\"" + cell.Replace("\"", "\"\"") + "\"";
        }

        /// One file per UTC day. A file started mid-day because the switch's
        /// gauges changed gets the time as well.
        public static string FileNameFor(DateTimeOffset utc, bool withTime = false) {
            var u = utc.ToUniversalTime();
            return FilePrefix + u.ToString(withTime ? "yyyy-MM-dd-HHmmss" : "yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";
        }

        /// Health files whose day is more than KeepDays old. Anything that does
        /// not look like one of ours is never chosen.
        public static IEnumerable<string> FilesToDelete(IEnumerable<string> fileNames, DateTimeOffset now, int keepDays = KeepDays) {
            var cutoff = now.ToUniversalTime().Date.AddDays(-keepDays);
            foreach (var name in fileNames) {
                if (name == null || !name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                    || name.Length < FilePrefix.Length + 10) continue;
                var datePart = name.Substring(FilePrefix.Length, 10);
                if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
                    && day < cutoff) {
                    yield return name;
                }
            }
        }
    }

    /// Appends rows to the day's file. Never throws: a failed write is counted
    /// and the next row tries again.
    public sealed class HealthLogWriter {
        private readonly string directory;
        private string currentPath;
        private string currentHeader;
        private DateTime currentDay;
        private DateTime lastPruneDay;

        public HealthLogWriter(string directory) {
            this.directory = directory;
        }

        public int ErrorCount { get; private set; }
        public string LastError { get; private set; }
        public string CurrentPath => currentPath;

        public bool TryWrite(HealthSnapshot s) {
            try {
                Directory.CreateDirectory(directory);
                var day = s.Utc.ToUniversalTime().Date;
                if (day != lastPruneDay) {
                    Prune(s.Utc);
                    lastPruneDay = day;
                }

                var header = HealthCsv.Line(HealthCsv.Columns(s));
                if (currentPath == null || day != currentDay) {
                    currentDay = day;
                    currentPath = Path.Combine(directory, HealthCsv.FileNameFor(s.Utc));
                    currentHeader = File.Exists(currentPath) ? ReadFirstLine(currentPath) : null;
                }
                if (currentHeader != null && currentHeader != header) {
                    // The switch's gauges changed, so start a file whose header fits.
                    currentPath = Path.Combine(directory, HealthCsv.FileNameFor(s.Utc, withTime: true));
                    currentHeader = File.Exists(currentPath) ? ReadFirstLine(currentPath) : null;
                }

                var text = new StringBuilder();
                if (currentHeader == null) {
                    text.Append(header).Append("\r\n");
                    currentHeader = header;
                }
                text.Append(HealthCsv.Line(HealthCsv.Values(s))).Append("\r\n");
                File.AppendAllText(currentPath, text.ToString(), new UTF8Encoding(false));
                return true;
            } catch (Exception ex) {
                ErrorCount++;
                LastError = ex.Message;
                return false;
            }
        }

        private void Prune(DateTimeOffset now) {
            try {
                var names = Directory.GetFiles(directory, HealthCsv.FilePrefix + "*.csv").Select(Path.GetFileName);
                foreach (var name in HealthCsv.FilesToDelete(names, now).ToList()) {
                    File.Delete(Path.Combine(directory, name));
                }
            } catch (Exception) {
                // Old files staying a little longer is harmless.
            }
        }

        private static string ReadFirstLine(string path) {
            using (var reader = new StreamReader(path)) {
                return reader.ReadLine();
            }
        }
    }
}
