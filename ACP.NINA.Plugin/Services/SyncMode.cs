namespace ACP.NINA.Plugin.Services {

    /// The "Which plans to load into Target Scheduler" switch from the v3 spec.
    ///
    /// The fingerprint is built and shown in both modes, and the focal length
    /// write-back runs in both. The mode only decides whether the match
    /// verdicts filter what gets loaded.
    public enum SyncMode {

        /// Every plan in ACP is loaded. Plans that do not fit the fingerprint
        /// are still loaded and named in one warning line. The default, because
        /// most people run one rig on one computer and want none of this in
        /// their way.
        Everything = 0,

        /// Only plans whose verdict is fit, plus plans with no gear set, which
        /// come back as unconstrained. For people with several rigs, sites or
        /// computers.
        OnlyWhatFits = 1,
    }

    public static class SyncModeText {

        /// Wire value sent as "mode" in the POST /api/plans/match body.
        public static string ToWire(this SyncMode mode) {
            return mode == SyncMode.OnlyWhatFits ? "fit" : "everything";
        }

        /// Label for the Options page picker.
        public static string ToLabel(this SyncMode mode) {
            return mode == SyncMode.OnlyWhatFits ? "Only what fits tonight" : "Everything";
        }
    }
}
