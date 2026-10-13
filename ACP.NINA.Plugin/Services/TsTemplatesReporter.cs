using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services {

    /// After a push, send ACP the exposure templates this rig's Target
    /// Scheduler holds for the profile.
    ///
    /// ACP on a server has no Target Scheduler database to read, so its
    /// template drop-down would be empty. The push reads every exposuretemplate
    /// row for the profile back anyway (TsPushResult.Templates), so this posts
    /// that list to POST /api/ts-templates. Both Sync for tonight and Sync All
    /// to TS call it. A failure never fails the sync: the rows are written and
    /// the drop-down just keeps the last list ACP had.
    public static class TsTemplatesReporter {

        /// The request for a push, or null when there is nothing to send.
        public static TsTemplatesRequest BuildRequest(TsPushResult push, string profileId, DateTime nowUtc) {
            if (push == null || !push.Success) return null;
            return BuildRequest(push.Templates, profileId, nowUtc);
        }

        /// The request for a list of templates read from the database, or
        /// null when there is nothing to send. Each template carries its id,
        /// guid, the columns Tidy compares and its exposure plans
        /// (docs/specs/gear-templates.md section 3); today's ACP ignores the
        /// extra fields.
        public static TsTemplatesRequest BuildRequest(
            IEnumerable<TsExposureTemplate> templates, string profileId, DateTime nowUtc
        ) {
            if (string.IsNullOrWhiteSpace(profileId)) return null;
            var list = (templates ?? Enumerable.Empty<TsExposureTemplate>()).ToList();
            if (list.Count == 0) return null;
            return new TsTemplatesRequest {
                ProfileId = profileId,
                SentAt = nowUtc.ToUniversalTime().ToString("o"),
                Templates = list
                    .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Name))
                    .Select(t => new TsTemplateInfo {
                        Name = t.Name,
                        Filter = t.FilterName,
                        DefaultExposureS = t.DefaultExposure,
                        Gain = t.Gain,
                        Offset = t.Offset,
                        Bin = t.Bin,
                        Id = t.Id,
                        Guid = t.Guid,
                        MoonAvoidanceEnabled = t.MoonAvoidanceEnabled,
                        MoonAvoidanceSeparation = t.MoonAvoidanceSeparation,
                        MoonAvoidanceWidth = t.MoonAvoidanceWidth,
                        TwilightLevel = t.TwilightLevel,
                        MaximumHumidity = t.MaximumHumidity,
                        DitherEvery = t.DitherEvery,
                        Plans = t.Plans
                            .Take(TargetSchedulerDb.MaxPlansPerTemplate)
                            .Select(pl => new TsTemplatePlan { Id = pl.Id, Exposure = pl.Exposure })
                            .ToList(),
                    })
                    .ToList(),
            };
        }

        /// Post a template list read before the push, so ACP's pull of
        /// TS-side edits runs first. Best effort, never throws.
        public static async Task<bool> PostAsync(
            AcpApiClient client, IEnumerable<TsExposureTemplate> templates, string profileId,
            CancellationToken ct = default
        ) {
            var request = BuildRequest(templates, profileId, DateTime.UtcNow);
            if (request == null) return false;
            try {
                await client.PostTsTemplatesAsync(request, ct).ConfigureAwait(false);
                return true;
            } catch (Exception ex) {
                Logger.Warning($"ACP: could not post the template list to ACP's /api/ts-templates: {ex.Message}");
                return false;
            }
        }

        /// Post the template list. Returns true when it went, false when it
        /// did not or there was nothing to send. Never throws.
        public static async Task<bool> PostAsync(
            AcpApiClient client, TsPushResult push, string profileId, CancellationToken ct = default
        ) {
            var request = BuildRequest(push, profileId, DateTime.UtcNow);
            if (request == null) return false;
            try {
                await client.PostTsTemplatesAsync(request, ct).ConfigureAwait(false);
                return true;
            } catch (Exception ex) {
                Logger.Warning($"ACP: could not post the template list to ACP's /api/ts-templates: {ex.Message}");
                return false;
            }
        }
    }
}
