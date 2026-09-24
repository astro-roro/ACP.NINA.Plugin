using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using NINA.Core.Utility;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services {

    /// After a push, tell ACP which TS rows each plan now lives in on this rig.
    ///
    /// The push works out every plan's row ids and snapshot anyway
    /// (TsPushResult.PlanStates). Posting them to POST /links keeps ACP's
    /// record of the last sync on this profile current, which is the base the
    /// upload's three-way merge compares against. Without it, every plugin
    /// push makes the next upload report conflicts that are not real.
    ///
    /// A failure here never fails the sync. The rows are written; ACP just
    /// does not know it yet.
    public static class TsLinksReporter {

        public const string NotToldWarning =
            "ACP was not told about this sync, so the next upload may ask about more plans.";

        /// The request for a push, or null when there is nothing to send.
        /// Only plans that reached TS get a state, because a link puts the
        /// plan in this rig's scope for later uploads.
        public static TsLinksRequest BuildRequest(TsPushResult push, string profileId, string machine) {
            if (push == null || !push.Success || string.IsNullOrWhiteSpace(profileId)) return null;
            var states = push.PlanStates
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.PlanId) && push.PushedPlanIds.Contains(s.PlanId))
                .Select(s => new TsLinkState { PlanId = s.PlanId, Refs = s.Refs, BaseSnapshot = s.BaseSnapshot })
                .ToList();
            if (states.Count == 0) return null;
            return new TsLinksRequest { ProfileId = profileId, Machine = machine, States = states };
        }

        /// Post the push state. Returns null when it went, or when there was
        /// nothing to send, and the warning line when it did not.
        public static async Task<string> PostAsync(
            AcpApiClient client, TsPushResult push, string profileId, CancellationToken ct = default
        ) {
            var request = BuildRequest(push, profileId, Environment.MachineName);
            if (request == null) return null;
            try {
                await client.PostTsLinksAsync(request, ct).ConfigureAwait(false);
                return null;
            } catch (Exception ex) {
                Logger.Warning($"ACP: could not post the push state to ACP's /links: {ex.Message}");
                return NotToldWarning;
            }
        }
    }
}
