using ACP.NINA.Plugin.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// A plan's filter rows, as the writer reads them.
    ///
    /// Ported from nina_ts_sync/rows.py, and both writers have to agree on
    /// every number here (docs/specs/goal-panel-and-ts-order.md section 3).
    /// A plan holds `filter_rows`, an ordered list; each row names a filter,
    /// a Target Scheduler template and a goal in hours ("h") or frames ("fr").
    ///
    /// A plan from an ACP that predates filter rows carries `filter_goals`
    /// only, and this plugin ships before ACP does. RowsOf derives rows from
    /// it the way ACP's one-shot migration does, with each row's id set to
    /// its goal key. ts_links keys are "<target id>_<goal key>" on such an
    /// ACP, so the push still finds its exposure plans, and ACP's migration
    /// later moves those keys onto real row ids.
    public static class TsRows {

        public static readonly string[] FilterOrder = { "L", "R", "G", "B", "Ha", "OIII", "SII" };
        public const double FrameUnitBelowS = 60.0;
        public const double DefaultSubS = 300.0;

        private static readonly HashSet<string> knownFilters = new HashSet<string>(
            new[] { "Ha", "OIII", "SII", "L", "R", "G", "B", "V", "IR", "UV", "NoFilter" },
            StringComparer.Ordinal);

        /// The row's template_sub_s, 300 when it has none.
        public static double SubS(FilterRow row) {
            var v = row?.TemplateSubS;
            return v.HasValue && v.Value > 0 && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value)
                ? v.Value
                : DefaultSubS;
        }

        /// Frames the writers ask TS for. 0 means the row pushes no plan.
        /// h: max(1, ceil(target_hours x 3600 / template_sub_s)); fr: target_frames.
        public static int Desired(FilterRow row) {
            if (row == null) return 0;
            if (row.Unit == "fr") {
                var frames = row.TargetFrames ?? 0.0;
                return frames > 0 ? (int)Math.Truncate(frames) : 0;
            }
            var hours = row.TargetHours ?? 0.0;
            if (hours <= 0) return 0;
            return Math.Max(1, (int)Math.Ceiling(hours * 3600.0 / SubS(row)));
        }

        /// The row's aac block, AAC's figures, or null. Matches aac_block in
        /// nina_ts_sync/rows.py.
        public static Newtonsoft.Json.Linq.JObject AacBlock(FilterRow row) {
            return row?.Aac as Newtonsoft.Json.Linq.JObject;
        }

        /// The plan's aac block when it carries a usable watermark and floor,
        /// else null: filed_through_night has to be an ISO date and
        /// min_total_frames a whole number of at least 0, so a malformed block
        /// can never open a write. Matches plan_aac in rows.py.
        public static Newtonsoft.Json.Linq.JObject PlanAac(Plan plan) {
            var block = plan?.Aac as Newtonsoft.Json.Linq.JObject;
            if (block == null) return null;
            var night = block["filed_through_night"];
            var floor = block["min_total_frames"];
            if (night == null || night.Type != Newtonsoft.Json.Linq.JTokenType.String) return null;
            var text = (string)night;
            DateTime parsed;
            if (text.Length != 10 || !DateTime.TryParseExact(
                    text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out parsed)) {
                return null;
            }
            if (floor == null || floor.Type != Newtonsoft.Json.Linq.JTokenType.Integer || (long)floor < 0) return null;
            return block;
        }

        /// Desired with AAC's floor: a row with an aac block on a plan with
        /// one never sits below min_total_frames. A row with a goal of 0 still
        /// pushes no plan. Matches desired_for in rows.py.
        public static int DesiredFor(FilterRow row, Plan plan) {
            var d = Desired(row);
            if (d <= 0 || AacBlock(row) == null) return d;
            var planBlock = PlanAac(plan);
            if (planBlock == null) return d;
            return Math.Max(d, (int)(long)planBlock["min_total_frames"]);
        }

        private static double? JsonNumber(Newtonsoft.Json.Linq.JToken token) {
            if (token == null) return null;
            if (token.Type != Newtonsoft.Json.Linq.JTokenType.Integer
                && token.Type != Newtonsoft.Json.Linq.JTokenType.Float) return null;
            var v = (double)token;
            return double.IsNaN(v) || double.IsInfinity(v) ? (double?)null : v;
        }

        /// The row's goal in its own unit: hours for "h", frames for "fr".
        /// Matches goal_of in rows.py.
        public static double GoalOf(FilterRow row) {
            if (row == null) return 0.0;
            if (row.Unit == "fr") {
                var frames = row.TargetFrames ?? 0.0;
                return frames > 0 ? Math.Truncate(frames) : 0.0;
            }
            return row.TargetHours ?? 0.0;
        }

        /// Whether the row's aac.complete still stands. AAC judged it against
        /// the goal the row had when it sent the figures (goal_at_send, in the
        /// row's unit); a goal raised since means AAC has not seen the new
        /// goal, so the row reads as incomplete until AAC reports again. A
        /// block with no goal_at_send has nothing to compare and stands.
        /// Matches complete_counts in rows.py.
        public static bool CompleteCounts(FilterRow row) {
            var block = AacBlock(row);
            if (block == null) return false;
            var complete = block["complete"];
            if (complete == null || complete.Type != Newtonsoft.Json.Linq.JTokenType.Boolean || !(bool)complete) {
                return false;
            }
            var sent = JsonNumber(block["goal_at_send"]);
            if (!sent.HasValue) return true;
            return GoalOf(row) <= sent.Value + 1e-9;
        }

        /// TS's desired read as ACP's goal. AAC's floor lifts what the writer
        /// sends (DesiredFor), so a desired of exactly that is the row's own
        /// goal, never a change to take; anything else is a real edit in TS.
        /// Matches unfloored_goal in rows.py.
        public static int UnflooredGoal(FilterRow row, Plan plan, int tsDesired) {
            if (AacBlock(row) != null && Desired(row) > 0 && tsDesired == DesiredFor(row, plan)) {
                return Desired(row);
            }
            return tsDesired;
        }

        /// The accepted count to write for an AAC-linked row, or null when the
        /// row has no usable figures. `wanted` is the row's desired after the
        /// floor. Matches aac_accepted in rows.py.
        public static int? AacAccepted(FilterRow row, Plan plan, int wanted) {
            var block = AacBlock(row);
            var planBlock = PlanAac(plan);
            if (block == null || planBlock == null) return null;
            var seconds = JsonNumber(block["accepted_seconds"]);
            var frames = JsonNumber(block["accepted_frames"]);
            if (!seconds.HasValue || seconds.Value < 0 || !frames.HasValue || frames.Value < 0) return null;
            var count = (int)Math.Floor(seconds.Value / SubS(row) + 0.000001);
            if (CompleteCounts(row)) {
                return Math.Max(count, wanted);
            }
            var floor = (int)(long)planBlock["min_total_frames"];
            var shortBy = Math.Max(1, floor - (int)Math.Truncate(frames.Value));
            return Math.Max(0, Math.Min(count, wanted - shortBy));
        }

        /// The row's actual_hours in frames, for an exposure plan's insert.
        /// Python's round() is round half to even.
        public static int AcquiredFrames(FilterRow row) {
            return (int)Math.Round(
                (row?.ActualHours ?? 0.0) * 3600.0 / Math.Max(1.0, SubS(row)),
                MidpointRounding.ToEven);
        }

        /// The canonical filter a goal key names, or null. "R Stars" is R:
        /// the alias table passes it through unchanged, so its first word is
        /// tried.
        public static string KnownFilter(string name) {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var whole = TsFilterNames.Canonical(name);
            if (knownFilters.Contains(whole)) return whole;
            var words = name.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1) {
                var head = TsFilterNames.Canonical(words[0]);
                if (knownFilters.Contains(head)) return head;
            }
            return null;
        }

        /// A row's filter for a goal key or a TS filtername: the one
        /// vocabulary ACP's migration, both writers and the importer share
        /// (Fixtures/row_filter_cases.json, the same table the Python sync
        /// and ACP test against). The known filter it names, else the
        /// trimmed name as it is. Matches row_filter in nina_ts_sync/rows.py.
        public static string RowFilter(string name) {
            return KnownFilter(name) ?? (name ?? string.Empty).Trim();
        }

        /// Whether two filter names are the same row filter. A difference
        /// only canonicalisation makes is never an edit.
        public static bool SameFilter(string a, string b) {
            return string.Equals(RowFilter(a), RowFilter(b), StringComparison.Ordinal);
        }

        private static int FilterRank(string name) {
            var i = Array.IndexOf(FilterOrder, name);
            return i >= 0 ? i : FilterOrder.Length;
        }

        /// The plan's filter rows, in shooting order.
        public static List<FilterRow> RowsOf(Plan plan, Camera camera) {
            if (plan?.FilterRows != null) {
                return plan.FilterRows.Where(r => r != null && !string.IsNullOrEmpty(r.Id)).ToList();
            }
            if (plan?.FilterGoals == null) return new List<FilterRow>();

            var staged = new List<Tuple<int, string, bool, string, FilterRow>>();
            foreach (var goal in plan.FilterGoals) {
                if (goal.Value == null) continue;
                var key = goal.Key;
                var filter = KnownFilter(key);
                string template;
                bool canonical;
                if (filter != null && filter == key) {
                    CameraFilter cfg = null;
                    camera?.Filters?.TryGetValue(key, out cfg);
                    template = !string.IsNullOrEmpty(cfg?.TsTemplateName)
                        ? cfg.TsTemplateName
                        : (!string.IsNullOrEmpty(camera?.Name) ? $"{key} ({camera.Name})" : key);
                    canonical = true;
                } else if (filter != null) {
                    template = key;
                    canonical = false;
                } else {
                    filter = key;
                    template = key;
                    canonical = true;
                }
                var raw = goal.Value.SubExposureS ?? 0.0;
                var sub = raw > 0 ? raw : DefaultSubS;
                var hours = goal.Value.TargetHours;
                var row = new FilterRow {
                    Id = key, Filter = filter, Template = template, TemplateSubS = sub,
                    ActualHours = goal.Value.ActualHours,
                };
                if (sub < FrameUnitBelowS) {
                    row.Unit = "fr";
                    row.TargetFrames = hours > 0
                        ? Math.Max(1, (int)Math.Ceiling(hours * 3600.0 / Math.Max(1, (int)Math.Truncate(sub))))
                        : 0;
                } else {
                    row.Unit = "h";
                    row.TargetHours = hours;
                }
                staged.Add(Tuple.Create(FilterRank(filter), filter, !canonical, template, row));
            }
            // Python sorts (rank, name) for the filter, then canonical first,
            // then template, all ordinal.
            return staged
                .OrderBy(s => s.Item1)
                .ThenBy(s => s.Item1 < FilterOrder.Length ? string.Empty : s.Item2, StringComparer.Ordinal)
                .ThenBy(s => s.Item3)
                .ThenBy(s => s.Item4, StringComparer.Ordinal)
                .Select(s => s.Item5)
                .ToList();
        }

        /// How a push result names one row: "R Stars", or the filter when the
        /// template is named after it.
        public static string RowLabel(FilterRow row) {
            var filter = row?.Filter ?? string.Empty;
            var template = row?.Template ?? string.Empty;
            return template.Length > 0 && template != filter ? template : filter;
        }

        /// The sub TS uses for an exposure plan: its own exposure when above
        /// 0, else its template's default (PlanningExposure.cs:50).
        public static double PlanSubS(double exposure, double templateDefault) {
            return exposure > 0 ? exposure : templateDefault;
        }
    }
}
