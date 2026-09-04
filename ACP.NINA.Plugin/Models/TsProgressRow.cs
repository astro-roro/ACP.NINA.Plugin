namespace ACP.NINA.Plugin.Models {

    /// One Target Scheduler exposureplan row, already joined to its
    /// exposuretemplate and its parent project, which is everything the
    /// count to hours conversion needs and nothing else.
    ///
    /// This is a flat row rather than the three TS entities because the only
    /// thing Part F does with the database is read acquired counts. Keeping
    /// it flat is also what lets the maths be tested from fixture rows with
    /// no sqlite file anywhere near the test.
    public class TsProgressRow {

        /// exposureplan.Id, only used for logging which row a number came from.
        public int ExposurePlanId { get; set; }

        /// exposureplan.targetid. The join back to an ACP plan happens on this.
        public int TargetId { get; set; }

        /// exposuretemplate.filtername. Sent to ACP as given: ACP canonicalises
        /// filter names its end, so "Antlia Ha" lands on the plan's Ha goal
        /// without the plugin having to carry the alias table.
        public string FilterName { get; set; }

        /// exposureplan.acquired, every sub captured.
        public int Acquired { get; set; }

        /// exposureplan.accepted, the subs the grader kept.
        public int Accepted { get; set; }

        /// exposureplan.exposure in seconds. Zero or less means the row does
        /// not override its template and the template's default applies.
        public double ExposureSeconds { get; set; }

        /// exposuretemplate.defaultexposure in seconds, the fallback.
        public double TemplateDefaultExposureSeconds { get; set; }

        /// project.enablegrader. When the grader is on, rejected frames are
        /// not integration time, so accepted is the honest count.
        public bool GraderEnabled { get; set; }
    }
}
