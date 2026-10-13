using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services {

    /// What Sync All to TS did.
    public class TsSyncAllResult {
        public TsPushResult Push { get; set; }

        /// Set when ACP was not told about the push. Null when it was.
        public string LinksWarning { get; set; }
    }

    /// Sync All to TS, as a sequence the dock calls and the tests can drive
    /// without a window: the fingerprint, then the template list, then the
    /// plans and gear, then the push, then the links and the list again.
    ///
    /// The fingerprint goes first so ACP's wheel data is as fresh as the push
    /// (docs/specs/gear-templates.md section 5). It is for display only: the
    /// push uses the profile's slots directly, so a fingerprint that cannot be
    /// sent never stops the sync. The template list goes before gear is read,
    /// so ACP's pull of TS-side edits runs before the push. The writers do not
    /// rely on that order.
    public static class TsSyncAllRunner {

        public static async Task<TsSyncAllResult> RunAsync(
            AcpApiClient client,
            IGearFingerprintService fingerprintService,
            ITsPushService tsPush,
            string profileId,
            string profileName,
            IReadOnlyList<string> wheelFilters,
            SyncMode mode,
            CancellationToken ct = default
        ) {
            // 1) The fingerprint, built the way Sync for tonight builds it,
            // with no solve.
            if (fingerprintService != null) {
                try {
                    var fingerprint = fingerprintService.Build(null);
                    await client.MatchPlansAsync(fingerprint, mode, ct).ConfigureAwait(false);
                } catch (AcpUnauthorizedException) {
                    throw;
                } catch (Exception ex) {
                    Logger.Warning($"ACP: could not send the gear fingerprint before Sync All to TS: {ex.Message}");
                }
            }

            // 2) The template list as the database holds it now.
            try {
                var templates = await tsPush.ReadTemplatesAsync(profileId, ct).ConfigureAwait(false);
                if (templates != null) {
                    await TsTemplatesReporter.PostAsync(client, templates, profileId, ct).ConfigureAwait(false);
                }
            } catch (Exception ex) {
                Logger.Warning($"ACP: could not send the template list before Sync All to TS: {ex.Message}");
            }

            // 3) Plans and gear, read after the pull.
            var plans = (await client.GetPlansAsync(ct).ConfigureAwait(false))?.Plans ?? new List<Plan>();
            GearResponse gear = null;
            try {
                gear = await client.GetGearAsync(ct).ConfigureAwait(false);
            } catch (AcpUnauthorizedException) {
                throw;
            } catch (Exception ex) {
                // Without gear the rows still load, with no mosaic panels
                // and default exposure settings, as Sync for tonight does.
                Logger.Warning($"ACP: could not read gear for Sync All to TS: {ex.Message}");
            }

            // 4) The push.
            var result = new TsSyncAllResult();
            result.Push = await tsPush
                .PushAsync(plans, gear, profileId, ct, wheelFilters, profileName)
                .ConfigureAwait(false);

            // 5) Tell ACP what was written. A failure costs a warning, not
            // the sync.
            if (result.Push.Success) {
                result.LinksWarning = await TsLinksReporter.PostAsync(client, result.Push, profileId, ct)
                    .ConfigureAwait(false);
                await TsTemplatesReporter.PostAsync(client, result.Push, profileId, ct)
                    .ConfigureAwait(false);
            }
            return result;
        }
    }
}
