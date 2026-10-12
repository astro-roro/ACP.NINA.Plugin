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
            outcome.Notes.AddRange(payload.Notes);

            // 0) Bring any rows still carrying the pre-length-prefix identity
            // onto the current recipe, so the writes below find them instead of
            // inserting a second tree beside them. Same transaction as the
            // push, so a failure anywhere below takes the restamping with it.
            var migration = TsMigration.MigrateLegacyGuids(conn, payload);
            outcome.MigratedGuids = migration.Rewritten;
            outcome.Notes.AddRange(migration.Notes);

            // 0b) Check every project and target Id the plans' ts_refs name
            // before writing anything, and drop the ones that no longer hold.
            // What survives is written by Id below, ahead of the guid and the
            // name. Exposure plan pins are checked as each plan is written.
            ResolvePins(conn, payload, outcome);

            // 0c) Refuse to create a second project beside one of the same name.
            RefuseDuplicateProjects(conn, payload);

            // 1) Exposure templates, looked up by profile and name. A hit is
            // used as it is, because Target Scheduler owns the templates it
            // has; a miss is inserted with the row's filter and sub
            // (docs/specs/goal-panel-and-ts-order.md section 3, "Templates").
            // A template TS lacks is inserted when the first plan that needs
            // it is written. An existing plan keeps its own template unless
            // the row's template changed in ACP, so it may never need one.
            var templateIdByGuid = new Dictionary<string, int>();
            var templateByGuid = new Dictionary<string, TsExposureTemplate>();
            foreach (var tpl in payload.Templates) {
                templateByGuid[tpl.Guid] = tpl;
                var found = TemplateIdByName(conn, tpl);
                if (found.HasValue) templateIdByGuid[tpl.Guid] = found.Value;
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
                    claimKeys: new[] { "profileId", "name" },
                    outcome: outcome);
                projectIdByGuid[proj.Guid] = pid;
                if (outcome.Project.Inserted == insertedBefore) {
                    ResetCadenceIfOrderWritten(conn, proj, pid, outcome);
                }

                List<TsRuleWeight> weights;
                payload.RuleWeightsByProjectGuid.TryGetValue(proj.Guid, out weights);
                if (outcome.Project.Inserted > insertedBefore) {
                    outcome.InsertedProjectGuids.Add(proj.Guid);
                    if (weights != null && weights.Count > 0 && SeedRuleWeights(conn, pid, weights)) {
                        outcome.RuleWeightSeededProjects++;
                    }
                } else if (weights != null && weights.Count > 0 && HealLegacyRuleWeights(conn, pid, weights)) {
                    outcome.Notes.Add($"project {proj.Name}: replaced legacy rule weight names");
                }
            }

            // 3) Targets. No profileId column of their own, so uniqueness is
            // scoped to the parent project.
            var targetIdByGuid = new Dictionary<string, int>();
            var insertedTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in payload.TargetsByProjectGuid) {
                int pid;
                if (!projectIdByGuid.TryGetValue(group.Key, out pid)) {
                    outcome.Notes.Add(
                        $"target group references unknown project guid {group.Key}, skipped");
                    continue;
                }
                foreach (var tgt in group.Value) {
                    tgt.ProjectId = pid;
                    var targetsInsertedBefore = outcome.Target.Inserted;
                    targetIdByGuid[tgt.Guid] = UpsertByGuid(
                        conn, "target", tgt, outcome.Target,
                        userVersion, columnsByTable,
                        claimKeys: new[] { "projectid", "name" });
                    if (outcome.Target.Inserted > targetsInsertedBefore) insertedTargets.Add(tgt.Guid);
                }
            }

            // 4) Exposure plans, found by the chain in section 3's "Exposure
            // plan identity", then disabled for rows removed in ACP. Then each
            // target's override rows, by section 5: ACP's order written, or the
            // rows left as they are. Left rows still follow the plans: a target
            // whose set of enabled plans changed other than by an insert has
            // them remapped, a plan that arrived gets its block on the end, and
            // its cadence rows go.
            var usedPlanIds = new HashSet<int>();
            foreach (var group in payload.PlansByTargetGuid) {
                int tid;
                if (!targetIdByGuid.TryGetValue(group.Key, out tid)) {
                    outcome.Notes.Add(
                        $"plan group references unknown target guid {group.Key}, skipped");
                    continue;
                }
                var before = EnabledPlanIds(conn, tid);
                var tsRows = OverrideRows(conn, tid);
                var hadOverride = tsRows.Count > 0;
                var inserted = new HashSet<int>();
                var written = new List<int>();
                var writtenRows = new List<string>();
                foreach (var plan in group.Value) {
                    string tplGuid;
                    if (!payload.TemplateGuidByPlanGuid.TryGetValue(plan.Guid, out tplGuid)
                        || !templateByGuid.ContainsKey(tplGuid)) {
                        outcome.Notes.Add(
                            $"plan {plan.Guid} for target {group.Key} has no template, skipped");
                        continue;
                    }
                    TsPlanLookup lookup;
                    if (!payload.PlanLookupByGuid.TryGetValue(plan.Guid, out lookup)) {
                        lookup = new TsPlanLookup { Label = plan.Guid };
                    }
                    var rowTemplateGuid = tplGuid;
                    Func<int> rowTemplate = () => {
                        int id;
                        if (templateIdByGuid.TryGetValue(rowTemplateGuid, out id)) return id;
                        var tpl = templateByGuid[rowTemplateGuid];
                        id = Insert(conn, "exposuretemplate", tpl,
                            WritableColumns("exposuretemplate", tpl.Columns, userVersion, columnsByTable),
                            outcome.ExposureTemplate);
                        templateIdByGuid[rowTemplateGuid] = id;
                        return id;
                    };
                    plan.TargetId = tid;
                    bool wasInsert;
                    var planId = UpsertExposurePlan(
                        conn, plan, lookup, rowTemplate, outcome.ExposurePlan, userVersion, columnsByTable,
                        usedPlanIds, out wasInsert);
                    usedPlanIds.Add(planId);
                    written.Add(planId);
                    writtenRows.Add(lookup.RowId ?? plan.Guid);
                    if (wasInsert) inserted.Add(planId);
                    NoteExposureMismatch(conn, planId, lookup.Label, outcome);
                }
                List<TsRemovedPlan> removedPlans;
                if (payload.RemovedByTargetGuid.TryGetValue(group.Key, out removedPlans)) {
                    foreach (var removed in removedPlans) {
                        if (usedPlanIds.Contains(removed.PlanId)) continue;
                        var row = ReadRow(
                            conn, "SELECT targetid, guid, enabled FROM exposureplan WHERE Id = $id",
                            removed.PlanId);
                        if (row == null || ToInt(row[0]) != tid) continue;
                        var guid = row[1] == null || row[1] == DBNull.Value ? null : row[1].ToString();
                        if (guid == null || !removed.Guids.Contains(guid)) continue;
                        var enabled = row[2] == null || row[2] == DBNull.Value ? 1 : ToInt(row[2]);
                        if (enabled == 0) continue;
                        using (var cmd = conn.CreateCommand()) {
                            cmd.CommandText = "UPDATE exposureplan SET enabled = 0 WHERE Id = $id";
                            cmd.Parameters.AddWithValue("$id", removed.PlanId);
                            cmd.ExecuteNonQuery();
                        }
                        outcome.DisabledPlans++;
                    }
                }
                var after = EnabledPlanIds(conn, tid);
                // Plans this push inserted or enabled again, in the order written.
                var arrived = written.Where(i => after.Contains(i) && !before.Contains(i)).ToList();
                var plansMoved = !after.Where(i => !inserted.Contains(i)).SequenceEqual(before)
                    || (hadOverride && arrived.Count > 0);
                TsTargetOrder targetOrder;
                payload.OrderByTargetGuid.TryGetValue(group.Key, out targetOrder);
                bool setInTs;
                // The row ids in the order tsRows shoot them, over the plans
                // enabled before this push. As ts_order in upsert.py.
                var rowOfPlan = new Dictionary<int, string>();
                for (var idx = 0; idx < written.Count; idx++) rowOfPlan[written[idx]] = writtenRows[idx];
                List<int> tsNeverShot;
                var tsOrder = TsOrder.EffectivePlanOrder(before, tsRows, out tsNeverShot)
                    .Where(rowOfPlan.ContainsKey).Select(orderedPlan => rowOfPlan[orderedPlan]).ToList();
                var decision = OrderDecision(
                    conn, tid, targetOrder, writtenRows, tsRows, tsOrder, insertedTargets.Contains(group.Key), out setInTs);
                if (decision == OrderLeave) {
                    if (plansMoved) {
                        if (hadOverride) {
                            RemapOverrideRows(conn, tid, before, after);
                            AppendOverrideBlocks(conn, tid, after, arrived);
                            outcome.OverrideRebuilt.Add(TargetName(conn, tid));
                        }
                        DeleteCadence(conn, tid);
                    }
                } else {
                    var generated = GeneratedRows(conn, tid, written, after);
                    if (!TsOrder.SameRows(generated, tsRows)) {
                        ReplaceOverrideRows(conn, tid, generated);
                        DeleteCadence(conn, tid);
                        var name = TargetName(conn, tid);
                        outcome.OrderWritten.Add(name);
                        if (setInTs) {
                            outcome.Notes.Add($"{name}: ACP's filter order replaced an order set in Target Scheduler.");
                        }
                    } else if (plansMoved) {
                        DeleteCadence(conn, tid);
                    }
                    outcome.OrderSyncedTargetIds.Add(tid);
                }
                // Restart the walk every night (section 5): TS reuses a stored
                // cadence before anything else (FilterCadenceFactory.cs:19-29),
                // so without this a shortfall found in the morning waits for the
                // whole circle. TS rebuilds the cadence from row 1 of the
                // override rows.
                if (HasOverrideRows(conn, tid)) DeleteCadence(conn, tid);
            }

            NoteStrayTemplates(conn, payload, outcome);

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
            string[] claimKeys,
            TsSyncOutcome outcome = null
        ) {
            var cols = WritableColumns(table, entity.Columns, userVersion, columnsByTable);

            // The UPDATE never touches the insert-only columns: the frame
            // counts on an exposure plan and a project's creation date belong
            // to whoever wrote them first. See TsSchema.ColumnsForUpdate. Nor
            // does it touch a conditional project column (state, priority,
            // minimumtime and the two dates) unless TsConvert decided this
            // push should write it; see TsSchema.ColumnsForConditionalUpdate.
            var updateCols = TsSchema.ColumnsForConditionalUpdate(
                table, TsSchema.ColumnsForUpdate(table, cols), entity);
            var setClause = string.Join(", ", updateCols.Select(c => $"{Quote(c)} = ${c}"));

            // 0) By the Id the plan's ts_refs name. ResolvePins has already
            // checked the row exists and sits where the refs say, so this is
            // the row the plan came from. A project row follows the same
            // three column sets as the guid and claim paths below, minus
            // guid: the row keeps the identity Target Scheduler gave it. See
            // TsSchema.ColumnsForPinnedProjectUpdate. Every other table
            // writes only the fields ACP edits (TsSchema.ColumnsForPinnedUpdate).
            if (entity.PinnedId.HasValue) {
                if (table == "project") CheckStateConflict(conn, entity as TsProject, entity.PinnedId.Value, outcome);
                var pinnedCols = table == "project"
                    ? TsSchema.ColumnsForPinnedProjectUpdate(updateCols)
                    : TsSchema.ColumnsForPinnedUpdate(table, cols);
                if (pinnedCols.Count > 0) {
                    var pinnedSet = string.Join(", ", pinnedCols.Select(c => $"{Quote(c)} = ${c}"));
                    ExecuteWithValues(
                        conn, $"UPDATE {table} SET {pinnedSet} WHERE Id = $__id",
                        pinnedCols, entity, entity.PinnedId.Value);
                }
                counts.Pinned++;
                return entity.PinnedId.Value;
            }

            // 1) By our own guid.
            var existingId = SelectId(conn, table, "guid = $g", new Dictionary<string, object> {
                { "$g", entity.Guid },
            });
            if (existingId.HasValue) {
                if (table == "project") CheckStateConflict(conn, entity as TsProject, existingId.Value, outcome);
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
                        if (table == "project") CheckStateConflict(conn, entity as TsProject, claim.Item1, outcome);
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

        /// The two dock lines from docs/specs/ts-project-settings.md section 5,
        /// read out before the update below overwrites (or deliberately
        /// leaves alone) the state column. Both compare against the row Target
        /// Scheduler holds right now, which the ordinary base comparison in
        /// TsConvert never sees.
        ///
        /// ACP wins: this push is about to write state because ACP changed it
        /// since the base, and doing so actually changes what the live row
        /// holds. Section 5's own example pairs this message with a live row
        /// that still equals the base (TS simply never moved), so the check is
        /// "does the write change anything live", not "did TS's row diverge
        /// from the base independently".
        ///
        /// TS only: this push is leaving state alone (ACP has not changed it
        /// since the base) and the live row disagrees with ACP's value
        /// anyway. Nothing was written; the line says an upload would bring
        /// the rig's change into ACP.
        private static void CheckStateConflict(SqliteConnection conn, TsProject proj, int id, TsSyncOutcome outcome) {
            if (proj == null || outcome == null) return;
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "SELECT state FROM project WHERE Id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                var value = cmd.ExecuteScalar();
                if (value == null || value == DBNull.Value) return;
                var liveState = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                if (liveState == proj.State) return;

                if (proj.ConditionalColumnsToWrite.Contains("state")) {
                    outcome.ConflictLines.Add(
                        $"{proj.Name}: paused from ACP, which undid a change made in TS");
                } else {
                    outcome.ConflictLines.Add(
                        $"{proj.Name}: TS has it {StateName(liveState)}, send TS to ACP to bring that in");
                }
            }
        }

        private static string StateName(int tsState) {
            switch (tsState) {
                case 0: return "draft";
                case 1: return "active";
                case 2: return "inactive";
                case 3: return "closed";
                default: return tsState.ToString(CultureInfo.InvariantCulture);
            }
        }

        // -- ts_refs ---------------------------------------------------------

        /// Keep each pin only while the row it names still holds, and work out
        /// which of the payload's templates are still needed.
        ///
        /// A pin holds when the row exists, is in this profile, sits under the
        /// parent the payload is writing (a target under its project's pinned
        /// row, an exposure plan under its target's pinned row), and no other
        /// entity in this push has already pinned it. A pin that fails falls
        /// back to the guid and name path, which is what every push did before
        /// ts_refs were read, and a note says why.
        ///
        /// A pinned exposure plan keeps the template its row already uses. An
        /// exposure plan with no row of its own yet uses the template the refs
        /// give for its filter, when there is one. Returns null when no entity
        /// carried a pin at all, which leaves the old path untouched.
        /// Stops the sync, before anything is written, when it would insert a
        /// project beside an existing project of the same name in this profile.
        ///
        /// That happens when a plan's link to its Target Scheduler rows is
        /// missing or no longer holds and the existing row carries a guid this
        /// sync did not write, so neither the guid nor the name claim can take
        /// it. On 2026-10-09 it put a second copy of 13 projects into Target
        /// Scheduler, which then imaged the old copies while ACP followed the
        /// new ones. A duplicate costs a night's progress, so the sync now
        /// refuses and says how to link the projects instead.
        private static void RefuseDuplicateProjects(SqliteConnection conn, TsSyncPayload payload) {
            var clashes = new List<string>();
            foreach (var proj in payload.Projects) {
                if (proj.PinnedId.HasValue) continue;
                var mine = SelectId(conn, "project", "guid = $g",
                    new Dictionary<string, object> { { "$g", proj.Guid } });
                if (mine.HasValue) continue;
                var other = SelectIdAndGuid(conn, "project", "\"profileId\" = $profileId AND \"name\" = $name",
                    new Dictionary<string, object> {
                        { "$profileId", proj.ProfileId }, { "$name", proj.Name },
                    });
                // An unstamped row is claimed below, which is not a duplicate.
                if (other != null && !string.IsNullOrWhiteSpace(other.Item2)) clashes.Add(proj.Name);
            }
            if (clashes.Count == 0) return;
            throw new TsPushValidationException(
                "Nothing was synced. Target Scheduler already has " +
                (clashes.Count == 1 ? "a project" : $"{clashes.Count} projects") +
                " named " + string.Join(", ", clashes.Select(n => $"'{n}'")) +
                " that this sync cannot match to their ACP plans, so it would have made a second copy. " +
                "In NINA press Send TS to ACP and link them on ACP's review page, then sync again.");
        }

        private static void ResolvePins(
            SqliteConnection conn, TsSyncPayload payload, TsSyncOutcome outcome
        ) {
            var targets = payload.TargetsByProjectGuid.Values.SelectMany(v => v).ToList();
            var anyPins = payload.Projects.Any(p => p.PinnedId.HasValue)
                || targets.Any(t => t.PinnedId.HasValue);
            if (!anyPins) return;

            var profileId = payload.ProfileId;

            var usedProjects = new HashSet<int>();
            var projectByGuid = new Dictionary<string, TsProject>();
            foreach (var proj in payload.Projects) {
                projectByGuid[proj.Guid] = proj;
                if (!proj.PinnedId.HasValue) continue;
                var row = ReadRow(conn, "SELECT profileId FROM project WHERE Id = $id", proj.PinnedId.Value);
                if (row == null || !SameProfile(row[0], profileId) || !usedProjects.Add(proj.PinnedId.Value)) {
                    outcome.Notes.Add(
                        $"ts_refs project {proj.PinnedId} for '{proj.Name}' is gone or not this profile's, " +
                        "matched by guid and name instead");
                    proj.PinnedId = null;
                }
            }

            var usedTargets = new HashSet<int>();
            foreach (var group in payload.TargetsByProjectGuid) {
                TsProject proj;
                projectByGuid.TryGetValue(group.Key, out proj);
                foreach (var tgt in group.Value) {
                    if (!tgt.PinnedId.HasValue) continue;
                    var row = ReadRow(conn, "SELECT projectid FROM target WHERE Id = $id", tgt.PinnedId.Value);
                    var parent = proj?.PinnedId;
                    if (row == null || !parent.HasValue || ToInt(row[0]) != parent.Value
                        || !usedTargets.Add(tgt.PinnedId.Value)) {
                        outcome.Notes.Add(
                            $"ts_refs target {tgt.PinnedId} for '{tgt.Name}' is gone or has moved, " +
                            "matched by guid and name instead");
                        tgt.PinnedId = null;
                    }
                }
            }
        }

        // -- Filter rows (docs/specs/goal-panel-and-ts-order.md section 3) --

        /// The Id of the profile's template named tpl.Name, or null. Two
        /// templates of one name: the lowest Id. Matches
        /// _template_id_by_name in nina_ts_sync/upsert.py.
        private static int? TemplateIdByName(SqliteConnection conn, TsExposureTemplate tpl) {
            return SelectId(conn, "exposuretemplate",
                "\"profileId\" = $p AND \"name\" = $n ORDER BY Id LIMIT 1",
                new Dictionary<string, object> { { "$p", tpl.ProfileId }, { "$n", tpl.Name } });
        }

        /// The exposure plan columns an update always writes.
        /// exposureTemplateId and exposure are written only when the row's
        /// template changed.
        private static readonly string[] planUpdateColumns =
            { "profileId", "targetid", "guid", "desired", "enabled" };

        /// The existing plan for a row: its own guid, the pinned id when that
        /// plan is on this target, then the goal-key recipes. A plan another
        /// row of this push already took is skipped.
        private static int? FindExposurePlan(
            SqliteConnection conn, TsExposurePlan plan, TsPlanLookup lookup, HashSet<int> used
        ) {
            var hit = ByGuid(conn, plan.Guid, used);
            if (hit.HasValue) return hit;
            if (lookup.PinnedId.HasValue && !used.Contains(lookup.PinnedId.Value)) {
                var row = ReadRow(conn, "SELECT targetid, profileId FROM exposureplan WHERE Id = $id",
                    lookup.PinnedId.Value);
                if (row != null && ToInt(row[0]) == plan.TargetId
                    && row[1] != null && row[1] != DBNull.Value
                    && string.Equals(row[1].ToString(), plan.ProfileId, StringComparison.Ordinal)) {
                    return lookup.PinnedId.Value;
                }
            }
            foreach (var guid in lookup.OldGuids) {
                hit = ByGuid(conn, guid, used);
                if (hit.HasValue) return hit;
            }
            return null;
        }

        private static int? ByGuid(SqliteConnection conn, string guid, HashSet<int> used) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "SELECT Id FROM exposureplan WHERE guid = $g ORDER BY Id";
                cmd.Parameters.AddWithValue("$g", (object)guid ?? DBNull.Value);
                using (var reader = cmd.ExecuteReader()) {
                    while (reader.Read()) {
                        var id = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                        if (!used.Contains(id)) return id;
                    }
                }
            }
            return null;
        }

        /// Write one row's exposure plan and return its Id. A plan found by
        /// any step of the chain is restamped with the row's guid and
        /// enabled, and its desired written. It keeps the template it has and
        /// its exposure, unless the row's template changed since the base:
        /// then it moves to the row's template with exposure -1. acquired and
        /// accepted always stay. Then the old claim of an unstamped plan on
        /// the same target and template, then an insert with exposure -1.
        ///
        /// rowTemplate gives the row's template Id, by name with an insert on
        /// a miss. It is called only when a plan needs the row's template, so
        /// a plan that keeps its own never makes a template. Matches
        /// _upsert_exposure_plan in nina_ts_sync/upsert.py.
        private static int UpsertExposurePlan(
            SqliteConnection conn, TsExposurePlan plan, TsPlanLookup lookup, Func<int> rowTemplate,
            TsTableCounts counts, int userVersion, IReadOnlyDictionary<string, HashSet<string>> columnsByTable,
            HashSet<int> used, out bool inserted
        ) {
            inserted = false;
            var found = FindExposurePlan(conn, plan, lookup, used);
            var move = !found.HasValue || lookup.TemplateChanged;
            if (move) plan.ExposureTemplateId = rowTemplate();
            var wanted = move
                ? planUpdateColumns.Concat(new[] { "exposureTemplateId", "exposure" })
                : planUpdateColumns;
            var updateCols = WritableColumns("exposureplan", wanted, userVersion, columnsByTable);
            var setClause = string.Join(", ", updateCols.Select(c => $"{Quote(c)} = ${c}"));

            if (found.HasValue) {
                ExecuteWithValues(conn, $"UPDATE exposureplan SET {setClause} WHERE Id = $__id",
                    updateCols, plan, found.Value);
                counts.Updated++;
                return found.Value;
            }

            var claim = SelectIdAndGuid(conn, "exposureplan",
                "\"targetid\" = $t AND \"exposureTemplateId\" = $x ORDER BY Id",
                new Dictionary<string, object> { { "$t", plan.TargetId }, { "$x", plan.ExposureTemplateId } });
            if (claim != null && string.IsNullOrWhiteSpace(claim.Item2) && !used.Contains(claim.Item1)) {
                ExecuteWithValues(conn, $"UPDATE exposureplan SET {setClause} WHERE Id = $__id",
                    updateCols, plan, claim.Item1);
                counts.Claimed++;
                return claim.Item1;
            }

            inserted = true;
            return Insert(conn, "exposureplan", plan,
                WritableColumns("exposureplan", plan.Columns, userVersion, columnsByTable), counts);
        }

        private static int Insert(
            SqliteConnection conn, string table, TsEntity entity, List<string> cols, TsTableCounts counts
        ) {
            var placeholders = string.Join(", ", cols.Select(c => "$" + c));
            var columnList = string.Join(", ", cols.Select(Quote));
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = $"INSERT INTO {table} ({columnList}) VALUES ({placeholders})";
                foreach (var c in cols) {
                    cmd.Parameters.AddWithValue("$" + c, entity.ValueOf(c) ?? DBNull.Value);
                }
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "SELECT last_insert_rowid()";
                var id = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                counts.Inserted++;
                return id;
            }
        }

        /// The target's enabled plans in natural order, which is what
        /// referenceIdx counts over (SchedulerPlanLoader.cs:53).
        private static List<int> EnabledPlanIds(SqliteConnection conn, int targetId) {
            var ids = new List<int>();
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText =
                    "SELECT Id FROM exposureplan WHERE targetid = $t AND COALESCE(enabled, 1) <> 0 ORDER BY Id";
                cmd.Parameters.AddWithValue("$t", targetId);
                using (var reader = cmd.ExecuteReader()) {
                    while (reader.Read()) ids.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
                }
            }
            return ids;
        }

        /// Keep a target's override rows pointing at the same plans after the
        /// enabled set changed. A row of a plan no longer enabled is dropped,
        /// and the rest are renumbered from 1. Returns whether the target had
        /// any. Used only for rows the order writer leaves alone, which are
        /// rows set in TS: rows the sync generated are built again from ACP's
        /// order.
        private static bool RemapOverrideRows(SqliteConnection conn, int targetId, List<int> before, List<int> after) {
            var rows = new List<Tuple<int, int?>>();
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText =
                    "SELECT \"action\", referenceIdx FROM overrideexposureorderitem " +
                    "WHERE targetid = $t ORDER BY \"order\", Id";
                cmd.Parameters.AddWithValue("$t", targetId);
                using (var reader = cmd.ExecuteReader()) {
                    while (reader.Read()) {
                        var action = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                        int? reference = reader.IsDBNull(1)
                            ? (int?)null
                            : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                        rows.Add(Tuple.Create(action, reference));
                    }
                }
            }
            if (rows.Count == 0) return false;

            var kept = new List<Tuple<int, int>>();
            foreach (var r in rows) {
                if (r.Item1 != 0) {
                    kept.Add(Tuple.Create(r.Item1, r.Item2 ?? -1));
                    continue;
                }
                if (!r.Item2.HasValue || r.Item2.Value < 0 || r.Item2.Value >= before.Count) continue;
                var newIndex = after.IndexOf(before[r.Item2.Value]);
                if (newIndex >= 0) kept.Add(Tuple.Create(0, newIndex));
            }
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "DELETE FROM overrideexposureorderitem WHERE targetid = $t";
                cmd.Parameters.AddWithValue("$t", targetId);
                cmd.ExecuteNonQuery();
            }
            var order = 1;
            foreach (var k in kept) {
                using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText =
                        "INSERT INTO overrideexposureorderitem (targetid, \"order\", \"action\", referenceIdx) " +
                        "VALUES ($t, $o, $a, $r)";
                    cmd.Parameters.AddWithValue("$t", targetId);
                    cmd.Parameters.AddWithValue("$o", order++);
                    cmd.Parameters.AddWithValue("$a", k.Item1);
                    cmd.Parameters.AddWithValue("$r", k.Item2);
                    cmd.ExecuteNonQuery();
                }
            }
            return true;
        }

        /// Delete the cadence rows of every target in an updated project whose
        /// switch frequency or smart order this push wrote. Target Scheduler
        /// rebuilds a cadence from the stored rows before anything else
        /// (FilterCadenceFactory.cs:19-29), so without this a new setting does
        /// nothing until the old cadence runs out. TS's own editor does the
        /// same (Project.cs:257). docs/specs/goal-panel-and-ts-order.md
        /// section 4; matches _reset_cadence_if_order_written in upsert.py.
        private static void ResetCadenceIfOrderWritten(
            SqliteConnection conn, TsProject proj, int projectId, TsSyncOutcome outcome
        ) {
            if (!proj.ConditionalColumnsToWrite.Contains("filterswitchfrequency")
                && !proj.ConditionalColumnsToWrite.Contains("smartexposureorder")) {
                return;
            }
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText =
                    "DELETE FROM filtercadenceitem WHERE targetid IN " +
                    "(SELECT Id FROM target WHERE projectid = $p)";
                cmd.Parameters.AddWithValue("$p", projectId);
                cmd.ExecuteNonQuery();
            }
            outcome.CadenceResetProjects.Add(proj.Name);
        }

        private static bool HasOverrideRows(SqliteConnection conn, int targetId) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "SELECT 1 FROM overrideexposureorderitem WHERE targetid = $t LIMIT 1";
                cmd.Parameters.AddWithValue("$t", targetId);
                return cmd.ExecuteScalar() != null;
            }
        }

        /// Target Scheduler's default exposure throttle, a percentage
        /// (ProfilePreference.cs:67).
        public const double DefaultThrottle = 125.0;

        /// How many override rows one plan's block holds: N at a switch
        /// frequency of N; at 0, the frames TS needs to call the plan done,
        /// desired with the grader on, else ceil(throttle / 100 x desired).
        /// Matches _block_size in nina_ts_sync/upsert.py.
        private static int BlockSize(SqliteConnection conn, int targetId, int planId) {
            var row = ReadRow(conn,
                "SELECT p.filterswitchfrequency, p.enablegrader, p.profileId, e.desired " +
                "FROM exposureplan e JOIN target t ON t.Id = e.targetid " +
                $"JOIN project p ON p.Id = t.projectid WHERE e.Id = $id AND t.Id = {targetId}", planId);
            if (row == null) return 1;
            var freq = row[0] == null || row[0] == DBNull.Value ? 0 : ToInt(row[0]);
            if (freq > 0) return freq;
            var desired = Math.Max(1, row[3] == null || row[3] == DBNull.Value ? 0 : ToInt(row[3]));
            var grader = row[1] == null || row[1] == DBNull.Value ? 0 : ToInt(row[1]);
            if (grader != 0) return desired;
            var throttle = DefaultThrottle;
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "SELECT exposurethrottle FROM profilepreference WHERE profileId = $p ORDER BY Id LIMIT 1";
                cmd.Parameters.AddWithValue("$p", row[2] ?? DBNull.Value);
                var value = cmd.ExecuteScalar();
                if (value != null && value != DBNull.Value) throttle = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            return Math.Max(1, (int)Math.Ceiling(Math.Round(throttle / 100.0 * desired, 9)));
        }

        /// Append a block for each plan this push inserted or enabled again,
        /// in the order written. A target with override rows shoots only the
        /// plans those rows name (PlanningTarget.cs:80-97), so without this TS
        /// would never shoot it. Section 5's "new plan rule", for a target
        /// whose rows the order writer left alone. Matches
        /// _append_override_blocks in nina_ts_sync/upsert.py.
        private static void AppendOverrideBlocks(SqliteConnection conn, int targetId, List<int> after, List<int> arrived) {
            if (arrived.Count == 0) return;
            int last;
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "SELECT COALESCE(MAX(\"order\"), 0) FROM overrideexposureorderitem WHERE targetid = $t";
                cmd.Parameters.AddWithValue("$t", targetId);
                last = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            foreach (var planId in arrived) {
                var index = after.IndexOf(planId);
                var size = BlockSize(conn, targetId, planId);
                for (var i = 0; i < size; i++) {
                    using (var cmd = conn.CreateCommand()) {
                        cmd.CommandText =
                            "INSERT INTO overrideexposureorderitem (targetid, \"order\", \"action\", referenceIdx) " +
                            "VALUES ($t, $o, 0, $r)";
                        cmd.Parameters.AddWithValue("$t", targetId);
                        cmd.Parameters.AddWithValue("$o", ++last);
                        cmd.Parameters.AddWithValue("$r", index);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        private static void DeleteCadence(SqliteConnection conn, int targetId) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "DELETE FROM filtercadenceitem WHERE targetid = $t";
                cmd.Parameters.AddWithValue("$t", targetId);
                cmd.ExecuteNonQuery();
            }
        }

        /// The target's override rows as (action, referenceIdx) pairs, in
        /// order. Matches _override_rows in nina_ts_sync/upsert.py.
        private static List<Tuple<int, int>> OverrideRows(SqliteConnection conn, int targetId) {
            var rows = new List<Tuple<int, int>>();
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText =
                    "SELECT \"action\", referenceIdx FROM overrideexposureorderitem " +
                    "WHERE targetid = $t ORDER BY \"order\", Id";
                cmd.Parameters.AddWithValue("$t", targetId);
                using (var reader = cmd.ExecuteReader()) {
                    while (reader.Read()) {
                        rows.Add(Tuple.Create(
                            Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                            reader.IsDBNull(1) ? -1 : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture)));
                    }
                }
            }
            return rows;
        }

        private static void ReplaceOverrideRows(SqliteConnection conn, int targetId, List<Tuple<int, int>> rows) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "DELETE FROM overrideexposureorderitem WHERE targetid = $t";
                cmd.Parameters.AddWithValue("$t", targetId);
                cmd.ExecuteNonQuery();
            }
            var order = 1;
            foreach (var row in rows) {
                using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText =
                        "INSERT INTO overrideexposureorderitem (targetid, \"order\", \"action\", referenceIdx) " +
                        "VALUES ($t, $o, $a, $r)";
                    cmd.Parameters.AddWithValue("$t", targetId);
                    cmd.Parameters.AddWithValue("$o", order++);
                    cmd.Parameters.AddWithValue("$a", row.Item1);
                    cmd.Parameters.AddWithValue("$r", row.Item2);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// The override rows that make TS shoot ACP's order: a block per plan
        /// in ACP's order, then dithers by TS's own rule when the project
        /// dithers. None when that order is the natural order. An enabled plan
        /// no ACP row wrote keeps its place after ACP's rows, in natural order,
        /// so the order never stops TS shooting it. Matches _generated_rows in
        /// nina_ts_sync/upsert.py.
        private static List<Tuple<int, int>> GeneratedRows(
            SqliteConnection conn, int targetId, List<int> acpPlanIds, List<int> after
        ) {
            var order = acpPlanIds.Distinct().Where(after.Contains).ToList();
            order.AddRange(after.Where(p => !order.Contains(p)).ToList());
            if (order.SequenceEqual(after)) return new List<Tuple<int, int>>();
            var items = new List<Tuple<int, int>>();
            foreach (var planId in order) {
                var index = after.IndexOf(planId);
                var size = BlockSize(conn, targetId, planId);
                for (var i = 0; i < size; i++) items.Add(Tuple.Create(TsOrder.Exposure, index));
            }
            var ditherRow = ReadRow(conn,
                "SELECT p.ditherevery FROM target t JOIN project p ON p.Id = t.projectid WHERE t.Id = $id", targetId);
            var ditherEvery = ditherRow == null || ditherRow[0] == null || ditherRow[0] == DBNull.Value
                ? 0 : ToInt(ditherRow[0]);
            if (ditherEvery <= 0) return items;
            var filters = new Dictionary<int, string>();
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText =
                    "SELECT e.Id, x.filtername FROM exposureplan e " +
                    "LEFT JOIN exposuretemplate x ON x.Id = e.exposureTemplateId WHERE e.targetid = $t";
                cmd.Parameters.AddWithValue("$t", targetId);
                using (var reader = cmd.ExecuteReader()) {
                    while (reader.Read()) {
                        filters[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                            reader.IsDBNull(1) ? string.Empty : reader.GetValue(1).ToString();
                    }
                }
            }
            var filterOf = new List<string>();
            foreach (var planId in after) {
                string filterName;
                filterOf.Add(filters.TryGetValue(planId, out filterName) ? filterName : string.Empty);
            }
            return TsOrder.InjectDithers(items, filterOf, ditherEvery);
        }

        private static bool SmartOrderOn(SqliteConnection conn, int targetId) {
            var row = ReadRow(conn,
                "SELECT p.smartexposureorder FROM target t JOIN project p ON p.Id = t.projectid WHERE t.Id = $id",
                targetId);
            return row != null && row[0] != null && row[0] != DBNull.Value && ToInt(row[0]) != 0;
        }

        private const string OrderLeave = "leave";
        private const string OrderWrite = "write";
        private const string OrderRefresh = "refresh";

        /// Which case of docs/specs/goal-panel-and-ts-order.md section 5
        /// applies, and whether writing replaces an order set in TS. tsOrder is
        /// the row ids in the order tsRows shoot them, over the plans enabled
        /// before this push. A target this push inserted has no rows of anyone's
        /// yet, so ACP's order goes in as a new project's state does. Smart
        /// order on: leave. No base order: leave. ACP's order changed since the
        /// target's base order (the anchor panel's): write, ACP wins. The same
        /// order and TS's rows still the base's, and those rows already shoot
        /// ACP's order: refresh the rows the sync wrote. Otherwise leave: the
        /// rows were set in TS, or they are another panel's order that ACP does
        /// not hold. Matches _order_decision in nina_ts_sync/upsert.py.
        private static string OrderDecision(
            SqliteConnection conn, int targetId, TsTargetOrder order, List<string> acpRowIds,
            List<Tuple<int, int>> tsRows, List<string> tsOrder, bool insertedTarget, out bool setInTs
        ) {
            setInTs = false;
            if (SmartOrderOn(conn, targetId)) return OrderLeave;
            if (insertedTarget) return OrderWrite;
            if (order?.BaseOrder == null) return OrderLeave;
            if (!TsOrder.SameOrder(acpRowIds, order.BaseOrder)) {
                setInTs = !TsOrder.SameRows(tsRows, order.BaseRows ?? new List<Tuple<int, int>>());
                return OrderWrite;
            }
            if (order.BaseRows != null && TsOrder.SameRows(tsRows, order.BaseRows)
                && TsOrder.SameOrder(acpRowIds, tsOrder)) {
                return OrderRefresh;
            }
            return OrderLeave;
        }

        private static string TargetName(SqliteConnection conn, int targetId) {
            var row = ReadRow(conn, "SELECT name FROM target WHERE Id = $id", targetId);
            return row != null && row[0] != null && row[0] != DBNull.Value
                ? row[0].ToString()
                : targetId.ToString(CultureInfo.InvariantCulture);
        }

        /// "M42 R: TS shoots 300 s, template R says 180 s" for a plan whose
        /// own exposure overrides its template's default.
        private static void NoteExposureMismatch(SqliteConnection conn, int planId, string label, TsSyncOutcome outcome) {
            var row = ReadRow(conn,
                "SELECT e.exposure, x.defaultexposure, x.name FROM exposureplan e " +
                "JOIN exposuretemplate x ON x.Id = e.exposureTemplateId WHERE e.Id = $id", planId);
            if (row == null) return;
            var exposure = ToDouble(row[0]);
            var dflt = ToDouble(row[1]);
            if (exposure > 0 && exposure != dflt) {
                outcome.ExposureMismatches.Add(
                    $"{label}: TS shoots {Seconds(exposure)} s, template {row[2]} says {Seconds(dflt)} s");
            }
        }

        /// The profile's templates whose filter is on none of the cameras this
        /// push used and that no enabled exposure plan uses. An older writer made some by using a goal key such as
        /// "R Stars" as a filter name; the sync never deletes them.
        private static void NoteStrayTemplates(SqliteConnection conn, TsSyncPayload payload, TsSyncOutcome outcome) {
            if (payload.CameraFilters.Count == 0) return;
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText =
                    "SELECT name, filtername FROM exposuretemplate WHERE profileId = $p " +
                    "AND Id NOT IN (SELECT exposureTemplateId FROM exposureplan " +
                    "WHERE COALESCE(enabled, 1) <> 0 AND exposureTemplateId IS NOT NULL) ORDER BY Id";
                cmd.Parameters.AddWithValue("$p", payload.ProfileId);
                using (var reader = cmd.ExecuteReader()) {
                    while (reader.Read()) {
                        var filter = reader.IsDBNull(1) ? string.Empty : reader.GetValue(1).ToString();
                        if (!payload.CameraFilters.Contains(TsFilterNames.Canonical(filter))) {
                            outcome.StrayTemplates.Add(reader.IsDBNull(0) ? string.Empty : reader.GetValue(0).ToString());
                        }
                    }
                }
            }
        }

        private static double ToDouble(object value) {
            if (value == null || value == DBNull.Value) return 0.0;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        /// Python's "{:g}": 300.0 reads "300", 2.5 reads "2.5".
        private static string Seconds(double value) {
            return value.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        private static object[] ReadRow(SqliteConnection conn, string sql, int id) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$id", id);
                using (var reader = cmd.ExecuteReader()) {
                    if (!reader.Read()) return null;
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    return values;
                }
            }
        }

        private static bool SameProfile(object value, string profileId) {
            var s = value == null || value == DBNull.Value ? null : value.ToString();
            return string.Equals(s, profileId, StringComparison.OrdinalIgnoreCase);
        }

        private static int ToInt(object value) {
            if (value == null || value == DBNull.Value) return -1;
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
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

        /// Replace rule weights an earlier version seeded under the wrong names.
        ///
        /// Only acts on a project carrying one of TsConvert.LegacyRuleNames,
        /// so a project made by hand in Target Scheduler is never touched. The
        /// legacy rows go, and each real rule name the project lacks is added
        /// at its default. Rows already under a real name keep their weight.
        /// Returns true when anything changed. Matches nina_ts_sync.
        private static bool HealLegacyRuleWeights(
            SqliteConnection conn, int projectId, List<TsRuleWeight> weights
        ) {
            var names = new HashSet<string>(StringComparer.Ordinal);
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "SELECT name FROM ruleweight WHERE projectid = $p";
                cmd.Parameters.AddWithValue("$p", projectId);
                using (var r = cmd.ExecuteReader()) {
                    while (r.Read()) {
                        if (!r.IsDBNull(0)) names.Add(r.GetString(0));
                    }
                }
            }
            var legacy = names.Where(n => TsConvert.LegacyRuleNames.Contains(n)).ToList();
            if (legacy.Count == 0) return false;
            foreach (var name in legacy) {
                using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText = "DELETE FROM ruleweight WHERE projectid = $p AND name = $n";
                    cmd.Parameters.AddWithValue("$p", projectId);
                    cmd.Parameters.AddWithValue("$n", name);
                    cmd.ExecuteNonQuery();
                }
            }
            foreach (var w in weights) {
                if (w.Name == null || names.Contains(w.Name)) continue;
                using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText =
                        "INSERT INTO ruleweight (projectid, name, weight) VALUES ($p, $n, $w)";
                    cmd.Parameters.AddWithValue("$p", projectId);
                    cmd.Parameters.AddWithValue("$n", w.Name);
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
        /// The row was found by the Id in the plan's ts_refs and updated in
        /// place, before any guid or name lookup.
        public int Pinned { get; set; }
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

        /// Guids of projects this push inserted rather than updated. Read by
        /// TsState.BuildBaseSnapshot to know when it is safe to establish a
        /// base for state, priority and minimumtime from ACP's own value: a
        /// brand new row is guaranteed to hold exactly what was just written.
        public HashSet<string> InsertedProjectGuids { get; } = new HashSet<string>(StringComparer.Ordinal);

        public List<string> Notes { get; } = new List<string>();

        /// Exposure plans of rows removed in ACP, set to enabled 0 by this push.
        public int DisabledPlans { get; set; }

        /// "M42 R: TS shoots 300 s, template R says 180 s" for every written
        /// plan whose own exposure overrides its template's default.
        public List<string> ExposureMismatches { get; } = new List<string>();

        /// Templates on the profile whose filter is on none of the cameras.
        /// The sync never deletes a template; these are for the user to remove.
        public List<string> StrayTemplates { get; } = new List<string>();

        /// Targets whose override rows were remapped because a plan was
        /// disabled or enabled again.
        public List<string> OverrideRebuilt { get; } = new List<string>();

        /// Projects whose switch frequency or smart order this push wrote, so
        /// every target's cadence rows were deleted.
        public List<string> CadenceResetProjects { get; } = new List<string>();

        /// Targets whose override rows this push replaced with ACP's order.
        public List<string> OrderWritten { get; } = new List<string>();

        /// Targets whose override rows are ACP's after this push, so the base
        /// records them and the order TS now shoots. Every other target keeps
        /// the order its old base recorded (docs/specs/goal-panel-and-ts-order.md
        /// section 5). Read by TsState.BuildBaseSnapshot.
        public HashSet<int> OrderSyncedTargetIds { get; } = new HashSet<int>();

        /// One line per project where this push's state decision met a value
        /// Target Scheduler already held that ACP did not expect, per
        /// docs/specs/ts-project-settings.md section 5. Worded for the dock
        /// and the sequencer log, not just diagnostics like Notes above.
        public List<string> ConflictLines { get; } = new List<string>();

        public string ToShortString() {
            var line =
                $"{Project.Inserted}+{Project.Updated} projects, " +
                $"{Target.Inserted}+{Target.Updated} targets, " +
                $"{ExposurePlan.Inserted}+{ExposurePlan.Updated} exposure plans, " +
                $"{ExposureTemplate.Inserted}+{ExposureTemplate.Updated} templates " +
                "(inserted+updated)";
            var pinned = Project.Pinned + Target.Pinned + ExposurePlan.Pinned;
            if (pinned > 0) {
                line += $", and {Project.Pinned} projects, {Target.Pinned} targets and " +
                        $"{ExposurePlan.Pinned} exposure plans updated in place from ts_refs";
            }
            return line;
        }
    }
}
