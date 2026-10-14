using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The pure half of the Activity events: night, text, and the builders that
    /// turn something NINA did into an event. No network, no NINA.
    public class ActivityEventTests {

        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 13, 22, 0, 0, TimeSpan.FromHours(11));

        private static JObject Json(ActivityEvent e) {
            using (var reader = new JsonTextReader(new StringReader(JsonConvert.SerializeObject(e))) {
                DateParseHandling = DateParseHandling.None,
            }) {
                return JObject.Load(reader);
            }
        }

        // -- Night -------------------------------------------------------------

        [Theory]
        [InlineData("2026-10-14T10:00:00+11:00", "2026-10-13")]
        [InlineData("2026-10-14T12:00:00+11:00", "2026-10-14")]
        [InlineData("2026-10-14T00:30:00+00:00", "2026-10-13")]
        [InlineData("2026-10-14T01:00:00+00:00", "2026-10-14")]
        [InlineData("2026-10-04T11:59:00+11:00", "2026-10-03")]
        [InlineData("2026-10-04T12:00:00+11:00", "2026-10-04")]
        [InlineData("2026-10-13T23:30:00+11:00", "2026-10-13")]
        public void The_night_cuts_over_at_local_noon_in_Sydney(string at, string night) {
            var when = DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(night, ActivityNight.For(when));
        }

        [Fact]
        public void At_is_iso_8601_with_an_offset() {
            var text = ActivityNight.Iso(new DateTimeOffset(2026, 10, 13, 22, 5, 9, TimeSpan.FromHours(11)));
            Assert.Equal("2026-10-13T22:05:09+11:00", text);
        }

        [Theory]
        [InlineData(@"C:\Users\Rohan1\AppData\Local\NINA\backups\db-1.sqlite", "db-1.sqlite")]
        [InlineData("/home/x/backups/db-2.sqlite", "db-2.sqlite")]
        [InlineData("plain.sqlite", "plain.sqlite")]
        [InlineData(null, null)]
        public void Basename_takes_either_separator(string path, string expected) {
            Assert.Equal(expected, ActivityNight.Basename(path));
        }

        [Fact]
        public void Clip_cuts_to_the_limit_and_flattens_lines() {
            var clipped = ActivityNight.Clip(new string('a', 300) + "\nb", 200);
            Assert.Equal(200, clipped.Length);
            Assert.Equal("a b", ActivityNight.Clip("a\r\nb", 200));
        }

        // -- Every builder obeys the live rules --------------------------------

        public static IEnumerable<object[]> Built() {
            var outcome = new SyncOutcome { Success = true };
            yield return new object[] { ActivityEvents.SyncForTonight(outcome, T0, "p1", "Rig") };
            yield return new object[] { ActivityEvents.SessionStart(T0, "p1", "Rig") };
            yield return new object[] { ActivityEvents.SessionEnd(T0, T0.AddHours(6), "p1", "Rig", "sequence finished") };
            yield return new object[] { ActivityEvents.MeridianFlip(T0, "p1", "Rig", true) };
            yield return new object[] { ActivityEvents.MeridianFlip(T0, "p1", "Rig", false) };
            yield return new object[] { ActivityEvents.Park(T0, "p1", "Rig", true) };
            yield return new object[] { ActivityEvents.Safety(T0, "p1", "Rig", false) };
            yield return new object[] { ActivityEvents.Safety(T0, "p1", "Rig", true) };
        }

        [Theory]
        [MemberData(nameof(Built))]
        public void Built_events_are_postable(ActivityEvent e) {
            Assert.Equal("plugin", e.Source);
            Assert.False(string.IsNullOrWhiteSpace(e.Key));
            Assert.True(e.Key.Length <= 200);
            Assert.False(string.IsNullOrWhiteSpace(e.Title));
            Assert.True(e.Title.Length <= 200);
            Assert.Equal("2026-10-13", e.Night);
            Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d$", e.At);
            Assert.Contains(e.Severity, new[] { "info", "warn", "error" });
            Assert.False(e.NeedsYou);
        }

        [Fact]
        public void Safety_unsafe_warns_and_safe_is_information() {
            Assert.Equal("warn", ActivityEvents.Safety(T0, "p1", "Rig", false).Severity);
            Assert.Equal("info", ActivityEvents.Safety(T0, "p1", "Rig", true).Severity);
            Assert.Equal("safety", ActivityEvents.Safety(T0, "p1", "Rig", true).Type);
        }

        [Fact]
        public void A_meridian_flip_says_whether_recentring_worked() {
            var good = ActivityEvents.MeridianFlip(T0, "p1", "Rig", true);
            var bad = ActivityEvents.MeridianFlip(T0, "p1", "Rig", false);
            Assert.Equal("meridian_flip", good.Type);
            Assert.Contains("recentred", good.Title);
            Assert.Contains("failed", bad.Title);
            Assert.Equal("warn", bad.Severity);
        }

        [Fact]
        public void Session_end_carries_the_reason_and_pairs_with_the_start() {
            var start = ActivityEvents.SessionStart(T0, "p1", "Rig");
            var end = ActivityEvents.SessionEnd(T0, T0.AddHours(6), "p1", "Rig", "NINA closed");
            Assert.Equal("session_start", start.Type);
            Assert.Equal("session_end", end.Type);
            Assert.Contains("NINA closed", end.Title);
            Assert.Equal(360.0, Convert.ToDouble(end.Data["duration_min"]));
            Assert.NotEqual(start.Key, end.Key);
        }

        // -- Test 46: sync for tonight -----------------------------------------

        private const string BackupFull =
            @"C:\Users\Rohan1\AppData\Local\NINA\Plugins\3.0.0\ACP.NINA.Plugin\backups\schedulerdb-20261013.sqlite";

        private static SyncOutcome SyncedThree() {
            var outcome = new SyncOutcome { Success = true, TsPush = new TsPushResult { Success = true, BackupPath = BackupFull } };
            outcome.TsPush.Pushed.AddRange(new[] { "Dolphin", "Sh2-240", "NGC 7000" });
            outcome.Lines.Add("Gear: ASI6200MM (mono, bin 1), L R G B Ha, EQ6R, 480 mm from the solve.");
            outcome.Lines.Add("3 plans loaded into Target Scheduler.");
            outcome.Lines.Add("The database was copied to " + BackupFull + " first.");
            return outcome;
        }

        [Fact]
        public void Sync_for_tonight_posts_one_event_with_the_plan_count_and_only_the_backup_basename() {
            var e = ActivityEvents.SyncForTonight(SyncedThree(), T0, "p1", "Rig");

            Assert.Equal("sync_for_tonight", e.Type);
            Assert.Equal(3, Convert.ToInt32(e.Data["plans"]));
            Assert.Contains("3 plans", e.Title);
            Assert.Contains("schedulerdb-20261013.sqlite", e.Detail);

            var wire = JsonConvert.SerializeObject(e);
            Assert.DoesNotContain("Rohan1", wire);
            Assert.DoesNotContain(@"C:\\Users", wire);
            Assert.DoesNotContain("AppData", wire);
        }

        [Fact]
        public void A_refused_or_failed_sync_is_not_reported_as_information() {
            var refused = new SyncOutcome { Success = true, TsPush = new TsPushResult { Success = false, Failure = "Target Scheduler is running." } };
            var failed = new SyncOutcome { Success = false, Failure = "ACP could not match the plans: boom" };

            Assert.Equal("warn", ActivityEvents.SyncForTonight(refused, T0, "p1", "Rig").Severity);
            Assert.Equal("error", ActivityEvents.SyncForTonight(failed, T0, "p1", "Rig").Severity);
            Assert.Contains("Target Scheduler is running", ActivityEvents.SyncForTonight(refused, T0, "p1", "Rig").Title);
        }

        [Fact]
        public void Paths_inside_free_text_are_cut_to_basenames() {
            var failed = new SyncOutcome {
                Success = false,
                Failure = @"Could not open C:\Users\Rohan1\AppData\Local\NINA\schedulerdb.sqlite for writing",
            };
            var e = ActivityEvents.SyncForTonight(failed, T0, "p1", "Rig");
            var wire = JsonConvert.SerializeObject(e);
            Assert.DoesNotContain("Rohan1", wire);
            Assert.Contains("schedulerdb.sqlite", wire);
        }

        // -- Test 44: imaging_target -------------------------------------------

        private static ImagingFrame Frame(DateTimeOffset at, string filter = "Ha", double exposure = 300) {
            return new ImagingFrame {
                ProfileId = "p1",
                ProfileName = "Rig",
                TsTargetId = 17,
                TargetName = "Dolphin",
                PlanId = "plan-9",
                Filter = filter,
                ExposureSeconds = exposure,
                At = at,
            };
        }

        [Fact]
        public void Two_frames_on_one_target_filter_and_exposure_make_one_event_with_count_two_and_a_higher_seq() {
            var clock = T0;
            var tracker = new ImagingTracker(() => clock);

            var first = tracker.AddFrame(Frame(T0));
            clock = clock.AddSeconds(310);
            var second = tracker.AddFrame(Frame(T0.AddSeconds(310)));

            Assert.Equal("imaging_target", second.Type);
            Assert.Equal("img:p1:17:2026-10-13", second.Key);
            Assert.Equal(first.Key, second.Key);
            Assert.True(second.Seq > first.Seq);

            var frames = (JArray)Json(second)["data"]["frames"];
            Assert.Single(frames);
            Assert.Equal("Ha", (string)frames[0]["filter"]);
            Assert.Equal(300.0, (double)frames[0]["exposure_s"]);
            Assert.Equal(2, (int)frames[0]["count"]);
            Assert.Equal("2026-10-13T22:00:00+11:00", (string)frames[0]["first_at"]);
            Assert.Equal("2026-10-13T22:05:10+11:00", (string)frames[0]["last_at"]);

            Assert.Equal(1, (int)Json(first)["data"]["frames"][0]["count"]);
            Assert.Equal("Dolphin", (string)Json(second)["refs"]["target"]);
            Assert.Equal("plan-9", (string)Json(second)["refs"]["plan_id"]);
        }

        [Fact]
        public void A_different_filter_adds_a_row_to_the_same_event() {
            var tracker = new ImagingTracker(() => T0);
            tracker.AddFrame(Frame(T0, "Ha"));
            var e = tracker.AddFrame(Frame(T0.AddSeconds(310), "OIII"));

            var frames = (JArray)Json(e)["data"]["frames"];
            Assert.Equal(2, frames.Count);
            Assert.Equal(new[] { "Ha", "OIII" }, frames.Select(f => (string)f["filter"]).ToArray());
        }

        [Fact]
        public void Seq_still_rises_when_the_clock_does_not_move() {
            var tracker = new ImagingTracker(() => T0);
            var a = tracker.AddFrame(Frame(T0));
            var b = tracker.AddFrame(Frame(T0));
            var c = tracker.AddFrame(Frame(T0));
            Assert.True(a.Seq < b.Seq && b.Seq < c.Seq);
        }

        [Fact]
        public void A_frame_after_noon_the_next_day_starts_a_new_night_and_a_new_event() {
            var tracker = new ImagingTracker(() => T0);
            var a = tracker.AddFrame(Frame(T0));
            var b = tracker.AddFrame(Frame(T0.AddHours(24)));
            Assert.NotEqual(a.Key, b.Key);
            Assert.Equal("2026-10-14", b.Night);
        }

        [Fact]
        public void A_target_with_no_Target_Scheduler_id_is_keyed_by_a_name_slug() {
            var tracker = new ImagingTracker(() => T0);
            var f = Frame(T0);
            f.TsTargetId = null;
            f.TargetName = "Sh2-308 / Dolphin";
            var e = tracker.AddFrame(f);
            Assert.StartsWith("img:p1:name-", e.Key);
            Assert.DoesNotContain("/", e.Key);
        }

        // -- autofocus_summary --------------------------------------------------

        [Fact]
        public void Autofocus_is_one_event_per_night_updated_per_run() {
            var tracker = new AutofocusTracker(() => T0);
            var one = tracker.AddRun(new AutofocusRun { ProfileId = "p1", ProfileName = "Rig", At = T0, Position = 12000, TemperatureC = 8.5 });
            var two = tracker.AddRun(new AutofocusRun { ProfileId = "p1", ProfileName = "Rig", At = T0.AddHours(2), Position = 12150, TemperatureC = 4.1 });

            Assert.Equal("autofocus_summary", two.Type);
            Assert.Equal(one.Key, two.Key);
            Assert.True(two.Seq > one.Seq);
            Assert.Equal(2, Convert.ToInt32(two.Data["runs"]));
            Assert.Equal(12000.0, Convert.ToDouble(two.Data["first_position"]));
            Assert.Equal(12150.0, Convert.ToDouble(two.Data["last_position"]));
            Assert.Equal(4.1, Convert.ToDouble(two.Data["temp_min_c"]));
            Assert.Equal(8.5, Convert.ToDouble(two.Data["temp_max_c"]));
        }
    }
}
