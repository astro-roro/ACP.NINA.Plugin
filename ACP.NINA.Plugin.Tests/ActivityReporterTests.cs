using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The queue and the sender, against a fake ACP. The route is already live
    /// in ACP, so these pin the plugin's side of its rules: batches of at most
    /// 100, rejected events never retried, a failed batch tried again with the
    /// same keys, and silence for the session after the first 404 or 403.
    public class ActivityReporterTests {

        private const string OkJson = "{\"stored\":1,\"updated\":0,\"unchanged\":0,\"stale\":0,\"rejected\":[]}";
        private const string Path = "/api/activity/events";

        private static ActivityEvent Ev(string key, long seq = 1, string severity = "info", bool needsYou = false) {
            return new ActivityEvent {
                Key = key, Seq = seq, Type = "park", Title = "t " + key, Severity = severity,
                At = "2026-10-13T22:00:00+11:00", Night = "2026-10-13", NeedsYou = needsYou,
                ResolveKey = needsYou ? "r:" + key : null,
            };
        }

        private static JObject ParseRaw(string json) {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None }) {
                return JObject.Load(reader);
            }
        }

        private static string[] KeysIn(string body) {
            return ((JArray)ParseRaw(body)["events"]).Select(e => (string)e["key"]).ToArray();
        }

        private class Rig {
            public FakeAcpServer Fake = new FakeAcpServer().Route(Path, OkJson);
            public List<string> Log = new List<string>();
            public ActivityReporter Reporter;
            public Func<bool> Enabled = () => true;

            public Rig(int capacity = 500) {
                var client = new AcpApiClient("http://localhost:5555", () => "tok", Fake);
                Reporter = new ActivityReporter(client, () => Enabled(), null, Log.Add, capacity: capacity);
            }
        }

        // -- Queue ---------------------------------------------------------------

        [Fact]
        public void A_full_queue_drops_the_oldest_information_event_first() {
            var q = new ActivityQueue(3);
            q.Enqueue(Ev("err", severity: "error"));
            q.Enqueue(Ev("i1"));
            q.Enqueue(Ev("i2"));
            q.Enqueue(Ev("i3"));

            Assert.Equal(new[] { "err", "i2", "i3" }, q.Snapshot().Select(e => e.Key).ToArray());
            Assert.Equal(1, q.DroppedCount);
        }

        [Fact]
        public void Needs_you_and_warnings_outlive_plain_information() {
            var q = new ActivityQueue(3);
            q.Enqueue(Ev("need", needsYou: true));
            q.Enqueue(Ev("warn", severity: "warn"));
            q.Enqueue(Ev("i1"));
            q.Enqueue(Ev("i2"));

            Assert.Equal(new[] { "need", "warn", "i2" }, q.Snapshot().Select(e => e.Key).ToArray());
        }

        [Fact]
        public void With_nothing_droppable_the_oldest_event_goes() {
            var q = new ActivityQueue(2);
            q.Enqueue(Ev("e1", severity: "error"));
            q.Enqueue(Ev("e2", severity: "error"));
            q.Enqueue(Ev("e3", severity: "error"));
            Assert.Equal(new[] { "e2", "e3" }, q.Snapshot().Select(e => e.Key).ToArray());
        }

        [Fact]
        public void The_same_key_queued_twice_keeps_the_newer_seq_only() {
            var q = new ActivityQueue();
            q.Enqueue(Ev("img", seq: 5));
            q.Enqueue(Ev("img", seq: 9));
            q.Enqueue(Ev("img", seq: 7));

            var only = Assert.Single(q.Snapshot());
            Assert.Equal(9, only.Seq);
        }

        [Fact]
        public void A_requeued_batch_goes_back_in_front_but_never_over_a_newer_event() {
            var q = new ActivityQueue();
            var a = Ev("a");
            var img = Ev("img", seq: 5);
            q.Enqueue(a);
            q.Enqueue(img);
            var batch = q.TakeBatch(100);
            q.Enqueue(Ev("img", seq: 8));
            q.Enqueue(Ev("later"));

            q.Requeue(batch);

            var now = q.Snapshot();
            Assert.Equal(new[] { "a", "img", "later" }, now.Select(e => e.Key).ToArray());
            Assert.Equal(8, now.Single(e => e.Key == "img").Seq);
        }

        // -- Sending -----------------------------------------------------------------

        [Fact]
        public async Task A_flush_posts_the_events_in_the_shape_ACP_takes() {
            var rig = new Rig();
            rig.Reporter.Post(Ev("park:1"));

            await rig.Reporter.FlushAsync();

            Assert.Equal(Path, rig.Fake.LastPath);
            Assert.Equal("Bearer tok", rig.Fake.LastAuthorizationHeader);
            var body = ParseRaw(rig.Fake.LastBody);
            var e = (JObject)((JArray)body["events"])[0];
            Assert.Equal("plugin", (string)e["source"]);
            Assert.Equal("park:1", (string)e["key"]);
            Assert.Equal("2026-10-13", (string)e["night"]);
            Assert.Contains("\"at\":\"2026-10-13T22:00:00+11:00\"", rig.Fake.LastBody);
            Assert.False(e.ContainsKey("needs_you"));
            Assert.Equal(0, rig.Reporter.QueuedCount);
        }

        [Fact]
        public async Task A_flush_sends_at_most_100_events_per_batch() {
            var rig = new Rig();
            for (var i = 0; i < 250; i++) rig.Reporter.Post(Ev("k" + i));

            var sent = await rig.Reporter.FlushAsync();

            Assert.Equal(250, sent);
            Assert.Equal(3, rig.Fake.RequestCount);
            Assert.Equal(new[] { 100, 100, 50 }, rig.Fake.Bodies.Select(b => KeysIn(b).Length).ToArray());
        }

        // Test 45, first half.
        [Fact]
        public async Task A_failed_batch_is_retried_with_the_same_keys() {
            var rig = new Rig();
            rig.Reporter.Post(Ev("a"));
            rig.Reporter.Post(Ev("b", seq: 3));
            rig.Fake.ForcedStatus = HttpStatusCode.ServiceUnavailable;

            var first = await rig.Reporter.FlushAsync();
            Assert.Equal(0, first);
            Assert.Equal(2, rig.Reporter.QueuedCount);

            rig.Fake.ForcedStatus = null;
            var second = await rig.Reporter.FlushAsync();

            Assert.Equal(2, second);
            Assert.Equal(2, rig.Fake.RequestCount);
            Assert.Equal(KeysIn(rig.Fake.Bodies[0]), KeysIn(rig.Fake.Bodies[1]));
            Assert.Equal(0, rig.Reporter.QueuedCount);
            Assert.False(rig.Reporter.IsDisabled);
        }

        [Fact]
        public async Task A_401_keeps_the_events_for_the_next_tick() {
            var rig = new Rig();
            rig.Reporter.Post(Ev("a"));
            rig.Fake.ForcedStatus = HttpStatusCode.Unauthorized;

            await rig.Reporter.FlushAsync();

            Assert.Equal(1, rig.Reporter.QueuedCount);
            Assert.False(rig.Reporter.IsDisabled);
        }

        // Test 45, second half.
        [Theory]
        [InlineData(HttpStatusCode.NotFound)]
        [InlineData(HttpStatusCode.Forbidden)]
        public async Task The_first_404_or_403_stops_posting_for_the_session_with_one_log_line(HttpStatusCode status) {
            var rig = new Rig();
            rig.Reporter.Post(Ev("a"));
            rig.Fake.ForcedStatus = status;

            await rig.Reporter.FlushAsync();

            Assert.True(rig.Reporter.IsDisabled);
            Assert.Single(rig.Log);
            Assert.Equal(0, rig.Reporter.QueuedCount);

            rig.Fake.ForcedStatus = null;
            rig.Reporter.Post(Ev("b"));
            await rig.Reporter.FlushAsync();

            Assert.Equal(1, rig.Fake.RequestCount);
            Assert.Equal(0, rig.Reporter.QueuedCount);
            Assert.Single(rig.Log);
        }

        [Fact]
        public async Task Rejected_events_are_logged_and_never_retried() {
            var rig = new Rig();
            rig.Fake.Route(Path,
                "{\"stored\":1,\"updated\":0,\"unchanged\":0,\"stale\":0,\"rejected\":[{\"key\":\"bad\",\"error\":\"title too long\"}]}");
            rig.Reporter.Post(Ev("good"));
            rig.Reporter.Post(Ev("bad"));

            await rig.Reporter.FlushAsync();
            await rig.Reporter.FlushAsync();

            Assert.Equal(1, rig.Fake.RequestCount);
            Assert.Equal(0, rig.Reporter.QueuedCount);
            Assert.Contains(rig.Log, l => l.Contains("bad") && l.Contains("title too long"));
        }

        [Theory]
        [InlineData(HttpStatusCode.RequestEntityTooLarge)]
        [InlineData(HttpStatusCode.BadRequest)]
        public async Task A_batch_ACP_refuses_outright_is_dropped_not_retried(HttpStatusCode status) {
            var rig = new Rig();
            rig.Reporter.Post(Ev("a"));
            rig.Fake.ForcedStatus = status;

            await rig.Reporter.FlushAsync();
            rig.Fake.ForcedStatus = null;
            await rig.Reporter.FlushAsync();

            Assert.Equal(1, rig.Fake.RequestCount);
            Assert.Equal(0, rig.Reporter.QueuedCount);
            Assert.False(rig.Reporter.IsDisabled);
        }

        [Fact]
        public async Task Nothing_is_queued_or_sent_while_the_option_is_off() {
            var rig = new Rig();
            rig.Enabled = () => false;

            rig.Reporter.Post(Ev("a"));
            await rig.Reporter.FlushAsync();

            Assert.Equal(0, rig.Reporter.QueuedCount);
            Assert.Equal(0, rig.Fake.RequestCount);
        }

        [Fact]
        public async Task The_queue_cap_is_applied_when_ACP_is_unreachable() {
            var rig = new Rig(capacity: 5);
            rig.Fake.ForcedStatus = HttpStatusCode.ServiceUnavailable;
            for (var i = 0; i < 12; i++) rig.Reporter.Post(Ev("k" + i));

            await rig.Reporter.FlushAsync();

            Assert.Equal(5, rig.Reporter.QueuedCount);
        }
    }
}
