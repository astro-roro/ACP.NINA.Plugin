using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ACP.NINA.Plugin.Tests {

    /// The filter row scenarios, step for step as tests/row_scenarios.py in
    /// the Python extension runs them for golden-rows.json
    /// (docs/specs/goal-panel-and-ts-order.md tests S1, S2, M2 to M4, R1, R3,
    /// and the imported plan that keeps its own template). Each starts from an
    /// empty database at user_version 28 and returns the last push's outcome.
    /// Every step is a push, a link, or a plain SQL edit, so the two sides
    /// start each push from the same database.
    public static class TsRowScenarios {

        private const string Profile = TsTestPlans.ProfileId;
        private const string M42 = "M42";

        public static readonly string[] Names = {
            "s1_new_star_row",
            "s2_existing_template",
            "m2_old_writer_rows_restamped",
            "m3_explicit_exposure_kept",
            "m4_template_change_writes_default",
            "r1_removed_row_disabled",
            "r3_readded_row_enabled",
            "imported_plan_keeps_its_template",
        };

        public static TsSyncOutcome Run(string name, TargetSchedulerDb db) {
            switch (name) {
                case "s1_new_star_row": return S1(db);
                case "s2_existing_template": return S2(db);
                case "m2_old_writer_rows_restamped": return M2(db);
                case "m3_explicit_exposure_kept": return M3(db);
                case "m4_template_change_writes_default": return M4(db);
                case "r1_removed_row_disabled": return R1(db);
                case "r3_readded_row_enabled": return R3(db);
                case "imported_plan_keeps_its_template": return ImportedPlan(db);
                default: throw new ArgumentOutOfRangeException(nameof(name), name, null);
            }
        }

        /// The push tests' camera with every filter the scenarios use, as
        /// ROW_GEAR in tests/row_scenarios.py.
        public static GearResponse Gear() {
            var gear = TsTestPlans.Gear();
            var filters = gear.Cameras[0].Filters;
            filters["L"] = new CameraFilter { DefaultSubS = 120, Gain = 100, Offset = 50, Bin = 1 };
            filters["R"] = new CameraFilter { TsTemplateName = "R", DefaultSubS = 180, Gain = 100, Offset = 50, Bin = 1 };
            filters["G"] = new CameraFilter { DefaultSubS = 180, Gain = 100, Offset = 50, Bin = 1 };
            filters["B"] = new CameraFilter { DefaultSubS = 180, Gain = 100, Offset = 50, Bin = 1 };
            filters["SII"] = new CameraFilter { TsTemplateName = "SII 3nm", DefaultSubS = 300, Gain = 100, Offset = 50, Bin = 1 };
            return gear;
        }

        public static Plan M42Plan(params FilterRow[] rows) {
            return TsTestPlans.Plan("m42", projectName: M42, targetName: M42, filterRows: rows.ToList());
        }

        public static TsSyncOutcome Push(TargetSchedulerDb db, params Plan[] plans) {
            return TsUpsert.Apply(db, TsConvert.BuildPayload(
                plans.ToList(), Gear(), Profile, TsTestPlans.FrozenNow));
        }

        /// What a push leaves on each plan: its link for this profile, and each
        /// row's template_sub_s from the base, as ACP's links route sets it.
        public static void Link(TargetSchedulerDb db, params Plan[] plans) {
            var snap = db.ReadAll(Profile);
            var camera = Gear().Cameras[0];
            foreach (var plan in plans) {
                var refs = TsState.BuildRefs(snap, plan, Profile, 28, TsState.OperationPush, camera: camera);
                var baseSnapshot = TsState.BuildBaseSnapshot(snap, plan, Profile, camera: camera);
                plan.TsLinks = new JObject {
                    { Profile, new JObject {
                        { "machine", JValue.CreateNull() },
                        { "refs", refs },
                        { "base_snapshot", baseSnapshot },
                    } },
                };
                var entries = baseSnapshot["exposure_plans_by_panel_filter"] as JObject;
                foreach (var row in plan.FilterRows ?? new List<FilterRow>()) {
                    var entry = entries?[$"1,1_{row.Id}"] as JObject;
                    var sub = (double?)entry?["template_sub_s"];
                    if (sub.HasValue && sub.Value > 0) row.TemplateSubS = sub.Value;
                }
            }
        }

        public static int M42TargetId(SqliteConnection conn) {
            return Scalar(conn, $"SELECT Id FROM target WHERE name = '{M42}'");
        }

        public static string M42TargetGuid() {
            return TsGuid.Target(Profile, M42, M42);
        }

        public static int AddTemplate(SqliteConnection conn, string name, string filterName, double dflt, string guid) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText =
                    "INSERT INTO exposuretemplate (profileId, name, filtername, gain, offset, bin, readoutmode, " +
                    "twilightlevel, moonavoidanceenabled, moonavoidanceseparation, moonavoidancewidth, " +
                    "maximumhumidity, defaultexposure, guid) " +
                    "VALUES ($p, $n, $f, 100, 50, 1, -1, 1, 0, 0.0, 0, 100.0, $d, $g)";
                cmd.Parameters.AddWithValue("$p", Profile);
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$f", filterName);
                cmd.Parameters.AddWithValue("$d", dflt);
                cmd.Parameters.AddWithValue("$g", guid);
                cmd.ExecuteNonQuery();
            }
            return Scalar(conn, "SELECT last_insert_rowid()");
        }

        /// An exposure plan the writer before filter rows made, stamped with
        /// the goal-key recipe.
        public static int AddOldPlan(SqliteConnection conn, int targetId, int templateId, string key,
                                     double exposure, int desired, int acquired, int accepted) {
            return InsertPlan(conn, targetId, templateId, exposure, desired, acquired, accepted,
                TsGuid.ExposurePlan(Profile, M42TargetGuid(), key));
        }

        private static int InsertPlan(SqliteConnection conn, int targetId, int templateId, double exposure,
                                      int desired, int acquired, int accepted, string guid) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText =
                    "INSERT INTO exposureplan (profileId, exposure, desired, acquired, accepted, targetid, " +
                    "exposureTemplateId, enabled, guid) VALUES ($p, $e, $d, $a, $c, $t, $x, 1, $g)";
                cmd.Parameters.AddWithValue("$p", Profile);
                cmd.Parameters.AddWithValue("$e", exposure);
                cmd.Parameters.AddWithValue("$d", desired);
                cmd.Parameters.AddWithValue("$a", acquired);
                cmd.Parameters.AddWithValue("$c", accepted);
                cmd.Parameters.AddWithValue("$t", targetId);
                cmd.Parameters.AddWithValue("$x", templateId);
                cmd.Parameters.AddWithValue("$g", guid);
                cmd.ExecuteNonQuery();
            }
            return Scalar(conn, "SELECT last_insert_rowid()");
        }

        /// Override rows as (action, referenceIdx), numbered from 1.
        public static void SetOverride(SqliteConnection conn, int targetId, IEnumerable<Tuple<int, int>> items) {
            Exec(conn, $"DELETE FROM overrideexposureorderitem WHERE targetid = {targetId}");
            var n = 1;
            foreach (var item in items) {
                Exec(conn,
                    "INSERT INTO overrideexposureorderitem (targetid, \"order\", \"action\", referenceIdx) " +
                    $"VALUES ({targetId}, {n++}, {item.Item1}, {item.Item2})");
            }
        }

        public static void AddCadence(SqliteConnection conn, int targetId, int count) {
            for (var i = 1; i <= count; i++) {
                Exec(conn,
                    "INSERT INTO filtercadenceitem (targetid, \"order\", \"next\", \"action\", referenceIdx) " +
                    $"VALUES ({targetId}, {i}, {(i == 1 ? 1 : 0)}, 0, {i % 2})");
            }
        }

        // -- The scenarios --------------------------------------------------

        public static TsSyncOutcome S1(TargetSchedulerDb db) {
            return Push(db, M42Plan(TsTestPlans.Row("row-rs", "R", "R Stars", 15, frames: 50)));
        }

        public static TsSyncOutcome S2(TargetSchedulerDb db) {
            AddTemplate(db.Connection, "R Stars", "R", 10, "nina-guid-r-stars");
            return S1(db);
        }

        public static Dictionary<string, int> M2OldWriterState(TargetSchedulerDb db) {
            Push(db, M42Plan());
            var conn = db.Connection;
            var tid = M42TargetId(conn);
            var tpl = new Dictionary<string, int> {
                { "L", AddTemplate(conn, "L (Test IMX571)", "L", 120, "old-tpl-l") },
                { "R", AddTemplate(conn, "R", "R", 180, "old-tpl-r") },
                { "R Stars", AddTemplate(conn, "R Stars (Test IMX571)", "R Stars", 15, "old-tpl-r-stars") },
                { "Ha", AddTemplate(conn, "Ha 3nm", "Ha", 300, "old-tpl-ha") },
                { "mine", AddTemplate(conn, "R Stars", "R", 10, "nina-guid-r-stars") },
            };
            AddOldPlan(conn, tid, tpl["L"], "L", 120, 60, 11, 10);
            AddOldPlan(conn, tid, tpl["R"], "R", 180, 60, 12, 11);
            AddOldPlan(conn, tid, tpl["R Stars"], "R Stars", 15, 20, 18, 18);
            AddOldPlan(conn, tid, tpl["Ha"], "Ha", 300, 48, 13, 9);
            return tpl;
        }

        public static FilterRow[] M1Rows() {
            return new[] {
                TsTestPlans.Row("row-l", "L", "L (Test IMX571)", 120, hours: 2.0),
                TsTestPlans.Row("row-r", "R", "R", 180, hours: 3.0),
                TsTestPlans.Row("row-rs", "R", "R Stars", 15, frames: 20),
                TsTestPlans.Row("row-ha", "Ha", "Ha 3nm", 300, hours: 4.0),
            };
        }

        public static TsSyncOutcome M2(TargetSchedulerDb db) {
            M2OldWriterState(db);
            return Push(db, M42Plan(M1Rows()));
        }

        private static Plan M3State(TargetSchedulerDb db) {
            Push(db, M42Plan());
            var conn = db.Connection;
            var tid = M42TargetId(conn);
            var tpl = AddTemplate(conn, "R", "R", 180, "old-tpl-r");
            AddOldPlan(conn, tid, tpl, "R", 300, 12, 4, 4);
            return M42Plan(TsTestPlans.Row("row-r", "R", "R", 300, hours: 1.0));
        }

        public static TsSyncOutcome M3(TargetSchedulerDb db) {
            return Push(db, M3State(db));
        }

        public static TsSyncOutcome M4(TargetSchedulerDb db) {
            var plan = M3State(db);
            Push(db, plan);
            Link(db, plan);
            AddTemplate(db.Connection, "R 300", "R", 300, "nina-guid-r-300");
            plan.FilterRows[0].Template = "R 300";
            return Push(db, plan);
        }

        private static Plan R1State(TargetSchedulerDb db) {
            var ha = TsTestPlans.Row("row-ha", "Ha", "Ha", 300, frames: 4);
            var oiii = TsTestPlans.Row("row-oiii", "OIII", "OIII", 300, frames: 4);
            var sii = TsTestPlans.Row("row-sii", "SII", "SII", 300, frames: 4);
            var plan = M42Plan(ha, oiii, sii);
            Push(db, plan);
            Link(db, plan);
            var conn = db.Connection;
            var tid = M42TargetId(conn);
            Exec(conn, "UPDATE exposureplan SET acquired = 3, accepted = 2 WHERE Id = 1");
            SetOverride(conn, tid,
                Enumerable.Repeat(Tuple.Create(0, 2), 4)
                    .Concat(Enumerable.Repeat(Tuple.Create(0, 0), 4))
                    .Concat(Enumerable.Repeat(Tuple.Create(0, 1), 4)));
            AddCadence(conn, tid, 3);
            plan.FilterRows = new List<FilterRow> { sii, ha, oiii };
            return plan;
        }

        public static TsSyncOutcome R1(TargetSchedulerDb db) {
            var plan = R1State(db);
            plan.FilterRows = plan.FilterRows.Where(r => r.Id != "row-ha").ToList();
            return Push(db, plan);
        }

        public static TsSyncOutcome R3(TargetSchedulerDb db) {
            var plan = R1State(db);
            plan.FilterRows = plan.FilterRows.Where(r => r.Id != "row-ha").ToList();
            Push(db, plan);
            Link(db, plan);
            plan.FilterRows.Insert(1, TsTestPlans.Row("row-ha-again", "Ha", "Ha", 300, frames: 4));
            return Push(db, plan);
        }

        public static TsSyncOutcome ImportedPlan(TargetSchedulerDb db) {
            Push(db, M42Plan());
            var conn = db.Connection;
            var tid = M42TargetId(conn);
            var mine = AddTemplate(conn, "Ha", "Ha", 600, "nina-guid-ha");
            var ep = InsertPlan(conn, tid, mine, -1, 24, 5, 5, "nina-guid-plan");
            var plan = M42Plan(TsTestPlans.Row("row-ha", "Ha", "Ha 3nm", 600, hours: 4.0));
            plan.TsLinks = new JObject {
                { Profile, new JObject {
                    { "machine", JValue.CreateNull() },
                    { "refs", new JObject {
                        { "profile_id", Profile },
                        { "project_id", 1 },
                        { "target_ids_by_panel", new JObject { { "1,1", tid } } },
                        { "template_ids_by_filter", new JObject { { "row-ha", mine } } },
                        { "exposure_plan_ids", new JObject { { $"{tid}_row-ha", ep } } },
                    } },
                    { "base_snapshot", new JObject {
                        { "exposure_plans_by_panel_filter", new JObject {
                            { "1,1_row-ha", new JObject {
                                { "desired", 24 }, { "filter", "Ha" }, { "template", "Ha 3nm" },
                            } },
                        } },
                    } },
                } },
            };
            return Push(db, plan);
        }

        private static void Exec(SqliteConnection conn, string sql) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        private static int Scalar(SqliteConnection conn, string sql) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
    }
}
