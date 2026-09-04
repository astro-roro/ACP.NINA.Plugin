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

        /// Seeded on a freshly inserted project only. Names have to match
        /// Target Scheduler's ScoringRule.GetAllScoringRules(); when in doubt
        /// seed nothing and let the plugin populate on next start.
        public static readonly IReadOnlyList<KeyValuePair<string, double>> DefaultRuleWeights =
            new List<KeyValuePair<string, double>> {
                new KeyValuePair<string, double>("PercentComplete", 50.0),
                new KeyValuePair<string, double>("MeridianWindow", 50.0),
                new KeyValuePair<string, double>("MosaicCompletion", 50.0),
                new KeyValuePair<string, double>("SettingSoonest", 50.0),
                new KeyValuePair<string, double>("SmartExposure", 50.0),
            };

        /// Build the payload for a run.
        ///
        /// `createDateUnix` is passed in rather than read from the clock so a
        /// test can compare its rows to the ones the Python extension writes
        /// under a frozen clock.
        public static TsSyncPayload BuildPayload(
            IEnumerable<Plan> plans,
            GearResponse gear,
            string profileId,
            long createDateUnix
        ) {
            var payload = new TsSyncPayload { ProfileId = profileId };
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

            var seenTemplateGuids = new HashSet<string>(StringComparer.Ordinal);

            foreach (var group in GroupByProject(planList)) {
                var projectName = group.Key;
                double minAltitude;
                int meridianWindow;
                string priorityName;
                ProjectConstraints(group.Value, out minAltitude, out meridianWindow, out priorityName);

                var proj = new TsProject {
                    ProfileId = profileId,
                    Name = projectName,
                    Guid = TsGuid.Project(profileId, projectName),
                    State = 1,
                    Priority = RankOf(priorityName),
                    CreateDate = createDateUnix,
                    MinimumAltitude = minAltitude,
                    MeridianWindow = meridianWindow,
                };
                payload.Projects.Add(proj);
                payload.RuleWeightsByProjectGuid[proj.Guid] = DefaultRuleWeights
                    .Select(kv => new TsRuleWeight { ProjectId = 0, Name = kv.Key, Weight = kv.Value })
                    .ToList();
                payload.TargetsByProjectGuid[proj.Guid] = new List<TsTarget>();

                foreach (var plan in group.Value) {
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

                    var goals = plan.FilterGoals ?? new Dictionary<string, FilterGoal>();
                    var cameraId = camera?.Id ?? string.Empty;

                    // Templates are built once per filter and camera per
                    // profile. This is exactly the dedup Target Scheduler's own
                    // Import Profile does not do.
                    foreach (var goal in goals) {
                        if ((goal.Value?.TargetHours ?? 0.0) <= 0.0) continue;
                        var templateGuid = TsGuid.Template(profileId, goal.Key, cameraId);
                        if (!seenTemplateGuids.Add(templateGuid)) continue;

                        var filterCfg = FilterConfig(camera, goal.Key);
                        var templateName = !string.IsNullOrWhiteSpace(filterCfg?.TsTemplateName)
                            ? filterCfg.TsTemplateName
                            : (camera != null ? $"{goal.Key} ({camera.Name})" : goal.Key);

                        payload.Templates.Add(new TsExposureTemplate {
                            ProfileId = profileId,
                            Name = templateName,
                            FilterName = goal.Key,
                            Guid = templateGuid,
                            DefaultExposure = FirstTruthy(
                                filterCfg?.DefaultSubS, goal.Value?.SubExposureS, 300.0),
                            Gain = filterCfg?.Gain ?? -1,
                            Offset = filterCfg?.Offset ?? -1,
                            Bin = filterCfg?.Bin ?? 1,
                        });
                    }

                    var panels = (rows > 1 || cols > 1) && fovW > 0 && fovH > 0
                        ? MosaicPanelCentres(raDeg, decDeg, fovW, fovH, rotDeg, rows, cols, overlapPct)
                        : new List<Panel> { new Panel { Row = 0, Col = 0, RaDeg = raDeg, DecDeg = decDeg } };

                    var baseName = FirstNonEmpty(target?.Name, plan.Id, "Untitled");
                    var multi = panels.Count > 1;

                    foreach (var panel in panels) {
                        var suffix = string.Empty;
                        if (multi) {
                            var panelIndex = panel.Row * cols + panel.Col + 1;
                            suffix = $" Panel {panelIndex} (R{panel.Row + 1}C{panel.Col + 1})";
                        }
                        var targetName = baseName + suffix;
                        var targetGuid = TsGuid.Target(profileId, projectName, targetName);

                        payload.TargetsByProjectGuid[proj.Guid].Add(new TsTarget {
                            ProjectId = 0,
                            Name = targetName,
                            Guid = targetGuid,
                            // Target Scheduler stores right ascension in hours.
                            Ra = panel.RaDeg / 15.0,
                            Dec = panel.DecDeg,
                            Rotation = rotDeg,
                        });

                        List<TsExposurePlan> planRows;
                        if (!payload.PlansByTargetGuid.TryGetValue(targetGuid, out planRows)) {
                            planRows = new List<TsExposurePlan>();
                            payload.PlansByTargetGuid[targetGuid] = planRows;
                        }

                        foreach (var goal in goals) {
                            var targetHours = goal.Value?.TargetHours ?? 0.0;
                            if (targetHours <= 0.0) continue;

                            // The Python side reads this as `int(sub or 300)`,
                            // so a missing or zero sub length falls back to 300
                            // and a fractional one truncates towards zero
                            // before the divisor is floored at 1.
                            var rawSub = goal.Value?.SubExposureS ?? 0.0;
                            var effectiveSub = rawSub != 0.0 ? rawSub : 300.0;
                            var subSeconds = (int)Math.Truncate(effectiveSub);
                            var divisor = Math.Max(1, subSeconds);

                            var desired = Math.Max(1, (int)Math.Ceiling(targetHours * 3600.0 / divisor));
                            var acquired = (int)Math.Round(
                                (goal.Value?.ActualHours ?? 0.0) * 3600.0 / divisor,
                                MidpointRounding.ToEven);

                            var planGuid = TsGuid.ExposurePlan(profileId, targetGuid, goal.Key);
                            planRows.Add(new TsExposurePlan {
                                ProfileId = profileId,
                                TargetId = 0,
                                ExposureTemplateId = 0,
                                Guid = planGuid,
                                Exposure = subSeconds,
                                Desired = desired,
                                Acquired = acquired,
                                Accepted = acquired,
                            });
                            payload.TemplateGuidByPlanGuid[planGuid] =
                                TsGuid.Template(profileId, goal.Key, cameraId);
                        }
                    }
                }
            }

            return payload;
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
            List<Plan> group, out double minAltitude, out int meridianWindow, out string priorityName
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
        }

        public static int RankOf(string priority) {
            int rank;
            if (priority != null && PriorityRank.TryGetValue(priority, out rank)) return rank;
            return 1;
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

        private static CameraFilter FilterConfig(Camera camera, string filterName) {
            if (camera?.Filters == null) return null;
            CameraFilter cfg;
            return camera.Filters.TryGetValue(filterName, out cfg) ? cfg : null;
        }

        /// Python's `a or b or c`, where zero and null are both falsy. Used for
        /// the template's default exposure, which falls back from the camera's
        /// filter setting to the plan's own sub length to 300 seconds.
        private static double FirstTruthy(double? a, double? b, double fallback) {
            if (a.HasValue && a.Value != 0.0) return a.Value;
            if (b.HasValue && b.Value != 0.0) return b.Value;
            return fallback;
        }

        private static string FirstNonEmpty(params string[] candidates) {
            foreach (var c in candidates) {
                if (!string.IsNullOrWhiteSpace(c)) return c;
            }
            return string.Empty;
        }
    }
}
