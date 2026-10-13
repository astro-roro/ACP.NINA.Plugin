using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ACP.NINA.Plugin.Tests {

    /// What one gear scenario step left behind.
    public class GearRun {
        public TsSyncPayload Payload { get; set; }
        public TsSyncOutcome Outcome { get; set; }
        public GearResponse Gear { get; set; }

        /// Every line the user would read after the sync.
        public List<string> Notes {
            get { return Payload.SyncNotes.Concat(Outcome.SyncNotes).ToList(); }
        }

        public TsTemplateResult ResultFor(string gearTemplateId) {
            return Outcome.GearResults.Single(r => r.TemplateId == gearTemplateId);
        }
    }

    /// The gear template scenarios of docs/specs/gear-templates.md section 8
    /// (S1 to S31), as recipes: each is a named sequence of steps over the
    /// 20 template fixture (edit gear, edit TS, set the slot list, push).
    /// The Python suite gets the same recipes in `row_scenarios.py` when the
    /// Python writer lands, and `dump_golden_rows.py` writes each one's rows
    /// to golden-rows.json for the plugin to compare with. Until then the
    /// C# tests assert the spec's expected column.
    public static partial class TsRowScenarios {

        public const string GearProfile = "9d3a61fd-9f35-42ae-a911-bd6e4b0f76a7";
        public const string Camera6200 = "zwo-asi6200mm-pro";
        public static readonly string[] WheelSlots = { "L", "R", "G", "B", "H", "O", "S" };

        public static readonly string[] GearNames = {
            "gear_s1_no_edits", "gear_s2_offset_written", "gear_s3_conflict_held",
            "gear_s4_ts_edit_left_for_pull", "gear_s5_rename_in_place", "gear_s6_rename_name_taken",
            "gear_s7_new_template_inserted", "gear_s8_off_wheel_skipped", "gear_s9_unknown_wheel",
            "gear_s10_strays", "gear_s11_sub_change", "gear_s12_zero_kept", "gear_s13_plan_not_moved",
            "gear_s14_plan_moved", "gear_s16_name_clash", "gear_s17_duplicate_names",
            "gear_s18_duplicate_not_linked", "gear_s19_adopted_by_name", "gear_s20_alias_other_group",
            "gear_s24_row_sub_deviates", "gear_s30_ts_names_never_find", "gear_s31_alias_finds_row",
        };

        // -- Setup ----------------------------------------------------------

        /// An empty 28 database holding the fixture's 20 templates and 244
        /// exposure plans.
        public static string MakeTemplateDb(string path) {
            TsFixtures.MakeDb(28, path);
            var builder = new SqliteConnectionStringBuilder {
                DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
            };
            using (var conn = new SqliteConnection(builder.ToString())) {
                conn.Open();
                // The fixture's plans name targets that have no row here.
                Exec(conn, "PRAGMA foreign_keys = OFF");
                Exec(conn, TsFixtures.ReadFixture("ts-templates-20.sql"));
            }
            return path;
        }

        /// The gear after the migration and adoption (gear-templates-v3.json).
        public static GearResponse GearV3() {
            return JsonConvert.DeserializeObject<GearResponse>(TsFixtures.ReadFixture("gear-templates-v3.json"));
        }

        public static Camera GearCamera(GearResponse gear, string cameraId = Camera6200) {
            return gear.Cameras.Single(c => c.Id == cameraId);
        }

        /// A gear template of a camera by its name.
        public static GearTemplate GearTemplateNamed(GearResponse gear, string name, string cameraId = Camera6200) {
            return GearCamera(gear, cameraId).Filters.Values
                .SelectMany(f => f.Templates ?? new List<GearTemplate>())
                .Single(t => t.Name == name);
        }

        public static CameraFilter GearFilterOf(GearResponse gear, GearTemplate template, string cameraId = Camera6200) {
            return GearCamera(gear, cameraId).Filters.Values.Single(f => f.Templates.Contains(template));
        }

        public static string GearFilterNameOf(GearResponse gear, GearTemplate template, string cameraId = Camera6200) {
            return GearCamera(gear, cameraId).Filters.Single(f => f.Value.Templates.Contains(template)).Key;
        }

        /// A row on a gear template: it carries the template's id and name,
        /// follows its sub unless `sub` says otherwise, and asks for `hours`.
        public static FilterRow GearRow(
            GearResponse gear, string rowId, string templateName, double hours = 1.0, double? sub = null,
            string cameraId = Camera6200
        ) {
            return GearRowFor(gear, rowId, GearTemplateNamed(gear, templateName, cameraId), hours, sub, cameraId);
        }

        public static FilterRow GearRowFor(
            GearResponse gear, string rowId, GearTemplate template, double hours = 1.0, double? sub = null,
            string cameraId = Camera6200
        ) {
            return new FilterRow {
                Id = rowId,
                Filter = GearFilterNameOf(gear, template, cameraId),
                Template = template.Name,
                TemplateId = template.Id,
                TemplateSubS = sub ?? template.SubS,
                Unit = "h",
                TargetHours = hours,
            };
        }

        public static Plan GearPlan(string id, string target, string cameraId, params FilterRow[] rows) {
            return TsTestPlans.Plan(
                id, projectName: target, targetName: target, telescopeId: "telescope-2", cameraId: cameraId,
                filterRows: rows.ToList());
        }

        public static GearRun GearPush(
            TargetSchedulerDb db, GearResponse gear, IReadOnlyList<string> slots, params Plan[] plans
        ) {
            var payload = TsConvert.BuildPayload(
                plans.ToList(), gear, GearProfile, TsTestPlans.FrozenNow, slots, null, "Foo");
            var outcome = TsUpsert.Apply(db, payload);
            return new GearRun { Payload = payload, Outcome = outcome, Gear = gear };
        }

        /// What a push leaves on each plan: its link for the gear profile,
        /// with the base the next push compares with.
        public static void GearLink(TargetSchedulerDb db, GearResponse gear, GearRun run, params Plan[] plans) {
            var snap = db.ReadAll(GearProfile);
            foreach (var plan in plans) {
                var camera = GearCamera(gear, plan.CameraId);
                var refs = TsState.BuildRefs(snap, plan, GearProfile, 28, TsState.OperationPush, camera: camera);
                var baseSnapshot = TsState.BuildBaseSnapshot(snap, plan, GearProfile, outcome: run?.Outcome, camera: camera);
                plan.TsLinks = new JObject {
                    { GearProfile, new JObject {
                        { "machine", JValue.CreateNull() },
                        { "refs", refs },
                        { "base_snapshot", baseSnapshot },
                    } },
                };
            }
        }

        /// Record the results of a push on the gear templates the way ACP's
        /// record_template_results does, so the next push has the new base.
        public static void RecordResults(GearRun run) {
            var all = run.Gear.Cameras.SelectMany(c => c.Filters.Values)
                .SelectMany(f => f.Templates ?? new List<GearTemplate>()).ToList();
            foreach (var result in run.Outcome.GearResults) {
                var template = all.Single(t => t.Id == result.TemplateId);
                if (result.TsId == null) continue;
                template.Ts[GearProfile] = new GearTemplateLink {
                    Id = result.TsId, Guid = result.Guid, Base = result.Base,
                    PushedIso = "2026-10-13T00:00:00Z",
                };
            }
        }

        public static List<Dictionary<string, object>> TemplateRows(SqliteConnection conn) {
            var rows = new List<Dictionary<string, object>>();
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "SELECT * FROM exposuretemplate ORDER BY Id";
                using (var r = cmd.ExecuteReader()) {
                    while (r.Read()) {
                        var row = new Dictionary<string, object>();
                        for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                        rows.Add(row);
                    }
                }
            }
            return rows;
        }

        public static string Cell(SqliteConnection conn, string sql) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                var v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? null : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // -- The scenarios --------------------------------------------------

        private static Plan Ha(GearResponse gear, double hours = 3.0) {
            return GearPlan("gear-ha", "M42", Camera6200, GearRow(gear, "row-ha", "Ha", hours));
        }

        public static GearRun GearS1(TargetSchedulerDb db, GearResponse gear) {
            return GearPush(db, gear, WheelSlots, Ha(gear));
        }

        public static GearRun GearS2(TargetSchedulerDb db, GearResponse gear) {
            GearTemplateNamed(gear, "Ha").Offset = 25;
            return GearPush(db, gear, WheelSlots, Ha(gear));
        }

        public static GearRun GearS3(TargetSchedulerDb db, GearResponse gear) {
            GearTemplateNamed(gear, "Ha").Offset = 25;
            Exec(db.Connection, "UPDATE exposuretemplate SET offset = 10 WHERE Id = 12");
            return GearPush(db, gear, WheelSlots, Ha(gear));
        }

        public static GearRun GearS4(TargetSchedulerDb db, GearResponse gear) {
            Exec(db.Connection, "UPDATE exposuretemplate SET offset = 10 WHERE Id = 12");
            return GearPush(db, gear, WheelSlots, Ha(gear));
        }

        public static GearRun GearS5(TargetSchedulerDb db, GearResponse gear) {
            var ha = GearTemplateNamed(gear, "Ha");
            ha.Name = "Ha 300";
            ha.Aliases.Insert(0, "Ha");
            var row = GearRow(gear, "row-ha", "Ha 300");
            return GearPush(db, gear, WheelSlots, GearPlan("gear-ha", "M42", Camera6200, row));
        }

        public static GearRun GearS6(TargetSchedulerDb db, GearResponse gear) {
            var ha = GearTemplateNamed(gear, "Ha");
            ha.Name = "OIII";
            ha.Aliases.Insert(0, "Ha");
            return GearPush(db, gear, WheelSlots, GearPlan("gear-ha", "M42", Camera6200, GearRowFor(gear, "row-ha", ha)));
        }

        public static GearRun GearS7(TargetSchedulerDb db, GearResponse gear) {
            var ha = GearTemplateNamed(gear, "Ha");
            GearFilterOf(gear, ha).Templates.Add(new GearTemplate {
                Id = "11111111-1111-4111-8111-111111111111", Name = "Ha 600", SubS = 600, Offset = 25,
            });
            return GearPush(db, gear, WheelSlots, Ha(gear));
        }

        public static GearRun GearS8(TargetSchedulerDb db, GearResponse gear, IReadOnlyList<string> slots) {
            var plan = GearPlan("gear-sii", "M42", Camera6200, GearRow(gear, "row-sii", "SII"));
            return GearPush(db, gear, slots, plan);
        }

        public static GearRun GearS9(TargetSchedulerDb db, GearResponse gear, IReadOnlyList<string> slots) {
            foreach (var name in new[] { "Ha", "OIII", "SII" }) GearTemplateNamed(gear, name).Offset = 25;
            var ha = GearTemplateNamed(gear, "Ha");
            var extra = new GearTemplate {
                Id = "22222222-2222-4222-8222-222222222222", Name = "Ha 600", SubS = 600, Offset = 25,
            };
            GearFilterOf(gear, ha).Templates.Add(extra);
            var plan = GearPlan("gear-ha", "M42", Camera6200,
                new FilterRow {
                    Id = "row-600", Filter = "Ha", Template = "Ha 600", TemplateId = extra.Id,
                    TemplateSubS = 600, Unit = "h", TargetHours = 1.0,
                });
            return GearPush(db, gear, slots, plan);
        }

        public static GearRun GearS10(TargetSchedulerDb db, GearResponse gear) {
            return GearPush(db, gear, WheelSlots, Ha(gear));
        }

        /// 23 Ha rows' worth of the S11 story, reduced to one pushed plan: the
        /// plan sits on id 12 at an explicit 300, the template and the row
        /// move to 600.
        public static GearRun GearS11(TargetSchedulerDb db, GearResponse gear, out Plan plan) {
            plan = Ha(gear);
            var first = GearPush(db, gear, WheelSlots, plan);
            Exec(db.Connection, $"UPDATE exposureplan SET exposure = 300 WHERE guid = '{PlanGuidOf(plan, "row-ha")}'");
            GearLink(db, gear, first, plan);
            RecordResults(first);
            GearTemplateNamed(gear, "Ha").SubS = 600;
            plan.FilterRows[0].TemplateSubS = 600;
            return GearPush(db, gear, WheelSlots, plan);
        }

        public static string PlanGuidOf(Plan plan, string rowId) {
            var row = plan.FilterRows.Single(r => r.Id == rowId);
            var targetGuid = TsGuid.Target(GearProfile, TsConvert.ProjectNameOf(plan), plan.Target.Name);
            return TsGuid.RowPlan(GearProfile, targetGuid, row.Filter, row.Template);
        }

        public static GearRun GearS12(TargetSchedulerDb db, GearResponse gear) {
            var ha = GearTemplateNamed(gear, "Ha");
            ha.Gain = 0;
            ha.Ts[GearProfile].Base.Gain = 0;
            Exec(db.Connection, "UPDATE exposuretemplate SET gain = 0 WHERE Id = 12");
            return GearPush(db, gear, WheelSlots, Ha(gear));
        }

        public static GearRun GearS13(TargetSchedulerDb db, GearResponse gear, out Plan plan) {
            plan = Ha(gear);
            var first = GearPush(db, gear, WheelSlots, plan);
            GearLink(db, gear, first, plan);
            RecordResults(first);
            Exec(db.Connection, $"UPDATE exposureplan SET exposureTemplateId = 4 WHERE guid = '{PlanGuidOf(plan, "row-ha")}'");
            return GearPush(db, gear, WheelSlots, plan);
        }

        public static GearRun GearS14(TargetSchedulerDb db, GearResponse gear, out Plan plan) {
            plan = Ha(gear);
            var first = GearPush(db, gear, WheelSlots, plan);
            GearLink(db, gear, first, plan);
            RecordResults(first);
            var oiii = GearTemplateNamed(gear, "OIII");
            var row = plan.FilterRows[0];
            row.Filter = "OIII";
            row.Template = oiii.Name;
            row.TemplateId = oiii.Id;
            row.TemplateSubS = oiii.SubS;
            // The plan's guid names the template, so it is found again through
            // the id pinned in ts_refs, as after any template change.
            return GearPush(db, gear, WheelSlots, plan);
        }

        public static GearRun GearS16(TargetSchedulerDb db, GearResponse gear) {
            var other = GearTemplateNamed(gear, "Ha (ZWO ASI2600MM Pro)", "zwo-asi2600mm-pro");
            other.Name = "Ha";
            var plan6200 = GearPlan("gear-ha", "M42", Camera6200, GearRow(gear, "row-ha", "Ha"));
            var plan2600 = GearPlan("gear-ha2", "M43", "zwo-asi2600mm-pro",
                GearRow(gear, "row-ha2", "Ha", 1.0, null, "zwo-asi2600mm-pro"));
            return GearPush(db, gear, WheelSlots, plan6200, plan2600);
        }

        public static GearRun GearS17(TargetSchedulerDb db, GearResponse gear, bool linked) {
            var l = GearTemplateNamed(gear, "L");
            if (linked) l.Offset = 25;
            else l.Ts.Clear();
            return GearPush(db, gear, WheelSlots,
                GearPlan("gear-l", "M42", Camera6200, GearRow(gear, "row-l", "L")));
        }

        public static GearRun GearS18(TargetSchedulerDb db, GearResponse gear) {
            var other = GearTemplateNamed(gear, "B (ZWO ASI2600MM Pro)", "zwo-asi2600mm-pro");
            other.Name = "B";
            return GearPush(db, gear, WheelSlots,
                GearPlan("gear-b", "M43", "zwo-asi2600mm-pro", GearRow(gear, "row-b", "B", 1.0, null, "zwo-asi2600mm-pro")));
        }

        public static GearRun GearS19(TargetSchedulerDb db, GearResponse gear) {
            var ha = GearTemplateNamed(gear, "Ha");
            ha.Ts.Clear();
            ha.Offset = 25;
            return GearPush(db, gear, WheelSlots, Ha(gear));
        }

        public static GearRun GearS20(TargetSchedulerDb db, GearResponse gear) {
            var stars = GearTemplateNamed(gear, "R Stars");
            stars.Ts.Clear();
            stars.Name = "R Stars 2";
            stars.Aliases.Insert(0, "R");
            return GearPush(db, gear, WheelSlots,
                GearPlan("gear-rs", "M42", Camera6200, GearRow(gear, "row-rs", "R Stars 2", 0.0)));
        }

        public static GearRun GearS24(TargetSchedulerDb db, GearResponse gear, out Plan plan) {
            var ha = GearTemplateNamed(gear, "Ha");
            plan = GearPlan("gear-ha", "M42", Camera6200, GearRow(gear, "row-ha", "Ha", 3.0, 400.0));
            var first = GearPush(db, gear, WheelSlots, plan);
            GearLink(db, gear, first, plan);
            RecordResults(first);
            plan.FilterRows[0].TemplateSubS = 450;
            return GearPush(db, gear, WheelSlots, plan);
        }

        public static GearRun GearS30(TargetSchedulerDb db, GearResponse gear) {
            var r = GearTemplateNamed(gear, "R (ZWO ASI6200MM Pro)");
            r.Ts.Clear();
            // Its plans stay behind in the fixture, so the delete needs the
            // foreign keys off for this connection.
            Exec(db.Connection, "PRAGMA foreign_keys = OFF");
            Exec(db.Connection, "DELETE FROM exposuretemplate WHERE Id = 19");
            return GearPush(db, gear, WheelSlots,
                GearPlan("gear-r", "M42", Camera6200, GearRow(gear, "row-r", "R (ZWO ASI6200MM Pro)", 0.0)));
        }

        public static GearRun GearS31(TargetSchedulerDb db, GearResponse gear) {
            var ha = GearTemplateNamed(gear, "Ha");
            ha.Name = "Ha 300";
            ha.Aliases.Insert(0, "Ha");
            ha.Ts.Clear();
            return GearPush(db, gear, WheelSlots,
                GearPlan("gear-ha", "M42", Camera6200, GearRow(gear, "row-ha", "Ha 300")));
        }
    }
}
