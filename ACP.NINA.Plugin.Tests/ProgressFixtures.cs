using ACP.NINA.Plugin.Models;
using System;
using System.Collections.Generic;

namespace ACP.NINA.Plugin.Tests {

    public static class Fixtures {

        /// One exposureplan row joined to its template and project, with the
        /// defaults a normal TS setup has: no grader, exposure set on the row.
        public static TsProgressRow Row(
            string filter,
            int acquired,
            double exposureSeconds = 300,
            int accepted = -1,
            bool graderEnabled = false,
            double templateDefaultSeconds = 0,
            int targetId = 1,
            int exposurePlanId = 1
        ) {
            return new TsProgressRow {
                ExposurePlanId = exposurePlanId,
                TargetId = targetId,
                FilterName = filter,
                Acquired = acquired,
                Accepted = accepted < 0 ? acquired : accepted,
                ExposureSeconds = exposureSeconds,
                TemplateDefaultExposureSeconds = templateDefaultSeconds,
                GraderEnabled = graderEnabled,
            };
        }

        /// A single target plan, the common case: one ACP plan, one TS target.
        public static TsPlanRefs SingleTargetPlan(
            string acpPlanId, int tsTargetId, string profileId = "profile-a"
        ) {
            return new TsPlanRefs {
                AcpPlanId = acpPlanId,
                ProfileId = profileId,
                ProjectId = 10,
                TargetIdsByPanel = new Dictionary<string, int>(StringComparer.Ordinal) {
                    { "1,1", tsTargetId },
                },
            };
        }

        /// A mosaic plan: one ACP plan spread over several TS targets, laid out
        /// row major from panel 1,1.
        public static TsPlanRefs MosaicPlan(
            string acpPlanId, int rows, int cols, int firstTargetId, string profileId = "profile-a"
        ) {
            var panels = new Dictionary<string, int>(StringComparer.Ordinal);
            var id = firstTargetId;
            for (var r = 1; r <= rows; r++) {
                for (var c = 1; c <= cols; c++) {
                    panels[$"{r},{c}"] = id++;
                }
            }
            return new TsPlanRefs {
                AcpPlanId = acpPlanId,
                ProfileId = profileId,
                ProjectId = 20,
                TargetIdsByPanel = panels,
            };
        }
    }
}
