using ACP.NINA.Plugin.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// The two pieces of state a push records against each ACP plan.
    ///
    /// Ported from nina_ts_sync/state.py, and it has to stay ported rather than
    /// reinvented: the Python extension reads back what the plugin wrote and
    /// the plugin reads back what the extension wrote, and the next import does
    /// a three way merge against whichever one of them ran last.
    ///
    /// ts_refs holds the Target Scheduler row Ids that were written, plus
    /// timestamps, for the dock's "synced N minutes ago" and for spotting rows
    /// the user later deleted in Target Scheduler.
    ///
    /// ts_base_snapshot holds the slice of Target Scheduler state that the next
    /// import diffs against. It is the BASE of the merge, so anything left out
    /// of it reads as "no remote value" and silently loses a user edit.
    ///
    /// The shapes are JSON objects rather than typed classes because they go
    /// straight back to ACP as opaque plan fields, and a typed model here would
    /// be a second place to keep the schema in step for no gain.
    public static class TsState {

        public const string OperationPush = "push";
        public const string OperationImport = "import";

        public static string NowIso() {
            return DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        /// Panel keys are the 1-indexed row and column, stringified. Kept apart
        /// from the target name on purpose, so renaming a target in Target
        /// Scheduler does not break the mapping.
        public static string PanelKey(int row1, int col1) {
            return row1.ToString(CultureInfo.InvariantCulture) + "," +
                   col1.ToString(CultureInfo.InvariantCulture);
        }

        /// Reproduce the target naming the converter uses, for guid derivation.
        /// A target renamed in Target Scheduler still resolves, because the
        /// guid was stamped at push time and never changes afterwards.
        public static string ExpectedTargetName(string baseName, int rows, int cols, int row1, int col1) {
            if (rows == 1 && cols == 1) return baseName;
            var panelIndex = (row1 - 1) * cols + col1;
            return $"{baseName} Panel {panelIndex} (R{row1}C{col1})";
        }

        public static void ResolvePanels(Plan plan, out int rows, out int cols, out string baseName) {
            var mosaic = plan?.Target?.Mosaic;
            rows = Math.Max(1, mosaic?.Rows ?? 1);
            cols = Math.Max(1, mosaic?.Cols ?? 1);
            baseName = !string.IsNullOrWhiteSpace(plan?.Target?.Name)
                ? plan.Target.Name
                : (!string.IsNullOrWhiteSpace(plan?.Id) ? plan.Id : "Untitled");
        }

        /// The mosaic struct as it stood at push time.
        ///
        /// Without it the next diff sees a null BASE for every mosaic field and
        /// treats any real value as a change to pull, which is how a user's
        /// overlap percentage gets quietly overwritten. Null for a 1 by 1
        /// "mosaic" so the diff can short circuit.
        public static JObject MosaicSnapshot(Plan plan) {
            var mosaic = plan?.Target?.Mosaic;
            var rows = Math.Max(1, mosaic?.Rows ?? 1);
            var cols = Math.Max(1, mosaic?.Cols ?? 1);
            if (rows == 1 && cols == 1) return null;
            return new JObject {
                { "rows", rows },
                { "cols", cols },
                { "overlap_pct", (int)(mosaic?.OverlapPct ?? 0.0) },
            };
        }

        /// The ts_refs for one plan, read out of a snapshot taken straight
        /// after the write.
        ///
        /// `priorRefs` is the plan's existing ts_refs, if it has any. The
        /// timestamp for the operation that did not just run is carried across
        /// rather than dropped, so a push does not erase when the plan was last
        /// imported.
        public static JObject BuildRefs(
            TsSnapshot snap,
            Plan plan,
            string profileId,
            int userVersion,
            string operation,
            JObject priorRefs = null,
            Camera camera = null
        ) {
            var projectName = TsConvert.ProjectNameOf(plan);
            int rows, cols;
            string baseName;
            ResolvePanels(plan, out rows, out cols, out baseName);

            // The pinned ids win over the guid, the same order TsUpsert writes
            // in. A row made in Target Scheduler keeps its own guid when a pin
            // updates it, so the guid lookup alone finds nothing for it, and the
            // link written back would name no project. ts_links wins over
            // ts_refs, so that empty link then hides the good one and the next
            // push duplicates the project. That is what happened on 2026-10-09.
            // A pinned id is kept only while its row is still in this profile's
            // snapshot under the same parent.
            var pinned = TsConvert.RefsFor(plan, profileId);

            int projectId;
            var haveProjectId = pinned?.ProjectId != null && snap.ProjectsById.ContainsKey(pinned.ProjectId.Value);
            if (haveProjectId) {
                projectId = pinned.ProjectId.Value;
            } else {
                haveProjectId = snap.ProjectIdByGuid.TryGetValue(TsGuid.Project(profileId, projectName), out projectId);
            }

            var targetIdsByPanel = new JObject();
            for (var r = 1; r <= rows; r++) {
                for (var c = 1; c <= cols; c++) {
                    var name = ExpectedTargetName(baseName, rows, cols, r, c);
                    int tid;
                    TsTarget pinnedTarget;
                    if (haveProjectId && pinned != null
                        && pinned.TargetIdsByPanel.TryGetValue(PanelKey(r, c), out tid)
                        && snap.TargetsById.TryGetValue(tid, out pinnedTarget)
                        && pinnedTarget.ProjectId == projectId) {
                        targetIdsByPanel[PanelKey(r, c)] = tid;
                    } else if (snap.TargetIdByGuid.TryGetValue(TsGuid.Target(profileId, projectName, name), out tid)) {
                        targetIdsByPanel[PanelKey(r, c)] = tid;
                    }
                }
            }

            // Keyed by row id: "<target id>_<row id>" for exposure plans, and
            // the row id for the template its plan points at, panel 1,1
            // first. Every row's plan carries the row's guid after a push.
            var templateIdsByFilter = new JObject();
            var exposurePlanIds = new JObject();
            var planRows = TsRows.RowsOf(plan, camera);
            for (var r = 1; r <= rows; r++) {
                for (var c = 1; c <= cols; c++) {
                    var name = ExpectedTargetName(baseName, rows, cols, r, c);
                    var targetGuid = TsGuid.Target(profileId, projectName, name);
                    var panelTargetId = targetIdsByPanel[PanelKey(r, c)];
                    var tid = panelTargetId != null ? (int)panelTargetId : 0;
                    foreach (var row in planRows) {
                        int planId;
                        if (!snap.PlanIdByGuid.TryGetValue(
                                TsGuid.RowPlan(profileId, targetGuid, row.Filter, row.Template), out planId)) {
                            continue;
                        }
                        exposurePlanIds[$"{tid}_{row.Id}"] = planId;
                        if (templateIdsByFilter[row.Id] == null) {
                            templateIdsByFilter[row.Id] = snap.PlansById[planId].ExposureTemplateId;
                        }
                    }
                }
            }

            var prior = priorRefs ?? new JObject();
            var refs = new JObject {
                { "profile_id", profileId },
                { "project_id", haveProjectId ? (JToken)projectId : JValue.CreateNull() },
                { "target_ids_by_panel", targetIdsByPanel },
                { "template_ids_by_filter", templateIdsByFilter },
                { "exposure_plan_ids", exposurePlanIds },
                { "last_pushed_iso", prior["last_pushed_iso"] ?? JValue.CreateNull() },
                { "last_pushed_user_version", prior["last_pushed_user_version"] ?? JValue.CreateNull() },
                { "last_imported_iso", prior["last_imported_iso"] ?? JValue.CreateNull() },
            };

            var now = NowIso();
            if (operation == OperationPush) {
                refs["last_pushed_iso"] = now;
                refs["last_pushed_user_version"] = userVersion;
            } else if (operation == OperationImport) {
                refs["last_imported_iso"] = now;
            }
            return refs;
        }

        /// The BASE for the next three way merge.
        ///
        /// Anything the snapshot does not have is left out, because the user
        /// having already deleted the Target Scheduler row is the same
        /// situation as never having written it: there is no remote value.
        ///
        /// `pushedProject` and `outcome` are the TsProject this push computed
        /// for this plan's project and the write outcome, both optional. When
        /// given, they decide the base for state, priority and minimumtime
        /// (see the block below); when null, those three fields are left out,
        /// which is always safe, just conservative. TsPushService passes both
        /// after a push; a caller building a base outside a push, if one ever
        /// exists, gets the conservative behaviour for free.
        /// Base project fields a push writes only when ACP changed them, and
        /// which otherwise keep the old base's value. See BuildBaseSnapshot.
        private static readonly string[] CarriedProjectFields = { "filterswitchfrequency", "smartexposureorder" };

        public static JObject BuildBaseSnapshot(
            TsSnapshot snap, Plan plan, string profileId,
            TsProject pushedProject = null, TsSyncOutcome outcome = null,
            Camera camera = null
        ) {
            var projectName = TsConvert.ProjectNameOf(plan);
            int rows, cols;
            string baseName;
            ResolvePanels(plan, out rows, out cols, out baseName);

            var targetsByPanel = new JObject();
            var templatesByFilter = new JObject();
            var plansByPanelFilter = new JObject();

            var snapshot = new JObject {
                { "captured_iso", NowIso() },
                { "project", JValue.CreateNull() },
                { "mosaic", (JToken)MosaicSnapshot(plan) ?? JValue.CreateNull() },
                { "targets_by_panel", targetsByPanel },
                { "templates_by_filter", templatesByFilter },
                { "exposure_plans_by_panel_filter", plansByPanelFilter },
            };

            // The pinned id wins over the guid, the same order TsUpsert writes
            // in. A project made by hand in Target Scheduler carries its own
            // guid, not ACP's TsGuid, so the guid lookup alone would find
            // nothing for it and the base snapshot would have no project
            // block at all. See docs/specs/ts-project-settings.md section 3.
            TsProject proj = null;
            var pinnedProjectId = TsConvert.RefsFor(plan, profileId)?.ProjectId;
            if (pinnedProjectId.HasValue) {
                snap.ProjectsById.TryGetValue(pinnedProjectId.Value, out proj);
            }
            if (proj == null) {
                int projectId;
                if (snap.ProjectIdByGuid.TryGetValue(TsGuid.Project(profileId, projectName), out projectId)) {
                    proj = snap.ProjectsById[projectId];
                }
            }
            if (proj != null) {
                var projectBlock = new JObject {
                    { "name", proj.Name },
                    { "minimumaltitude", proj.MinimumAltitude },
                    { "meridianwindow", proj.MeridianWindow },
                    { "enablegrader", proj.EnableGrader },
                };

                // state, priority and minimumtime never come from what Target
                // Scheduler actually holds: this push may have deliberately
                // left one of them alone (section 5's conflict rule), and
                // reading it back here would make that value look agreed on,
                // so the next push could overwrite it. ACP's own computed
                // value is the only safe source, and only once there is
                // either an existing base to carry forward or a fresh insert
                // that is guaranteed to hold exactly what was written. See
                // docs/specs/ts-project-settings.md section 5, "No base".
                var establishBase = pushedProject != null &&
                    (pushedProject.HasBase ||
                     (outcome != null && outcome.InsertedProjectGuids.Contains(pushedProject.Guid)));
                if (establishBase) {
                    projectBlock["state"] = pushedProject.State;
                    projectBlock["priority"] = pushedProject.Priority;
                    projectBlock["minimumtime"] = pushedProject.MinimumTime;
                }

                // The shooting-order settings
                // (docs/specs/goal-panel-and-ts-order.md section 4). The base
                // takes what Target Scheduler holds, except that a setting this
                // push did not write keeps the old base's value, for the same
                // reason as state above. ditherevery is TS's own, kept for the
                // order writer. Matches _project_snapshot and
                // carry_unwritten_project_fields in state.py.
                projectBlock["filterswitchfrequency"] = proj.FilterSwitchFrequency;
                projectBlock["smartexposureorder"] = proj.SmartExposureOrder;
                projectBlock["ditherevery"] = proj.DitherEvery;
                if (pushedProject != null) {
                    var oldProject = TsConvert.BaseProjectFor(plan, profileId);
                    foreach (var column in CarriedProjectFields) {
                        if (oldProject == null || pushedProject.ConditionalColumnsToWrite.Contains(column)) continue;
                        var old = oldProject[column];
                        if (old != null && old.Type != JTokenType.Null) projectBlock[column] = old.DeepClone();
                    }
                }

                snapshot["project"] = projectBlock;
            }

            // Each panel's target records exposure_order and override_rows
            // (docs/specs/goal-panel-and-ts-order.md section 5). The targets
            // whose override rows are ACP's after the push record what TS now
            // has; every other target keeps what the old base recorded, or
            // nothing, because a push that left TS's rows alone keeps the old
            // base order. With no outcome, each target records the order TS
            // shoots, and its override rows only when TS has none, as an import
            // does, so rows set in TS are never taken for rows the sync wrote.
            // The base order is the anchor panel's (1,1) for every panel.
            // Matches build_base_snapshot in state.py.
            var planRows = TsRows.RowsOf(plan, camera);
            var orderTargets = outcome?.OrderSyncedTargetIds;
            var oldTargets = TsConvert.BaseSnapshotFor(plan, profileId)?["targets_by_panel"] as JObject;
            for (var r = 1; r <= rows; r++) {
                for (var c = 1; c <= cols; c++) {
                    var name = ExpectedTargetName(baseName, rows, cols, r, c);
                    var targetGuid = TsGuid.Target(profileId, projectName, name);
                    int tid;
                    if (!snap.TargetIdByGuid.TryGetValue(targetGuid, out tid)) {
                        continue;
                    }
                    var tgt = snap.TargetsById[tid];
                    var entry = new JObject {
                        { "name", tgt.Name },
                        // Hours, matching what Target Scheduler stores.
                        { "ra", tgt.Ra },
                        { "dec", tgt.Dec },
                        { "rotation", tgt.Rotation },
                    };
                    if (orderTargets == null || orderTargets.Contains(tid)) {
                        var rowIdByPlan = new Dictionary<int, string>();
                        foreach (var row in planRows) {
                            int planId;
                            if (snap.PlanIdByGuid.TryGetValue(
                                    TsGuid.RowPlan(profileId, targetGuid, row.Filter, row.Template), out planId)
                                && !rowIdByPlan.ContainsKey(planId)) {
                                rowIdByPlan[planId] = row.Id;
                            }
                        }
                        List<Tuple<int, int>> overrideRows;
                        if (!snap.OverrideRowsByTarget.TryGetValue(tid, out overrideRows)) {
                            overrideRows = new List<Tuple<int, int>>();
                        }
                        List<int> neverShot;
                        var order = TsOrder.EffectivePlanOrder(snap.EnabledPlanIds(tid), overrideRows, out neverShot);
                        entry["exposure_order"] = new JArray(
                            order.Where(rowIdByPlan.ContainsKey).Select(p => rowIdByPlan[p]));
                        if (orderTargets != null || overrideRows.Count == 0) {
                            entry["override_rows"] = TsOrder.ToJson(overrideRows);
                        }
                    } else {
                        var old = oldTargets?[PanelKey(r, c)] as JObject;
                        foreach (var key in new[] { "exposure_order", "override_rows" }) {
                            var value = old?[key];
                            if (value != null) entry[key] = value.DeepClone();
                        }
                    }
                    targetsByPanel[PanelKey(r, c)] = entry;
                }
            }
            ShareAnchorOrder(targetsByPanel);

            for (var r = 1; r <= rows; r++) {
                for (var c = 1; c <= cols; c++) {
                    var name = ExpectedTargetName(baseName, rows, cols, r, c);
                    var targetGuid = TsGuid.Target(profileId, projectName, name);
                    foreach (var row in planRows) {
                        int planId;
                        if (!snap.PlanIdByGuid.TryGetValue(
                                TsGuid.RowPlan(profileId, targetGuid, row.Filter, row.Template), out planId)) {
                            continue;
                        }
                        var ep = snap.PlansById[planId];
                        TsExposureTemplate tpl;
                        snap.TemplatesById.TryGetValue(ep.ExposureTemplateId, out tpl);
                        if (tpl != null && templatesByFilter[row.Id] == null) {
                            templatesByFilter[row.Id] = TemplateEntry(tpl);
                        }
                        plansByPanelFilter[$"{PanelKey(r, c)}_{row.Id}"] = PlanEntry(ep, tpl, row.Filter, row.Template);
                    }
                }
            }

            return snapshot;
        }

        /// The base order is one per target: the anchor panel's (1,1). Every
        /// other panel records the anchor's order as its exposure_order, never
        /// its own, so a hand-made order on one panel is not mistaken for ACP's
        /// order at the next push (docs/specs/goal-panel-and-ts-order.md
        /// section 5). Without an anchor order, no panel records one. Matches
        /// _share_anchor_order in state.py.
        private static void ShareAnchorOrder(JObject targetsByPanel) {
            var anchor = targetsByPanel["1,1"] as JObject;
            var order = anchor?["exposure_order"] as JArray;
            foreach (var pair in targetsByPanel.Properties().ToList()) {
                if (pair.Name == "1,1") continue;
                var entry = pair.Value as JObject;
                if (entry == null) continue;
                if (order != null) {
                    entry["exposure_order"] = order.DeepClone();
                } else {
                    entry.Remove("exposure_order");
                }
            }
        }

        private static JObject TemplateEntry(TsExposureTemplate tpl) {
            return new JObject {
                { "name", tpl.Name },
                { "defaultexposure", tpl.DefaultExposure },
                { "gain", tpl.Gain },
                { "offset", tpl.Offset },
                { "bin", tpl.Bin },
            };
        }

        /// One exposure plan in the base. filter and template say which row
        /// it was written for, so the next push can tell a template change,
        /// the one update that writes exposure. template_sub_s is the sub TS
        /// uses, which ACP copies onto the row when the links post arrives.
        /// Matches _plan_entry in nina_ts_sync/state.py.
        private static JObject PlanEntry(TsExposurePlan ep, TsExposureTemplate tpl, string filter, string template) {
            return new JObject {
                { "desired", ep.Desired },
                { "acquired", ep.Acquired },
                { "accepted", ep.Accepted },
                { "exposure", ep.Exposure },
                { "filter", filter },
                { "template", template },
                { "template_sub_s", TsRows.PlanSubS(ep.Exposure, tpl?.DefaultExposure ?? 0.0) },
            };
        }
    }

    /// The state a push captured for one plan, ready to be posted back to ACP.
    public class TsPlanState {
        public string PlanId { get; set; }
        public JObject Refs { get; set; }
        public JObject BaseSnapshot { get; set; }
    }
}
