using System;
using System.Collections.Generic;

namespace ACP.NINA.Plugin.Models {

    /// The Target Scheduler row ids a previous sync stamped onto one ACP plan.
    ///
    /// This mirrors the `ts_refs` block the Python extension writes onto every
    /// plan in plans.json, and the same block the v3.1 sync stores after a
    /// push. Part F only reads it, and only for the join: given a TS target id
    /// out of a TargetStart event, which ACP plan is that?
    public class TsPlanRefs {

        /// The ACP plan id this block belongs to. The path parameter of
        /// POST /api/plans/<id>/progress.
        public string AcpPlanId { get; set; }

        /// The NINA profile the sync ran under. TS ids are only unique within
        /// a profile, so a refs block from another profile must not be joined
        /// against events from this one.
        public string ProfileId { get; set; }

        /// project.Id in TS.
        public int? ProjectId { get; set; }

        /// Panel key "row,col" (both 1 indexed) to target.Id in TS. A single
        /// target plan has exactly one entry, "1,1". An N by M mosaic has one
        /// per panel.
        public Dictionary<string, int> TargetIdsByPanel { get; set; }
            = new Dictionary<string, int>(StringComparer.Ordinal);

        /// Filter name to exposuretemplate.Id.
        public Dictionary<string, int> TemplateIdsByFilter { get; set; }
            = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// "<target id>_<filter name>" to exposureplan.Id, the same key shape
        /// the Python extension uses.
        public Dictionary<string, int> ExposurePlanIds { get; set; }
            = new Dictionary<string, int>(StringComparer.Ordinal);
    }
}
