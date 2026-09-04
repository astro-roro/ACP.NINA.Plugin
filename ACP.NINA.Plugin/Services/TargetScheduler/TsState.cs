using ACP.NINA.Plugin.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;

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
            JObject priorRefs = null
        ) {
            var projectName = TsConvert.ProjectNameOf(plan);
            int rows, cols;
            string baseName;
            ResolvePanels(plan, out rows, out cols, out baseName);
            var cameraId = (plan?.CameraId ?? string.Empty).Trim();

            var projectGuid = TsGuid.Project(profileId, projectName);
            int projectId;
            var haveProjectId = snap.ProjectIdByGuid.TryGetValue(projectGuid, out projectId);

            var targetIdsByPanel = new JObject();
            for (var r = 1; r <= rows; r++) {
                for (var c = 1; c <= cols; c++) {
                    var name = ExpectedTargetName(baseName, rows, cols, r, c);
                    int tid;
                    if (snap.TargetIdByGuid.TryGetValue(TsGuid.Target(profileId, projectName, name), out tid)) {
                        targetIdsByPanel[PanelKey(r, c)] = tid;
                    }
                }
            }

            var templateIdsByFilter = new JObject();
            foreach (var filterName in FilterNames(plan)) {
                int tplId;
                if (snap.TemplateIdByGuid.TryGetValue(TsGuid.Template(profileId, filterName, cameraId), out tplId)) {
                    templateIdsByFilter[filterName] = tplId;
                }
            }

            var exposurePlanIds = new JObject();
            for (var r = 1; r <= rows; r++) {
                for (var c = 1; c <= cols; c++) {
                    var name = ExpectedTargetName(baseName, rows, cols, r, c);
                    var targetGuid = TsGuid.Target(profileId, projectName, name);
                    foreach (var filterName in FilterNames(plan)) {
                        int planId;
                        if (!snap.PlanIdByGuid.TryGetValue(
                                TsGuid.ExposurePlan(profileId, targetGuid, filterName), out planId)) {
                            continue;
                        }
                        var panelTargetId = targetIdsByPanel[PanelKey(r, c)];
                        var tid = panelTargetId != null ? (int)panelTargetId : 0;
                        exposurePlanIds[$"{tid}_{filterName}"] = planId;
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
        public static JObject BuildBaseSnapshot(TsSnapshot snap, Plan plan, string profileId) {
            var projectName = TsConvert.ProjectNameOf(plan);
            int rows, cols;
            string baseName;
            ResolvePanels(plan, out rows, out cols, out baseName);
            var cameraId = (plan?.CameraId ?? string.Empty).Trim();

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

            int projectId;
            if (snap.ProjectIdByGuid.TryGetValue(TsGuid.Project(profileId, projectName), out projectId)) {
                var proj = snap.ProjectsById[projectId];
                snapshot["project"] = new JObject {
                    { "name", proj.Name },
                    { "priority", proj.Priority },
                    { "minimumaltitude", proj.MinimumAltitude },
                    { "meridianwindow", proj.MeridianWindow },
                    { "enablegrader", proj.EnableGrader },
                };
            }

            for (var r = 1; r <= rows; r++) {
                for (var c = 1; c <= cols; c++) {
                    var name = ExpectedTargetName(baseName, rows, cols, r, c);
                    int tid;
                    if (!snap.TargetIdByGuid.TryGetValue(
                            TsGuid.Target(profileId, projectName, name), out tid)) {
                        continue;
                    }
                    var tgt = snap.TargetsById[tid];
                    targetsByPanel[PanelKey(r, c)] = new JObject {
                        { "name", tgt.Name },
                        // Hours, matching what Target Scheduler stores.
                        { "ra", tgt.Ra },
                        { "dec", tgt.Dec },
                        { "rotation", tgt.Rotation },
                    };
                }
            }

            foreach (var filterName in FilterNames(plan)) {
                int tplId;
                if (!snap.TemplateIdByGuid.TryGetValue(
                        TsGuid.Template(profileId, filterName, cameraId), out tplId)) {
                    continue;
                }
                var tpl = snap.TemplatesById[tplId];
                templatesByFilter[filterName] = new JObject {
                    { "name", tpl.Name },
                    { "defaultexposure", tpl.DefaultExposure },
                    { "gain", tpl.Gain },
                    { "offset", tpl.Offset },
                    { "bin", tpl.Bin },
                };
            }

            for (var r = 1; r <= rows; r++) {
                for (var c = 1; c <= cols; c++) {
                    var name = ExpectedTargetName(baseName, rows, cols, r, c);
                    var targetGuid = TsGuid.Target(profileId, projectName, name);
                    foreach (var filterName in FilterNames(plan)) {
                        int planId;
                        if (!snap.PlanIdByGuid.TryGetValue(
                                TsGuid.ExposurePlan(profileId, targetGuid, filterName), out planId)) {
                            continue;
                        }
                        var row = snap.PlansById[planId];
                        plansByPanelFilter[$"{PanelKey(r, c)}_{filterName}"] = new JObject {
                            { "desired", row.Desired },
                            { "acquired", row.Acquired },
                            { "accepted", row.Accepted },
                            { "exposure", row.Exposure },
                        };
                    }
                }
            }

            return snapshot;
        }

        private static IEnumerable<string> FilterNames(Plan plan) {
            if (plan?.FilterGoals == null) return new List<string>();
            return plan.FilterGoals.Keys;
        }
    }

    /// The state a push captured for one plan, ready to be posted back to ACP.
    public class TsPlanState {
        public string PlanId { get; set; }
        public JObject Refs { get; set; }
        public JObject BaseSnapshot { get; set; }
    }
}
