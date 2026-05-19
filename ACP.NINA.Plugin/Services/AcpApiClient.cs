using ACP.NINA.Plugin.Models;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services {

    /// HTTP wrapper around ACP's REST surface. Stateless — instances reuse a
    /// single HttpClient with a short timeout (NINA UI shouldn't hang waiting
    /// for an unreachable ACP). Errors surface as exceptions which callers
    /// translate to user-visible status text.
    public class AcpApiClient {

        private static readonly HttpClient http = new HttpClient {
            Timeout = TimeSpan.FromSeconds(5),
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

        private async Task<string> GetStringAsync(string path, CancellationToken ct) {
            var url = baseUrl + path;
            using (var resp = await http.GetAsync(url, ct)) {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync();
            }
        }
    }
}
