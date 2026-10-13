using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using ACP.NINA.Plugin.Services.TargetScheduler;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    public class TsRunnerTests {

        private const string Base = "http://acp.test";
        private const string Profile = TsRowScenarios.GearProfile;

        private class FakeWriteBack : IProfileWriteBack {
            public WriteBackResult Apply(Fingerprint fingerprint, IEnumerable<Telescope> acpTelescopes, bool enabled) {
                return new WriteBackResult { Reason = "nothing to correct" };
            }
        }

        private static ScriptedAcp Acp(List<string> events, bool failTemplates) {
            return new ScriptedAcp(seen => {
                events.Add(seen.Method.Method + " " + seen.Path);
                switch (seen.Path) {
                    case "/api/gear":
                        return ScriptedAcp.Json(HttpStatusCode.OK, "{\"version\": 3, \"telescopes\": [], \"cameras\": []}");
                    case "/api/plans/match":
                        return ScriptedAcp.Json(HttpStatusCode.OK,
                            "{\"plans\": [{\"id\": \"p1\", \"target\": {\"name\": \"M42\"}, \"match\": {\"verdict\": \"fit\"}}]}");
                    case "/api/ts-templates":
                        return failTemplates
                            ? ScriptedAcp.Json(HttpStatusCode.InternalServerError, "{\"error\": \"boom\"}")
                            : ScriptedAcp.Json(HttpStatusCode.OK, "{}");
                    default:
                        return ScriptedAcp.Json(HttpStatusCode.OK, "{}");
                }
            });
        }

        private static SyncForTonightRunner RunnerFor(ScriptedAcp acp, List<string> events) {
            return new SyncForTonightRunner(
                new TsGearPluginTests.FakeFingerprints(), new FakeWriteBack(), new TsGearPluginTests.FakeTsPush(events),
                () => new SyncForTonightRunner.ActiveProfileView {
                    Id = Profile, Name = "Foo", WheelFilters = new List<string>(TsRowScenarios.WheelSlots),
                },
                () => new AcpSettings { SyncMode = SyncMode.Everything },
                url => new AcpApiClient(Base, () => "tok", acp));
        }

        [Fact]
        public async Task L3_SyncForTonightPostsTemplatesThenRereadsGearBeforeThePush() {
            var events = new List<string>();
            var outcome = await RunnerFor(Acp(events, false), events).RunAsync(null, false, default);

            Assert.True(outcome.Success, outcome.Failure);
            var match = events.IndexOf("POST /api/plans/match");
            var templates = events.IndexOf("POST /api/ts-templates");
            var push = events.IndexOf("push");
            var lastGear = events.LastIndexOf("GET /api/gear");
            Assert.True(match >= 0 && match < templates, string.Join(" | ", events));
            Assert.True(templates < lastGear, string.Join(" | ", events));
            Assert.True(lastGear < push, string.Join(" | ", events));
        }

        [Fact]
        public async Task L3_AFailedTemplatePostBeforeTheSyncForTonightPushDoesNotStopIt() {
            var events = new List<string>();
            var outcome = await RunnerFor(Acp(events, true), events).RunAsync(null, false, default);

            Assert.True(outcome.Success, outcome.Failure);
            Assert.Contains("push", events);
            // Gear was not read again, because the post that would have run the pull failed.
            Assert.Equal(1, events.Count(e => e == "GET /api/gear"));
        }
    }
}
