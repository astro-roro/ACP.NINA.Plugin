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
    /// (docs/specs/goal-panel-and-ts-order.md tests S1, S2, M2 to M4, R1 to
    /// R3, F1, F2, the O tests of the order writer, and the imported plan that
    /// keeps its own template). Each starts from an
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
            "derived_row_keeps_existing_template",
            "acp_migration_keeps_plans",
            "older_acp_goals",
            "f1_switch_frequency_written",
            "f2_switch_frequency_set_in_ts_kept",
            "smart_order_written",
            "r2_ts_rows_kept_and_remapped",
            "o1_acp_order_written",
            "o3_acp_order_replaces_ts_order",
            "o4_rotate_every_3",
            "o4c_cadence_reset_every_push",
            "o4d_goal_change_refreshes_rows",
            "o7_dither_rows",
            "o8_new_plan_appended_to_ts_order",
            "o11_no_base_order_writes_nothing",
            "f1_override_rows_rebuilt",
            "x1_mosaic_hand_order_on_panel_two_kept",
            "x2_mosaic_acp_reorder_writes_both_panels",
            "x3_added_row_dragged_to_top_is_a_reorder",
            "x3b_added_row_at_end_keeps_hand_order",
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
                case "derived_row_keeps_existing_template": return DerivedRowKeepsExistingTemplate(db);
                case "acp_migration_keeps_plans": return AcpMigrationKeepsPlans(db);
                case "older_acp_goals": return OlderAcpGoals(db);
                case "f1_switch_frequency_written": return F1(db);
                case "f2_switch_frequency_set_in_ts_kept": return F2(db);
                case "smart_order_written": return SmartOrderOn(db);
                case "r2_ts_rows_kept_and_remapped": return R2(db);
                case "o1_acp_order_written": return O1(db);
                case "o3_acp_order_replaces_ts_order": return O3(db);
                case "o4_rotate_every_3": return O4Rotate(db);
                case "o4c_cadence_reset_every_push": return O4c(db);
                case "o4d_goal_change_refreshes_rows": return O4d(db);
                case "o7_dither_rows": return O7(db);
                case "o8_new_plan_appended_to_ts_order": return O8(db);
                case "o11_no_base_order_writes_nothing": return O11(db);
                case "f1_override_rows_rebuilt": return F1Override(db);
                case "x1_mosaic_hand_order_on_panel_two_kept": return X1(db);
                case "x2_mosaic_acp_reorder_writes_both_panels": return X2(db);
                case "x3_added_row_dragged_to_top_is_a_reorder": return X3(db);
                case "x3b_added_row_at_end_keeps_hand_order": return X3b(db);
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
        /// Each target records the order TS shoots, and its override rows only
        /// when TS has none; see the overload below.
        public static void Link(TargetSchedulerDb db, params Plan[] plans) {
            Link(db, null, plans);
        }

        /// As above, given the push's outcome: a target whose override rows
        /// the push left alone keeps the order its old base recorded, as a
        /// real sync does. As link(conn, plans, report) in
        /// tests/row_scenarios.py.
        public static void Link(TargetSchedulerDb db, TsSyncOutcome outcome, params Plan[] plans) {
            var snap = db.ReadAll(Profile);
            var camera = Gear().Cameras[0];
            foreach (var plan in plans) {
                var refs = TsState.BuildRefs(snap, plan, Profile, 28, TsState.OperationPush, camera: camera);
                var baseSnapshot = TsState.BuildBaseSnapshot(snap, plan, Profile, outcome: outcome, camera: camera);
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

        /// Ha, OIII and SII pushed in that order, the grader turned on, linked,
        /// then reordered to SII, Ha, OIII and pushed, so the order writer
        /// wrote SII x4, Ha x4, OIII x4, and linked again. Three cadence rows.
        private static Plan R1State(TargetSchedulerDb db) {
            var ha = TsTestPlans.Row("row-ha", "Ha", "Ha", 300, frames: 4);
            var oiii = TsTestPlans.Row("row-oiii", "OIII", "OIII", 300, frames: 4);
            var sii = TsTestPlans.Row("row-sii", "SII", "SII", 300, frames: 4);
            var plan = M42Plan(ha, oiii, sii);
            Link(db, Push(db, plan), plan);
            GraderOn(db);
            var conn = db.Connection;
            var tid = M42TargetId(conn);
            Exec(conn, "UPDATE exposureplan SET acquired = 3, accepted = 2 WHERE Id = 1");
            plan.FilterRows = new List<FilterRow> { sii, ha, oiii };
            Link(db, Push(db, plan), plan);
            AddCadence(conn, tid, 3);
            return plan;
        }

        public static TsSyncOutcome R1(TargetSchedulerDb db) {
            var plan = R1State(db);
            plan.FilterRows = plan.FilterRows.Where(r => r.Id != "row-ha").ToList();
            return Push(db, plan);
        }

        /// R2: as R1, but the override rows were set by hand to SII, Ha, OIII
        /// (one each) before the removal. The push keeps them, remapped.
        public static TsSyncOutcome R2(TargetSchedulerDb db) {
            var plan = R1State(db);
            SetOverride(db.Connection, M42TargetId(db.Connection),
                new[] { Tuple.Create(0, 2), Tuple.Create(0, 0), Tuple.Create(0, 1) });
            plan.FilterRows = plan.FilterRows.Where(r => r.Id != "row-ha").ToList();
            return Push(db, plan);
        }

        public static TsSyncOutcome R3(TargetSchedulerDb db) {
            var plan = R1State(db);
            plan.FilterRows = plan.FilterRows.Where(r => r.Id != "row-ha").ToList();
            Link(db, Push(db, plan), plan);
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

        /// A plan as an ACP from before filter rows hands it over: goals only.
        public static Plan OldAcp(Dictionary<string, FilterGoal> goals) {
            var plan = M42Plan();
            plan.FilterRows = null;
            plan.FilterGoals = goals;
            return plan;
        }

        /// An existing plan on the user's template "G", a link pinning "G
        /// Stars" for its goal, and rows derived from filter_goals naming the
        /// template "G (Test IMX571)". The plan stays on "G".
        public static TsSyncOutcome DerivedRowKeepsExistingTemplate(TargetSchedulerDb db) {
            Push(db, M42Plan());
            var conn = db.Connection;
            var tid = M42TargetId(conn);
            var mine = AddTemplate(conn, "G", "G", 60, "nina-guid-g");
            var stars = AddTemplate(conn, "G Stars", "G", 10, "nina-guid-g-stars");
            var ep = InsertPlan(conn, tid, mine, 60, 120, 7, 7, "nina-guid-plan-g");
            var plan = OldAcp(new Dictionary<string, FilterGoal> {
                { "G", new FilterGoal { TargetHours = 2.0, SubExposureS = 60 } },
            });
            plan.TsLinks = new JObject {
                { Profile, new JObject {
                    { "machine", JValue.CreateNull() },
                    { "refs", new JObject {
                        { "profile_id", Profile },
                        { "project_id", 1 },
                        { "target_ids_by_panel", new JObject { { "1,1", tid } } },
                        { "template_ids_by_filter", new JObject { { "G", stars } } },
                        { "exposure_plan_ids", new JObject { { $"{tid}_G", ep } } },
                    } },
                    { "base_snapshot", new JObject {
                        { "exposure_plans_by_panel_filter", new JObject {
                            { "1,1_G", new JObject { { "desired", 120 } } },
                        } },
                    } },
                } },
            };
            return Push(db, plan);
        }

        public static Dictionary<string, FilterGoal> SeqGoals() {
            return new Dictionary<string, FilterGoal> {
                { "L", new FilterGoal { TargetHours = 2.0, SubExposureS = 120, ActualHours = 0.5 } },
                { "R Stars", new FilterGoal { TargetHours = 0.0833, SubExposureS = 15 } },
                { "Ha", new FilterGoal { TargetHours = 4.0, SubExposureS = 300, ActualHours = 2.0 } },
                { "Antlia Ha", new FilterGoal { TargetHours = 1.0, SubExposureS = 120 } },
            };
        }

        /// What the writer before filter rows left for SeqGoals.
        public static void SeqOldWriterState(TargetSchedulerDb db) {
            Push(db, M42Plan());
            var conn = db.Connection;
            var tid = M42TargetId(conn);
            foreach (var t in new[] {
                Tuple.Create("L", "L (Test IMX571)", "L", 120, 60),
                Tuple.Create("R Stars", "R Stars (Test IMX571)", "R Stars", 15, 20),
                Tuple.Create("Ha", "Ha 3nm", "Ha", 300, 48),
                Tuple.Create("Antlia Ha", "Antlia Ha (Test IMX571)", "Antlia Ha", 120, 30),
            }) {
                var tpl = AddTemplate(conn, t.Item2, t.Item3, t.Item4, $"old-tpl-{t.Item1}");
                AddOldPlan(conn, tid, tpl, t.Item1, t.Item4, t.Item5, 3, 3);
            }
        }

        /// ACP's one-shot migration as it treats a plan the writers derived
        /// rows for, as migrate_like_acp in tests/row_scenarios.py does it.
        public static void MigrateLikeAcp(Plan plan) {
            var rows = TsRows.RowsOf(plan, Gear().Cameras[0]);
            var newId = rows.ToDictionary(r => r.Id, r => "m-" + r.Id, StringComparer.Ordinal);
            foreach (var r in rows) r.Id = newId[r.Id];
            var byId = rows.ToDictionary(r => r.Id, r => r, StringComparer.Ordinal);
            plan.FilterRows = rows;
            plan.FilterGoals = null;
            Func<string, string> rekey = key => newId.TryGetValue(key, out var id) ? id : key;
            var links = plan.TsLinks as JObject;
            if (links == null) return;
            foreach (var pair in links) {
                var link = pair.Value as JObject;
                var refs = link?["refs"] as JObject;
                if (refs?["exposure_plan_ids"] is JObject epi) {
                    var moved = new JObject();
                    foreach (var kv in epi) {
                        var cut = kv.Key.IndexOf('_');
                        moved[kv.Key.Substring(0, cut) + "_" + rekey(kv.Key.Substring(cut + 1))] = kv.Value;
                    }
                    refs["exposure_plan_ids"] = moved;
                }
                if (refs?["template_ids_by_filter"] is JObject tif) {
                    var moved = new JObject();
                    foreach (var kv in tif) moved[rekey(kv.Key)] = kv.Value;
                    refs["template_ids_by_filter"] = moved;
                }
                var baseSnapshot = link?["base_snapshot"] as JObject;
                if (baseSnapshot?["exposure_plans_by_panel_filter"] is JObject plans) {
                    var moved = new JObject();
                    foreach (var kv in plans) {
                        var cut = kv.Key.IndexOf('_');
                        var rowId = rekey(kv.Key.Substring(cut + 1));
                        var entry = (JObject)kv.Value.DeepClone();
                        FilterRow row;
                        if (byId.TryGetValue(rowId, out row)) {
                            entry["filter"] = row.Filter;
                            entry["template"] = row.Template;
                        }
                        moved[kv.Key.Substring(0, cut) + "_" + rowId] = entry;
                    }
                    baseSnapshot["exposure_plans_by_panel_filter"] = moved;
                }
                if (baseSnapshot?["templates_by_filter"] is JObject tbf) {
                    var moved = new JObject();
                    foreach (var kv in tbf) moved[rekey(kv.Key)] = kv.Value;
                    baseSnapshot["templates_by_filter"] = moved;
                }
            }
        }

        /// The probe seq.py: older-writer plans pushed by the new plugin
        /// against an older ACP, linked, then ACP migrates and the push runs
        /// again. Every plan keeps its template and exposure.
        public static TsSyncOutcome AcpMigrationKeepsPlans(TargetSchedulerDb db) {
            SeqOldWriterState(db);
            var plan = OldAcp(SeqGoals());
            Push(db, plan);
            Link(db, plan);
            MigrateLikeAcp(plan);
            return Push(db, plan);
        }

        /// A plan from an ACP before filter rows, with alias, star, brand and
        /// long-name keys, into an empty database.
        public static TsSyncOutcome OlderAcpGoals(TargetSchedulerDb db) {
            return Push(db, OldAcp(new Dictionary<string, FilterGoal> {
                { "Ha", new FilterGoal { TargetHours = 2.0, SubExposureS = 300 } },
                { "O", new FilterGoal { TargetHours = 1.0, SubExposureS = 300 } },
                { "R Stars", new FilterGoal { TargetHours = 0.05, SubExposureS = 10 } },
                { "Antlia Ha", new FilterGoal { TargetHours = 1.0, SubExposureS = 120 } },
                { "Lum", new FilterGoal { TargetHours = 1.0, SubExposureS = 60 } },
            }));
        }

        // -- Switch frequency and smart order (tests F1 and F2) --------------

        private const string M43 = "M43";

        /// Two plans in project M42, M42 and M43, each with rows OIII then Ha
        /// at 300 s and 4 frames, pushed with switch frequency `freq`, linked,
        /// and three cadence rows left on each target. As f_state in
        /// tests/row_scenarios.py.
        public static Plan[] FState(TargetSchedulerDb db, int freq) {
            var plans = new[] {
                M42Plan(
                    TsTestPlans.Row("row-oiii", "OIII", "OIII", 300, frames: 4),
                    TsTestPlans.Row("row-ha", "Ha", "Ha", 300, frames: 4)),
                TsTestPlans.Plan("m43", projectName: M42, targetName: M43, ra: 101.0, filterRows: new List<FilterRow> {
                    TsTestPlans.Row("row-oiii-43", "OIII", "OIII", 300, frames: 4),
                    TsTestPlans.Row("row-ha-43", "Ha", "Ha", 300, frames: 4),
                }),
            };
            foreach (var plan in plans) plan.FilterSwitchFrequency = freq;
            Link(db, Push(db, plans), plans);
            foreach (var name in new[] { M42, M43 }) {
                AddCadence(db.Connection, Scalar(db.Connection, $"SELECT Id FROM target WHERE name = '{name}'"), 3);
            }
            return plans;
        }

        /// F1: base 0, ACP 3, TS 0. The push writes 3 and deletes the cadence
        /// rows of both targets in the project.
        public static TsSyncOutcome F1(TargetSchedulerDb db) {
            var plans = FState(db, 0);
            foreach (var plan in plans) plan.FilterSwitchFrequency = 3;
            return Push(db, plans);
        }

        /// F2: base 0, ACP 0, TS 3. The push leaves 3 and the cadence rows.
        public static TsSyncOutcome F2(TargetSchedulerDb db) {
            var plans = FState(db, 0);
            Exec(db.Connection, $"UPDATE project SET filterswitchfrequency = 3 WHERE name = '{M42}'");
            return Push(db, plans);
        }

        /// Smart order turned on in ACP since the base.
        public static TsSyncOutcome SmartOrderOn(TargetSchedulerDb db) {
            var plans = FState(db, 0);
            foreach (var plan in plans) plan.SmartExposureOrder = true;
            return Push(db, plans);
        }

        // -- The order writer (section 5) ---------------------------------

        /// Turn the project's grader on by hand, as the spec's fixture has it.
        public static void GraderOn(TargetSchedulerDb db) {
            Exec(db.Connection, $"UPDATE project SET enablegrader = 1 WHERE name = '{M42}'");
        }

        public static void GraderOn(TargetSchedulerDb db, string project) {
            Exec(db.Connection, $"UPDATE project SET enablegrader = 1 WHERE name = '{project}'");
        }

        public static void Reorder(Plan plan, params string[] ids) {
            var byId = plan.FilterRows.ToDictionary(r => r.Id, r => r, StringComparer.Ordinal);
            plan.FilterRows = ids.Select(i => byId[i]).ToList();
        }

        /// The spec's fixture: M42 with rows Ha then OIII at 300 s and 4
        /// frames, pushed in that order so the natural order is Ha, OIII, the
        /// grader on, switch frequency `freq`, linked. As o_state.
        public static Plan OState(TargetSchedulerDb db, int freq = 0) {
            var plan = M42Plan(
                TsTestPlans.Row("row-ha", "Ha", "Ha", 300, frames: 4),
                TsTestPlans.Row("row-oiii", "OIII", "OIII", 300, frames: 4));
            plan.FilterSwitchFrequency = freq;
            var outcome = Push(db, plan);
            GraderOn(db);
            Link(db, outcome, plan);
            return plan;
        }

        /// O1's order OIII, Ha pushed and linked. As o1_state.
        public static Plan O1State(TargetSchedulerDb db) {
            var plan = OState(db);
            Reorder(plan, "row-oiii", "row-ha");
            Link(db, Push(db, plan), plan);
            return plan;
        }

        public static TsSyncOutcome O1(TargetSchedulerDb db) {
            var plan = OState(db);
            Reorder(plan, "row-oiii", "row-ha");
            return Push(db, plan);
        }

        /// O2 then O3: rows edited in TS to Ha, OIII and a push with no ACP
        /// change, then ACP reordered to Ha, OIII.
        public static TsSyncOutcome O3(TargetSchedulerDb db) {
            var plan = O1State(db);
            SetOverride(db.Connection, M42TargetId(db.Connection), new[] { Tuple.Create(0, 0), Tuple.Create(0, 1) });
            Link(db, Push(db, plan), plan);
            Reorder(plan, "row-ha", "row-oiii");
            return Push(db, plan);
        }

        public static TsSyncOutcome O4Rotate(TargetSchedulerDb db) {
            var plan = OState(db, 3);
            Reorder(plan, "row-oiii", "row-ha");
            return Push(db, plan);
        }

        /// O4c: after O1, OIII x4 and one Ha shot (row 6 next), then OIII's
        /// accepted lowered to 3. Push.
        public static TsSyncOutcome O4c(TargetSchedulerDb db) {
            var plan = O1State(db);
            var conn = db.Connection;
            var tid = M42TargetId(conn);
            Exec(conn, "UPDATE exposureplan SET acquired = 4, accepted = 3 WHERE Id = 2");
            Exec(conn, "UPDATE exposureplan SET acquired = 1, accepted = 1 WHERE Id = 1");
            for (var n = 1; n <= 8; n++) {
                Exec(conn,
                    "INSERT INTO filtercadenceitem (targetid, \"order\", \"next\", \"action\", referenceIdx) " +
                    $"VALUES ({tid}, {n}, {(n == 6 ? 1 : 0)}, 0, {(n <= 4 ? 1 : 0)})");
            }
            return Push(db, plan);
        }

        public static TsSyncOutcome O4d(TargetSchedulerDb db) {
            var plan = O1State(db);
            plan.FilterRows.First(r => r.Id == "row-oiii").TargetFrames = 6;
            return Push(db, plan);
        }

        public static TsSyncOutcome O7(TargetSchedulerDb db) {
            var plan = OState(db);
            Exec(db.Connection, $"UPDATE project SET ditherevery = 2 WHERE name = '{M42}'");
            Reorder(plan, "row-oiii", "row-ha");
            return Push(db, plan);
        }

        public static TsSyncOutcome O8(TargetSchedulerDb db) {
            var plan = OState(db);
            var tid = M42TargetId(db.Connection);
            SetOverride(db.Connection, tid, new[] { Tuple.Create(0, 0), Tuple.Create(0, 1) });
            AddCadence(db.Connection, tid, 2);
            plan.FilterRows.Add(TsTestPlans.Row("row-sii", "SII", "SII", 300, frames: 4));
            return Push(db, plan);
        }

        /// O11: a base from before the order was recorded, TS in natural
        /// order OIII, Ha, ACP listing Ha, OIII.
        public static TsSyncOutcome O11(TargetSchedulerDb db) {
            var plan = M42Plan(
                TsTestPlans.Row("row-oiii", "OIII", "OIII", 300, frames: 4),
                TsTestPlans.Row("row-ha", "Ha", "Ha", 300, frames: 4));
            Link(db, Push(db, plan), plan);
            GraderOn(db);
            var panels = ((JObject)plan.TsLinks)[Profile]["base_snapshot"]["targets_by_panel"] as JObject;
            foreach (var pair in panels) {
                var entry = (JObject)pair.Value;
                entry.Remove("exposure_order");
                entry.Remove("override_rows");
            }
            Reorder(plan, "row-ha", "row-oiii");
            return Push(db, plan);
        }

        // -- One order decision per target (section 5) --------------------

        private const string Mos = "Mos";

        private static readonly Tuple<int, int>[] HandOrder = {
            Tuple.Create(0, 2), Tuple.Create(0, 0), Tuple.Create(0, 1),
        };

        /// A 1 by 2 mosaic with rows Ha, OIII and SII, pushed and linked, the
        /// grader on. Gives the plan and its two target Ids. As mosaic_state.
        public static Plan MosaicState(TargetSchedulerDb db, out int target1, out int target2) {
            var plan = TsTestPlans.Plan("mos", projectName: Mos, targetName: Mos, rows: 1, cols: 2,
                filterRows: new List<FilterRow> {
                    TsTestPlans.Row("row-ha", "Ha", "Ha", 300, frames: 4),
                    TsTestPlans.Row("row-oiii", "OIII", "OIII", 300, frames: 4),
                    TsTestPlans.Row("row-sii", "SII", "SII", 300, frames: 4),
                });
            Link(db, Push(db, plan), plan);
            GraderOn(db, Mos);
            target1 = Scalar(db.Connection, "SELECT Id FROM target ORDER BY Id LIMIT 1");
            target2 = Scalar(db.Connection, "SELECT Id FROM target ORDER BY Id LIMIT 1 OFFSET 1");
            return plan;
        }

        /// A hand order on panel 2 only, SII, Ha, OIII. Two pushes with no ACP
        /// change leave it. As x1.
        public static TsSyncOutcome X1(TargetSchedulerDb db) {
            int target1;
            int target2;
            var plan = MosaicState(db, out target1, out target2);
            SetOverride(db.Connection, target2, HandOrder);
            Link(db, Push(db, plan), plan);
            return Push(db, plan);
        }

        /// As X1 with a different hand order, then ACP reordered to SII, Ha,
        /// OIII. Both panels get ACP's order. As x2.
        public static TsSyncOutcome X2(TargetSchedulerDb db) {
            int target1;
            int target2;
            var plan = MosaicState(db, out target1, out target2);
            SetOverride(db.Connection, target2, new[] { Tuple.Create(0, 1), Tuple.Create(0, 0), Tuple.Create(0, 2) });
            Link(db, Push(db, plan), plan);
            Reorder(plan, "row-sii", "row-ha", "row-oiii");
            return Push(db, plan);
        }

        /// Hand-made override rows OIII, Ha on the spec's fixture, pushed and
        /// linked, so the base order stays Ha, OIII. As added_row_state.
        private static Plan AddedRowState(TargetSchedulerDb db) {
            var plan = OState(db);
            SetOverride(db.Connection, M42TargetId(db.Connection), new[] { Tuple.Create(0, 1), Tuple.Create(0, 0) });
            Link(db, Push(db, plan), plan);
            return plan;
        }

        /// A row for SII added in ACP and dragged to the top. ACP's order is
        /// written over the hand order. As x3.
        public static TsSyncOutcome X3(TargetSchedulerDb db) {
            var plan = AddedRowState(db);
            plan.FilterRows.Insert(0, TsTestPlans.Row("row-sii", "SII", "SII", 300, frames: 4));
            return Push(db, plan);
        }

        /// A row for SII added at the end. The hand order stays and SII's
        /// block is appended. As x3b.
        public static TsSyncOutcome X3b(TargetSchedulerDb db) {
            var plan = AddedRowState(db);
            plan.FilterRows.Add(TsTestPlans.Row("row-sii", "SII", "SII", 300, frames: 4));
            return Push(db, plan);
        }

        /// F1 on O1's database: base 0, ACP 3, TS 0. The override rows are
        /// rebuilt in blocks of 3.
        public static TsSyncOutcome F1Override(TargetSchedulerDb db) {
            var plan = O1State(db);
            plan.FilterSwitchFrequency = 3;
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
