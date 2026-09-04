using ACP.NINA.Plugin.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ACP.NINA.Plugin.Services {

    /// The join between what Target Scheduler is imaging and what ACP calls it.
    ///
    /// TS events and TS rows speak in integer target ids. ACP speaks in plan
    /// ids. The bridge is the refs block a sync stamped onto each plan, which
    /// records exactly which TS target id each panel of each plan became. That
    /// is why Part F never has to guess from names: a target renamed in the TS
    /// UI still carries the id it was created with.
    public static class ProgressMapper {

        /// The panel whose counts stand for the whole plan.
        ///
        /// ACP stores a mosaic's filter goals per panel, not summed over
        /// panels, so reporting one panel's hours is the correct thing and
        /// summing them would be several times too high. Panel 1,1 is the one
        /// picked, which is what the Python extension's sync-acquired does, so
        /// both paths land on the same number.
        public const string AnchorPanelKey = "1,1";

        /// Find the plan a TS target id belongs to.
        ///
        /// Every panel counts for the join, not just the anchor: TS raises
        /// TargetStart for whichever panel it is about to shoot, and panel 4 of
        /// a mosaic is still news about that plan.
        public static TsPlanRefs FindPlanForTarget(
            IEnumerable<TsPlanRefs> allRefs, int tsTargetId, string profileId
        ) {
            if (allRefs == null) return null;
            foreach (var refs in allRefs) {
                if (refs?.TargetIdsByPanel == null) continue;
                if (!SameProfile(refs, profileId)) continue;
                if (refs.TargetIdsByPanel.Values.Contains(tsTargetId)) return refs;
            }
            return null;
        }

        /// The TS target id whose exposure plans should be read for this plan.
        ///
        /// Panel 1,1 when it is there. When it is not, which happens if a sync
        /// was interrupted part way through a mosaic, the lowest panel key by
        /// row then column stands in, so the choice is at least stable between
        /// runs rather than whatever the dictionary iterates first.
        public static int? AnchorTargetId(TsPlanRefs refs) {
            if (refs?.TargetIdsByPanel == null || refs.TargetIdsByPanel.Count == 0) return null;
            if (refs.TargetIdsByPanel.TryGetValue(AnchorPanelKey, out var anchor)) return anchor;

            var ordered = refs.TargetIdsByPanel
                .OrderBy(kv => PanelOrder(kv.Key).Item1)
                .ThenBy(kv => PanelOrder(kv.Key).Item2)
                .ThenBy(kv => kv.Value)
                .First();
            return ordered.Value;
        }

        /// Every plan we could report on, in a stable order. The fallback timer
        /// uses this when no event has told it which target moved.
        public static List<TsPlanRefs> ReportablePlans(
            IEnumerable<TsPlanRefs> allRefs, string profileId
        ) {
            if (allRefs == null) return new List<TsPlanRefs>();
            return allRefs
                .Where(r => r != null
                    && !string.IsNullOrWhiteSpace(r.AcpPlanId)
                    && r.TargetIdsByPanel != null
                    && r.TargetIdsByPanel.Count > 0
                    && SameProfile(r, profileId))
                .OrderBy(r => r.AcpPlanId, StringComparer.Ordinal)
                .ToList();
        }

        /// A refs block with no profile recorded is treated as belonging to
        /// whatever profile is asking. Blocks written before the profile was
        /// stamped are the only way that happens, and refusing them would make
        /// the first upgrade look like a total failure to report.
        private static bool SameProfile(TsPlanRefs refs, string profileId) {
            if (string.IsNullOrWhiteSpace(profileId)) return true;
            if (string.IsNullOrWhiteSpace(refs.ProfileId)) return true;
            return string.Equals(refs.ProfileId, profileId, StringComparison.OrdinalIgnoreCase);
        }

        private static Tuple<int, int> PanelOrder(string panelKey) {
            var parts = (panelKey ?? string.Empty).Split(',');
            int row, col;
            if (parts.Length != 2
                || !int.TryParse(parts[0], out row)
                || !int.TryParse(parts[1], out col)) {
                return Tuple.Create(int.MaxValue, int.MaxValue);
            }
            return Tuple.Create(row, col);
        }
    }
}
