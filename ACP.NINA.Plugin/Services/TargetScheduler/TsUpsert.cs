using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// Write a TsSyncPayload into a Target Scheduler database at user_version
    /// 23 to 28. Ported from nina_ts_sync/upsert.py, statement for statement.
    ///
    /// Per entity the pattern is: look the row up by our deterministic guid and
    /// update it if it is there; otherwise look it up by the natural key and
    /// claim it if it has no guid at all; otherwise insert. The claim step is
    /// the bridge between rows the user made by hand in Target Scheduler and a
    /// push that must not duplicate them. A row already stamped by somebody
    /// else is left alone and a fresh row goes in beside it, because fighting
    /// over another tool's identity is worse than a visible duplicate.
    ///
    /// Insert order is fixed by the foreign keys even though Target Scheduler
    /// does not enforce them: templates, projects with their rule weights,
    /// targets, then exposure plans.
    ///
    /// One code path serves the whole version range. The column list for each
    /// statement is the entity's own column list narrowed to what the open
    /// database has, so target.priority, which arrived at user_version 24, is
    /// simply left out against a 23 database and Target Scheduler's own default
    /// applies. See docs/schema-history.md in the acp-nina-ts-sync repo.
    public static class TsUpsert {

        public static TsSyncOutcome Apply(TargetSchedulerDb db, TsSyncPayload payload) {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            return Apply(db.Connection, db.UserVersion, db.ColumnsByTable, payload);
        }

        public static TsSyncOutcome Apply(
            SqliteConnection conn,
            int userVersion,
            IReadOnlyDictionary<string, HashSet<string>> columnsByTable,
            TsSyncPayload payload
        ) {
            var outcome = new TsSyncOutcome();

            Validate(payload);

            // 0) Bring any rows still carrying the pre-length-prefix identity
            // onto the current recipe, so the writes below find them instead of
            // inserting a second tree beside them. Same transaction as the
            // push, so a failure anywhere below takes the restamping with it.
            var migration = TsMigration.MigrateLegacyGuids(conn, payload);
            outcome.MigratedGuids = migration.Rewritten;
            outcome.Notes.AddRange(migration.Notes);

            // 1) Exposure templates. Filter wheel slot names are unique within
            // a profile by convention, so a name collision is a real conflict.
            var templateIdByGuid = new Dictionary<string, int>();
            foreach (var tpl in payload.Templates) {
                templateIdByGuid[tpl.Guid] = UpsertByGuid(
                    conn, "exposuretemplate", tpl, outcome.ExposureTemplate,
                    userVersion, columnsByTable,
                    claimKeys: new[] { "profileId", "name" });
            }

            // 2) Projects. Rule weights are seeded only when the project was
            // genuinely new, never when it was claimed or updated, because once
            // rows exist they are the user's to tune.
            var projectIdByGuid = new Dictionary<string, int>();
            foreach (var proj in payload.Projects) {
                var insertedBefore = outcome.Project.Inserted;
                var pid = UpsertByGuid(
                    conn, "project", proj, outcome.Project,
                    userVersion, columnsByTable,
                    claimKeys: new[] { "profileId", "name" });
                projectIdByGuid[proj.Guid] = pid;

                if (outcome.Project.Inserted > insertedBefore) {
                    List<TsRuleWeight> weights;
                    if (payload.RuleWeightsByProjectGuid.TryGetValue(proj.Guid, out weights)
                        && weights.Count > 0
                        && SeedRuleWeights(conn, pid, weights)) {
                        outcome.RuleWeightSeededProjects++;
                    }
                }
            }

            // 3) Targets. No profileId column of their own, so uniqueness is
            // scoped to the parent project.
            var targetIdByGuid = new Dictionary<string, int>();
            foreach (var group in payload.TargetsByProjectGuid) {
                int pid;
                if (!projectIdByGuid.TryGetValue(group.Key, out pid)) {
                    outcome.Notes.Add(
                        $"target group references unknown project guid {group.Key}, skipped");
                    continue;
                }
                foreach (var tgt in group.Value) {
                    tgt.ProjectId = pid;
                    targetIdByGuid[tgt.Guid] = UpsertByGuid(
                        conn, "target", tgt, outcome.Target,
                        userVersion, columnsByTable,
                        claimKeys: new[] { "projectid", "name" });
                }
            }

            // 4) Exposure plans. One per target and template pair by
            // definition, so that pair is the claim key; there is no name.
            foreach (var group in payload.PlansByTargetGuid) {
                int tid;
                if (!targetIdByGuid.TryGetValue(group.Key, out tid)) {
                    outcome.Notes.Add(
                        $"plan group references unknown target guid {group.Key}, skipped");
                    continue;
                }
                foreach (var plan in group.Value) {
                    string tplGuid;
                    int tplId;
                    if (!payload.TemplateGuidByPlanGuid.TryGetValue(plan.Guid, out tplGuid)
                        || !templateIdByGuid.TryGetValue(tplGuid, out tplId)) {
                        outcome.Notes.Add(
                            $"plan {plan.Guid} for target {group.Key} has no template, skipped");
                        continue;
                    }
                    plan.TargetId = tid;
                    plan.ExposureTemplateId = tplId;
                    UpsertByGuid(
                        conn, "exposureplan", plan, outcome.ExposurePlan,
                        userVersion, columnsByTable,
                        claimKeys: new[] { "targetid", "exposureTemplateId" });
                }
            }

            return outcome;
        }

        /// Refuse a payload that cannot be written back to safely.
        ///
        /// Two rules, both about being able to find a row again on the next
        /// push. A target with no name cannot be identified by a human or by
        /// us, so it is refused rather than written under a blank name. And two
        /// entities that hash to one identity would collapse into a single row
        /// while the log claims both were loaded, so that is refused too,
        /// naming what collided rather than silently merging.
        ///
        /// TsConvert catches the target cases earlier and can name the ACP
        /// plans involved. This is the backstop for any other caller, and its
        /// messages are deliberately about the rows rather than the plans.
        public static void Validate(TsSyncPayload payload) {
            foreach (var tgt in payload.TargetsByProjectGuid.Values.SelectMany(v => v)) {
                if (string.IsNullOrWhiteSpace(tgt.Name)) {
                    throw new TsPushValidationException(
                        "A target has no name, so Target Scheduler would have no way to " +
                        "show it and the next push no way to find it. Give every target " +
                        "a name and push again. Nothing was written.");
                }
            }

            NoDuplicateGuids("exposure template", payload.Templates.Select(t => t.Guid), null);
            NoDuplicateGuids("project", payload.Projects.Select(p => p.Guid), null);

            var targets = payload.TargetsByProjectGuid.Values.SelectMany(v => v).ToList();
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in targets) names[t.Guid] = t.Name;
            NoDuplicateGuids("target", targets.Select(t => t.Guid), names);

            NoDuplicateGuids(
                "exposure plan",
                payload.PlansByTargetGuid.Values.SelectMany(v => v).Select(p => p.Guid),
                null);
        }

        private static void NoDuplicateGuids(
            string what, IEnumerable<string> guids, Dictionary<string, string> labels
        ) {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var guid in guids) {
                if (seen.Add(guid)) continue;
                string label;
                var named = labels != null && labels.TryGetValue(guid, out label)
                    ? $" ({label})"
                    : string.Empty;
                throw new TsPushValidationException(
                    $"Two {what} rows in this push share the identity {guid}{named}, so " +
                    "one would overwrite the other and the log would report both as " +
                    "loaded. Nothing was written.");
            }
        }

        /// Count what a push would do without writing anything. Plan counts are
        /// best effort, because resolving a new plan's template Id needs the
        /// writes that a preview does not perform.
        public static TsSyncOutcome Preview(TargetSchedulerDb db, TsSyncPayload payload) {
            var outcome = new TsSyncOutcome();
            var conn = db.Connection;

            foreach (var tpl in payload.Templates) {
                Bump(outcome.ExposureTemplate, ExistsByGuid(conn, "exposuretemplate", tpl.Guid));
            }
            foreach (var proj in payload.Projects) {
                Bump(outcome.Project, ExistsByGuid(conn, "project", proj.Guid));
            }
            foreach (var tgt in payload.TargetsByProjectGuid.Values.SelectMany(v => v)) {
                Bump(outcome.Target, ExistsByGuid(conn, "target", tgt.Guid));
            }
            foreach (var plan in payload.PlansByTargetGuid.Values.SelectMany(v => v)) {
                Bump(outcome.ExposurePlan, ExistsByGuid(conn, "exposureplan", plan.Guid));
            }
            return outcome;
        }

        private static void Bump(TsTableCounts counts, bool exists) {
            if (exists) counts.Updated++; else counts.Inserted++;
        }

        private static bool ExistsByGuid(SqliteConnection conn, string table, string guid) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = $"SELECT 1 FROM {table} WHERE guid = $g";
                cmd.Parameters.AddWithValue("$g", (object)guid ?? DBNull.Value);
                return cmd.ExecuteScalar() != null;
            }
        }

        // -- The upsert itself ----------------------------------------------

        private static int UpsertByGuid(
            SqliteConnection conn,
            string table,
            TsEntity entity,
            TsTableCounts counts,
            int userVersion,
            IReadOnlyDictionary<string, HashSet<string>> columnsByTable,
            string[] claimKeys
        ) {
            var cols = WritableColumns(table, entity.Columns, userVersion, columnsByTable);

            // The UPDATE never touches the insert-only columns: the frame
            // counts on an exposure plan and a project's creation date belong
            // to whoever wrote them first. See TsSchema.ColumnsForUpdate.
            var updateCols = TsSchema.ColumnsForUpdate(table, cols);
            var setClause = string.Join(", ", updateCols.Select(c => $"{Quote(c)} = ${c}"));

            // 1) By our own guid.
            var existingId = SelectId(conn, table, "guid = $g", new Dictionary<string, object> {
                { "$g", entity.Guid },
            });
            if (existingId.HasValue) {
                ExecuteWithValues(
                    conn, $"UPDATE {table} SET {setClause} WHERE Id = $__id",
                    updateCols, entity, existingId.Value);
                counts.Updated++;
                return existingId.Value;
            }

            // 2) By the natural key, claiming a row that carries no stamp.
            if (claimKeys != null && claimKeys.Length > 0) {
                var where = string.Join(" AND ", claimKeys.Select(k => $"{Quote(k)} = ${k}"));
                var args = claimKeys.ToDictionary(k => "$" + k, k => entity.ValueOf(k));
                var claim = SelectIdAndGuid(conn, table, where, args);
                if (claim != null) {
                    if (string.IsNullOrWhiteSpace(claim.Item2)) {
                        // No stamp at all, so this is a row the user set up by
                        // hand and we can safely take ownership of it. A claim
                        // is an update of a row that already existed, so the
                        // insert-only columns are left alone here too: the
                        // frames it has already taken are not ours to reset.
                        ExecuteWithValues(
                            conn, $"UPDATE {table} SET {setClause} WHERE Id = $__id",
                            updateCols, entity, claim.Item1);
                        counts.Claimed++;
                        return claim.Item1;
                    }
                    // Somebody else's stamp. Fall through and insert beside it.
                }
            }

            // 3) Insert. Id is never in the column list, so autoincrement runs.
            var placeholders = string.Join(", ", cols.Select(c => "$" + c));
            var columnList = string.Join(", ", cols.Select(Quote));
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = $"INSERT INTO {table} ({columnList}) VALUES ({placeholders})";
                foreach (var c in cols) {
                    cmd.Parameters.AddWithValue("$" + c, entity.ValueOf(c) ?? DBNull.Value);
                }
                cmd.ExecuteNonQuery();
            }
            // A separate statement rather than appending SELECT last_insert_rowid()
            // to the INSERT, because ExecuteScalar over a two statement command
            // is doing more work than the guarantee it comes with.
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "SELECT last_insert_rowid()";
                var id = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                counts.Inserted++;
                return id;
            }
        }

        /// Narrow an entity's columns to the ones this database can take: the
        /// declared user_version first, then what PRAGMA table_info actually
        /// reported, so a drifted database still gets a working statement.
        private static List<string> WritableColumns(
            string table,
            IEnumerable<string> columns,
            int userVersion,
            IReadOnlyDictionary<string, HashSet<string>> columnsByTable
        ) {
            var allowed = TsSchema.ColumnsForVersion(table, columns, userVersion);
            HashSet<string> actual;
            if (columnsByTable == null || !columnsByTable.TryGetValue(table, out actual)) {
                return allowed;
            }
            return allowed.Where(actual.Contains).ToList();
        }

        private static int? SelectId(
            SqliteConnection conn, string table, string where, Dictionary<string, object> args
        ) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = $"SELECT Id FROM {table} WHERE {where}";
                foreach (var kv in args) cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                var value = cmd.ExecuteScalar();
                if (value == null || value == DBNull.Value) return null;
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }

        private static Tuple<int, string> SelectIdAndGuid(
            SqliteConnection conn, string table, string where, Dictionary<string, object> args
        ) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = $"SELECT Id, guid FROM {table} WHERE {where}";
                foreach (var kv in args) cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                using (var reader = cmd.ExecuteReader()) {
                    if (!reader.Read()) return null;
                    var id = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                    var guid = reader.IsDBNull(1) ? null : reader.GetValue(1)?.ToString();
                    return Tuple.Create(id, guid);
                }
            }
        }

        private static void ExecuteWithValues(
            SqliteConnection conn, string sql, List<string> cols, TsEntity entity, int id
        ) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                foreach (var c in cols) {
                    cmd.Parameters.AddWithValue("$" + c, entity.ValueOf(c) ?? DBNull.Value);
                }
                cmd.Parameters.AddWithValue("$__id", id);
                cmd.ExecuteNonQuery();
            }
        }

        /// Insert the rule weight rows only when the project has none. Target
        /// Scheduler's own RepairAndUpdate() heals anything missing on the next
        /// NINA start, so this only saves the user a restart.
        private static bool SeedRuleWeights(
            SqliteConnection conn, int projectId, List<TsRuleWeight> weights
        ) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "SELECT COUNT(*) FROM ruleweight WHERE projectid = $p";
                cmd.Parameters.AddWithValue("$p", projectId);
                if (Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) {
                    return false;
                }
            }
            foreach (var w in weights) {
                using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText =
                        "INSERT INTO ruleweight (projectid, name, weight) VALUES ($p, $n, $w)";
                    cmd.Parameters.AddWithValue("$p", projectId);
                    cmd.Parameters.AddWithValue("$n", (object)w.Name ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$w", w.Weight);
                    cmd.ExecuteNonQuery();
                }
            }
            return true;
        }

        /// `offset` is a SQLite keyword and the schema uses it as a column
        /// name, so every identifier goes in double quotes.
        private static string Quote(string identifier) {
            return "\"" + identifier.Replace("\"", "\"\"") + "\"";
        }
    }

    public class TsTableCounts {
        public int Inserted { get; set; }
        public int Updated { get; set; }
        /// The row existed under its natural key with no guid at all, so the
        /// push stamped its own guid on it rather than making a duplicate.
        public int Claimed { get; set; }
    }

    /// What one push did, in the same shape as the Python extension's
    /// SyncReport so the two tools' logs can be read side by side.
    public class TsSyncOutcome {

        public TsTableCounts ExposureTemplate { get; } = new TsTableCounts();
        public TsTableCounts Project { get; } = new TsTableCounts();
        public TsTableCounts Target { get; } = new TsTableCounts();
        public TsTableCounts ExposurePlan { get; } = new TsTableCounts();
        public int RuleWeightSeededProjects { get; set; }

        /// Rows restamped from the pre-length-prefix identity recipe onto the
        /// current one. Zero on a database that has already been migrated.
        public int MigratedGuids { get; set; }

        public List<string> Notes { get; } = new List<string>();

        public string ToShortString() {
            return
                $"{Project.Inserted}+{Project.Updated} projects, " +
                $"{Target.Inserted}+{Target.Updated} targets, " +
                $"{ExposurePlan.Inserted}+{ExposurePlan.Updated} exposure plans, " +
                $"{ExposureTemplate.Inserted}+{ExposureTemplate.Updated} templates " +
                "(inserted+updated)";
        }
    }
}
