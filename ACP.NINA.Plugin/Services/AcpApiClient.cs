using ACP.NINA.Plugin.Models;
using Newtonsoft.Json;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services {

    /// Raised when ACP answers 401. Kept separate from the generic transport
    /// failure so the dock can say "ACP rejected the token" instead of a
    /// connection error, which is the difference between the user fixing their
    /// token and the user restarting their router.
    public class AcpUnauthorizedException : Exception {
        public AcpUnauthorizedException(string message) : base(message) { }
    }

    /// The transport the whole plugin talks to ACP through. Every request
    /// carries Authorization: Bearer when a token is stored.
    ///
    /// The HttpClient is static and shared because that is what HttpClient
    /// wants, so the token cannot live in DefaultRequestHeaders: it is set on
    /// each request instead. That also means a token changed on the Options
    /// page takes effect on the next call with no restart.
    public class AcpApiClient {

        private static readonly HttpClient http = new HttpClient {
            // Long enough to absorb a TS sync's BEGIN IMMEDIATE + backup +
            // upsert + plans.json save without timing out, short enough that
            // an unreachable ACP doesn't lock the UI for ages.
            Timeout = TimeSpan.FromSeconds(30),
        };

        private readonly string baseUrl;
        private readonly Func<string> tokenSource;
        private readonly HttpClient client;

        public AcpApiClient(string baseUrl) : this(baseUrl, TokenStore.Read, null) { }

        /// The token is read through a delegate rather than captured at
        /// construction so tests can supply one without touching Credential
        /// Manager, and so a live client picks up a token edited mid-session.
        ///
        /// The handler is the seam the tests use. Passing one builds a private
        /// HttpClient over it, so a fake ACP can answer without a socket, which
        /// is what lets the match client be exercised before the server side
        /// exists. Passing null uses the shared client, which is what
        /// everything in the plugin does.
        public AcpApiClient(string baseUrl, Func<string> tokenSource, HttpMessageHandler handler = null) {
            // Trim trailing slashes so we can concatenate paths cleanly.
            this.baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
            this.tokenSource = tokenSource ?? (() => null);
            this.client = handler == null
                ? http
                : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        /// True when the configured URL is https, which the v3 spec allows and
        /// which nothing here has to do anything special about: the default
        /// certificate validation applies, so a self signed certificate is
        /// refused. Pinning a self signed fingerprint is not in v3.0.
        public bool IsHttps =>
            baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // -- Endpoints ------------------------------------------------------

        /// GET /api/version. Cheap enough to poll every 60 seconds.
        public async Task<VersionInfo> GetVersionAsync(CancellationToken ct = default) {
            var json = await SendAsync(HttpMethod.Get, "/api/version", null, ct).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<VersionInfo>(json) ?? new VersionInfo();
        }

        /// Probe ACP for liveness. /api/version is the right endpoint since
        /// PR #69 landed; /api/plans is the fallback for a server old enough
        /// not to have it, which keeps the v1 dock working against a v1 server.
        public async Task<string> ProbeAsync(CancellationToken ct = default) {
            try {
                var v = await GetVersionAsync(ct).ConfigureAwait(false);
                return $"ACP {v?.Version ?? "responding"} (API v{v?.ApiVersion ?? 0})";
            } catch (AcpUnauthorizedException) {
                throw;
            } catch (HttpRequestException) {
                var json = await SendAsync(HttpMethod.Get, "/api/plans", null, ct).ConfigureAwait(false);
                var doc = JsonConvert.DeserializeObject<PlansResponse>(json);
                return $"ACP responding (plans schema v{doc?.Version ?? 0})";
            }
        }

        public async Task<PlansResponse> GetPlansAsync(CancellationToken ct = default) {
            var json = await SendAsync(HttpMethod.Get, "/api/plans", null, ct).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<PlansResponse>(json) ?? new PlansResponse();
        }

        public async Task<GearResponse> GetGearAsync(CancellationToken ct = default) {
            var json = await SendAsync(HttpMethod.Get, "/api/gear", null, ct).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<GearResponse>(json) ?? new GearResponse();
        }

        /// POST /api/plans/match. Body is the fingerprint plus "mode". Returns
        /// every plan with a verdict, including the ones that do not fit, so
        /// the dock can say why something was left out.
        public async Task<MatchResponse> MatchPlansAsync(
            Fingerprint fingerprint,
            SyncMode mode,
            CancellationToken ct = default
        ) {
            if (fingerprint == null) throw new ArgumentNullException(nameof(fingerprint));
            fingerprint.Mode = mode.ToWire();
            var body = JsonConvert.SerializeObject(fingerprint);
            var json = await SendAsync(HttpMethod.Post, "/api/plans/match", body, ct).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<MatchResponse>(json) ?? new MatchResponse();
        }

        /// POST to the private nina_ts_sync extension. Body is
        /// {profile_id: "<NINA profile GUID>"}. On success returns the
        /// SyncReport plus paths to the DB backup and plans.json backup ACP
        /// wrote before the transaction.
        public async Task<TsSyncResponse> SyncToTsAsync(string profileId, CancellationToken ct = default) {
            var body = JsonConvert.SerializeObject(new { profile_id = profileId });
            var json = await SendAsync(
                HttpMethod.Post, "/api/ext/nina-ts-sync/sync", body, ct
            ).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<TsSyncResponse>(json) ?? new TsSyncResponse();
        }

        // -- Transport ------------------------------------------------------

        private async Task<string> SendAsync(
            HttpMethod method, string path, string jsonBody, CancellationToken ct
        ) {
            using (var req = new HttpRequestMessage(method, baseUrl + path)) {
                var token = tokenSource();
                if (!string.IsNullOrWhiteSpace(token)) {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                if (jsonBody != null) {
                    req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                }

                using (var resp = await client.SendAsync(req, ct).ConfigureAwait(false)) {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (resp.StatusCode == HttpStatusCode.Unauthorized) {
                        // The one status the user can actually act on, so it
                        // gets its own type and its own words in the dock.
                        throw new AcpUnauthorizedException(
                            string.IsNullOrWhiteSpace(token)
                                ? "ACP needs a token. Add one on the ACP options page."
                                : "ACP rejected the token"
                        );
                    }
                    if (!resp.IsSuccessStatusCode) {
                        throw new HttpRequestException(
                            $"HTTP {(int)resp.StatusCode}: {ExtractErrorMessage(text) ?? text}"
                        );
                    }
                    return text;
                }
            }
        }

        private static string ExtractErrorMessage(string json) {
            try {
                var err = JsonConvert.DeserializeAnonymousType(json, new { error = "" });
                return string.IsNullOrEmpty(err?.error) ? null : err.error;
            } catch {
                return null;
            }
        }
    }
}
