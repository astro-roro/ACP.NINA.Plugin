using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// The shooting order, as Target Scheduler reads it from override rows.
    ///
    /// docs/specs/goal-panel-and-ts-order.md section 5. TS has no column for
    /// plan order: a target's enabled exposure plans in Id order are its
    /// natural order, and overrideexposureorderitem rows, when a target has
    /// any, say what it shoots instead. Each row is an (action, referenceIdx)
    /// pair: action 0 is an exposure of the enabled plan at that index in
    /// natural order, action 1 a dither with referenceIdx -1
    /// (Database/Schema/OverrideExposureOrderItem.cs:12-14).
    ///
    /// Ported from nina_ts_sync/order.py. Both writers have to produce the
    /// same rows.
    public static class TsOrder {

        public const int Exposure = 0;
        public const int Dither = 1;

        /// A stored list of [action, referenceIdx] pairs, or null when the
        /// token is not such a list. A token that is not a pair of integers
        /// makes the whole list absent. Matches as_rows in order.py.
        public static List<Tuple<int, int>> AsRows(JToken raw) {
            var array = raw as JArray;
            if (array == null) return null;
            var rows = new List<Tuple<int, int>>();
            foreach (var item in array) {
                var pair = item as JArray;
                if (pair == null || pair.Count != 2) return null;
                int action;
                int reference;
                if (!TryInt(pair[0], out action) || !TryInt(pair[1], out reference)) return null;
                rows.Add(Tuple.Create(action, reference));
            }
            return rows;
        }

        /// An integer token that fits 32 bits. A float, a string, a boolean
        /// or a larger integer is not one.
        private static bool TryInt(JToken token, out int value) {
            value = 0;
            if (token == null || token.Type != JTokenType.Integer) return false;
            var raw = ((JValue)token).Value;
            if (raw is int) {
                value = (int)raw;
                return true;
            }
            if (raw is long) {
                var wide = (long)raw;
                if (wide < int.MinValue || wide > int.MaxValue) return false;
                value = (int)wide;
                return true;
            }
            return false;
        }

        public static JArray ToJson(IEnumerable<Tuple<int, int>> rows) {
            return new JArray(rows.Select(r => new JArray(r.Item1, r.Item2)));
        }

        /// The plans of one target in the order TS shoots them, and the plans
        /// it never shoots. With override rows, the order is each plan's first
        /// exposure row; a plan no row names is never shot
        /// (PlanningTarget.cs:80-97) and goes at the end. Without, it is the
        /// natural order.
        public static List<int> EffectivePlanOrder(List<int> enabled, List<Tuple<int, int>> rows, out List<int> neverShot) {
            if (rows == null || rows.Count == 0) {
                neverShot = new List<int>();
                return new List<int>(enabled);
            }
            var order = new List<int>();
            foreach (var row in rows) {
                if (row.Item1 == Exposure && row.Item2 >= 0 && row.Item2 < enabled.Count
                    && !order.Contains(enabled[row.Item2])) {
                    order.Add(enabled[row.Item2]);
                }
            }
            neverShot = enabled.Where(p => !order.Contains(p)).ToList();
            order.AddRange(neverShot);
            return order;
        }

        /// Dither rows inserted by Target Scheduler's own rule,
        /// DitherInjector.Inject (Planning/DitherInjector.cs:31-72, the count
        /// in FindNextDither at 124-141). A dither goes before the item where
        /// any filter has been seen ditherEvery + 1 times since the last
        /// dither, with the list treated as a circle. filterOf gives the filter
        /// name of each referenceIdx, which is how TS counts.
        public static List<Tuple<int, int>> InjectDithers(
            List<Tuple<int, int>> items, IList<string> filterOf, int ditherEvery
        ) {
            if (ditherEvery <= 0) return new List<Tuple<int, int>>(items);
            return InjectDithers(items, filterOf, _ => ditherEvery);
        }

        /// The same rule with a count per filter, for a project whose own
        /// "dither every" is off: each filter dithers by its template's count,
        /// as Target Scheduler does when it shoots without an override. A
        /// filter whose count is 0 or less never calls for a dither.
        public static List<Tuple<int, int>> InjectDithers(
            List<Tuple<int, int>> items, IList<string> filterOf, Func<string, int> everyFor
        ) {
            if (items.Count == 0) return new List<Tuple<int, int>>(items);
            var work = new List<Tuple<int, int>>(items) { items[0] };
            var unique = work.Where(i => i.Item1 == Exposure).Select(i => filterOf[i.Item2]).Distinct().ToList();
            var output = new List<Tuple<int, int>>();
            var pos = 0;
            while (pos < work.Count) {
                var counts = unique.ToDictionary(f => f, f => 0, StringComparer.Ordinal);
                var found = -1;
                for (var i = pos; i < work.Count; i++) {
                    if (work[i].Item1 != Exposure) continue;
                    var name = filterOf[work[i].Item2];
                    counts[name]++;
                    var every = everyFor(name);
                    if (every > 0 && counts[name] == every + 1) {
                        found = i;
                        break;
                    }
                }
                if (found < 0) {
                    output.AddRange(work.Skip(pos));
                    break;
                }
                output.AddRange(work.Skip(pos).Take(found - pos));
                output.Add(Tuple.Create(Dither, -1));
                pos = found;
            }
            output.RemoveAt(output.Count - 1);
            return output;
        }

        /// Whether ACP's order equals the base's, over the rows both have, and
        /// no row new since the base sits before one the base has. A row
        /// removed since the base is not a change of order, and neither is a
        /// row added at the end; a row added anywhere else is the user
        /// placing it, so it is a reorder. "Added" means a row id the base did
        /// not know at all: known lists every id the base knew, including rows
        /// with no plan, so a goal raised from 0 keeps its place without
        /// counting. Without known, only the base order's own ids are known.
        /// Matches same_order in order.py.
        public static bool SameOrder(IList<string> acp, IList<string> baseOrder, IList<string> known = null) {
            var baseSet = new HashSet<string>(baseOrder, StringComparer.Ordinal);
            var knownSet = new HashSet<string>(baseOrder, StringComparer.Ordinal);
            if (known != null) knownSet.UnionWith(known);
            var common = new HashSet<string>(acp, StringComparer.Ordinal);
            common.IntersectWith(baseSet);
            if (!acp.Where(common.Contains).SequenceEqual(baseOrder.Where(common.Contains), StringComparer.Ordinal)) {
                return false;
            }
            var last = -1;
            for (var i = 0; i < acp.Count; i++) {
                if (common.Contains(acp[i])) last = i;
            }
            for (var i = 0; i < last; i++) {
                if (!knownSet.Contains(acp[i])) return false;
            }
            return true;
        }

        /// The ACP row ids a base knew at the last link or push: known_rows,
        /// which includes rows with no plan (goal 0) and disabled ones. A base
        /// from before known_rows falls back to the ids in exposure_order and
        /// in exposure_plans_by_panel_filter ("<panel>_<row id>" keys), which
        /// is every row that had a plan. Matches known_rows_of_base in
        /// order.py.
        public static List<string> KnownRowsOfBase(
            Newtonsoft.Json.Linq.JObject entry, Newtonsoft.Json.Linq.JObject plansByPanelFilter
        ) {
            var raw = entry?["known_rows"] as Newtonsoft.Json.Linq.JArray;
            if (raw != null) return raw.Select(t => (string)t).ToList();
            var output = new List<string>();
            var order = entry?["exposure_order"] as Newtonsoft.Json.Linq.JArray;
            if (order != null) output.AddRange(order.Select(t => (string)t));
            if (plansByPanelFilter != null) {
                foreach (var pair in plansByPanelFilter.Properties()) {
                    var at = pair.Name.IndexOf('_');
                    if (at >= 0 && at + 1 < pair.Name.Length) output.Add(pair.Name.Substring(at + 1));
                }
            }
            return output.Distinct(StringComparer.Ordinal).ToList();
        }

        public static bool SameRows(IList<Tuple<int, int>> a, IList<Tuple<int, int>> b) {
            if (a == null || b == null) return a == null && b == null;
            return a.Count == b.Count && a.Zip(b, (x, y) => x.Item1 == y.Item1 && x.Item2 == y.Item2).All(v => v);
        }
    }

    /// One target's order as the base recorded it after the last sync.
    /// BaseOrder is exposure_order, the row ids in the order TS shot them;
    /// null means the base has none, so the push leaves the override rows
    /// alone. BaseRows is override_rows; null means the base did not record
    /// them, which never equals TS's rows. Matches TargetOrder in
    /// nina_ts_sync/schema.py.
    public class TsTargetOrder {
        public List<string> BaseOrder { get; set; }
        public List<Tuple<int, int>> BaseRows { get; set; }
        /// known_rows: every ACP row id the base knew, including rows with no
        /// plan; null means an older base, where the ids of BaseOrder and of
        /// the base's exposure plans stand for it.
        public List<string> KnownRows { get; set; }
    }
}
