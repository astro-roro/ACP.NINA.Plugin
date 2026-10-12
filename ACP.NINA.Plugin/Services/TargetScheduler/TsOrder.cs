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
        /// token is not such a list.
        public static List<Tuple<int, int>> AsRows(JToken raw) {
            var array = raw as JArray;
            if (array == null) return null;
            var rows = new List<Tuple<int, int>>();
            foreach (var item in array) {
                var pair = item as JArray;
                if (pair == null || pair.Count != 2) return null;
                if (pair[0].Type != JTokenType.Integer || pair[1].Type != JTokenType.Integer) return null;
                rows.Add(Tuple.Create((int)pair[0], (int)pair[1]));
            }
            return rows;
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
            if (ditherEvery <= 0 || items.Count == 0) return new List<Tuple<int, int>>(items);
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
                    if (counts[name] == ditherEvery + 1) {
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

        /// Whether ACP's order equals the base's, over the rows both have. A
        /// row added or removed since the base is not a change of order.
        public static bool SameOrder(IList<string> acp, IList<string> baseOrder) {
            var common = new HashSet<string>(acp, StringComparer.Ordinal);
            common.IntersectWith(baseOrder);
            return acp.Where(common.Contains).SequenceEqual(baseOrder.Where(common.Contains), StringComparer.Ordinal);
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
    }
}
