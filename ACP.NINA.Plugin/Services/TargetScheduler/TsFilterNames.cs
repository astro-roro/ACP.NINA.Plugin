using System;
using System.Collections.Generic;
using System.Linq;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// Maps the filter names ACP plans use onto the names in the NINA profile's
    /// filter wheel.
    ///
    /// Target Scheduler matches an exposure template to a wheel slot by exact
    /// name, so a plan goal of "OIII" never runs on a wheel whose slot is called
    /// "O". ACP itself treats those as one filter (its archive scanner folds H,
    /// O and S into Ha, OIII and SII), so plans reach the plugin in a mix of
    /// spellings. This resolves each one to the wheel's own spelling.
    ///
    /// The alias table is the core of FILTER_CANON in ACP's
    /// scripts/build_archive_manifest.py: the narrowband, broadband and
    /// no-filter spellings. Keys are matched upper-cased, as there.
    public static class TsFilterNames {

        private static readonly Dictionary<string, string> Canon =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "H", "Ha" }, { "HA", "Ha" }, { "HALPHA", "Ha" }, { "H-ALPHA", "Ha" }, { "H_ALPHA", "Ha" },
                { "O", "OIII" }, { "O3", "OIII" }, { "OIII", "OIII" }, { "O-III", "OIII" }, { "O_III", "OIII" },
                { "S", "SII" }, { "S2", "SII" }, { "SII", "SII" }, { "S-II", "SII" }, { "S_II", "SII" },
                { "L", "L" }, { "LUM", "L" }, { "LUMINANCE", "L" }, { "LIGHT", "L" }, { "CLEAR", "L" },
                { "R", "R" }, { "RED", "R" },
                { "G", "G" }, { "GREEN", "G" },
                { "B", "B" }, { "BLUE", "B" },
                { "V", "V" }, { "IR", "IR" }, { "UV", "UV" },
                { "NOFILTER", "NoFilter" }, { "NO FILTER", "NoFilter" }, { "NO_FILTER", "NoFilter" },
                { "NONE", "NoFilter" }, { "EMPTY", "NoFilter" }, { "OPEN", "NoFilter" },
            };

        /// ACP's canonical name for a filter, or the trimmed input when the
        /// table does not know it.
        public static string Canonical(string name) {
            var trimmed = (name ?? string.Empty).Trim();
            string canon;
            return Canon.TryGetValue(trimmed, out canon) ? canon : trimmed;
        }

        /// The wheel slot a plan's filter should use, or null when no slot is
        /// the same filter.
        ///
        /// An exact match wins, then a match ignoring case, then a match on the
        /// canonical name. The order matters for a wheel that has both an "H"
        /// and an "Ha" slot: a plan asking for "Ha" gets the "Ha" slot.
        public static string ResolveToWheel(string planFilter, IReadOnlyList<string> wheelFilters) {
            if (string.IsNullOrWhiteSpace(planFilter) || wheelFilters == null) return null;
            var slots = wheelFilters.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

            var exact = slots.FirstOrDefault(s => string.Equals(s, planFilter, StringComparison.Ordinal));
            if (exact != null) return exact;

            var anyCase = slots.FirstOrDefault(s => string.Equals(s.Trim(), planFilter.Trim(), StringComparison.OrdinalIgnoreCase));
            if (anyCase != null) return anyCase;

            var want = Canonical(planFilter);
            return slots.FirstOrDefault(s => string.Equals(Canonical(s), want, StringComparison.OrdinalIgnoreCase));
        }
    }
}
