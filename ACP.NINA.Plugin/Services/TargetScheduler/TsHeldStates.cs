using ACP.NINA.Plugin.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// The one thing Sync for tonight does to a project it is not loading:
    /// write the state column, and only that column, to a row Target
    /// Scheduler already has for it. Ported from
    /// docs/specs/ts-project-settings.md section 6.
    ///
    /// A paused plan's row still has to be set to inactive, or the push would
    /// never touch it and Target Scheduler would keep imaging it. This is
    /// separate from TsUpsert.Apply, which never writes a row for a plan that
    /// is not being loaded: no insert happens here either, only an update to
    /// a row that already exists.
    public static class TsHeldStates {

        public class Outcome {
            public int DraftCount { get; set; }
            public int InactiveCount { get; set; }
            public int ClosedCount { get; set; }
            public List<string> Notes { get; } = new List<string>();

            /// The dock's line, or null when nothing was held. Wording matches
            /// docs/specs/ts-project-settings.md section 6: "Set 2 projects
            /// inactive, 1 closed".
            public string Summary() {
                var parts = new List<string>();
                if (InactiveCount > 0) parts.Add($"{InactiveCount} inactive");
                if (ClosedCount > 0) parts.Add($"{ClosedCount} closed");
                if (DraftCount > 0) parts.Add($"{DraftCount} draft");
                if (parts.Count == 0) return null;
                var total = InactiveCount + ClosedCount + DraftCount;
                var noun = total == 1 ? "project" : "projects";
                return $"Set {total} {noun} " + string.Join(", ", parts) + ".";
            }
        }

        /// `heldPlans` are the draft, inactive and closed plans /api/plans/match
        /// returned once the fingerprint said supports_state. Grouped by
        /// project the same way a push groups plans, so one project with
        /// several held plans is written once.
        public static Outcome Apply(
            SqliteConnection conn,
            int userVersion,
            IReadOnlyDictionary<string, HashSet<string>> columnsByTable,
            IReadOnlyList<Plan> heldPlans,
            string profileId,
            long nowUnix
        ) {
            var outcome = new Outcome();
            if (heldPlans == null || heldPlans.Count == 0) return outcome;

            foreach (var group in TsConvert.GroupByProject(heldPlans.Where(p => p != null).ToList())) {
                var projectName = group.Key;
                var stateName = TsConvert.GroupState(group.Value);
                // "active" should never reach here, since a caller only passes
                // plans /api/plans/match marked held. Skip rather than write a
                // state that would undo the whole point of this path.
                if (string.Equals(stateName, "active", StringComparison.OrdinalIgnoreCase)) continue;
                var tsState = TsConvert.StateToTsInt[stateName];

                var projectId = FindProjectId(conn, group.Value, profileId, projectName);
                if (!projectId.HasValue) {
                    // No row: never inserted for these plans.
                    continue;
                }

                var cols = new List<string> { "state" };
                var values = new Dictionary<string, object> { { "state", tsState } };
                if (tsState == TsConvert.StateToTsInt["active"]) {
                    cols.Add("activedate");
                    values["activedate"] = nowUnix;
                } else if (tsState == TsConvert.StateToTsInt["inactive"] || tsState == TsConvert.StateToTsInt["closed"]) {
                    cols.Add("inactivedate");
                    values["inactivedate"] = nowUnix;
                }
                cols = TsSchema.ColumnsForVersion("project", cols, userVersion);
                HashSet<string> actual;
                if (columnsByTable != null && columnsByTable.TryGetValue("project", out actual)) {
                    cols = cols.Where(actual.Contains).ToList();
                }
                if (cols.Count == 0) continue;

                var setClause = string.Join(", ", cols.Select(c => $"\"{c}\" = ${c}"));
                using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText = $"UPDATE project SET {setClause} WHERE Id = $__id";
                    foreach (var c in cols) cmd.Parameters.AddWithValue("$" + c, values[c]);
                    cmd.Parameters.AddWithValue("$__id", projectId.Value);
                    cmd.ExecuteNonQuery();
                }

                if (tsState == TsConvert.StateToTsInt["inactive"]) outcome.InactiveCount++;
                else if (tsState == TsConvert.StateToTsInt["closed"]) outcome.ClosedCount++;
                else if (tsState == TsConvert.StateToTsInt["draft"]) outcome.DraftCount++;
            }

            return outcome;
        }

        /// The pinned id wins over the guid, the same order a push resolves a
        /// project. Returns null when neither finds a row, which is the "no
        /// row" case: nothing is written or inserted.
        private static int? FindProjectId(
            SqliteConnection conn, List<Plan> group, string profileId, string projectName
        ) {
            foreach (var plan in group) {
                var refs = TsConvert.RefsFor(plan, profileId);
                if (refs == null || !refs.ProjectId.HasValue) continue;
                var pid = refs.ProjectId.Value;
                using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText = "SELECT profileId FROM project WHERE Id = $id";
                    cmd.Parameters.AddWithValue("$id", pid);
                    var row = cmd.ExecuteScalar();
                    if (row != null && string.Equals(
                            row.ToString(), profileId, StringComparison.OrdinalIgnoreCase)) {
                        return pid;
                    }
                }
            }
            var guid = TsGuid.Project(profileId, projectName);
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "SELECT Id FROM project WHERE guid = $g";
                cmd.Parameters.AddWithValue("$g", guid);
                var value = cmd.ExecuteScalar();
                if (value == null || value == DBNull.Value) return null;
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }
    }
}
