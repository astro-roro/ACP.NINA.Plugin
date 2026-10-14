using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using ACP.NINA.Plugin.Services.TargetScheduler;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The part of the collector that decides what to say, driven without NINA.
    /// The NINA event handlers only unpack their arguments and call these.
    public class ActivityCollectorTests {

        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 13, 22, 0, 0, TimeSpan.FromHours(11));

        private class Rig {
            public List<ActivityEvent> Posted = new List<ActivityEvent>();
            public DateTimeOffset Now = T0;
            public ActivityCollector Collector;

            public Rig(Func<string, int?> tsId = null, Func<int?, string, Task<string>> planId = null) {
                Collector = new ActivityCollector(
                    Posted.Add, () => "p1", () => "Rig", () => Now,
                    tsId ?? (name => 17),
                    planId ?? ((id, name) => Task.FromResult("plan-9")));
            }
        }

        // Test 44, through the collector.
        [Fact]
        public async Task Two_light_frames_post_one_key_with_count_two_and_a_higher_seq() {
            var rig = new Rig();

            await rig.Collector.OnLightFrameAsync("Dolphin", "Ha", 300, T0);
            rig.Now = rig.Now.AddSeconds(310);
            await rig.Collector.OnLightFrameAsync("Dolphin", "Ha", 300, T0.AddSeconds(310));

            Assert.Equal(2, rig.Posted.Count);
            Assert.Equal(rig.Posted[0].Key, rig.Posted[1].Key);
            Assert.Equal("img:p1:17:2026-10-13", rig.Posted[1].Key);
            Assert.True(rig.Posted[1].Seq > rig.Posted[0].Seq);
            Assert.Equal("plan-9", rig.Posted[1].Refs["plan_id"]);
            var frames = (List<Dictionary<string, object>>)rig.Posted[1].Data["frames"];
            Assert.Equal(2, Convert.ToInt32(frames[0]["count"]));
        }

        [Fact]
        public async Task A_failing_lookup_does_not_stop_the_frame_being_reported() {
            var rig = new Rig(
                tsId: name => throw new InvalidOperationException("db locked"),
                planId: (id, name) => throw new InvalidOperationException("acp down"));

            await rig.Collector.OnLightFrameAsync("Dolphin", "Ha", 300, T0);

            var e = Assert.Single(rig.Posted);
            Assert.StartsWith("img:p1:name-dolphin:", e.Key);
            Assert.False(e.Refs.ContainsKey("plan_id"));
        }

        [Fact]
        public void The_safety_monitor_posts_only_when_the_state_changes() {
            var rig = new Rig();

            rig.Collector.OnSafetyChanged(true);
            rig.Collector.OnSafetyChanged(true);
            rig.Collector.OnSafetyChanged(false);
            rig.Collector.OnSafetyChanged(false);

            Assert.Equal(new[] { "Safety monitor: safe", "Safety monitor: unsafe" }, rig.Posted.Select(e => e.Title).ToArray());
        }

        [Fact]
        public void A_sequence_makes_a_session_start_and_a_matching_session_end() {
            var rig = new Rig();

            rig.Collector.OnSequenceStarting();
            rig.Now = rig.Now.AddHours(5);
            rig.Collector.OnSequenceFinished();

            Assert.Equal(new[] { "session_start", "session_end" }, rig.Posted.Select(e => e.Type).ToArray());
            Assert.Contains("sequence finished", rig.Posted[1].Title);
            Assert.Equal(300.0, Convert.ToDouble(rig.Posted[1].Data["duration_min"]));
        }

        [Fact]
        public void A_sequence_that_ends_while_the_safety_monitor_reads_unsafe_says_so() {
            var rig = new Rig();

            rig.Collector.OnSequenceStarting();
            rig.Collector.OnSafetyChanged(false);
            rig.Collector.OnSequenceFinished();

            Assert.Contains("unsafe", rig.Posted.Last().Title);
        }

        [Fact]
        public void Closing_NINA_ends_an_open_session_once_and_only_then() {
            var rig = new Rig();

            rig.Collector.Close();
            Assert.Empty(rig.Posted);

            rig.Collector.OnSequenceStarting();
            rig.Collector.Close();
            rig.Collector.Close();

            Assert.Equal(new[] { "session_start", "session_end" }, rig.Posted.Select(e => e.Type).ToArray());
            Assert.Contains("NINA closed", rig.Posted[1].Title);
        }

        [Fact]
        public void Autofocus_runs_update_one_summary_for_the_night() {
            var rig = new Rig();

            rig.Collector.OnAutofocus(12000, 8.5, T0);
            rig.Collector.OnAutofocus(12150, 4.1, T0.AddHours(2));

            Assert.Equal(2, rig.Posted.Count);
            Assert.Equal(rig.Posted[0].Key, rig.Posted[1].Key);
            Assert.Equal(2, Convert.ToInt32(rig.Posted[1].Data["runs"]));
        }

        [Fact]
        public void Park_and_meridian_flip_post_their_events() {
            var rig = new Rig();

            rig.Collector.OnParked(true);
            rig.Collector.OnMeridianFlip(false);

            Assert.Equal(new[] { "park", "meridian_flip" }, rig.Posted.Select(e => e.Type).ToArray());
            Assert.Equal("warn", rig.Posted[1].Severity);
        }

        // -- The Sync for tonight runner posts one event -----------------------

        private class FakeWriteBack : IProfileWriteBack {
            public WriteBackResult Apply(Fingerprint fingerprint, IEnumerable<Telescope> acpTelescopes, bool enabled) {
                return new WriteBackResult { Reason = "nothing to correct" };
            }
        }

        private static SyncForTonightRunner Runner(ScriptedAcp acp, SyncMode mode, List<ActivityEvent> posted) {
            return new SyncForTonightRunner(
                new TsGearPluginTests.FakeFingerprints(), new FakeWriteBack(),
                new TsGearPluginTests.FakeTsPush(new List<string>()),
                () => new SyncForTonightRunner.ActiveProfileView {
                    Id = TsRowScenarios.GearProfile, Name = "Foo", WheelFilters = new List<string>(TsRowScenarios.WheelSlots),
                },
                () => new AcpSettings { SyncMode = mode },
                url => new AcpApiClient("http://acp.test", () => "tok", acp),
                posted.Add);
        }

        // Test 46, through the runner.
        [Fact]
        public async Task Sync_for_tonight_posts_one_sync_event_with_the_plan_count() {
            var acp = new ScriptedAcp(seen => {
                switch (seen.Path) {
                    case "/api/gear": return ScriptedAcp.Json(HttpStatusCode.OK, "{\"version\": 3, \"telescopes\": [], \"cameras\": []}");
                    case "/api/plans/match":
                        return ScriptedAcp.Json(HttpStatusCode.OK,
                            "{\"plans\": [{\"id\": \"p1\", \"target\": {\"name\": \"M42\"}, \"match\": {\"verdict\": \"fit\"}}]}");
                    default: return ScriptedAcp.Json(HttpStatusCode.OK, "{}");
                }
            });
            var posted = new List<ActivityEvent>();

            var outcome = await Runner(acp, SyncMode.Everything, posted).RunAsync(null, false, default);

            Assert.True(outcome.Success, outcome.Failure);
            var e = Assert.Single(posted);
            Assert.Equal("sync_for_tonight", e.Type);
            Assert.Equal("Foo", e.Refs["profile"]);
            Assert.Equal(outcome.TsPush.Pushed.Count, Convert.ToInt32(e.Data["plans"]));
        }

        [Fact]
        public async Task A_sync_that_could_not_match_posts_an_error_event() {
            var acp = new ScriptedAcp(seen => {
                switch (seen.Path) {
                    case "/api/gear": return ScriptedAcp.Json(HttpStatusCode.OK, "{\"version\": 3, \"telescopes\": [], \"cameras\": []}");
                    case "/api/plans/match": return ScriptedAcp.Json(HttpStatusCode.InternalServerError, "{\"error\": \"boom\"}");
                    default: return ScriptedAcp.Json(HttpStatusCode.OK, "{}");
                }
            });
            var posted = new List<ActivityEvent>();

            var outcome = await Runner(acp, SyncMode.OnlyWhatFits, posted).RunAsync(null, false, default);

            Assert.False(outcome.Success);
            var e = Assert.Single(posted);
            Assert.Equal("error", e.Severity);
        }
    }
}
