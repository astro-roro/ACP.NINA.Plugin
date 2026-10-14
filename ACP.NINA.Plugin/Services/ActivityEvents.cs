using ACP.NINA.Plugin.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ACP.NINA.Plugin.Services {

    /// Night, time and text rules for the events the plugin posts. Kept apart
    /// from the builders so the rules ACP enforces sit in one small place.
    public static class ActivityNight {

        /// The zone ACP cuts nights in by default (ACP_ACTIVITY_TZ). The plugin
        /// always sends the night itself, so it has to agree with ACP's default
        /// rather than with whatever zone this PC happens to be set to.
        private static readonly Lazy<TimeZoneInfo> zone = new Lazy<TimeZoneInfo>(() => {
            foreach (var id in new[] { "Australia/Sydney", "AUS Eastern Standard Time" }) {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (Exception) { }
            }
            return TimeZoneInfo.Utc;
        });

        /// The observing night an instant belongs to, as the date the evening
        /// started. Local noon is the cut-over, so 10:00 on 14 Oct is 2026-10-13.
        /// Daylight saving does not matter: the rule is noon on each local day.
        public static string For(DateTimeOffset when) {
            var local = TimeZoneInfo.ConvertTime(when, zone.Value);
            return local.DateTime.AddHours(-12).Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        /// The observing night an instant belongs to as a time span: local noon
        /// to the next local noon, in the same zone the night rule uses.
        public static void Window(DateTimeOffset when, out DateTimeOffset from, out DateTimeOffset to) {
            var date = DateTime.ParseExact(For(when), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var noon = new DateTime(date.Year, date.Month, date.Day, 12, 0, 0, DateTimeKind.Unspecified);
            var nextNoon = noon.AddDays(1);
            from = new DateTimeOffset(noon, zone.Value.GetUtcOffset(noon));
            to = new DateTimeOffset(nextNoon, zone.Value.GetUtcOffset(nextNoon));
        }

        /// ISO 8601 with an offset, which is what ACP wants in "at".
        public static string Iso(DateTimeOffset when) {
            return when.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        }

        /// The last path segment, whichever separator was used. Path fields go
        /// to ACP as basenames only.
        public static string Basename(string path) {
            if (string.IsNullOrEmpty(path)) return path;
            var cut = path.LastIndexOfAny(new[] { '\\', '/' });
            return cut < 0 ? path : path.Substring(cut + 1);
        }

        private static readonly Regex WindowsPath =
            new Regex(@"[A-Za-z]:\\[^\s""'<>|]*", RegexOptions.Compiled);

        /// Replace every Windows path in free text with its basename, so a
        /// failure message cannot carry a user folder to ACP.
        public static string ScrubPaths(string text) {
            if (string.IsNullOrEmpty(text)) return text;
            return WindowsPath.Replace(text, m => Basename(m.Value.TrimEnd('.', ',', ';', ':')));
        }

        /// One line, cut to the limit. Titles over 200 characters are rejected
        /// by ACP, so they are cut here instead.
        public static string Clip(string text, int max) {
            if (text == null) return null;
            var flat = Regex.Replace(text, @"\s*[\r\n]+\s*", " ").Trim();
            return flat.Length <= max ? flat : flat.Substring(0, max);
        }

        /// A key-safe fragment: letters, digits and dashes.
        public static string Slug(string text) {
            var sb = new StringBuilder();
            foreach (var ch in (text ?? string.Empty).Trim().ToLowerInvariant()) {
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
            }
            return sb.ToString().Trim('-');
        }

        /// A compact UTC stamp for keys that name one moment.
        public static string Stamp(DateTimeOffset when) {
            return when.UtcDateTime.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        }
    }

    /// Turns something NINA did into an event. Pure: every input comes in as an
    /// argument, so the tests need no NINA, no network and no clock.
    ///
    /// Every event gets "at" and "night" (ACP rejects needs-you and resolving
    /// events without "at", and the plugin always sends the night), source
    /// "plugin" and a title of 200 characters or fewer.
    public static class ActivityEvents {

        private static ActivityEvent Make(
            string type, string key, DateTimeOffset at, string profileId, string profileName,
            string title, string severity = ActivityEvent.SeverityInfo, string detail = null,
            Dictionary<string, object> data = null, long seq = 1
        ) {
            var refs = new Dictionary<string, object>();
            var profile = !string.IsNullOrWhiteSpace(profileName) ? profileName : profileId;
            if (!string.IsNullOrWhiteSpace(profile)) refs["profile"] = profile;

            return new ActivityEvent {
                Key = ActivityNight.Clip(key, 200),
                Seq = seq,
                Type = type,
                At = ActivityNight.Iso(at),
                Night = ActivityNight.For(at),
                Title = ActivityNight.Clip(ActivityNight.ScrubPaths(title), 200),
                Detail = detail == null ? null : ActivityNight.ScrubPaths(detail),
                Severity = severity,
                Refs = refs.Count == 0 ? null : refs,
                Data = data,
            };
        }

        private static string P(string profileId) {
            return string.IsNullOrWhiteSpace(profileId) ? "noprofile" : profileId;
        }

        /// One event per Sync for tonight run, with the plan count and the
        /// backup file's basename. Not the full path: ACP is told paths as
        /// basenames only.
        public static ActivityEvent SyncForTonight(
            SyncOutcome outcome, DateTimeOffset at, string profileId, string profileName
        ) {
            var push = outcome?.TsPush;
            var plans = push != null && push.Success ? push.Pushed.Count : (outcome?.Selected?.Count ?? 0);
            string title;
            var severity = ActivityEvent.SeverityInfo;

            if (outcome == null || !outcome.Success) {
                title = "Sync for tonight did not finish: " + (outcome?.Failure ?? "no reason given");
                severity = ActivityEvent.SeverityError;
            } else if (outcome.PushRefused) {
                title = "Sync for tonight: " + push.Failure;
                severity = ActivityEvent.SeverityWarn;
            } else if (push != null && push.Success) {
                title = $"Sync for tonight: {plans} {(plans == 1 ? "plan" : "plans")} loaded into Target Scheduler";
            } else {
                title = $"Sync for tonight: {plans} {(plans == 1 ? "plan" : "plans")} fit tonight's gear, Target Scheduler left as it was";
            }

            var backup = ActivityNight.Basename(push?.BackupPath);
            var lines = new List<string>();
            if (outcome?.Lines != null) {
                foreach (var line in outcome.Lines) {
                    // The backup line names the full path; the basename is
                    // added below instead.
                    if (!string.IsNullOrEmpty(push?.BackupPath) && line.Contains(push.BackupPath)) continue;
                    lines.Add(line);
                }
            }
            if (!string.IsNullOrEmpty(backup)) lines.Add("Database backup: " + backup);

            var data = new Dictionary<string, object> { { "plans", plans } };
            return Make(
                "sync_for_tonight", $"sync_for_tonight:{P(profileId)}:{ActivityNight.Stamp(at)}",
                at, profileId, profileName, title, severity,
                lines.Count == 0 ? null : string.Join("\n", lines), data);
        }

        public static ActivityEvent SessionStart(DateTimeOffset at, string profileId, string profileName) {
            return Make(
                "session_start", $"session_start:{P(profileId)}:{ActivityNight.Stamp(at)}",
                at, profileId, profileName, "Imaging session started");
        }

        /// Keyed on the start time, so an end sits beside its start.
        public static ActivityEvent SessionEnd(
            DateTimeOffset startedAt, DateTimeOffset at, string profileId, string profileName, string reason
        ) {
            var minutes = Math.Round((at - startedAt).TotalMinutes, 1);
            return Make(
                "session_end", $"session_end:{P(profileId)}:{ActivityNight.Stamp(startedAt)}",
                at, profileId, profileName,
                $"Imaging session ended: {(string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason)}",
                data: new Dictionary<string, object> { { "duration_min", minutes } });
        }

        public static ActivityEvent MeridianFlip(
            DateTimeOffset at, string profileId, string profileName, bool recentred
        ) {
            return Make(
                "meridian_flip", $"meridian_flip:{P(profileId)}:{ActivityNight.Stamp(at)}",
                at, profileId, profileName,
                recentred ? "Meridian flip done, recentred" : "Meridian flip done, recentring failed",
                recentred ? ActivityEvent.SeverityInfo : ActivityEvent.SeverityWarn);
        }

        public static ActivityEvent Park(DateTimeOffset at, string profileId, string profileName, bool parked) {
            return Make(
                "park", $"park:{P(profileId)}:{ActivityNight.Stamp(at)}",
                at, profileId, profileName, parked ? "Mount parked" : "Mount unparked");
        }

        public static ActivityEvent Safety(DateTimeOffset at, string profileId, string profileName, bool isSafe) {
            return Make(
                "safety", $"safety:{P(profileId)}:{ActivityNight.Stamp(at)}",
                at, profileId, profileName,
                isSafe ? "Safety monitor: safe" : "Safety monitor: unsafe",
                isSafe ? ActivityEvent.SeverityInfo : ActivityEvent.SeverityWarn);
        }

        /// Used by the trackers, which own their own seq.
        internal static ActivityEvent MakeTracked(
            string type, string key, DateTimeOffset at, string profileId, string profileName,
            string title, Dictionary<string, object> data, long seq, Dictionary<string, object> extraRefs = null
        ) {
            var e = Make(type, key, at, profileId, profileName, title, data: data, seq: seq);
            if (extraRefs != null) {
                if (e.Refs == null) e.Refs = new Dictionary<string, object>();
                foreach (var pair in extraRefs) {
                    if (pair.Value != null) e.Refs[pair.Key] = pair.Value;
                }
            }
            return e;
        }
    }

    /// One light frame that has just been saved.
    public class ImagingFrame {
        public string ProfileId { get; set; }
        public string ProfileName { get; set; }
        /// Target Scheduler's id for the target, when the frame can be tied to one.
        public int? TsTargetId { get; set; }
        public string TargetName { get; set; }
        /// The ACP plan that owns the target, when known.
        public string PlanId { get; set; }
        public string Filter { get; set; }
        public double ExposureSeconds { get; set; }
        public DateTimeOffset At { get; set; }
    }

    /// A frame taken earlier tonight, read back from Target Scheduler.
    public class ImagingSeed {
        public string Filter { get; set; }
        public double ExposureSeconds { get; set; }
        public DateTimeOffset At { get; set; }
    }

    /// Reads frames already taken tonight for a target out of Target
    /// Scheduler's acquiredimage table, through a read function so tests can
    /// use a scratch database. Rows at or after the frame that triggers the
    /// seed are left out: that frame and every later one are counted live, and
    /// Target Scheduler may already have written the triggering frame's row.
    /// That cut-off is what stops a frame being counted twice.
    public class ActivityImagingSeeder {

        private readonly Func<string, long, long, IReadOnlyList<TargetScheduler.TsAcquiredImage>> read;

        public ActivityImagingSeeder(Func<string, long, long, IReadOnlyList<TargetScheduler.TsAcquiredImage>> read) {
            this.read = read;
        }

        public IEnumerable<ImagingSeed> Seed(ImagingFrame trigger) {
            if (trigger == null || !trigger.TsTargetId.HasValue || string.IsNullOrWhiteSpace(trigger.ProfileId)) {
                return new ImagingSeed[0];
            }
            DateTimeOffset from, to;
            ActivityNight.Window(trigger.At, out from, out to);
            var cutoff = trigger.At.ToUnixTimeSeconds();
            return read(trigger.ProfileId, from.ToUnixTimeSeconds(), to.ToUnixTimeSeconds())
                .Where(i => i.TargetId == trigger.TsTargetId.Value && i.AcquiredUnix < cutoff)
                .Select(i => new ImagingSeed {
                    Filter = i.FilterName,
                    ExposureSeconds = i.ExposureSeconds,
                    At = DateTimeOffset.FromUnixTimeSeconds(i.AcquiredUnix).ToOffset(trigger.At.Offset),
                })
                .ToList();
        }
    }

    /// Keeps the running totals behind the per-target imaging event.
    ///
    /// One event per profile, target and night. Each frame returns the full
    /// current picture under the same key with a higher seq, so ACP replaces
    /// the totals and a stale retry cannot lower them.
    ///
    /// After a restart the totals are rebuilt from Target Scheduler (see the
    /// seeder), so they do not drop back to zero.
    ///
    /// seq is the larger of the key's previous seq plus one and the clock in
    /// milliseconds, rather than a bare counter. A counter would start again at
    /// 1 after NINA restarts mid-night, and ACP would ignore every update until
    /// it passed the old value. The clock keeps it rising across restarts.
    public class ImagingTracker {

        private class Group {
            public string Filter;
            public double Exposure;
            public int Count;
            public DateTimeOffset First;
            public DateTimeOffset Last;
        }

        private class State {
            public long Seq;
            public readonly Dictionary<string, Group> Groups = new Dictionary<string, Group>();
        }

        private readonly Func<DateTimeOffset> clock;
        private readonly Func<ImagingFrame, IEnumerable<ImagingSeed>> seeder;
        private readonly object gate = new object();
        private readonly Dictionary<string, State> states = new Dictionary<string, State>();

        /// <param name="seeder">Frames already taken tonight for the frame's
        /// target, asked once per key when its first frame arrives. This is what
        /// keeps the totals right after NINA restarts mid-night, when the memory
        /// of earlier frames is gone. Null means start from zero.</param>
        public ImagingTracker(Func<DateTimeOffset> clock = null, Func<ImagingFrame, IEnumerable<ImagingSeed>> seeder = null) {
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
            this.seeder = seeder;
        }

        private static void Tally(State state, string filter, double exposureSeconds, DateTimeOffset at) {
            var exposure = Math.Round(exposureSeconds, 2);
            var groupKey = (filter ?? string.Empty).ToLowerInvariant() + "|" + exposure.ToString("R", CultureInfo.InvariantCulture);
            Group group;
            if (!state.Groups.TryGetValue(groupKey, out group)) {
                group = new Group { Filter = filter ?? string.Empty, Exposure = exposure, First = at, Last = at };
                state.Groups[groupKey] = group;
            }
            group.Count++;
            if (at < group.First) group.First = at;
            if (at > group.Last) group.Last = at;
        }

        private void SeedFrom(State state, ImagingFrame frame) {
            if (seeder == null) return;
            try {
                var seeds = seeder(frame);
                if (seeds == null) return;
                foreach (var seed in seeds) Tally(state, seed.Filter, seed.ExposureSeconds, seed.At);
            } catch (Exception ex) {
                global::NINA.Core.Utility.Logger.Debug($"ACP: could not seed tonight's frame counts: {ex.Message}");
            }
        }

        public ActivityEvent AddFrame(ImagingFrame frame) {
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            var night = ActivityNight.For(frame.At);
            var idPart = frame.TsTargetId.HasValue
                ? frame.TsTargetId.Value.ToString(CultureInfo.InvariantCulture)
                : "name-" + ActivityNight.Slug(frame.TargetName);
            var key = $"img:{(string.IsNullOrWhiteSpace(frame.ProfileId) ? "noprofile" : frame.ProfileId)}:{idPart}:{night}";

            lock (gate) {
                State state;
                if (!states.TryGetValue(key, out state)) {
                    state = new State();
                    states[key] = state;
                    SeedFrom(state, frame);
                }

                Tally(state, frame.Filter, frame.ExposureSeconds, frame.At);

                state.Seq = Math.Max(state.Seq + 1, clock().ToUnixTimeMilliseconds());

                var ordered = state.Groups.Values
                    .OrderBy(g => g.Filter, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(g => g.Exposure)
                    .ToList();
                var frames = ordered.Select(g => new Dictionary<string, object> {
                    { "filter", g.Filter },
                    { "exposure_s", g.Exposure },
                    { "count", g.Count },
                    { "first_at", ActivityNight.Iso(g.First) },
                    { "last_at", ActivityNight.Iso(g.Last) },
                }).ToList();

                var total = ordered.Sum(g => g.Count);
                var perFilter = string.Join(", ", ordered
                    .GroupBy(g => g.Filter, StringComparer.OrdinalIgnoreCase)
                    .Select(g => $"{g.Key} {g.Sum(x => x.Count)}"));
                var name = string.IsNullOrWhiteSpace(frame.TargetName) ? "Unknown target" : frame.TargetName;
                var title = $"{name}: {total} {(total == 1 ? "frame" : "frames")} ({perFilter})";

                var extra = new Dictionary<string, object> {
                    { "target", frame.TargetName },
                    { "plan_id", frame.PlanId },
                };
                return ActivityEvents.MakeTracked(
                    "imaging_target", key, frame.At, frame.ProfileId, frame.ProfileName, title,
                    new Dictionary<string, object> { { "frames", frames } }, state.Seq, extra);
            }
        }
    }

    /// One autofocus run that has just finished.
    public class AutofocusRun {
        public string ProfileId { get; set; }
        public string ProfileName { get; set; }
        public DateTimeOffset At { get; set; }
        public double Position { get; set; }
        public double TemperatureC { get; set; }
    }

    /// One autofocus summary per night, updated on each run: the run count,
    /// the first and last focuser positions and the temperature range. seq
    /// follows the same rule as the imaging tracker.
    ///
    /// These totals live in memory only. They restart at zero after a NINA
    /// restart, so a restart mid-night shows the later runs alone.
    public class AutofocusTracker {

        private class State {
            public long Seq;
            public int Runs;
            public double FirstPosition;
            public double LastPosition;
            public double TempMin;
            public double TempMax;
        }

        private readonly Func<DateTimeOffset> clock;
        private readonly object gate = new object();
        private readonly Dictionary<string, State> states = new Dictionary<string, State>();

        public AutofocusTracker(Func<DateTimeOffset> clock = null) {
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public ActivityEvent AddRun(AutofocusRun run) {
            if (run == null) throw new ArgumentNullException(nameof(run));

            var night = ActivityNight.For(run.At);
            var key = $"autofocus:{(string.IsNullOrWhiteSpace(run.ProfileId) ? "noprofile" : run.ProfileId)}:{night}";

            lock (gate) {
                State s;
                if (!states.TryGetValue(key, out s)) {
                    s = new State { FirstPosition = run.Position, TempMin = run.TemperatureC, TempMax = run.TemperatureC };
                    states[key] = s;
                }
                s.Runs++;
                s.LastPosition = run.Position;
                s.TempMin = Math.Min(s.TempMin, run.TemperatureC);
                s.TempMax = Math.Max(s.TempMax, run.TemperatureC);
                s.Seq = Math.Max(s.Seq + 1, clock().ToUnixTimeMilliseconds());

                var title = string.Format(
                    CultureInfo.InvariantCulture,
                    "Autofocus: {0} {1}, focuser {2:0} to {3:0}, {4:0.#} to {5:0.#} C",
                    s.Runs, s.Runs == 1 ? "run" : "runs", s.FirstPosition, s.LastPosition, s.TempMin, s.TempMax);
                var data = new Dictionary<string, object> {
                    { "runs", s.Runs },
                    { "first_position", s.FirstPosition },
                    { "last_position", s.LastPosition },
                    { "temp_min_c", s.TempMin },
                    { "temp_max_c", s.TempMax },
                };
                return ActivityEvents.MakeTracked(
                    "autofocus_summary", key, run.At, run.ProfileId, run.ProfileName, title, data, s.Seq);
            }
        }
    }
}
