using ACP.NINA.Plugin.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// ACP plans into the payload the upsert writes.
    ///
    /// Ported from nina_ts_sync/convert.py, which in turn mirrors ACP's own
    /// _build_ts_export. All three have to agree, because a user can push from
    /// the plugin one night and from the Python extension the next and the rows
    /// have to be the same rows.
    ///
    /// Strictest wins per project group: the highest minimum altitude, the
    /// tightest non-zero meridian window, the highest priority.
    public static class TsConvert {

        public static readonly IReadOnlyDictionary<string, int> PriorityRank =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
                { "low", 0 }, { "normal", 1 }, { "high", 2 },
            };

        /// ACP's four project states to Target Scheduler's integer, matching
        /// the import mapping in docs/specs/ts-project-settings.md section 3.
        public static readonly IReadOnlyDictionary<string, int> StateToTsInt =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
                { "draft", 0 }, { "active", 1 }, { "inactive", 2 }, { "closed", 3 },
            };

        /// The order state resolves in when a project's plans disagree: the
        /// most-running value wins, so one active plan keeps the project
        /// imaging. Matches docs/specs/ts-project-settings.md section 1.
        private static readonly string[] StatePrecedence = { "active", "inactive", "draft", "closed" };

        /// Seeded on a freshly inserted project. Target Scheduler looks a
        /// project's weights up by each scoring rule's display name and throws
        /// when one is missing, so planning fails for every target until NINA
        /// restarts and Target Scheduler repairs the rows. These are the eight
        /// rules Target Scheduler 5 defines, at its own DEFAULT_WEIGHT x 100.
        /// Matches DEFAULT_RULE_WEIGHTS in nina_ts_sync.
        public static readonly IReadOnlyList<KeyValuePair<string, double>> DefaultRuleWeights =
            new List<KeyValuePair<string, double>> {
                new KeyValuePair<string, double>("Percent Complete", 50.0),
                new KeyValuePair<string, double>("Project Priority", 50.0),
                new KeyValuePair<string, double>("Setting Soonest", 50.0),
                new KeyValuePair<string, double>("Meridian Window Priority", 75.0),
                new KeyValuePair<string, double>("Target Switch Penalty", 67.0),
                new KeyValuePair<string, double>("Mosaic Completion", 0.0),
                new KeyValuePair<string, double>("Smart Exposure Order", 0.0),
                new KeyValuePair<string, double>("Meridian Flip Penalty", 0.0),
            };

        /// The internal names earlier versions seeded by mistake (2026-10-09:
        /// planning threw "The given key 'Meridian Flip Penalty' was not
        /// present in the dictionary"). A sync that finds them on a project it
        /// touches removes them and adds whichever real names are missing.
        public static readonly ISet<string> LegacyRuleNames = new HashSet<string>(StringComparer.Ordinal) {
            "PercentComplete", "MeridianWindow", "MosaicCompletion", "SettingSoonest", "SmartExposure",
        };

        /// Build the payload for a run.
        ///
        /// `createDateUnix` is passed in rather than read from the clock so a
        /// test can compare its rows to the ones the Python extension writes
        /// under a frozen clock.
        ///
        /// `wheelFilters` are the slot names in the NINA profile's filter wheel.
        /// When given, each template's filter name is written under the wheel's
        /// spelling (see TsFilterNames), because Target Scheduler matches by
        /// exact name. Identity is left alone: the template guid still comes
        /// from the plan's own filter name, so a re-push updates the same rows
        /// and the Python extension still finds them. Null keeps the plan's
        /// names as they are, which is what the Python extension does.
        ///
        /// `now` is the moment the AAC freshness rule is judged at; it
        /// defaults to createDateUnix.
        public static TsSyncPayload BuildPayload(
            IEnumerable<Plan> plans,
            GearResponse gear,
            string profileId,
            long createDateUnix,
            IReadOnlyList<string> wheelFilters = null,
            DateTimeOffset? now = null,
            string profileName = null
        ) {
            var payload = new TsSyncPayload {
                ProfileId = profileId, ProfileName = profileName, WheelSlots = wheelFilters,
            };
            // Null means the caller gave no wheel at all, which keeps today's
            // behaviour: names are written as the plan has them. A list is an
            // answer, even an empty one: empty is "the wheel is unknown", and
            // a list with no slot for a filter is "not on the wheel yet". Both
            // skip the rows (docs/specs/gear-templates.md section 5).
            var wheelGiven = wheelFilters != null;
            var wheelKnown = wheelFilters != null && wheelFilters.Any(f => !string.IsNullOrWhiteSpace(f));
            var profileLabel = string.IsNullOrWhiteSpace(profileName) ? "this profile" : profileName;
            var gearWorkByKey = new Dictionary<string, TsGearWork>(StringComparer.Ordinal);
            var templateByName = new Dictionary<string, TsExposureTemplate>(StringComparer.Ordinal);
            // (camera, filter) to the rows skipped because the filter is not
            // on the wheel, in the order met.
            var offWheelOrder = new List<string>();
            var offWheelRows = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var offWheelOwner = new Dictionary<string, Tuple<string, string>>(StringComparer.Ordinal);
            // The moment the AAC freshness rule is judged at
            // (docs/specs/goal-panel-and-ts-order.md section 6).
            payload.AacNow = now ?? DateTimeOffset.FromUnixTimeSeconds(createDateUnix);
            var planList = (plans ?? Enumerable.Empty<Plan>()).Where(p => p != null).ToList();

            var telescopesById = new Dictionary<string, Telescope>(StringComparer.Ordinal);
            var camerasById = new Dictionary<string, Camera>(StringComparer.Ordinal);
            if (gear != null) {
                foreach (var t in gear.Telescopes ?? new List<Telescope>()) {
                    if (t?.Id != null) telescopesById[t.Id] = t;
                }
                foreach (var c in gear.Cameras ?? new List<Camera>()) {
                    if (c?.Id != null) camerasById[c.Id] = c;
                }
            }

            var templateGuidByName = new Dictionary<string, string>(StringComparer.Ordinal);
            // Target identity to the ACP plan that produced it, so a collision
            // can say which two plans need looking at rather than just that one
            // happened.
            var planLabelByTargetGuid = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var group in GroupByProject(planList)) {
                var projectName = group.Key;
                double minAltitude;
                int meridianWindow;
                string priorityName;
                string stateName;
                int minimumTime;
                ProjectConstraints(
                    group.Value, out minAltitude, out meridianWindow, out priorityName,
                    out stateName, out minimumTime);

                var tsState = StateToTsInt[stateName];
                var tsPriority = RankOf(priorityName);
                int? switchFrequency;
                int? smartOrder;
                GroupOrderSettings(projectName, group.Value, out switchFrequency, out smartOrder, payload.Notes);

                var proj = new TsProject {
                    ProfileId = profileId,
                    Name = projectName,
                    Guid = TsGuid.Project(profileId, projectName),
                    State = tsState,
                    Priority = tsPriority,
                    CreateDate = createDateUnix,
                    MinimumAltitude = minAltitude,
                    MeridianWindow = meridianWindow,
                    MinimumTime = minimumTime,
                    // On insert, ACP's value, or 0 and off when the plan has
                    // none (docs/specs/goal-panel-and-ts-order.md section 4).
                    FilterSwitchFrequency = switchFrequency ?? 0,
                    SmartExposureOrder = smartOrder ?? 0,
                    // Set for insert correctness whenever the resolved state
                    // is active or inactive/closed. Whether the update
                    // actually writes either column follows
                    // ConditionalColumnsToWrite below, per section 4: "The
                    // writer adds the matching one only when it writes
                    // state, so they never change on their own."
                    ActiveDate = tsState == StateToTsInt["active"] ? createDateUnix : (long?)null,
                    InactiveDate = tsState == StateToTsInt["inactive"] || tsState == StateToTsInt["closed"]
                        ? createDateUnix
                        : (long?)null,
                    // The first plan in the group whose refs name a project.
                    // TsUpsert checks it still exists in this profile.
                    PinnedId = group.Value
                        .Select(p => RefsFor(p, profileId)?.ProjectId)
                        .FirstOrDefault(id => id.HasValue),
                };
                var baseProject = group.Value
                    .Select(p => BaseProjectFor(p, profileId))
                    .FirstOrDefault(b => b != null);
                proj.HasBase = baseProject != null;
                proj.ConditionalColumnsToWrite = ConditionalColumnsFor(
                    baseProject, tsState, tsPriority, minimumTime, switchFrequency, smartOrder);
                payload.Projects.Add(proj);
                payload.RuleWeightsByProjectGuid[proj.Guid] = DefaultRuleWeights
                    .Select(kv => new TsRuleWeight { ProjectId = 0, Name = kv.Key, Weight = kv.Value })
                    .ToList();
                payload.TargetsByProjectGuid[proj.Guid] = new List<TsTarget>();
                var aac = new TsAacProject { Name = projectName };
                payload.AacByProjectGuid[proj.Guid] = aac;
                var watermarks = new List<string>();

                foreach (var plan in group.Value) {
                    var refs = RefsFor(plan, profileId);
                    var target = plan.Target;
                    var raDeg = target?.CenterRaDeg ?? 0.0;
                    var decDeg = target?.CenterDecDeg ?? 0.0;
                    var rotDeg = target?.RotationDeg ?? 0.0;

                    Telescope telescope = null;
                    Camera camera = null;
                    if (plan.TelescopeId != null) telescopesById.TryGetValue(plan.TelescopeId, out telescope);
                    if (plan.CameraId != null) camerasById.TryGetValue(plan.CameraId, out camera);

                    double fovW, fovH;
                    FovArcmin(telescope, camera, out fovW, out fovH);

                    var mosaic = target?.Mosaic;
                    var rows = Math.Max(1, mosaic?.Rows ?? 1);
                    var cols = Math.Max(1, mosaic?.Cols ?? 1);
                    var overlapPct = mosaic?.OverlapPct ?? 0.0;

                    var cameraId = camera?.Id ?? string.Empty;
                    if (camera?.Filters != null) {
                        foreach (var name in camera.Filters.Keys) payload.CameraFilters.Add(TsFilterNames.Canonical(name));
                    }
                    var planRows = TsRows.RowsOf(plan, camera);
                    var pushed = planRows.Where(r => TsRows.Desired(r) > 0).ToList();
                    var planAac = TsRows.PlanAac(plan);
                    if (planAac == null) {
                        if (aac.Blocked == null) aac.Blocked = $"{PlanLabel(plan)} has no AAC figures yet";
                    } else {
                        watermarks.Add((string)planAac["filed_through_night"]);
                    }
                    if (plan.Aac is Newtonsoft.Json.Linq.JObject || planRows.Any(r => TsRows.AacBlock(r) != null)) {
                        aac.Linked = true;
                    }

                    // One template per name per profile. The push looks each
                    // one up by name and inserts it only when TS has none
                    // (docs/specs/goal-panel-and-ts-order.md section 3,
                    // "Templates"). A camera whose filters carry gear
                    // templates is written by the gear template writer
                    // instead, one template per gear template
                    // (docs/specs/gear-templates.md section 8).
                    if (camera != null) {
                        RegisterGearTemplates(
                            payload, camera, profileId, wheelFilters, wheelKnown, gearWorkByKey);
                    }
                    var owner = RowOwnerLabel(plan);
                    var usable = new List<FilterRow>();
                    var gearOf = new Dictionary<string, TsGearWork>(StringComparer.Ordinal);
                    foreach (var row in pushed) {
                        var filterCfg = FilterConfig(camera, row.Filter);
                        var gearTemplates = filterCfg?.Templates;
                        if (gearTemplates != null && gearTemplates.Count > 0) {
                            var found = ResolveGearTemplate(gearTemplates, row);
                            TsGearWork work;
                            if (found == null ||
                                !gearWorkByKey.TryGetValue(camera.Id + "|" + found.Id, out work)) {
                                payload.Notes.Add($"{owner} {row.Filter}: template '{row.Template}' is not in gear, row skipped");
                                payload.SyncNotes.Add($"{owner} {row.Filter}: template '{row.Template}' is not in gear, row skipped");
                                continue;
                            }
                            if (work.OffWheel) {
                                NoteOffWheelRow(
                                    camera, row.Filter, owner, offWheelOrder, offWheelRows, offWheelOwner);
                                payload.FiltersNotOnWheel.Add(row.Filter);
                                continue;
                            }
                            work.RowLabels.Add($"{owner} {row.Filter}");
                            gearOf[row.Id] = work;
                            usable.Add(row);
                            continue;
                        }

                        // Identity uses the row's own filter. Only the
                        // written filter name follows the wheel's spelling.
                        var filterName = row.Filter;
                        if (wheelKnown) {
                            var slot = TsFilterNames.ResolveToWheel(row.Filter, wheelFilters);
                            if (slot == null) {
                                NoteOffWheelRow(
                                    camera, row.Filter, owner, offWheelOrder, offWheelRows, offWheelOwner);
                                payload.FiltersNotOnWheel.Add(row.Filter);
                                continue;
                            }
                            if (!string.Equals(slot, row.Filter, StringComparison.Ordinal)) {
                                payload.FilterRenames[row.Filter] = slot;
                                filterName = slot;
                            }
                        }
                        usable.Add(row);

                        var templateGuid = TsGuid.RowTemplate(profileId, row.Filter, cameraId, row.Template);
                        TsExposureTemplate seenTemplate;
                        if (templateByName.TryGetValue(row.Template, out seenTemplate)) {
                            seenTemplate.RowLabels.Add($"{owner} {row.Filter}");
                            continue;
                        }
                        var legacyFilterCfg = filterCfg;
                        var legacyTemplate = new TsExposureTemplate {
                            ProfileId = profileId,
                            Name = row.Template,
                            FilterName = filterName,
                            Guid = templateGuid,
                            DefaultExposure = TsRows.SubS(row),
                            Gain = legacyFilterCfg?.Gain ?? -1,
                            Offset = legacyFilterCfg?.Offset ?? -1,
                            Bin = legacyFilterCfg?.Bin ?? 1,
                            SkipIfMissing = wheelGiven && !wheelKnown,
                            CameraName = camera?.Name,
                        };
                        legacyTemplate.RowLabels.Add($"{owner} {row.Filter}");
                        payload.Templates.Add(legacyTemplate);
                        templateByName[row.Template] = legacyTemplate;
                        templateGuidByName[row.Template] = templateGuid;
                    }

                    var basePlans = BaseSnapshotFor(plan, profileId)?["exposure_plans_by_panel_filter"]
                        as Newtonsoft.Json.Linq.JObject;
                    // Rows derived from filter_goals (an ACP before filter
                    // rows) never move an existing plan to another template.
                    var derived = plan.FilterRows == null;
                    var firstOfFilter = new HashSet<string>(StringComparer.Ordinal);
                    var seenFilters = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var row in planRows) {
                        if (seenFilters.Add(row.Filter)) firstOfFilter.Add(row.Id);
                    }

                    var panels = (rows > 1 || cols > 1) && fovW > 0 && fovH > 0
                        ? MosaicPanelCentres(raDeg, decDeg, fovW, fovH, rotDeg, rows, cols, overlapPct)
                        : new List<Panel> { new Panel { Row = 0, Col = 0, RaDeg = raDeg, DecDeg = decDeg } };

                    // FirstNonEmpty skips whitespace, so the Python side's
                    // `tg.get("name") or pl.get("id") or "Untitled"` is what
                    // this has to match: a whitespace-only name is truthy in
                    // Python and reaches the payload as a blank name there.
                    var baseName = PythonFirstTruthyName(target?.Name, plan.Id);
                    if (string.IsNullOrWhiteSpace(baseName)) {
                        throw new TsPushValidationException(
                            $"The plan {PlanLabel(plan)} has a target with no name, so Target " +
                            "Scheduler would have nothing to show and the next push nothing " +
                            "to match. Give the target a name and push again. Nothing was " +
                            "written.");
                    }
                    var multi = panels.Count > 1;
                    if (multi && aac.Blocked == null) {
                        aac.Blocked = $"{PlanLabel(plan)} is a mosaic, and AAC's counts are per row";
                    }

                    foreach (var panel in panels) {
                        var suffix = string.Empty;
                        if (multi) {
                            var panelIndex = panel.Row * cols + panel.Col + 1;
                            suffix = $" Panel {panelIndex} (R{panel.Row + 1}C{panel.Col + 1})";
                        }
                        var targetName = baseName + suffix;
                        var targetGuid = TsGuid.Target(profileId, projectName, targetName);

                        // Two plans in one project with the same target name
                        // used to produce two payload entries under one guid,
                        // which collapsed into a single row while the log
                        // reported both as loaded. Refuse instead, and say
                        // which plans need renaming.
                        string firstPlan;
                        if (planLabelByTargetGuid.TryGetValue(targetGuid, out firstPlan)) {
                            throw new TsPushValidationException(
                                $"The plans {firstPlan} and {PlanLabel(plan)} both produce the " +
                                $"target '{targetName}' in the project '{projectName}', so one " +
                                "would overwrite the other. Rename one of them, or put them in " +
                                "different projects, and push again. Nothing was written.");
                        }
                        planLabelByTargetGuid[targetGuid] = PlanLabel(plan);

                        // ts_refs key panels as "row,col", both from 1.
                        int pinnedTargetId;
                        int? targetPin = refs != null && refs.TargetIdsByPanel.TryGetValue(
                            $"{panel.Row + 1},{panel.Col + 1}", out pinnedTargetId)
                            ? pinnedTargetId
                            : (int?)null;

                        payload.TargetsByProjectGuid[proj.Guid].Add(new TsTarget {
                            ProjectId = 0,
                            Name = targetName,
                            Guid = targetGuid,
                            // Target Scheduler stores right ascension in hours.
                            Ra = panel.RaDeg / 15.0,
                            Dec = panel.DecDeg,
                            Rotation = rotDeg,
                            PinnedId = targetPin,
                        });

                        List<TsExposurePlan> targetPlans;
                        if (!payload.PlansByTargetGuid.TryGetValue(targetGuid, out targetPlans)) {
                            targetPlans = new List<TsExposurePlan>();
                            payload.PlansByTargetGuid[targetGuid] = targetPlans;
                        }

                        var panelKey = $"{panel.Row + 1},{panel.Col + 1}";
                        // The base order is the target's, which is the anchor
                        // panel's (1,1): ACP's order has changed when it differs
                        // from that, for every panel. Each panel's base rows are
                        // its own (section 5).
                        var baseTargets = BaseSnapshotFor(plan, profileId)?["targets_by_panel"] as Newtonsoft.Json.Linq.JObject;
                        var anchorTarget = baseTargets?["1,1"] as Newtonsoft.Json.Linq.JObject;
                        var seenTarget = baseTargets?[panelKey] as Newtonsoft.Json.Linq.JObject;
                        var anchorOrder = anchorTarget?["exposure_order"] as Newtonsoft.Json.Linq.JArray;
                        if (anchorOrder != null) {
                            payload.OrderByTargetGuid[targetGuid] = new TsTargetOrder {
                                BaseOrder = anchorOrder.Select(t => (string)t).ToList(),
                                BaseRows = seenTarget == null ? null : TsOrder.AsRows(seenTarget["override_rows"]),
                                KnownRows = TsOrder.KnownRowsOfBase(
                                    anchorTarget,
                                    BaseSnapshotFor(plan, profileId)?["exposure_plans_by_panel_filter"] as Newtonsoft.Json.Linq.JObject),
                            };
                        }
                        foreach (var row in usable) {
                            var planGuid = TsGuid.RowPlan(profileId, targetGuid, row.Filter, row.Template);
                            var acquired = TsRows.AcquiredFrames(row);
                            var wanted = TsRows.DesiredFor(row, plan);
                            TsGearWork rowWork;
                            gearOf.TryGetValue(row.Id, out rowWork);
                            var rowSub = TsRows.SubS(row);
                            // A row on a gear template inserts its plan on the
                            // template's default (-1) when it follows it, and
                            // with its own sub when it deviates, so TS never
                            // shoots a sub other than the row's.
                            var follows = rowWork != null && SameSub(rowSub, rowWork.Template.SubS);
                            targetPlans.Add(new TsExposurePlan {
                                ProfileId = profileId,
                                TargetId = 0,
                                ExposureTemplateId = 0,
                                Guid = planGuid,
                                Exposure = rowWork == null || follows ? -1.0 : rowSub,
                                Desired = wanted,
                                Acquired = acquired,
                                Accepted = acquired,
                            });
                            var counted = TsRows.AacAccepted(row, plan, wanted);
                            if (!counted.HasValue) {
                                if (aac.Blocked == null) aac.Blocked = $"{TsRows.RowLabel(row)} has no AAC figures yet";
                            } else {
                                aac.AcceptedByPlanGuid[planGuid] = counted.Value;
                            }
                            payload.TemplateGuidByPlanGuid[planGuid] = rowWork != null
                                ? rowWork.Guid
                                : templateGuidByName[row.Template];

                            var lookup = new TsPlanLookup { Label = $"{targetName} {TsRows.RowLabel(row)}", RowId = row.Id };
                            lookup.OldGuids.Add(TsGuid.ExposurePlan(profileId, targetGuid, row.Template));
                            if (firstOfFilter.Contains(row.Id)) {
                                lookup.OldGuids.Add(TsGuid.ExposurePlan(profileId, targetGuid, row.Filter));
                            }
                            // ts_refs key exposure plans as "<target id>_<row id>".
                            int pinnedPlanId;
                            if (targetPin.HasValue && refs.ExposurePlanIds.TryGetValue(
                                    $"{targetPin.Value}_{row.Id}", out pinnedPlanId)) {
                                lookup.PinnedId = pinnedPlanId;
                            }
                            var seen = basePlans?[$"{panelKey}_{row.Id}"] as Newtonsoft.Json.Linq.JObject;
                            var seenFilter = (string)seen?["filter"];
                            var seenTemplate = (string)seen?["template"];
                            if (rowWork == null) {
                                lookup.TemplateChanged = !derived && !string.IsNullOrEmpty(seenTemplate) &&
                                    (!TsRows.SameFilter(seenFilter, row.Filter) || seenTemplate != row.Template);
                            } else {
                                lookup.GearMode = true;
                                lookup.RowSubS = rowSub;
                                lookup.RowFollows = follows;
                                var seenSub = JsonNumberOf(seen?["template_sub_s"]);
                                lookup.RowSubChanged = seenSub.HasValue && !SameSub(seenSub.Value, rowSub);
                                lookup.TemplateChanged = GearRowMoved(seen, rowWork.Template, profileId);
                                var link = rowWork.Template.LinkFor(profileId);
                                if (link?.RepointFrom != null) {
                                    foreach (var fromId in link.RepointFrom) lookup.RepointFrom.Add(fromId);
                                }
                            }
                            payload.PlanLookupByGuid[planGuid] = lookup;
                        }

                        // Rows removed in ACP: their exposure plans are
                        // disabled, never deleted, and only when they carry
                        // ACP's stamp.
                        if (targetPin.HasValue) {
                            // A row with a goal of 0 pushes no plan, so its plan
                            // is disabled as if the row were gone.
                            var rowIds = new HashSet<string>(pushed.Select(r => r.Id), StringComparer.Ordinal);
                            var prefix = $"{targetPin.Value}_";
                            foreach (var pair in refs.ExposurePlanIds) {
                                if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                                var rowId = pair.Key.Substring(prefix.Length);
                                if (rowIds.Contains(rowId)) continue;
                                var seen = basePlans?[$"{panelKey}_{rowId}"] as Newtonsoft.Json.Linq.JObject;
                                var seenFilter = (string)seen?["filter"];
                                var seenTemplate = (string)seen?["template"];
                                if (string.IsNullOrEmpty(seenFilter) || string.IsNullOrEmpty(seenTemplate)) continue;
                                var removed = new TsRemovedPlan { PlanId = pair.Value };
                                removed.Guids.Add(TsGuid.RowPlan(profileId, targetGuid, seenFilter, seenTemplate));
                                removed.Guids.Add(TsGuid.ExposurePlan(profileId, targetGuid, seenTemplate));
                                removed.Guids.Add(TsGuid.ExposurePlan(profileId, targetGuid, seenFilter));
                                List<TsRemovedPlan> list;
                                if (!payload.RemovedByTargetGuid.TryGetValue(targetGuid, out list)) {
                                    list = new List<TsRemovedPlan>();
                                    payload.RemovedByTargetGuid[targetGuid] = list;
                                }
                                list.Add(removed);
                            }
                        }
                    }
                }
                aac.FiledThroughNight = watermarks.Count > 0
                    ? watermarks.OrderBy(w => w, StringComparer.Ordinal).First()
                    : null;
                if (aac.AcceptedByPlanGuid.Count == 0 && aac.Blocked == null) aac.Blocked = "no row has AAC figures yet";
            }

            // The rows a filter that is not on the wheel kept out, said once
            // per camera and filter (docs/specs/gear-templates.md section 5).
            foreach (var key in offWheelOrder) {
                var rowsHere = offWheelRows[key];
                var who = offWheelOwner[key];
                var line = $"{who.Item1}: {who.Item2} is not on the wheel of {profileLabel} yet, " +
                           $"so {rowsHere.Count} {(rowsHere.Count == 1 ? "row was" : "rows were")} skipped: " +
                           $"{string.Join(", ", rowsHere)}.";
                payload.Notes.Add(line);
                payload.SyncNotes.Add(line);
            }

            return payload;
        }

        // -- Gear templates --------------------------------------------------

        /// Add the camera's gear templates to the payload's work list, once
        /// per camera, in gear order. A filter is on the wheel when the
        /// profile's slots hold one for it (TsFilterNames.ResolveToWheel). The
        /// template is registered either way: the gear template writer reports
        /// it, and skips it when the filter is off the wheel.
        private static void RegisterGearTemplates(
            TsSyncPayload payload, Camera camera, string profileId, IReadOnlyList<string> wheelFilters,
            bool wheelKnown, Dictionary<string, TsGearWork> workByKey
        ) {
            if (camera?.Filters == null || string.IsNullOrEmpty(camera.Id)) return;
            foreach (var pair in camera.Filters) {
                var templates = pair.Value?.Templates;
                if (templates == null || templates.Count == 0) continue;
                string slot = null;
                var offWheel = false;
                if (wheelKnown) {
                    slot = TsFilterNames.ResolveToWheel(pair.Key, wheelFilters);
                    offWheel = slot == null;
                }
                foreach (var template in templates) {
                    if (template == null || string.IsNullOrEmpty(template.Id)) continue;
                    var key = camera.Id + "|" + template.Id;
                    if (workByKey.ContainsKey(key)) continue;
                    var work = new TsGearWork {
                        CameraId = camera.Id,
                        CameraName = camera.Name,
                        Filter = pair.Key,
                        Template = template,
                        Slot = slot,
                        OffWheel = offWheel,
                        Guid = TsGuid.GearTemplate(profileId, template.Id),
                    };
                    workByKey[key] = work;
                    payload.GearWorks.Add(work);
                    payload.Templates.Add(new TsExposureTemplate {
                        ProfileId = profileId,
                        Name = template.Name,
                        FilterName = slot ?? string.Empty,
                        Guid = work.Guid,
                        DefaultExposure = template.SubS,
                        Gain = template.Gain,
                        Offset = template.Offset,
                        Bin = template.Bin,
                        Gear = work,
                        CameraName = camera.Name,
                    });
                }
            }
        }

        /// The gear template a row uses: its template_id, else its template
        /// name, else an alias or a ts_names entry. Null when none matches, and
        /// then the row is skipped with a note. There is no fallback to the
        /// starred template.
        public static GearTemplate ResolveGearTemplate(List<GearTemplate> templates, FilterRow row) {
            if (templates == null || row == null) return null;
            if (!string.IsNullOrEmpty(row.TemplateId)) {
                var byId = templates.FirstOrDefault(t => t != null && string.Equals(t.Id, row.TemplateId, StringComparison.Ordinal));
                if (byId != null) return byId;
            }
            var name = row.Template;
            if (string.IsNullOrEmpty(name)) return null;
            return templates.FirstOrDefault(t => t != null && string.Equals(t.Name, name, StringComparison.Ordinal))
                ?? templates.FirstOrDefault(t => t?.Aliases != null && t.Aliases.Contains(name))
                ?? templates.FirstOrDefault(t => t?.TsNames != null && t.TsNames.Contains(name));
        }

        /// Whether ACP moved the row to another template since the last push
        /// (docs/specs/gear-templates.md section 8, "Which template a plan is
        /// on"). The base entry's template_id is compared with the row's. A
        /// base from before gear templates has the template's name only: the
        /// row is unchanged when that name is one the template is known by.
        /// No base entry at all is not a change.
        private static bool GearRowMoved(Newtonsoft.Json.Linq.JObject seen, GearTemplate template, string profileId) {
            if (seen == null) return false;
            var seenId = (string)seen["template_id"];
            if (!string.IsNullOrEmpty(seenId)) {
                return !string.Equals(seenId, template.Id, StringComparison.Ordinal);
            }
            var seenName = (string)seen["template"];
            if (string.IsNullOrEmpty(seenName)) return false;
            if (string.Equals(seenName, template.Name, StringComparison.Ordinal)) return false;
            if (template.Aliases != null && template.Aliases.Contains(seenName)) return false;
            if (template.TsNames != null && template.TsNames.Contains(seenName)) return false;
            var baseName = template.LinkFor(profileId)?.Base?.Name;
            if (baseName != null && string.Equals(seenName, baseName, StringComparison.Ordinal)) return false;
            return true;
        }

        private static void NoteOffWheelRow(
            Camera camera, string filter, string owner, List<string> order,
            Dictionary<string, List<string>> rowsByKey, Dictionary<string, Tuple<string, string>> whoByKey
        ) {
            var cameraName = camera?.Name ?? "The camera";
            var key = (camera?.Id ?? string.Empty) + "|" + filter;
            List<string> list;
            if (!rowsByKey.TryGetValue(key, out list)) {
                list = new List<string>();
                rowsByKey[key] = list;
                whoByKey[key] = Tuple.Create(cameraName, filter);
                order.Add(key);
            }
            list.Add($"{owner} {filter}");
        }

        /// The plan's target name for a note, the plan id when it has none.
        private static string RowOwnerLabel(Plan plan) {
            var name = (plan?.Target?.Name ?? string.Empty).Trim();
            if (name.Length > 0) return name;
            return FirstNonEmpty(plan?.Id, "Untitled");
        }

        private static bool SameSub(double a, double b) {
            return Math.Abs(a - b) < 1e-9;
        }

        private static double? JsonNumberOf(Newtonsoft.Json.Linq.JToken token) {
            if (token == null) return null;
            if (token.Type != Newtonsoft.Json.Linq.JTokenType.Integer
                && token.Type != Newtonsoft.Json.Linq.JTokenType.Float) return null;
            return (double)token;
        }

        // -- Grouping and constraints ---------------------------------------

        /// The grouping rule from ACP's _build_ts_export: an explicit project
        /// name if there is one, otherwise the target name, otherwise the plan
        /// id. Insertion order is preserved so the rows land in a predictable
        /// order, the way Python's dicts do.
        public static List<KeyValuePair<string, List<Plan>>> GroupByProject(IEnumerable<Plan> plans) {
            var order = new List<string>();
            var groups = new Dictionary<string, List<Plan>>(StringComparer.Ordinal);
            foreach (var plan in plans) {
                var name = ProjectNameOf(plan);
                List<Plan> bucket;
                if (!groups.TryGetValue(name, out bucket)) {
                    bucket = new List<Plan>();
                    groups[name] = bucket;
                    order.Add(name);
                }
                bucket.Add(plan);
            }
            return order.Select(n => new KeyValuePair<string, List<Plan>>(n, groups[n])).ToList();
        }

        public static string ProjectNameOf(Plan plan) {
            var explicitName = (plan?.ProjectName ?? string.Empty).Trim();
            if (explicitName.Length > 0) return explicitName;
            var targetName = (plan?.Target?.Name ?? string.Empty).Trim();
            if (targetName.Length > 0) return targetName;
            return FirstNonEmpty(plan?.Id, "Untitled");
        }

        public static void ProjectConstraints(
            List<Plan> group, out double minAltitude, out int meridianWindow, out string priorityName,
            out string stateName, out int minimumTime
        ) {
            minAltitude = group.Max(p => p.MinAltitudeDeg ?? 0.0);

            var nonZero = group.Select(p => p.MeridianWindowMin ?? 0).Where(v => v > 0).ToList();
            meridianWindow = nonZero.Count > 0 ? nonZero.Min() : 0;

            // Python's max() over a key returns the first plan holding the
            // highest rank, so ties go to the earliest plan in the group.
            var best = group[0];
            var bestRank = RankOf(best.Priority);
            foreach (var p in group.Skip(1)) {
                var rank = RankOf(p.Priority);
                if (rank > bestRank) {
                    best = p;
                    bestRank = rank;
                }
            }
            priorityName = best.Priority ?? "normal";

            stateName = GroupState(group);
            // The largest wins, the same rule as minimum altitude. Missing is 0.
            minimumTime = group.Max(p => p.MinimumTimeMin ?? 0);
        }

        /// The most-running state among a project's plans, in the order
        /// active, inactive, draft, closed, per
        /// docs/specs/ts-project-settings.md section 1. A plan with no state
        /// at all reads as active, matching the "absent means active" rule.
        public static string GroupState(List<Plan> group) {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in group) {
                var s = string.IsNullOrWhiteSpace(p.State) ? "active" : p.State;
                present.Add(s);
            }
            foreach (var candidate in StatePrecedence) {
                if (present.Contains(candidate)) return candidate;
            }
            return "active";
        }

        public static int RankOf(string priority) {
            int rank;
            if (priority != null && PriorityRank.TryGetValue(priority, out rank)) return rank;
            return 1;
        }

        /// Which of state, priority and minimumtime this push should actually
        /// write, per the conflict rule in
        /// docs/specs/ts-project-settings.md section 5: write ACP's value only
        /// when ACP changed it since the last sync on this rig. The base is
        /// the first plan in the group that carries one, read from
        /// ts_links[profileId].base_snapshot.project. A group with no base at
        /// all writes none of the three on update (the values still land on
        /// insert, since an insert writes every column regardless of this
        /// set). A base missing a field it predates reads the default from
        /// section 3: state absent is active, minimumtime absent is 0.
        ///
        /// The filter switch frequency and smart exposure order follow the
        /// same rule (docs/specs/goal-panel-and-ts-order.md section 4), except
        /// that a group with no value for one (null) never writes it. A base
        /// from before they were recorded reads as 0 and off.
        private static HashSet<string> ConditionalColumnsFor(
            Newtonsoft.Json.Linq.JObject baseProject, int tsState, int tsPriority, int minimumTime,
            int? switchFrequency = null, int? smartOrder = null
        ) {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (baseProject == null) return result;

            var baseState = (int?)baseProject["state"] ?? StateToTsInt["active"];
            var basePriority = (int?)baseProject["priority"] ?? RankOf("normal");
            var baseMinimumTime = (int?)baseProject["minimumtime"] ?? 0;

            if (baseState != tsState) {
                result.Add("state");
                if (tsState == StateToTsInt["active"]) result.Add("activedate");
                else if (tsState == StateToTsInt["inactive"] || tsState == StateToTsInt["closed"]) result.Add("inactivedate");
            }
            if (basePriority != tsPriority) result.Add("priority");
            if (baseMinimumTime != minimumTime) result.Add("minimumtime");
            if (switchFrequency.HasValue &&
                ((int?)baseProject["filterswitchfrequency"] ?? 0) != switchFrequency.Value) {
                result.Add("filterswitchfrequency");
            }
            if (smartOrder.HasValue &&
                ((int?)baseProject["smartexposureorder"] ?? 0) != smartOrder.Value) {
                result.Add("smartexposureorder");
            }
            return result;
        }

        /// The switch frequency and smart order for a project group, as Target
        /// Scheduler integers. Both belong to the TS project; when plans in
        /// one project disagree, the first plan in the list that has a value
        /// wins and a line goes into the push result
        /// (docs/specs/goal-panel-and-ts-order.md section 4). Null means no
        /// plan has one. Mirrors _group_order_settings in convert.py.
        public static void GroupOrderSettings(
            string projectName, List<Plan> group, out int? switchFrequency, out int? smartOrder,
            List<string> notes
        ) {
            var freqs = group
                .Where(p => p.FilterSwitchFrequency.HasValue
                    && p.FilterSwitchFrequency.Value >= 0 && p.FilterSwitchFrequency.Value <= 99)
                .Select(p => Tuple.Create(p.Id, p.FilterSwitchFrequency.Value))
                .ToList();
            var smarts = group
                .Where(p => p.SmartExposureOrder.HasValue)
                .Select(p => Tuple.Create(p.Id, p.SmartExposureOrder.Value ? 1 : 0))
                .ToList();
            switchFrequency = freqs.Count > 0 ? freqs[0].Item2 : (int?)null;
            smartOrder = smarts.Count > 0 ? smarts[0].Item2 : (int?)null;
            if (notes == null) return;
            if (freqs.Select(f => f.Item2).Distinct().Count() > 1) {
                notes.Add($"{projectName}: plans disagree on the filter switch frequency, so the push used " +
                    $"{freqs[0].Item2} from plan {freqs[0].Item1}, the first in the list.");
            }
            if (smarts.Select(f => f.Item2).Distinct().Count() > 1) {
                notes.Add($"{projectName}: plans disagree on the smart order, so the push used " +
                    $"{(smarts[0].Item2 == 1 ? "on" : "off")} from plan {smarts[0].Item1}, the first in the list.");
            }
        }

        /// The base snapshot's project block for this plan and rig, or null
        /// when there is none. Only ts_links carries a base snapshot; unlike
        /// RefsFor, there is no ts_refs fallback, because a plan synced before
        /// ts_links existed never had a base recorded either.
        public static Newtonsoft.Json.Linq.JObject BaseProjectFor(Plan plan, string profileId) {
            var links = plan?.TsLinks as Newtonsoft.Json.Linq.JObject;
            if (links == null || string.IsNullOrWhiteSpace(profileId)) return null;
            foreach (var pair in links) {
                if (!string.Equals(pair.Key, profileId, StringComparison.OrdinalIgnoreCase)) continue;
                var baseSnapshot = (pair.Value as Newtonsoft.Json.Linq.JObject)?["base_snapshot"]
                    as Newtonsoft.Json.Linq.JObject;
                return baseSnapshot?["project"] as Newtonsoft.Json.Linq.JObject;
            }
            return null;
        }

        /// The base snapshot this rig recorded for the plan, from
        /// ts_links[profileId], or null.
        public static Newtonsoft.Json.Linq.JObject BaseSnapshotFor(Plan plan, string profileId) {
            var links = plan?.TsLinks as Newtonsoft.Json.Linq.JObject;
            if (links == null || string.IsNullOrWhiteSpace(profileId)) return null;
            foreach (var pair in links) {
                if (!string.Equals(pair.Key, profileId, StringComparison.OrdinalIgnoreCase)) continue;
                return (pair.Value as Newtonsoft.Json.Linq.JObject)?["base_snapshot"]
                    as Newtonsoft.Json.Linq.JObject;
            }
            return null;
        }

        // -- Geometry --------------------------------------------------------

        /// On-sky field of view in arcminutes, mirroring ACP's _fov_arcmin.
        /// Zero for either axis means "single panel only", which is what a
        /// missing telescope or camera comes to.
        public static void FovArcmin(Telescope telescope, Camera camera, out double width, out double height) {
            width = 0.0;
            height = 0.0;
            if (telescope == null || camera == null) return;

            var focalLength = telescope.FocalLengthMm ?? 0.0;
            var pixelSize = camera.PixelSizeUm ?? 0.0;
            int sensorW, sensorH;
            if (!camera.TryGetSensorPx(out sensorW, out sensorH)) return;
            if (focalLength <= 0.0 || pixelSize <= 0.0) return;

            var arcsecPerPx = 206.265 * pixelSize / focalLength;
            width = Math.Round(sensorW * arcsecPerPx / 60.0, 2, MidpointRounding.ToEven);
            height = Math.Round(sensorH * arcsecPerPx / 60.0, 2, MidpointRounding.ToEven);
        }

        public class Panel {
            public int Row { get; set; }
            public int Col { get; set; }
            public double RaDeg { get; set; }
            public double DecDeg { get; set; }
        }

        /// Panel centres for a rows by cols mosaic, the same formula as ACP's
        /// _mosaic_panel_centers. It has to stay in step, because the panel
        /// suffix in the target name feeds the target guid, which is the dedup
        /// key: a panel that moves by a rounding error becomes a new row.
        public static List<Panel> MosaicPanelCentres(
            double raCentre, double decCentre,
            double fovWidthArcmin, double fovHeightArcmin,
            double rotationDeg, int rows, int cols, double overlapPct
        ) {
            rows = Math.Max(1, rows);
            cols = Math.Max(1, cols);
            var overlap = Math.Max(0.0, Math.Min(0.99, overlapPct / 100.0));
            var strideW = (fovWidthArcmin / 60.0) * (1.0 - overlap);
            var strideH = (fovHeightArcmin / 60.0) * (1.0 - overlap);
            var r = rotationDeg * Math.PI / 180.0;
            var cosR = Math.Cos(r);
            var sinR = Math.Sin(r);
            var cosD = Math.Max(1e-6, Math.Cos(decCentre * Math.PI / 180.0));

            var panels = new List<Panel>();
            for (var i = 0; i < rows; i++) {
                for (var j = 0; j < cols; j++) {
                    var cx = (j - (cols - 1) / 2.0) * strideW;
                    var cy = ((rows - 1) / 2.0 - i) * strideH;
                    var de = cx * cosR + cy * sinR;
                    var dn = -cx * sinR + cy * cosR;
                    panels.Add(new Panel {
                        Row = i,
                        Col = j,
                        RaDeg = raCentre + de / cosD,
                        DecDeg = decCentre + dn,
                    });
                }
            }
            return panels;
        }

        // -- Small helpers ---------------------------------------------------

        /// The plan's refs for this profile, or null when it has none. Row Ids
        /// are only unique within one database, and a profile's refs say
        /// nothing about another's rows.
        ///
        /// The per-profile link in ts_links wins. ts_refs is the fallback,
        /// used only when its profile_id is this profile, which is what a plan
        /// last synced before ts_links existed still carries. A link that
        /// names no project also falls back: until 2026-10-09 a push wrote
        /// exactly that for projects made in Target Scheduler, and letting it
        /// win hid good ts_refs and duplicated 13 projects.
        public static TsPlanRefs RefsFor(Plan plan, string profileId) {
            var links = plan?.TsLinks as Newtonsoft.Json.Linq.JObject;
            if (links != null && !string.IsNullOrWhiteSpace(profileId)) {
                foreach (var pair in links) {
                    if (!string.Equals(pair.Key, profileId, StringComparison.OrdinalIgnoreCase)) continue;
                    var linkRefs = (pair.Value as Newtonsoft.Json.Linq.JObject)?["refs"]
                        as Newtonsoft.Json.Linq.JObject;
                    if (linkRefs == null) continue;
                    var fromLink = TsPlanRefs.FromJson(plan.Id, profileId, linkRefs);
                    if (fromLink.ProjectId.HasValue) return fromLink;
                }
            }

            var block = plan?.TsRefs as Newtonsoft.Json.Linq.JObject;
            if (block == null) return null;
            var refsProfile = (string)(block["profile_id"] as Newtonsoft.Json.Linq.JValue);
            if (!string.Equals(refsProfile, profileId, StringComparison.OrdinalIgnoreCase)) return null;
            return TsPlanRefs.FromJson(plan.Id, profileId, block);
        }

        private static CameraFilter FilterConfig(Camera camera, string filterName) {
            if (camera?.Filters == null) return null;
            CameraFilter cfg;
            return camera.Filters.TryGetValue(filterName, out cfg) ? cfg : null;
        }

        /// Python's `tg.get("name") or pl.get("id") or "Untitled"`. Only an
        /// empty string is falsy there, so a whitespace-only target name is
        /// kept rather than skipped, and the blank-name refusal is what catches
        /// it. FirstNonEmpty skips whitespace, which would have made the two
        /// implementations name the same target differently.
        private static string PythonFirstTruthyName(string targetName, string planId) {
            if (!string.IsNullOrEmpty(targetName)) return targetName;
            if (!string.IsNullOrEmpty(planId)) return planId;
            return "Untitled";
        }

        /// How to name one ACP plan in a message the user has to act on. The
        /// plan id first, because the messages that use this are about two
        /// plans that share a target name, where naming them by target name
        /// would say nothing. Matches _plan_label in nina_ts_sync/convert.py.
        private static string PlanLabel(Plan plan) {
            var planId = (plan?.Id ?? string.Empty).Trim();
            if (planId.Length > 0) return $"'{planId}'";
            var targetName = (plan?.Target?.Name ?? string.Empty).Trim();
            return targetName.Length > 0 ? $"'{targetName}'" : "an unnamed plan";
        }

        private static string FirstNonEmpty(params string[] candidates) {
            foreach (var c in candidates) {
                if (!string.IsNullOrWhiteSpace(c)) return c;
            }
            return string.Empty;
        }
    }
}
