using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The client for POST /api/plans/match against a fake, plus the mode
    /// switch that decides what is done with the verdicts. The ACP side is
    /// being built in parallel, so this pins the shape both sides agreed on.
    public class MatchClientTests {

        private const string MatchResponseJson = @"{
          ""fingerprint_id"": ""fp_abc123"",
          ""plans"": [
            {""id"": ""p1"", ""project_name"": ""Rosette"",
             ""target"": {""name"": ""NGC 2244""},
             ""match"": {""verdict"": ""fit"", ""pixel_scale_ratio"": 1.02,
                         ""fov_ratio"": [1.4, 1.2], ""filters_missing"": [], ""reasons"": []}},
            {""id"": ""p2"", ""project_name"": ""Horsehead"",
             ""target"": {""name"": ""IC 434""},
             ""match"": {""verdict"": ""fit_with_warnings"", ""pixel_scale_ratio"": 1.12,
                         ""fov_ratio"": [0.95, 0.9], ""filters_missing"": [],
                         ""reasons"": [""field of view is only just wide enough""]}},
            {""id"": ""p3"", ""project_name"": ""Widefield Orion"",
             ""target"": {""name"": ""Orion""},
             ""match"": {""verdict"": ""no_fit"", ""pixel_scale_ratio"": 2.4,
                         ""fov_ratio"": [0.3, 0.25], ""filters_missing"": [""SII""],
                         ""reasons"": [""pixel scale is 2.4 times the plan's""]}},
            {""id"": ""p4"", ""project_name"": ""Anything goes"",
             ""target"": {""name"": ""M31""},
             ""match"": {""verdict"": ""unconstrained"", ""filters_missing"": [],
                         ""reasons"": [""the plan has no gear recorded""]}}
          ],
          ""summary"": {""fit"": 1, ""fit_with_warnings"": 1, ""no_fit"": 1, ""unconstrained"": 1}
        }";

        private static Fingerprint SampleFingerprint() {
            return new Fingerprint {
                Camera = new FingerprintCamera {
                    Name = "QHY268M",
                    SensorPx = new[] { 6252, 4176 },
                    PixelSizeUm = 3.76,
                    Colour = false,
                    Binning = 1,
                },
                Filters = new List<string> { "L", "R", "G", "B", "Ha", "OIII", "SII" },
                Mount = new FingerprintMount { Name = "EQ6-R Pro" },
                Site = new FingerprintSite { Lat = -33.87, Lon = 151.21, ElevM = 40 },
                FocalLengthMm = new FingerprintFocalLength {
                    Profile = 250.0, Solved = 540.4, Source = "solved",
                },
                PixelScaleArcsec = 1.436,
                RotationDeg = 12.3,
                ProfileName = "Travel rig",
                NinaVersion = "3.3.0.1041",
            };
        }

        // -- The request ------------------------------------------------------

        [Fact]
        public async Task Every_request_carries_the_bearer_token() {
            var fake = new FakeAcpServer().Route("/api/plans/match", MatchResponseJson);
            var client = new AcpApiClient("http://acp.local:5555", () => "s3cret", fake);

            await client.MatchPlansAsync(SampleFingerprint(), SyncMode.Everything);

            Assert.Equal("Bearer s3cret", fake.LastAuthorizationHeader);
        }

        [Fact]
        public async Task No_token_means_no_authorization_header_rather_than_an_empty_one() {
            // Same machine users have no token set, and an empty Bearer header
            // would look like a bad token to the server.
            var fake = new FakeAcpServer().Route("/api/version", "{\"version\": \"0.9\"}");
            var client = new AcpApiClient("http://127.0.0.1:5555", () => null, fake);

            await client.GetVersionAsync();

            Assert.Null(fake.LastAuthorizationHeader);
        }

        [Fact]
        public async Task The_body_is_the_fingerprint_plus_the_mode() {
            var fake = new FakeAcpServer().Route("/api/plans/match", MatchResponseJson);
            var client = new AcpApiClient("http://acp.local:5555", () => null, fake);

            await client.MatchPlansAsync(SampleFingerprint(), SyncMode.OnlyWhatFits);

            var sent = JObject.Parse(fake.LastBody);
            Assert.Equal("fit", sent["mode"].Value<string>());
            Assert.Equal("QHY268M", sent["camera"]["name"].Value<string>());
            Assert.Equal(3.76, sent["camera"]["pixel_size_um"].Value<double>());
            Assert.False(sent["camera"]["colour"].Value<bool>());
            Assert.Equal(new[] { 6252, 4176 }, sent["camera"]["sensor_px"].ToObject<int[]>());
            Assert.Equal(1, sent["camera"]["binning"].Value<int>());
            Assert.Equal("SII", sent["filters"].ToObject<string[]>()[6]);
            Assert.Equal(540.4, sent["focal_length_mm"]["solved"].Value<double>());
            Assert.Equal("solved", sent["focal_length_mm"]["source"].Value<string>());
            Assert.Equal(1.436, sent["pixel_scale_arcsec"].Value<double>());
            Assert.Equal("Travel rig", sent["profile_name"].Value<string>());
        }

        [Fact]
        public async Task Everything_mode_sends_mode_everything() {
            var fake = new FakeAcpServer().Route("/api/plans/match", MatchResponseJson);
            var client = new AcpApiClient("http://acp.local:5555", () => null, fake);

            await client.MatchPlansAsync(SampleFingerprint(), SyncMode.Everything);

            Assert.Equal("everything", JObject.Parse(fake.LastBody)["mode"].Value<string>());
        }

        // -- The response -----------------------------------------------------

        [Fact]
        public async Task The_response_deserialises_into_plans_with_verdicts() {
            var fake = new FakeAcpServer().Route("/api/plans/match", MatchResponseJson);
            var client = new AcpApiClient("http://acp.local:5555", () => null, fake);

            var response = await client.MatchPlansAsync(SampleFingerprint(), SyncMode.Everything);

            Assert.Equal("fp_abc123", response.FingerprintId);
            Assert.Equal(4, response.Plans.Count);
            Assert.Equal("Rosette", response.Plans[0].ProjectName);
            Assert.Equal("NGC 2244", response.Plans[0].Target.Name);
            Assert.Equal(MatchVerdict.Fit, response.Plans[0].Match.Verdict);
            Assert.Equal(1.02, response.Plans[0].Match.PixelScaleRatio.Value, 6);
            Assert.Equal(new[] { "SII" }, response.Plans[2].Match.FiltersMissing);
            Assert.Equal(1, response.Summary.Fit);
            Assert.Equal(1, response.Summary.Unconstrained);
        }

        [Fact]
        public async Task A_401_says_the_token_was_rejected_rather_than_looking_like_a_network_fault() {
            var fake = new FakeAcpServer { ForcedStatus = HttpStatusCode.Unauthorized };
            var client = new AcpApiClient("http://acp.local:5555", () => "wrong-token", fake);

            var ex = await Assert.ThrowsAsync<AcpUnauthorizedException>(
                () => client.MatchPlansAsync(SampleFingerprint(), SyncMode.Everything)
            );
            Assert.Equal("ACP rejected the token", ex.Message);
        }

        [Fact]
        public async Task A_401_with_no_token_set_tells_the_user_to_add_one() {
            var fake = new FakeAcpServer { ForcedStatus = HttpStatusCode.Unauthorized };
            var client = new AcpApiClient("http://acp.local:5555", () => null, fake);

            var ex = await Assert.ThrowsAsync<AcpUnauthorizedException>(() => client.GetPlansAsync());
            Assert.Contains("Add one on the ACP options page", ex.Message);
        }

        [Fact]
        public void Https_urls_are_recognised_as_such() {
            Assert.True(new AcpApiClient("https://acp.example:5555").IsHttps);
            Assert.False(new AcpApiClient("http://acp.example:5555").IsHttps);
        }

        // -- The two mode switch ----------------------------------------------

        private static MatchResponse Parsed() {
            return JsonConvert.DeserializeObject<MatchResponse>(MatchResponseJson);
        }

        [Fact]
        public void Everything_mode_takes_every_plan_including_the_ones_that_do_not_fit() {
            var selected = MatchSelection.SelectForMode(Parsed(), SyncMode.Everything);
            Assert.Equal(4, selected.Count);
        }

        [Fact]
        public void Fit_mode_takes_the_fits_and_the_unconstrained_and_nothing_else() {
            // The spec is explicit: a plan with no gear set is synced in both
            // modes, and it arrives as unconstrained.
            var selected = MatchSelection.SelectForMode(Parsed(), SyncMode.OnlyWhatFits);
            Assert.Equal(new[] { "p1", "p4" }, selected.ConvertAll(p => p.Id).ToArray());
        }

        [Fact]
        public void Fit_with_warnings_is_not_a_fit() {
            Assert.False(MatchSelection.IsFit(MatchVerdict.FitWithWarnings));
        }

        [Fact]
        public void An_unrecognised_verdict_from_a_newer_server_counts_as_not_a_fit() {
            // A server that grows a fifth verdict must not quietly widen what
            // a user asked to narrow.
            Assert.False(MatchSelection.IsFit("probably_fine"));
        }

        [Fact]
        public void Everything_mode_names_the_plans_that_do_not_suit_tonight() {
            var line = MatchSelection.Summarise(Parsed(), SyncMode.Everything);
            Assert.Contains("4 plans to load", line);
            Assert.Contains("Loaded anyway but not suited to tonight", line);
            Assert.Contains("IC 434", line);
            Assert.Contains("Orion", line);
        }

        [Fact]
        public void Fit_mode_says_how_many_of_how_many_fit() {
            var line = MatchSelection.Summarise(Parsed(), SyncMode.OnlyWhatFits);
            Assert.Contains("2 of 4 plans fit tonight's gear", line);
            Assert.Contains("Left out", line);
        }

        [Fact]
        public void Nothing_fitting_says_so_plainly_and_promises_to_leave_ts_alone() {
            var response = new MatchResponse {
                Plans = new List<MatchedPlan> {
                    new MatchedPlan {
                        Id = "p3",
                        Target = new PlanTarget { Name = "Orion" },
                        Match = new PlanMatch { Verdict = MatchVerdict.NoFit },
                    },
                },
            };
            var line = MatchSelection.Summarise(response, SyncMode.OnlyWhatFits);
            Assert.Contains("Nothing in ACP fits tonight's gear", line);
            Assert.Contains("left as it was", line);
        }

        [Fact]
        public void No_plans_at_all_is_reported_as_such_rather_than_as_a_failure() {
            Assert.Equal(
                "ACP returned no plans to consider.",
                MatchSelection.Summarise(new MatchResponse(), SyncMode.Everything)
            );
        }
    }
}
