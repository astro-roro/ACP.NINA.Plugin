using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// Stop buttons and the time limit on ACP actions.
    public class ActionRunTests {

        [Fact]
        public async Task TheTimeLimitStopsTheRunAndSaysSo() {
            using (var run = new ActionRun(default, TimeSpan.FromMilliseconds(50))) {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => Task.Delay(TimeSpan.FromSeconds(5), run.Token));
                Assert.True(run.TimedOut);
                Assert.Contains("minute limit", run.Why("Sync for tonight"));
            }
        }

        [Fact]
        public void StopIsNotATimeout() {
            using (var run = new ActionRun(default, TimeSpan.FromMinutes(10))) {
                Assert.True(run.Stop());
                Assert.False(run.Stop());
                Assert.True(run.Token.IsCancellationRequested);
                Assert.False(run.TimedOut);
                Assert.Equal("Sync All to TS stopped.", run.Why("Sync All to TS"));
            }
        }

        [Fact]
        public void NinaCancellingIsNotATimeout() {
            using (var outer = new CancellationTokenSource())
            using (var run = new ActionRun(outer.Token, TimeSpan.FromMinutes(10))) {
                outer.Cancel();
                Assert.True(run.Token.IsCancellationRequested);
                Assert.False(run.TimedOut);
            }
        }

        [Fact]
        public void ZeroMeansNoLimit() {
            using (var run = new ActionRun(default, TimeSpan.Zero)) {
                Assert.False(run.Token.IsCancellationRequested);
            }
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(3, 3)]
        [InlineData(99, 20)]
        public void SolveAttemptsAreClamped(int given, int expected) {
            Assert.Equal(expected, AcpSettings.ClampSolveAttempts(given));
        }

        [Theory]
        [InlineData(-5, 0)]
        [InlineData(10, 10)]
        [InlineData(1000, 240)]
        public void TheTimeLimitIsClamped(int given, int expected) {
            Assert.Equal(expected, AcpSettings.ClampTimeoutMinutes(given));
        }

        [Fact]
        public async Task SyncAllStoppedBeforeThePushLeavesTargetSchedulerAlone() {
            var events = new List<string>();
            var acp = new ScriptedAcp(seen => {
                events.Add(seen.Method.Method + " " + seen.Path);
                if (seen.Path == "/api/plans") return ScriptedAcp.Json(HttpStatusCode.OK, "{\"version\": 2, \"plans\": []}");
                return ScriptedAcp.Json(HttpStatusCode.OK, "{}");
            });
            using (var stop = new CancellationTokenSource()) {
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TsSyncAllRunner.RunAsync(
                    new AcpApiClient("http://acp.test", () => "tok", acp), new TsGearPluginTests.FakeFingerprints(),
                    new TsGearPluginTests.FakeTsPush(events), TsRowScenarios.GearProfile, "Foo",
                    TsRowScenarios.WheelSlots, SyncMode.Everything, stop.Token));
            }
            Assert.DoesNotContain("push", events);
        }
    }
}
