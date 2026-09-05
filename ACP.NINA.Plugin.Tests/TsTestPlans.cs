using ACP.NINA.Plugin.Models;
using System.Collections.Generic;

namespace ACP.NINA.Plugin.Tests {

    /// The three plans and the gear the push tests use.
    ///
    /// A port of tests/plans.py from acp-nina-ts-sync, which in turn copied
    /// ACP's own `_plan` and `_save_gear` helpers. Keeping the same three plans
    /// is what lets the rows this plugin writes be compared with the rows the
    /// Python extension writes for the same input.
    public static class TsTestPlans {

        public const string ProfileId = "profile-under-test";

        /// The clock the golden rows were captured under. `createdate` is the
        /// only column that would otherwise move between runs.
        public const long FrozenNow = 1700000000L;

        public static GearResponse Gear() {
            return new GearResponse {
                Version = 2,
                Telescopes = new List<Telescope> {
                    new Telescope {
                        Id = "tel-1", Name = "Test 600mm",
                        FocalLengthMm = 600.0, ApertureMm = 130,
                    },
                },
                Cameras = new List<Camera> {
                    new Camera {
                        Id = "cam-1", Name = "Test IMX571",
                        PixelSizeUm = 3.76,
                        SensorPx = new[] { 6248, 4176 },
                        Filters = new Dictionary<string, CameraFilter> {
                            { "OIII", new CameraFilter {
                                TsTemplateName = "OIII 3nm", DefaultSubS = 300,
                                Gain = 100, Offset = 50, Bin = 1 } },
                            { "Ha", new CameraFilter {
                                TsTemplateName = "Ha 3nm", DefaultSubS = 600,
                                Gain = 100, Offset = 50, Bin = 1 } },
                        },
                    },
                },
            };
        }

        /// ACP's `_plan` helper, with the same defaults.
        public static Plan Plan(
            string id,
            string projectName = null,
            string targetName = null,
            double ra = 100.0,
            double dec = -30.0,
            double rot = 0.0,
            int rows = 1,
            int cols = 1,
            double overlapPct = 15,
            string telescopeId = "tel-1",
            string cameraId = "cam-1",
            Dictionary<string, FilterGoal> filterGoals = null,
            string priority = "normal",
            double minAltitudeDeg = 30,
            int? meridianWindowMin = null
        ) {
            return new Plan {
                Id = id,
                Guid = "guid-" + id,
                ProjectName = projectName ?? ("Project " + id),
                State = "active",
                Priority = priority,
                MinAltitudeDeg = minAltitudeDeg,
                MeridianWindowMin = meridianWindowMin,
                TelescopeId = telescopeId,
                CameraId = cameraId,
                Target = new PlanTarget {
                    Name = targetName ?? ("T" + id),
                    CenterRaDeg = ra,
                    CenterDecDeg = dec,
                    RotationDeg = rot,
                    Mosaic = new Mosaic { Rows = rows, Cols = cols, OverlapPct = overlapPct },
                },
                FilterGoals = filterGoals ?? new Dictionary<string, FilterGoal> {
                    { "OIII", new FilterGoal { TargetHours = 1.0, SubExposureS = 300 } },
                },
            };
        }

        /// One single target, one 2 by 2 mosaic, one with two filters.
        ///
        /// The filter order on the two filter plan is Ha then OIII, which is
        /// the order the Python fixture declares. It decides nothing about
        /// correctness, because every row is found by its guid, but keeping it
        /// means the row Ids line up with the golden capture too.
        public static List<Plan> ThreePlans() {
            return new List<Plan> {
                Plan("single", projectName: "Single Target", targetName: "NGC 253"),
                Plan("mosaic", projectName: "Mosaic", targetName: "M31",
                     rows: 2, cols: 2, overlapPct: 15, ra: 10.68, dec: 41.27),
                Plan("twofilter", projectName: "Two Filters", targetName: "NGC 7000",
                     ra: 314.7, dec: 44.5,
                     filterGoals: new Dictionary<string, FilterGoal> {
                         { "Ha", new FilterGoal { TargetHours = 4.0, SubExposureS = 600 } },
                         { "OIII", new FilterGoal { TargetHours = 2.0, SubExposureS = 300 } },
                     }),
            };
        }

        /// Several plans under one project name, each with its own constraints,
        /// for the strictest wins tests.
        public static List<Plan> SharedProject(params Plan[] plans) {
            return new List<Plan>(plans);
        }
    }
}
