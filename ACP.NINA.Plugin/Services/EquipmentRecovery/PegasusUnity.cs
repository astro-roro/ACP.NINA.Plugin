using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services.EquipmentRecovery {

    /// What a Pegasus power box reports through the Pegasus Unity server.
    /// Every field is null when the report did not carry it.
    public sealed class PowerBoxReport {
        public string DeviceName { get; set; }
        public double? Voltage { get; set; }
        public double? Current { get; set; }
        public double? Power { get; set; }
        public double? AmpHours { get; set; }
        public bool? OverCurrent { get; set; }
        public TimeSpan? UpTime { get; set; }
    }

    /// Reads one report per connected Pegasus device. Empty when there is
    /// nothing to read: the server is switched off in the options, not
    /// running, or slow.
    public interface IPowerBoxSource {
        Task<IReadOnlyList<PowerBoxReport>> ReadAllAsync(CancellationToken ct);
    }

    /// The JSON side of the Pegasus Unity server, kept apart from HTTP so the
    /// tests can feed it canned replies.
    public static class PegasusUnityJson {

        /// Connected devices from GET /Server/DeviceManager/Connected, as
        /// name and unique key pairs, from data[].name and data[].uniqueKey.
        public static IReadOnlyList<(string Name, string UniqueKey)> ParseConnected(string json) {
            var list = new List<(string, string)>();
            var data = Field(JToken.Parse(json) as JObject, "data") as JArray;
            if (data == null) return list;
            foreach (var item in data.OfType<JObject>()) {
                var name = Field(item, "name")?.ToString();
                var key = Field(item, "uniqueKey")?.ToString();
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(key)) list.Add((name, key));
            }
            return list;
        }

        /// One device's report from GET /Driver/{name}/Report, read from
        /// data.message. Null when the reply has no message object.
        public static PowerBoxReport ParseReport(string json, string deviceName = null) {
            var message = Field(Field(JToken.Parse(json) as JObject, "data") as JObject, "message") as JObject;
            if (message == null) return null;
            return new PowerBoxReport {
                DeviceName = deviceName,
                Voltage = Number(Field(message, "voltage")),
                Current = Number(Field(message, "current")),
                Power = Number(Field(message, "power")),
                AmpHours = Number(Field(message, "ampsPerHour")),
                OverCurrent = Bool(Field(message, "isOverCurrent")),
                UpTime = ParseUpTime(Field(message, "upTime")?.ToString()),
            };
        }

        private static readonly Regex UpTimePattern = new Regex(
            @"^\s*(?:(\d+)\.)?(\d+):(\d{1,2}):(\d{1,2}(?:\.\d+)?)\s*$", RegexOptions.CultureInvariant);

        /// "hh:mm:ss.fffffff", where the hours can run past 24, or .NET's
        /// "d.hh:mm:ss.fffffff". Null for anything else.
        public static TimeSpan? ParseUpTime(string text) {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = UpTimePattern.Match(text);
            if (!m.Success) return null;
            var days = m.Groups[1].Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            var hours = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var minutes = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            var seconds = double.Parse(m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (minutes > 59 || seconds >= 60) return null;
            var total = ((days * 24 + hours) * 60 + minutes) * 60 + seconds;
            return TimeSpan.FromTicks((long)Math.Round(total * TimeSpan.TicksPerSecond));
        }

        private static JToken Field(JObject obj, string name) =>
            obj?.GetValue(name, StringComparison.OrdinalIgnoreCase);

        private static double? Number(JToken t) {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return t.Value<double>();
            return double.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
        }

        private static bool? Bool(JToken t) {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Boolean) return t.Value<bool>();
            return bool.TryParse(t.ToString(), out var b) ? b : (bool?)null;
        }
    }

    /// Asks the Pegasus Unity server on this machine for a report from every
    /// connected device, so any Pegasus box works, not only one model. The
    /// whole read has one 2 second budget and every failure comes back as an
    /// empty list or a missing device.
    public sealed class PegasusUnityClient : IPowerBoxSource {

        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

        private static readonly HttpClient Http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        private readonly Func<string> baseUrl;

        public PegasusUnityClient(Func<string> baseUrl) {
            this.baseUrl = baseUrl ?? (() => null);
        }

        public async Task<IReadOnlyList<PowerBoxReport>> ReadAllAsync(CancellationToken ct) {
            var reports = new List<PowerBoxReport>();
            var root = (baseUrl() ?? "").Trim().TrimEnd('/');
            if (root.Length == 0) return reports;
            try {
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                    limit.CancelAfter(Timeout);
                    var connected = PegasusUnityJson.ParseConnected(
                        await GetAsync(root + "/Server/DeviceManager/Connected", limit.Token).ConfigureAwait(false));
                    foreach (var (name, key) in connected) {
                        try {
                            var url = root + "/Driver/" + Uri.EscapeDataString(name) + "/Report?DriverUniqueKey=" + Uri.EscapeDataString(key);
                            var report = PegasusUnityJson.ParseReport(await GetAsync(url, limit.Token).ConfigureAwait(false), name);
                            if (report != null) reports.Add(report);
                        } catch (OperationCanceledException) {
                            throw;
                        } catch (Exception) {
                            // Not a power box, or not one that reports. Try the next.
                        }
                    }
                }
            } catch (Exception) {
                // Unity not running, not answering in time, or saying something
                // unexpected. Whatever was read before that still counts.
            }
            return reports;
        }

        private static async Task<string> GetAsync(string url, CancellationToken ct) {
            using (var resp = await Http.GetAsync(url, ct).ConfigureAwait(false)) {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }
    }

    /// Whether the power box restarted during a drop, worked out from when it
    /// says it was powered on.
    public static class PowerBoxRestart {

        /// A power on time this close before the drop still counts as after
        /// it. The box's uptime counter can drift from this computer's clock,
        /// a box that loses power reboots a moment after the drop begins, and
        /// the watcher sees the drop up to one poll late.
        public static readonly TimeSpan Slack = TimeSpan.FromMinutes(3);

        /// The first report that carries an uptime, or null.
        public static TimeSpan? FirstUpTime(IEnumerable<PowerBoxReport> reports) =>
            reports?.FirstOrDefault(r => r?.UpTime != null)?.UpTime;

        public static bool RestartedSince(DateTimeOffset poweredOn, DateTimeOffset dropAt) =>
            poweredOn >= dropAt - Slack;

        public static string Describe(DateTimeOffset now, TimeSpan upTime, DateTimeOffset dropAt, TimeZoneInfo zone) {
            zone = zone ?? TimeZoneInfo.Local;
            var poweredOn = now - upTime;
            var on = TimeZoneInfo.ConvertTime(poweredOn, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var drop = TimeZoneInfo.ConvertTime(dropAt, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            var slack = $"{Slack.TotalMinutes:0} minutes";
            return RestartedSince(poweredOn, dropAt)
                ? $"ACP: the power box was powered on at {on} local time, after the drop started at {drop} (allowing {slack} for clock drift). It restarted, so it lost power or reset."
                : $"ACP: the power box was powered on at {on} local time, before the drop started at {drop} (allowing {slack} for clock drift). It kept running, so only USB dropped.";
        }
    }
}
