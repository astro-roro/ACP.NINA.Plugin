using ACP.NINA.Plugin.Models;
using Newtonsoft.Json;
using System;
using System.IO;
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

    /// Raised when ACP answers with any other failure status. It is still an
    /// HttpRequestException, so every existing catch keeps working, but it
    /// carries the status and ACP's own words, which the upload needs to tell
    /// a 413 from a 415 without parsing a message.
    public class AcpHttpException : HttpRequestException {
        /// Never null here, unlike the base class's nullable StatusCode.
        public HttpStatusCode Status { get; }

        /// The "error" field of ACP's JSON body, or null when there was none.
        public string ServerMessage { get; }

        public AcpHttpException(HttpStatusCode status, string serverMessage, string body)
            : base($"HTTP {(int)status}: {serverMessage ?? body}", null, status) {
            Status = status;
            ServerMessage = serverMessage;
        }
    }

    /// The transport the whole plugin talks to ACP through. Every request
    /// carries Authorization: Bearer when a token is stored.
    ///
    /// The HttpClient is static and shared because that is what HttpClient
    /// wants, so the token cannot live in DefaultRequestHeaders: it is set on
    /// each request instead. That also means a token changed on the Options
    /// page takes effect on the next call with no restart.
    public class AcpApiClient : IProgressSink {

        private static readonly HttpClient http = new HttpClient {
            // Long enough to absorb a TS sync's BEGIN IMMEDIATE + backup +
            // upsert + plans.json save without timing out, short enough that
            // an unreachable ACP doesn't lock the UI for ages.
            Timeout = TimeSpan.FromSeconds(30),
        };

        /// The TS upload sends the whole database file, which can take minutes
        /// over a VPN from a dark site. It gets its own client so the 30 second
        /// limit above stays as it is for everything else.
        private static readonly HttpClient uploadHttp = new HttpClient {
            Timeout = TimeSpan.FromMinutes(10),
        };

        private readonly string baseUrl;
        private readonly Func<string> tokenSource;
        private readonly HttpClient client;
        private readonly HttpClient uploadClient;

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
            this.uploadClient = handler == null
                ? uploadHttp
                : new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        }

        /// The configured server URL with no trailing slash.
        public string BaseUrl => baseUrl;

        /// Turn a URL ACP handed back into one to call or open. A path is
        /// joined to the configured server, the same way every request here
        /// is built. An absolute URL is kept only when it points at the same
        /// server; otherwise its path is joined to the configured one, so a
        /// reply can never send the browser or the token somewhere else.
        public string Resolve(string url) {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (Uri.TryCreate(url, UriKind.Absolute, out var abs) &&
                (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps)) {
                if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var mine) &&
                    string.Equals(abs.GetLeftPart(UriPartial.Authority),
                        mine.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)) {
                    return abs.ToString();
                }
                return baseUrl + abs.PathAndQuery;
            }
            return baseUrl + (url.StartsWith("/") ? url : "/" + url);
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

        /// POST /api/plans/{id}/progress. Reports what a session has actually
        /// acquired against a plan's per filter goals.
        ///
        /// ACP raises actual_hours and never lowers it unless the body says
        /// force, which this plugin never does, so sending the same numbers
        /// twice is free and sending them late is harmless. Filters the plan
        /// has no goal for come back in unknown_filters rather than being
        /// invented, and are worth a log line but not a failure.
        public async Task<ProgressResponse> ReportProgressAsync(
            string planId, ProgressRequest body, CancellationToken ct = default
        ) {
            if (string.IsNullOrWhiteSpace(planId)) {
                throw new ArgumentException("A plan id is required.", nameof(planId));
            }
            if (body == null) throw new ArgumentNullException(nameof(body));

            var json = JsonConvert.SerializeObject(body);
            var path = $"/api/plans/{Uri.EscapeDataString(planId)}/progress";
            var text = await SendAsync(HttpMethod.Post, path, json, ct).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<ProgressResponse>(text) ?? new ProgressResponse();
        }

        // -- TS upload (spec: ts-upload-import.md, Part A) -------------------

        public const string TsUploadPath = "/api/ext/nina-ts-sync/import/uploads";
        public const string TsLinksPath = "/api/ext/nina-ts-sync/links";

        /// POST the consistent copy of TS's database as multipart/form-data,
        /// with a `file` part and a `meta` JSON part. ACP answers 202 with the
        /// upload id and where to look next. `progress` gets the fraction of
        /// the file sent, 0 to 1.
        public async Task<TsUploadAccepted> UploadTsDatabaseAsync(
            string filePath, TsUploadMeta meta, IProgress<double> progress = null,
            CancellationToken ct = default
        ) {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentNullException(nameof(filePath));
            if (meta == null) throw new ArgumentNullException(nameof(meta));

            using (var stream = new FileStream(
                       filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            using (var form = new MultipartFormDataContent()) {
                var filePart = new ProgressStreamContent(stream, progress);
                filePart.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.sqlite3");
                form.Add(filePart, "file", "schedulerdb.sqlite");

                var metaPart = new StringContent(
                    JsonConvert.SerializeObject(meta), Encoding.UTF8, "application/json");
                form.Add(metaPart, "meta");

                var json = await SendAsync(uploadClient, HttpMethod.Post, baseUrl + TsUploadPath, form, ct)
                    .ConfigureAwait(false);
                return JsonConvert.DeserializeObject<TsUploadAccepted>(json) ?? new TsUploadAccepted();
            }
        }

        /// GET the upload's status. `statusUrl` is what the 202 named, as a
        /// path or a URL on this server.
        public async Task<TsUploadStatus> GetTsUploadStatusAsync(string statusUrl, CancellationToken ct = default) {
            var url = Resolve(statusUrl);
            if (url == null) throw new ArgumentNullException(nameof(statusUrl));
            var json = await SendAsync(client, HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            return TsUploadStatus.Parse(json);
        }

        /// POST the push state for one profile, so ACP's record of the last
        /// sync on this rig matches what was just written.
        public async Task PostTsLinksAsync(TsLinksRequest body, CancellationToken ct = default) {
            if (body == null) throw new ArgumentNullException(nameof(body));
            var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            await SendAsync(client, HttpMethod.Post, baseUrl + TsLinksPath, content, ct).ConfigureAwait(false);
        }

        // -- Transport ------------------------------------------------------

        private Task<string> SendAsync(
            HttpMethod method, string path, string jsonBody, CancellationToken ct
        ) {
            var content = jsonBody == null
                ? null
                : new StringContent(jsonBody, Encoding.UTF8, "application/json");
            return SendAsync(client, method, baseUrl + path, content, ct);
        }

        private async Task<string> SendAsync(
            HttpClient via, HttpMethod method, string url, HttpContent content, CancellationToken ct
        ) {
            using (var req = new HttpRequestMessage(method, url)) {
                var token = tokenSource();
                if (!string.IsNullOrWhiteSpace(token)) {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                req.Content = content;

                using (var resp = await via.SendAsync(req, ct).ConfigureAwait(false)) {
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
                        throw new AcpHttpException(resp.StatusCode, ExtractErrorMessage(text), text);
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
