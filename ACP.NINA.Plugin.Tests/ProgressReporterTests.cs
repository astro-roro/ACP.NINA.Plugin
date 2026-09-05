using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using ACP.NINA.Plugin.Services.TargetScheduler;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// A Target Scheduler event arriving at one end and the right payload
    /// coming out the other. The broker is left out on purpose: NINA owns the
    /// delivery, and what this has to get right is everything after delivery.
    public class ProgressReporterTests {

        private static readonly DateTimeOffset Now =
            new DateTimeOffset(2026, 9, 4, 11, 0, 0, TimeSpan.Zero);

        private static ProgressReporter Build(
            FakeTsProgressSource ts,
            FakePlanRefsSource refs,
            FakeProgressSink sink,
            bool enabled = true,
            string profileId = "profile-a",
            ITsContainerWatch watch = null
        ) {
            return new ProgressReporter(
                broker: null,
                tsSource: ts,
                refsSource: refs,
                sink: sink,
                containerWatch: watch,
                isEnabled: () => enabled,
                profileId: () => profileId,
                clock: () => Now
            );
        }

        private static (FakeTsProgressSource ts, FakePlanRefsSource refs, FakeProgressSink sink) Rosette() {
            var ts = new FakeTsProgressSource().With(41,
                Fixtures.Row("Ha", acquired: 18, exposureSeconds: 300, targetId: 41),
                Fixtures.Row("OIII", acquired: 12, exposureSeconds: 300, targetId: 41)
            );
            var refs = new FakePlanRefsSource();
            refs.Refs.Add(Fixtures.SingleTargetPlan("acp-rosette", 41));
            return (ts, refs, new FakeProgressSink());
        }

        // -- Event to payload --------------------------------------------------

        [Fact]
        public async Task A_TargetStart_carrying_a_payload_object_reports_that_plan() {
            var (ts, refs, sink) = Rosette();
            var reporter = Build(ts, refs, sink);

            await reporter.OnMessageReceived(FakeMessage.On(
                TsContainerWatch.TopicTargetStart,
                new FakeTsTargetPayload { TargetId = 41, TargetName = "NGC 2244" }
            ));

            var sent = Assert.Single(sink.Sent);
            var body = sent.Item2;
            Assert.Equal("acp-rosette", sent.Item1);
            Assert.Equal("ts", body.Source);
            Assert.Equal("2026-09-04T11:00:00+00:00", body.At);
            Assert.Equal(1.5, body.Filters["Ha"].AcquiredHours, 4);
            Assert.Equal(18, body.Filters["Ha"].AcquiredCount);
            Assert.Equal(1.0, body.Filters["OIII"].AcquiredHours, 4);
            Assert.Null(body.Force);
        }

        [Fact]
        public async Task A_TargetComplete_reports_the_same_way() {
            var (ts, refs, sink) = Rosette();

            await Build(ts, refs, sink).OnMessageReceived(FakeMessage.On(
                TsContainerWatch.TopicTargetComplete,
                new FakeTsTargetPayload { TargetId = 41 }
            ));

            Assert.Equal("acp-rosette", Assert.Single(sink.Sent).Item1);
        }

        [Fact]
        public async Task A_NewTargetStart_reports_too() {
            var (ts, refs, sink) = Rosette();

            await Build(ts, refs, sink).OnMessageReceived(
                FakeMessage.On(TsContainerWatch.TopicNewTargetStart, 41)
            );

            Assert.Equal("acp-rosette", Assert.Single(sink.Sent).Item1);
        }

        [Fact]
        public async Task A_bare_integer_content_is_read_as_the_target_id() {
            var (ts, refs, sink) = Rosette();

            await Build(ts, refs, sink).OnMessageReceived(
                FakeMessage.On(TsContainerWatch.TopicTargetStart, 41)
            );

            Assert.Equal("acp-rosette", Assert.Single(sink.Sent).Item1);
        }

        [Fact]
        public async Task A_target_id_in_the_custom_headers_is_found() {
            var (ts, refs, sink) = Rosette();

            var message = FakeMessage.On(TsContainerWatch.TopicTargetStart, "NGC 2244");
            message.CustomHeaders = new Dictionary<string, object> { { "TargetId", 41 } };
            await Build(ts, refs, sink).OnMessageReceived(message);

            Assert.Equal("acp-rosette", Assert.Single(sink.Sent).Item1);
        }

        [Fact]
        public async Task An_unreadable_payload_falls_back_to_reporting_every_plan() {
            // The payload shape cannot be checked without a running NINA, so
            // not recognising it has to be survivable. It degrades to exactly
            // what the five minute timer does.
            var ts = new FakeTsProgressSource()
                .With(41, Fixtures.Row("Ha", acquired: 18, exposureSeconds: 300))
                .With(42, Fixtures.Row("L", acquired: 30, exposureSeconds: 120));
            var refs = new FakePlanRefsSource();
            refs.Refs.Add(Fixtures.SingleTargetPlan("acp-rosette", 41));
            refs.Refs.Add(Fixtures.SingleTargetPlan("acp-horsehead", 42));
            var sink = new FakeProgressSink();

            await Build(ts, refs, sink).OnMessageReceived(
                FakeMessage.On(TsContainerWatch.TopicTargetStart, new { Nothing = "useful" })
            );

            Assert.Equal(
                new[] { "acp-horsehead", "acp-rosette" },
                sink.Sent.Select(s => s.Item1).OrderBy(s => s).ToArray()
            );
        }

        [Fact]
        public async Task A_target_no_plan_claims_sends_nothing() {
            var (ts, refs, sink) = Rosette();

            await Build(ts, refs, sink).OnMessageReceived(
                FakeMessage.On(TsContainerWatch.TopicTargetStart, 999)
            );

            Assert.Empty(sink.Sent);
        }

        [Fact]
        public async Task A_mosaic_reports_panel_one_one_whichever_panel_fired() {
            // ACP's goals are per panel. Reporting the panel that fired would
            // make actual_hours jump between panels, and since ACP only ever
            // raises the number, the busiest panel would win permanently.
            var ts = new FakeTsProgressSource()
                .With(50, Fixtures.Row("Ha", acquired: 18, exposureSeconds: 300))
                .With(53, Fixtures.Row("Ha", acquired: 90, exposureSeconds: 300));
            var refs = new FakePlanRefsSource();
            refs.Refs.Add(Fixtures.MosaicPlan("acp-veil", rows: 2, cols: 3, firstTargetId: 50));
            var sink = new FakeProgressSink();

            await Build(ts, refs, sink).OnMessageReceived(
                FakeMessage.On(TsContainerWatch.TopicTargetStart, 53)
            );

            var sent = Assert.Single(sink.Sent);
            Assert.Equal("acp-veil", sent.Item1);
            Assert.Equal(1.5, sent.Item2.Filters["Ha"].AcquiredHours, 4);
            Assert.Contains(50, ts.TargetsRead);
        }

        [Fact]
        public async Task Force_is_never_set() {
            // Hours only ever go up. A count that went backwards in Target
            // Scheduler is a culled frame or a reset project, and rewinding a
            // plan the user has watched fill up is worse than being stale.
            var (ts, refs, sink) = Rosette();
            await Build(ts, refs, sink).OnMessageReceived(
                FakeMessage.On(TsContainerWatch.TopicTargetStart, 41)
            );
            Assert.Null(Assert.Single(sink.Sent).Item2.Force);
        }

        // -- Who is allowed to move the numbers ---------------------------------

        [Fact]
        public async Task A_message_from_another_publisher_is_ignored() {
            // Another plugin publishing on a topic of the same name does not
            // get to move ACP's numbers. Same check the container watch makes.
            var (ts, refs, sink) = Rosette();

            await Build(ts, refs, sink).OnMessageReceived(
                FakeMessage.FromStranger(TsContainerWatch.TopicTargetStart, 41)
            );

            Assert.Empty(sink.Sent);
        }

        [Fact]
        public async Task An_unrelated_topic_is_ignored() {
            var (ts, refs, sink) = Rosette();
            await Build(ts, refs, sink).OnMessageReceived(
                FakeMessage.On("SomeOtherPlugin-Thing", 41)
            );
            Assert.Empty(sink.Sent);
        }

        // -- The toggle --------------------------------------------------------

        [Fact]
        public async Task Nothing_is_sent_while_the_toggle_is_off() {
            var (ts, refs, sink) = Rosette();
            await Build(ts, refs, sink, enabled: false)
                .OnMessageReceived(FakeMessage.On(TsContainerWatch.TopicTargetStart, 41));
            Assert.Empty(sink.Sent);
        }

        [Fact]
        public void The_status_line_says_so_while_the_toggle_is_off() {
            var (ts, refs, sink) = Rosette();
            Assert.Equal(ProgressStatus.Off, Build(ts, refs, sink, enabled: false).StatusLine);
        }

        // -- The container stopping ---------------------------------------------

        [Fact]
        public async Task A_container_stop_sends_one_last_report() {
            // Otherwise the night's totals are always one event short.
            var (ts, refs, sink) = Rosette();

            await Build(ts, refs, sink).OnMessageReceived(
                FakeMessage.On(TsContainerWatch.TopicContainerStopped)
            );

            Assert.Equal("acp-rosette", Assert.Single(sink.Sent).Item1);
        }

        // -- The fallback timer's gate ------------------------------------------

        [Fact]
        public void The_timer_reports_while_a_container_is_running() {
            var (ts, refs, sink) = Rosette();
            var watch = new FakeContainerWatch { IsRunning = true };
            Assert.True(Build(ts, refs, sink, watch: watch).ShouldReportOnTimer);
        }

        [Fact]
        public void The_timer_stays_quiet_while_nothing_is_imaging() {
            // An idle NINA sitting on the desktop all day never touches the
            // network.
            var (ts, refs, sink) = Rosette();
            var watch = new FakeContainerWatch { IsRunning = false };
            Assert.False(Build(ts, refs, sink, watch: watch).ShouldReportOnTimer);
        }

        [Fact]
        public void With_no_container_watch_the_timer_runs_regardless() {
            // No watch means no broker, so no event will ever arrive and the
            // timer is the only path there is. Same call v3.1 made about its
            // own missing broker: degrade, do not silently do nothing.
            var (ts, refs, sink) = Rosette();
            Assert.True(Build(ts, refs, sink, watch: null).ShouldReportOnTimer);
        }

        // -- Failures ----------------------------------------------------------

        [Fact]
        public async Task A_failing_server_lands_in_the_status_line_rather_than_throwing() {
            var (ts, refs, sink) = Rosette();
            sink.ThrowOnSend = new Exception("connection refused");
            var reporter = Build(ts, refs, sink);

            await reporter.OnMessageReceived(FakeMessage.On(TsContainerWatch.TopicTargetStart, 41));

            Assert.Contains("connection refused", reporter.StatusLine);
            Assert.Null(reporter.LastSentUtc);
        }

        [Fact]
        public async Task A_rejected_token_says_so_in_the_words_the_dock_uses() {
            var (ts, refs, sink) = Rosette();
            sink.ThrowOnSend = new AcpUnauthorizedException("ACP rejected the token");
            var reporter = Build(ts, refs, sink);

            await reporter.OnMessageReceived(FakeMessage.On(TsContainerWatch.TopicTargetStart, 41));

            Assert.Equal("ACP rejected the token", reporter.StatusLine);
        }

        [Fact]
        public async Task An_unreadable_database_lands_in_the_status_line() {
            var (ts, refs, sink) = Rosette();
            ts.ThrowOnRead = new Exception("database is locked");
            var reporter = Build(ts, refs, sink);

            await reporter.OnMessageReceived(FakeMessage.On(TsContainerWatch.TopicTargetStart, 41));

            Assert.Contains("database is locked", reporter.StatusLine);
            Assert.Empty(sink.Sent);
        }

        [Fact]
        public async Task An_unreachable_ACP_while_listing_plans_lands_in_the_status_line() {
            var (ts, refs, sink) = Rosette();
            refs.ThrowOnRead = new Exception("connection refused");
            var reporter = Build(ts, refs, sink);

            await reporter.OnMessageReceived(FakeMessage.On(TsContainerWatch.TopicTargetStart, 41));

            Assert.Contains("connection refused", reporter.StatusLine);
            Assert.Empty(sink.Sent);
        }

        [Fact]
        public async Task A_target_with_no_rows_sends_nothing_and_is_not_an_error() {
            var refs = new FakePlanRefsSource();
            refs.Refs.Add(Fixtures.SingleTargetPlan("acp-rosette", 41));
            var sink = new FakeProgressSink();
            var reporter = Build(new FakeTsProgressSource(), refs, sink);

            await reporter.OnMessageReceived(FakeMessage.On(TsContainerWatch.TopicTargetStart, 41));

            Assert.Empty(sink.Sent);
            Assert.Null(reporter.LastError);
        }

        [Fact]
        public async Task A_successful_report_clears_an_earlier_error() {
            var (ts, refs, sink) = Rosette();
            sink.ThrowOnSend = new Exception("connection refused");
            var reporter = Build(ts, refs, sink);
            await reporter.OnMessageReceived(FakeMessage.On(TsContainerWatch.TopicTargetStart, 41));
            Assert.NotNull(reporter.LastError);

            sink.ThrowOnSend = null;
            await reporter.OnMessageReceived(FakeMessage.On(TsContainerWatch.TopicTargetStart, 41));

            Assert.Null(reporter.LastError);
            Assert.Equal(Now, reporter.LastSentUtc);
        }

        // -- Status wording ----------------------------------------------------

        [Fact]
        public void The_footer_reads_the_way_the_spec_asks() {
            Assert.Equal(
                "Progress sent 22 s ago",
                ProgressStatus.Describe(true, Now.AddSeconds(-22), null, Now)
            );
        }

        [Theory]
        [InlineData(0, "0 s")]
        [InlineData(22, "22 s")]
        [InlineData(89, "89 s")]
        [InlineData(90, "2 min")]
        [InlineData(300, "5 min")]
        [InlineData(5400, "1.5 h")]
        public void Elapsed_time_is_described_coarsely(int seconds, string expected) {
            Assert.Equal(expected, ProgressStatus.Ago(TimeSpan.FromSeconds(seconds)));
        }

        [Fact]
        public void An_error_wins_over_a_time_because_it_is_what_needs_acting_on() {
            Assert.Equal(
                "ACP rejected the token",
                ProgressStatus.Describe(true, Now.AddSeconds(-22), "ACP rejected the token", Now)
            );
        }

        [Fact]
        public void Before_anything_has_been_sent_the_footer_says_so() {
            Assert.Equal(ProgressStatus.NothingYet, ProgressStatus.Describe(true, null, null, Now));
        }
    }
}
