using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The progress client against a fake ACP. The server side of
    /// POST /api/plans/{id}/progress is being built in parallel on the ACP
    /// repo's feat/v3-server-side branch, so this pins the body shape both
    /// sides agreed on in docs/api.md rather than waiting for a live server.
    public class ProgressClientTests {

        private const string ProgressResponseJson = @"{
          ""ok"": true,
          ""plan"": {""id"": ""p1""},
          ""updated"": {""Ha"": 1.5},
          ""unknown_filters"": [""SII""],
          ""not_lowered"": [""OIII""]
        }";

        /// Read the body exactly as it went down the wire.
        ///
        /// Plain JObject.Parse would not do: Newtonsoft recognises an ISO 8601
        /// string while reading and hands back a DateTime rendered in the
        /// machine's own locale, so a perfectly correct "at" field comes out of
        /// the parser as "09/04/2026 21:00:00" and the test fails on something
        /// the client never sent. ACP reads the raw text, so the test should too.
        private static JObject ParseRaw(string json) {
            using (var reader = new JsonTextReader(new StringReader(json)) {
                DateParseHandling = DateParseHandling.None,
            }) {
                return JObject.Load(reader);
            }
        }

        private static ProgressRequest SampleBody() {
            return new ProgressRequest {
                Filters = new Dictionary<string, ProgressFilter>(StringComparer.OrdinalIgnoreCase) {
                    { "Ha", new ProgressFilter { AcquiredHours = 1.5, AcquiredCount = 18 } },
                },
                Source = "ts",
                At = "2026-09-04T11:00:00+00:00",
            };
        }

        [Fact]
        public async Task It_posts_to_the_plans_progress_path() {
            var fake = new FakeAcpServer().Route("/api/plans/p1/progress", ProgressResponseJson);
            var client = new AcpApiClient("http://localhost:5555", () => null, fake);

            await client.ReportProgressAsync("p1", SampleBody());

            Assert.Equal("/api/plans/p1/progress", fake.LastPath);
        }

        [Fact]
        public async Task The_body_matches_the_shape_in_the_api_docs() {
            var fake = new FakeAcpServer().Route("/api/plans/p1/progress", ProgressResponseJson);
            var client = new AcpApiClient("http://localhost:5555", () => null, fake);

            await client.ReportProgressAsync("p1", SampleBody());

            var body = ParseRaw(fake.LastBody);
            Assert.Equal("ts", (string)body["source"]);
            Assert.Equal("2026-09-04T11:00:00+00:00", (string)body["at"]);
            // And as literal text, because "at" is the one field a JSON
            // library is most likely to helpfully rewrite on the way past.
            Assert.Contains("\"at\":\"2026-09-04T11:00:00+00:00\"", fake.LastBody);
            Assert.Equal(1.5, (double)body["filters"]["Ha"]["acquired_hours"]);
            Assert.Equal(18, (int)body["filters"]["Ha"]["acquired_count"]);
        }

        [Fact]
        public async Task Force_is_left_out_of_the_body_entirely_when_it_is_not_set() {
            // A body carrying "force": false and a body with no force at all
            // mean the same thing to ACP, but only one of them makes it
            // obvious from a packet capture that the plugin never forces.
            var fake = new FakeAcpServer().Route("/api/plans/p1/progress", ProgressResponseJson);
            var client = new AcpApiClient("http://localhost:5555", () => null, fake);

            await client.ReportProgressAsync("p1", SampleBody());

            Assert.Null(ParseRaw(fake.LastBody)["force"]);
        }

        [Fact]
        public async Task It_sends_the_bearer_token_when_one_is_stored() {
            var fake = new FakeAcpServer().Route("/api/plans/p1/progress", ProgressResponseJson);
            var client = new AcpApiClient("http://localhost:5555", () => "sekrit", fake);

            await client.ReportProgressAsync("p1", SampleBody());

            Assert.Equal("Bearer sekrit", fake.LastAuthorizationHeader);
        }

        [Fact]
        public async Task It_sends_no_authorization_header_when_no_token_is_stored() {
            var fake = new FakeAcpServer().Route("/api/plans/p1/progress", ProgressResponseJson);
            var client = new AcpApiClient("http://localhost:5555", () => null, fake);

            await client.ReportProgressAsync("p1", SampleBody());

            Assert.Null(fake.LastAuthorizationHeader);
        }

        [Fact]
        public async Task It_reads_back_what_changed_what_was_dropped_and_what_was_held() {
            var fake = new FakeAcpServer().Route("/api/plans/p1/progress", ProgressResponseJson);
            var client = new AcpApiClient("http://localhost:5555", () => null, fake);

            var resp = await client.ReportProgressAsync("p1", SampleBody());

            Assert.True(resp.Ok);
            Assert.Equal(1.5, resp.Updated["Ha"]);
            Assert.Equal(new[] { "SII" }, resp.UnknownFilters);
            Assert.Equal(new[] { "OIII" }, resp.NotLowered);
        }

        [Fact]
        public async Task The_summary_line_names_all_three_outcomes() {
            var fake = new FakeAcpServer().Route("/api/plans/p1/progress", ProgressResponseJson);
            var client = new AcpApiClient("http://localhost:5555", () => null, fake);

            var summary = (await client.ReportProgressAsync("p1", SampleBody())).ToShortString();

            Assert.Contains("Ha 1.5h", summary);
            Assert.Contains("SII", summary);
            Assert.Contains("OIII", summary);
        }

        [Fact]
        public async Task A_rejected_token_raises_the_unauthorized_type() {
            var fake = new FakeAcpServer { ForcedStatus = HttpStatusCode.Unauthorized };
            var client = new AcpApiClient("http://localhost:5555", () => "wrong", fake);

            var ex = await Assert.ThrowsAsync<AcpUnauthorizedException>(
                () => client.ReportProgressAsync("p1", SampleBody())
            );
            Assert.Equal("ACP rejected the token", ex.Message);
        }

        [Fact]
        public async Task An_empty_plan_id_is_refused_before_anything_is_sent() {
            var fake = new FakeAcpServer().Route("/api/plans/p1/progress", ProgressResponseJson);
            var client = new AcpApiClient("http://localhost:5555", () => null, fake);

            await Assert.ThrowsAsync<ArgumentException>(
                () => client.ReportProgressAsync("  ", SampleBody())
            );
            Assert.Equal(0, fake.RequestCount);
        }
    }
}
