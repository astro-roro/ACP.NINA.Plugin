using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using System;
using System.Collections.Generic;

namespace ACP.NINA.Plugin.Services {

    /// Acquired counts read out of the real Target Scheduler database, through
    /// the v3.1 database layer.
    public class TsDatabaseProgressSource : ITsProgressSource {

        private readonly TsSnapshotCache cache;
        private readonly Func<string> profileIdProvider;

        public TsDatabaseProgressSource(TsSnapshotCache cache, Func<string> profileIdProvider) {
            this.cache = cache;
            this.profileIdProvider = profileIdProvider ?? (() => null);
        }

        public IReadOnlyList<TsProgressRow> ReadRowsForTarget(int tsTargetId) {
            var rows = new List<TsProgressRow>();
            var snap = cache?.Get(profileIdProvider());
            if (snap == null) return rows;

            TsTarget target;
            if (!snap.TargetsById.TryGetValue(tsTargetId, out target)) return rows;

            // The grader lives on the project, not the target, and it decides
            // whether accepted or acquired is the honest count.
            var graderEnabled = false;
            TsProject project;
            if (snap.ProjectsById.TryGetValue(target.ProjectId, out project)) {
                graderEnabled = project.EnableGrader == 1;
            }

            // The row Id is the dictionary key rather than a property: the
            // entity classes leave Id off so the upsert cannot write it.
            foreach (var entry in snap.PlansById) {
                var plan = entry.Value;
                if (plan == null || plan.TargetId != tsTargetId) continue;

                TsExposureTemplate template;
                if (!snap.TemplatesById.TryGetValue(plan.ExposureTemplateId, out template)) {
                    // An exposure plan whose template is gone has no filter
                    // name and no sub length, so there is nothing truthful to
                    // report about it.
                    continue;
                }

                rows.Add(new TsProgressRow {
                    ExposurePlanId = entry.Key,
                    TargetId = plan.TargetId,
                    FilterName = template.FilterName,
                    Acquired = plan.Acquired,
                    Accepted = plan.Accepted,
                    // Target Scheduler stores -1 for "use the template", which
                    // is also the entity default, so anything not positive
                    // means fall back.
                    ExposureSeconds = plan.Exposure,
                    TemplateDefaultExposureSeconds = template.DefaultExposure,
                    GraderEnabled = graderEnabled,
                });
            }
            return rows;
        }
    }
}
