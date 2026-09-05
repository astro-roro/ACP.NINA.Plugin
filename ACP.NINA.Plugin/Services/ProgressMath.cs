using ACP.NINA.Plugin.Models;
using System;
using System.Collections.Generic;

namespace ACP.NINA.Plugin.Services {

    /// Turns Target Scheduler's sub counts into the hours ACP stores.
    ///
    /// The rules are lifted from the Python extension so a same machine user
    /// running both gets the same number from either. See
    /// nina_ts_sync/reader.py read_acquired and from_ts.py
    /// _filter_goals_from_plans.
    public static class ProgressMath {

        /// Subs that count as integration time. The grader is the whole
        /// question here: with it on, TS has already decided some frames are
        /// not worth keeping, and counting them would tell ACP the target is
        /// further along than it is. With it off, every captured sub counts.
        public static int GoodCount(TsProgressRow row) {
            if (row == null) return 0;
            return row.GraderEnabled ? Math.Max(0, row.Accepted) : Math.Max(0, row.Acquired);
        }

        /// The sub exposure that applies to this row. An exposureplan may
        /// override its template; zero means it does not.
        ///
        /// The Python extension's read_acquired uses exposureplan.exposure on
        /// its own and reports nothing when it is zero. from_ts.py falls back
        /// to the template. The fallback is the better of the two, because a
        /// row that leaves the exposure to its template is a normal TS setup
        /// and silently reporting nothing for it is a gap the user cannot see.
        public static double SubExposureSeconds(TsProgressRow row) {
            if (row == null) return 0;
            if (row.ExposureSeconds > 0) return row.ExposureSeconds;
            if (row.TemplateDefaultExposureSeconds > 0) return row.TemplateDefaultExposureSeconds;
            return 0;
        }

        /// Acquired hours for one row, rounded the way ACP's plans.json stores
        /// them so a value that has not moved does not look like it has.
        public static double AcquiredHours(TsProgressRow row) {
            var subs = GoodCount(row);
            var seconds = SubExposureSeconds(row);
            if (subs <= 0 || seconds <= 0) return 0;
            return Math.Round(subs * seconds / 3600.0, 4);
        }

        /// Collapse a target's rows into the per filter body ACP wants.
        ///
        /// Rows with no usable sub exposure are left out rather than sent as
        /// zero: zero would be a claim that nothing has been shot, and ACP
        /// would have to decide whether to believe it. Leaving the filter out
        /// says nothing, which is the truth.
        ///
        /// Two rows for one filter on one target is not something TS produces,
        /// but if it ever does they are summed rather than one of them winning
        /// silently.
        public static Dictionary<string, ProgressFilter> BuildFilters(IEnumerable<TsProgressRow> rows) {
            var result = new Dictionary<string, ProgressFilter>(StringComparer.OrdinalIgnoreCase);
            if (rows == null) return result;

            foreach (var row in rows) {
                if (row == null || string.IsNullOrWhiteSpace(row.FilterName)) continue;
                if (SubExposureSeconds(row) <= 0) continue;

                var name = row.FilterName.Trim();
                if (!result.TryGetValue(name, out var entry)) {
                    entry = new ProgressFilter();
                    result[name] = entry;
                }
                entry.AcquiredCount += GoodCount(row);
                entry.AcquiredHours = Math.Round(entry.AcquiredHours + AcquiredHours(row), 4);
            }
            return result;
        }
    }
}
