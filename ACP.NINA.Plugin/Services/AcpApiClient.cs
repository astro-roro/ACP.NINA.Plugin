using ACP.NINA.Plugin.Models;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services {

    /// HTTP wrapper around ACP's REST surface. Stateless — instances reuse a
    /// single HttpClient with a short timeout for the lightweight endpoints
    /// and a longer one for the TS sync (DB write + backup can take seconds).
    /// Errors surface as exceptions which callers translate to user-visible
    /// status text.
    public class AcpApiClient {

        private static readonly HttpClient http = new HttpClient {
            // Long enough to absorb a TS sync's BEGIN IMMEDIATE + backup +
            // upsert + plans.json save without timing out, short enough that
            // an unreachable ACP doesn't lock the UI for ages.
            Timeout = TimeSpan.FromSeconds(30),
        };

        private readonly string baseUrl;

        public AcpApiClient(string baseUrl) {
            // Trim trailing slashes so we can concatenate paths cleanly.
            this.baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
        }

        /// Probe ACP for liveness. ACP doesn't yet expose /api/version (audit
        /// flagged it as a v1.x add), so we fall back to /api/plans — it
        /// always returns 200 with a version field on a live server.
        public async Task<string> ProbeAsync(CancellationToken ct = default) {
            var json = await GetStringAsync("/api/plans", ct);
            var doc = JsonConvert.DeserializeObject<PlansResponse>(json);
            return $"ACP responding (plans schema v{doc?.Version ?? 0})";
        }

        public async Task<PlansResponse> GetPlansAsync(CancellationToken ct = default) {
            var json = await GetStringAsync("/api/plans", ct);
            return JsonConvert.DeserializeObject<PlansResponse>(json)
                ?? new PlansResponse();
        }

        public async Task<GearResponse> GetGearAsync(CancellationToken ct = default) {
            var json = await GetStringAsync("/api/gear", ct);
            return JsonConvert.DeserializeObject<GearResponse>(json)
                ?? new GearResponse();
        }

        /// POST to the private nina_ts_sync extension. Body is
        /// {profile_id: "<NINA profile GUID>"}. On success returns the
        /// SyncReport plus paths to the DB backup and plans.json backup ACP
        /// wrote before the transaction.
        ///
        /// Surfaces errors via HttpRequestException with the response body
        /// when ACP returns a 4xx/5xx, so callers can render the JSON
        /// {error: "..."} payload that the extension produces (e.g.
        /// "profile_id is required" or a SchemaVersionError).
        public async Task<TsSyncResponse> SyncToTsAsync(string profileId, CancellationToken ct = default) {
            var url = baseUrl + "/api/ext/nina-ts-sync/sync";
            var body = JsonConvert.SerializeObject(new { profile_id = profileId });
            var content = new StringContent(body, Encoding.UTF8, "application/json");

            using (var resp = await http.PostAsync(url, content, ct)) {
                var json = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) {
                    throw new HttpRequestException(
                        $"HTTP {(int)resp.StatusCode}: {ExtractErrorMessage(json) ?? json}"
                    );
                }
                return JsonConvert.DeserializeObject<TsSyncResponse>(json)
                    ?? new TsSyncResponse();
            }
        }

        private async Task<string> GetStringAsync(string path, CancellationToken ct) {
            var url = baseUrl + path;
            using (var resp = await http.GetAsync(url, ct)) {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync();
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
