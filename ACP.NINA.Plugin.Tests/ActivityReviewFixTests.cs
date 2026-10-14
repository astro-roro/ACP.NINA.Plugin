using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// Fixes from the phase 2 review: restarts, text scrubbing, disabled
    /// reporting and shutdown.
    public class ActivityReviewFixTests {

        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 13, 22, 0, 0, TimeSpan.FromHours(11));

        private static ImagingFrame Frame(DateTimeOffset at, int? id, string filter = "Ha") {
            return new ImagingFrame {
                ProfileId = "p1", ProfileName = "Rig", TsTargetId = id, TargetName = "Dolphin",
                Filter = filter, ExposureSeconds = 300, At = at,
            };
        }

        private static int CountOf(ActivityEvent e) {
            var frames = (List<Dictionary<string, object>>)e.Data["frames"];
            return frames.Sum(f => Convert.ToInt32(f["count"]));
        }

        // -- 2. Autofocus is per session ---------------------------------------

        [Fact]
        public void Each_NINA_session_gets_its_own_autofocus_row() {
            var a = new AutofocusTracker(() => T0, T0);
            var b = new AutofocusTracker(() => T0.AddHours(3), T0.AddHours(3));
            var run = new AutofocusRun { ProfileId = "p1", ProfileName = "Rig", At = T0.AddHours(4), Position = 100, TemperatureC = 5 };

            var first = a.AddRun(run);
            var second = b.AddRun(run);

            Assert.NotEqual(first.Key, second.Key);
            Assert.EndsWith(":" + ActivityNight.Stamp(T0), first.Key);
            Assert.StartsWith("autofocus:p1:2026-10-13:", first.Key);
            Assert.Equal(1, Convert.ToInt32(second.Data["runs"]));
        }

        // -- 3. seq across tracker instances ------------------------------------

        [Fact]
        public void A_later_imaging_tracker_starts_above_the_last_seq_of_an_earlier_one() {
            var early = new ImagingTracker(() => T0);
            early.AddFrame(Frame(T0, 17));
            early.AddFrame(Frame(T0, 17));
            var last = early.AddFrame(Frame(T0, 17));

            var late = new ImagingTracker(() => T0.AddMinutes(10));
            var first = late.AddFrame(Frame(T0.AddMinutes(10), 17));

            Assert.Equal(last.Key, first.Key);
            Assert.True(first.Seq > last.Seq);
        }

        [Fact]
        public void A_later_autofocus_tracker_starts_above_the_last_seq_of_an_earlier_one() {
            var run = new AutofocusRun { ProfileId = "p1", ProfileName = "Rig", At = T0, Position = 100, TemperatureC = 5 };
            var early = new AutofocusTracker(() => T0, T0);
            early.AddRun(run);
            var last = early.AddRun(run);

            var late = new AutofocusTracker(() => T0.AddMinutes(10), T0);
            var first = late.AddRun(run);

            Assert.Equal(last.Key, first.Key);
            Assert.True(first.Seq > last.Seq);
        }

        // -- 4. A switched-off reporter does no lookups --------------------------

        [Fact]
        public async Task Saved_frames_cause_no_lookups_while_reporting_is_off() {
            var posted = new List<ActivityEvent>();
            var tsCalls = 0;
            var planCalls = 0;
            var active = false;
            var collector = new ActivityCollector(
                posted.Add, () => "p1", () => "Rig", () => T0,
                n => { tsCalls++; return 17; },
                (id, n) => { planCalls++; return Task.FromResult("plan"); },
                null, () => active);

            var taken = collector.HandleSavedImage("LIGHT", "Dolphin", "Ha", 300, T0.LocalDateTime);
            await collector.FlushFramesAsync();

            Assert.False(taken);
            Assert.Equal(0, tsCalls);
            Assert.Equal(0, planCalls);
            Assert.Empty(posted);

            active = true;
            Assert.True(collector.HandleSavedImage("LIGHT", "Dolphin", "Ha", 300, T0.LocalDateTime));
            await collector.FlushFramesAsync();
            Assert.Single(posted);
            Assert.False(collector.HandleSavedImage("DARK", "Dolphin", "Ha", 300, T0.LocalDateTime));
        }

        // -- 5. Restart double count -------------------------------------------

        [Fact]
        public void When_the_id_resolves_the_name_slug_counts_fold_into_the_id_key_without_double_counting_the_seed() {
            // Two frames were counted under the name slug, and the seed (from
            // the database) already covers them.
            var seeds = new List<ImagingSeed> {
                new ImagingSeed { Filter = "Ha", ExposureSeconds = 300, At = T0 },
                new ImagingSeed { Filter = "Ha", ExposureSeconds = 300, At = T0.AddMinutes(6) },
            };
            var tracker = new ImagingTracker(() => T0, f => f.TsTargetId.HasValue ? seeds : new List<ImagingSeed>());

            var s1 = tracker.AddFrame(Frame(T0, null));
            var s2 = tracker.AddFrame(Frame(T0.AddMinutes(6), null));
            Assert.StartsWith("img:p1:name-", s2.Key);
            Assert.Equal(2, CountOf(s2));

            var folded = tracker.AddFrame(Frame(T0.AddMinutes(12), 17));
            Assert.Equal("img:p1:17:2026-10-13", folded.Key);
            Assert.Equal(3, CountOf(folded));

            // A later frame whose lookup fails again still lands on the id key.
            var later = tracker.AddFrame(Frame(T0.AddMinutes(18), null));
            Assert.Equal(folded.Key, later.Key);
            Assert.Equal(4, CountOf(later));
            Assert.True(later.Seq > folded.Seq);
        }

        [Fact]
        public void Folding_keeps_the_slug_counts_when_there_is_no_seed() {
            var tracker = new ImagingTracker(() => T0);
            tracker.AddFrame(Frame(T0, null));
            tracker.AddFrame(Frame(T0.AddMinutes(6), null));

            var folded = tracker.AddFrame(Frame(T0.AddMinutes(12), 17));

            Assert.Equal(3, CountOf(folded));
        }

        [Fact]
        public async Task Frames_are_handled_one_at_a_time_in_arrival_order() {
            var posted = new List<ActivityEvent>();
            var running = 0;
            var maxRunning = 0;
            var collector = new ActivityCollector(
                posted.Add, () => "p1", () => "Rig", () => T0,
                n => 17,
                async (id, n) => {
                    var now = Interlocked.Increment(ref running);
                    maxRunning = Math.Max(maxRunning, now);
                    await Task.Delay(15);
                    Interlocked.Decrement(ref running);
                    return "plan";
                });

            for (var i = 0; i < 6; i++) collector.EnqueueLightFrame("Dolphin", "Ha", 300, T0.AddMinutes(i));
            await collector.FlushFramesAsync();

            Assert.Equal(1, maxRunning);
            Assert.Equal(6, posted.Count);
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, posted.Select(CountOf).ToArray());
        }

        // -- 6. Missing temperatures ---------------------------------------------

        [Fact]
        public void A_run_with_no_temperature_leaves_the_range_alone() {
            var tracker = new AutofocusTracker(() => T0, T0);
            tracker.AddRun(new AutofocusRun { ProfileId = "p1", At = T0, Position = 100, TemperatureC = 8 });
            var e = tracker.AddRun(new AutofocusRun { ProfileId = "p1", At = T0.AddHours(1), Position = 110, TemperatureC = double.NaN });

            Assert.Equal(8.0, Convert.ToDouble(e.Data["temp_min_c"]));
            Assert.Equal(8.0, Convert.ToDouble(e.Data["temp_max_c"]));
            Assert.Equal(2, Convert.ToInt32(e.Data["runs"]));
            Assert.DoesNotContain("NaN", JsonConvert.SerializeObject(e));
        }

        [Fact]
        public void With_no_temperature_ever_read_the_title_and_data_leave_it_out() {
            var tracker = new AutofocusTracker(() => T0, T0);
            var e = tracker.AddRun(new AutofocusRun { ProfileId = "p1", At = T0, Position = 100, TemperatureC = double.NaN });

            Assert.False(e.Data.ContainsKey("temp_min_c"));
            Assert.False(e.Data.ContainsKey("temp_max_c"));
            Assert.DoesNotContain(" C", e.Title);
            Assert.DoesNotContain("NaN", JsonConvert.SerializeObject(e));
        }

        // -- 7. Path scrubbing ----------------------------------------------------

        [Theory]
        [InlineData(@"Could not open \\nas01\astro share\NINA\Backups 2026\schedulerdb.sqlite for writing", "schedulerdb.sqlite for writing", new[] { "nas01", "astro share", "Backups" })]
        [InlineData(@"Copy failed: C:\Users\Rohan1\My Documents\Target Scheduler\db backup 1.sqlite (disk full)", "db backup 1.sqlite (disk full)", new[] { "Rohan1", "My Documents", "Target Scheduler" })]
        [InlineData(@"Wrote C:\a\b.sqlite and \\srv\s\c.sqlite.", "Wrote b.sqlite and c.sqlite.", new[] { "C:\\a", "srv" })]
        public void Any_path_in_free_text_is_cut_to_its_basename(string text, string expected, string[] gone) {
            var scrubbed = ActivityNight.ScrubPaths(text);
            Assert.Contains(expected, scrubbed);
            foreach (var g in gone) Assert.DoesNotContain(g, scrubbed);
        }

        [Fact]
        public void Text_with_no_path_is_left_alone() {
            Assert.Equal("3 plans loaded: Dolphin / Sh2-240", ActivityNight.ScrubPaths("3 plans loaded: Dolphin / Sh2-240"));
        }

        // -- 8. Shutdown ----------------------------------------------------------

        [Fact]
        public void Shutdown_ends_the_session_and_flushes_once_however_often_it_is_called() {
            var fake = new FakeAcpServer().Route("/api/activity/events",
                "{\"stored\":1,\"updated\":0,\"unchanged\":0,\"stale\":0,\"rejected\":[]}");
            var reporter = new ActivityReporter(new AcpApiClient("http://localhost:5555", () => "t", fake), () => true);
            var collector = new ActivityCollector(reporter.Post, () => "p1", () => "Rig", () => T0, null, null);
            ActivityHub.Attach(reporter, collector);
            try {
                collector.OnSequenceStarting();

                ActivityHub.Shutdown();
                ActivityHub.Shutdown();
                collector.Close();
                reporter.FlushAsync().Wait();

                Assert.Equal(1, fake.RequestCount);
                var types = ((JArray)JObject.Parse(fake.LastBody)["events"]).Select(e => (string)e["type"]).ToArray();
                Assert.Equal(new[] { "session_start", "session_end" }, types);
            } finally {
                ActivityHub.Detach(reporter);
            }
        }
    }
}
