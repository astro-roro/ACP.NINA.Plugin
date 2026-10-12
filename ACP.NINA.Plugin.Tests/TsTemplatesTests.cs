using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// T2 of docs/specs/goal-panel-and-ts-order.md: the template list goes to
    /// POST /api/ts-templates after a sync.
    public class TsTemplatesTests {

        private const string Profile = TsTestPlans.ProfileId;
        private const string Base = "http://acp.test";

        private static TsPushResult PushWithThreeTemplates() {
            var push = new TsPushResult { Success = true };
            push.Templates.Add(new TsExposureTemplate {
                ProfileId = Profile, Name = "L", FilterName = "L",
                DefaultExposure = 120, Gain = 100, Offset = 50, Bin = 1,
            });
            push.Templates.Add(new TsExposureTemplate {
                ProfileId = Profile, Name = "R", FilterName = "R",
                DefaultExposure = 180, Gain = 100, Offset = 50, Bin = 1,
            });
            push.Templates.Add(new TsExposureTemplate {
                ProfileId = Profile, Name = "R Stars", FilterName = "R",
                DefaultExposure = 15, Gain = 100, Offset = 50, Bin = 2,
            });
            return push;
        }

        [Fact]
        public async Task ASyncPostsTheThreeTemplatesOnceToTsTemplates() {
            var acp = new ScriptedAcp(_ => ScriptedAcp.Json(HttpStatusCode.OK, "{\"count\": 3}"));
            var client = new AcpApiClient(Base, () => "tok-123", acp);

            var sent = await TsTemplatesReporter.PostAsync(client, PushWithThreeTemplates(), Profile);

            Assert.True(sent);
            var post = Assert.Single(acp.Requests);
            Assert.Equal(HttpMethod.Post, post.Method);
            Assert.Equal(AcpApiClient.TsTemplatesPath, post.Path);
            Assert.Equal("/api/ts-templates", post.Path);
            Assert.Equal("Bearer tok-123", post.Authorization);

            var body = JObject.Parse(post.Body);
            Assert.Equal(Profile, (string)body["profile_id"]);
            Assert.NotNull(DateTime.Parse((string)body["sent_at"]));
            var templates = (JArray)body["templates"];
            Assert.Equal(new[] { "L", "R", "R Stars" }, templates.Select(t => (string)t["name"]).ToArray());
            var stars = templates.First(t => (string)t["name"] == "R Stars");
            Assert.Equal("R", (string)stars["filter"]);
            Assert.Equal(15.0, (double)stars["default_exposure_s"]);
            Assert.Equal(100, (int)stars["gain"]);
            Assert.Equal(50, (int)stars["offset"]);
            Assert.Equal(2, (int)stars["bin"]);
        }

        [Fact]
        public async Task APushedDatabaseHandsBackItsTemplatesForThePost() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var service = new TsPushService(
                    new FakeContainerWatch(), p => TargetSchedulerDb.Open(path), () => TsTestPlans.FrozenNow
                ) { DbPathOverride = path, MakeBackup = false };
                var push = await service.PushAsync(TsTestPlans.ThreePlans(), TsTestPlans.Gear(), Profile);
                Assert.True(push.Success, push.Failure);
                Assert.NotEmpty(push.Templates);

                var acp = new ScriptedAcp(_ => ScriptedAcp.Json(HttpStatusCode.OK, "{}"));
                await TsTemplatesReporter.PostAsync(new AcpApiClient(Base, () => null, acp), push, Profile);

                var post = Assert.Single(acp.Requests);
                var names = ((JArray)JObject.Parse(post.Body)["templates"]).Select(t => (string)t["name"]).ToList();
                Assert.Equal(push.Templates.Select(t => t.Name).OrderBy(n => n), names.OrderBy(n => n));
            }
        }

        [Fact]
        public async Task AFailedPostIsSwallowedAndAFailedPushSendsNothing() {
            var failing = new ScriptedAcp(_ => ScriptedAcp.Json(HttpStatusCode.InternalServerError, "{\"error\": \"boom\"}"));
            var sent = await TsTemplatesReporter.PostAsync(
                new AcpApiClient(Base, () => null, failing), PushWithThreeTemplates(), Profile);
            Assert.False(sent);

            var idle = new ScriptedAcp(_ => ScriptedAcp.Json(HttpStatusCode.OK, "{}"));
            var refused = new TsPushResult { Success = false, Failure = "locked" };
            Assert.False(await TsTemplatesReporter.PostAsync(new AcpApiClient(Base, () => null, idle), refused, Profile));
            Assert.Empty(idle.Requests);
        }
    }
}
