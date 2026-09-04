using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using NINA.Core.Utility;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services {

    /// Works out which Target Scheduler rows belong to which ACP plan, by
    /// recomputing the deterministic guids rather than remembering anything.
    ///
    /// Why not read a stored mapping: the v3.1 push builds exactly this in
    /// TsPushResult.PlanStates and then drops it on the floor, because nothing
    /// persists it yet. Rather than add a state file that can go stale, get
    /// out of step with the database, or simply not exist yet on a machine
    /// that has only ever synced from the Python extension, this recomputes
    /// the mapping from the two things that are always true: what plans ACP
    /// has, and what rows the database holds.
    ///
    /// That is also what the Python extension's sync-acquired does. It does not
    /// read stored refs either; it derives target and exposure plan guids on
    /// the fly and looks them up. Deriving has three advantages worth the
    /// round trip. It works for rows written by the extension, by another
    /// machine, or before this feature existed. It cannot disagree with the
    /// database, because it is read from the database. And a plan renamed in
    /// ACP simply stops matching its old rows, which is the correct answer
    /// rather than a stale mapping pointing at somebody else's target.
    ///
    /// The recipe itself is not repeated here. TsState.BuildRefs is the one
    /// definition of how a plan maps onto rows, shared with the push, so the
    /// two can never drift apart.
    public class TsPlanRefsSource : IPlanRefsSource {

        /// Plans change far less often than counts do, and every reporting pass
        /// needs the whole list. One minute keeps a plan edited in ACP's web UI
        /// arriving promptly without asking on every single event.
        public static readonly TimeSpan PlansCacheWindow = TimeSpan.FromMinutes(1);

        private readonly TsSnapshotCache cache;
        private readonly Func<string> profileIdProvider;
        private readonly Func<CancellationToken, Task<IReadOnlyList<Plan>>> plansFetcher;
        private readonly Func<DateTimeOffset> clock;
        private readonly object gate = new object();

        private IReadOnlyList<Plan> cachedPlans;
        private DateTimeOffset cachedPlansAt;

        public TsPlanRefsSource(
            TsSnapshotCache cache,
            Func<string> profileIdProvider,
            Func<CancellationToken, Task<IReadOnlyList<Plan>>> plansFetcher,
            Func<DateTimeOffset> clock = null
        ) {
            this.cache = cache;
            this.profileIdProvider = profileIdProvider ?? (() => null);
            this.plansFetcher = plansFetcher;
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public async Task<IReadOnlyList<TsPlanRefs>> ReadPlanRefsAsync(CancellationToken ct) {
            var result = new List<TsPlanRefs>();

            var profileId = profileIdProvider();
            if (string.IsNullOrWhiteSpace(profileId)) return result;

            var snap = cache?.Get(profileId);
            if (snap == null) return result;

            var plans = await PlansAsync(ct).ConfigureAwait(false);
            if (plans == null) return result;

            foreach (var plan in plans) {
                if (plan == null || string.IsNullOrWhiteSpace(plan.Id)) continue;

                JObject refs;
                try {
                    // "read" is neither push nor import, which is what leaves
                    // the two timestamps alone: this is not a sync and must not
                    // look like one.
                    // The user version is only ever stamped into the two sync
                    // timestamps, which "read" leaves alone, so zero here is
                    // not a value anything goes on to use.
                    refs = TsState.BuildRefs(snap, plan, profileId, 0, "read");
                } catch (Exception ex) {
                    Logger.Debug($"ACP: could not map plan {plan.Id} onto Target Scheduler rows: {ex.Message}");
                    continue;
                }

                var mapped = FromJson(plan.Id, profileId, refs);
                // A plan that has never been synced has no panels, and there is
                // nothing to report against it.
                if (mapped.TargetIdsByPanel.Count == 0) continue;
                result.Add(mapped);
            }
            return result;
        }

        private static TsPlanRefs FromJson(string planId, string profileId, JObject refs) {
            var mapped = new TsPlanRefs {
                AcpPlanId = planId,
                ProfileId = profileId,
                ProjectId = refs?["project_id"]?.Type == JTokenType.Integer
                    ? (int?)refs["project_id"]
                    : null,
            };
            CopyInts(refs?["target_ids_by_panel"] as JObject, mapped.TargetIdsByPanel);
            CopyInts(refs?["template_ids_by_filter"] as JObject, mapped.TemplateIdsByFilter);
            CopyInts(refs?["exposure_plan_ids"] as JObject, mapped.ExposurePlanIds);
            return mapped;
        }

        private static void CopyInts(JObject from, IDictionary<string, int> into) {
            if (from == null) return;
            foreach (var pair in from) {
                if (pair.Value == null || pair.Value.Type != JTokenType.Integer) continue;
                into[pair.Key] = (int)pair.Value;
            }
        }

        private async Task<IReadOnlyList<Plan>> PlansAsync(CancellationToken ct) {
            lock (gate) {
                if (cachedPlans != null && clock() - cachedPlansAt < PlansCacheWindow) {
                    return cachedPlans;
                }
            }
            if (plansFetcher == null) return null;

            var fetched = await plansFetcher(ct).ConfigureAwait(false);
            lock (gate) {
                cachedPlans = fetched;
                cachedPlansAt = clock();
            }
            return fetched;
        }

        /// Forget the cached plan list, so the next pass asks ACP again. Used
        /// after a sync, when what is in Target Scheduler has just changed.
        public void Invalidate() {
            lock (gate) {
                cachedPlans = null;
            }
        }
    }
}
