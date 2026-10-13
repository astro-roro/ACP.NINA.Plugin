using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The gear template writer, scenario by scenario, against the 20
    /// template fixture (docs/specs/gear-templates.md section 8, S1 to S31).
    /// The recipes live in TsRowScenarios.Gear.cs.
    public class TsGearTemplateTests {

        private static readonly string[] NoS = { "L", "R", "G", "B", "H", "O" };

        private static TargetSchedulerDb Open(TempDir tmp) {
            return TargetSchedulerDb.Open(TsRowScenarios.MakeTemplateDb(tmp.File("schedulerdb.sqlite")));
        }

        private static string Json(object value) {
            return JsonConvert.SerializeObject(value);
        }

        private static string Rows(TargetSchedulerDb db) {
            return Json(TsRowScenarios.TemplateRows(db.Connection));
        }

        private static string Cell(TargetSchedulerDb db, string sql) {
            return TsRowScenarios.Cell(db.Connection, sql);
        }

        /// The highest exposure plan Id the fixture holds, so a test can tell
        /// the fixture's plans from the ones a push adds.
        private static string FixturePlanMax(TargetSchedulerDb db) {
            return Cell(db, "SELECT MAX(Id) FROM exposureplan");
        }

        private static Dictionary<string, object> RowOf(TargetSchedulerDb db, int id) {
            return TsRowScenarios.TemplateRows(db.Connection).Single(r => (long)r["Id"] == id);
        }

        [Fact]
        public void S1_NoGearEditsWritesNothingAndReportsTenUnchanged() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var before = Rows(db);
                var gear = TsRowScenarios.GearV3();
                var run = TsRowScenarios.GearS1(db, gear);

                Assert.Equal(10, run.Outcome.GearResults.Count);
                Assert.All(run.Outcome.GearResults, r => Assert.Equal("unchanged", r.Action));
                Assert.Equal(0, run.Outcome.ExposureTemplate.Inserted);
                Assert.Equal(0, run.Outcome.ExposureTemplate.Updated);
                Assert.Equal(before, Rows(db));
            }
        }

        [Fact]
        public void S2_AnOffsetChangedInAcpIsWrittenToThatTemplateOnly() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var beforeRows = TsRowScenarios.TemplateRows(db.Connection);
                var gear = TsRowScenarios.GearV3();
                var run = TsRowScenarios.GearS2(db, gear);

                var ha = TsRowScenarios.GearTemplateNamed(gear, "Ha");
                var result = run.ResultFor(ha.Id);
                Assert.Equal("updated", result.Action);
                Assert.Equal(new[] { "offset" }, result.FieldsWritten);
                Assert.Equal(25, result.Base.Offset);
                Assert.Equal(12, result.TsId);

                var afterRows = TsRowScenarios.TemplateRows(db.Connection);
                foreach (var before in beforeRows) {
                    var after = afterRows.Single(r => (long)r["Id"] == (long)before["Id"]);
                    if ((long)before["Id"] == 12) {
                        Assert.Equal(25L, after["offset"]);
                        after["offset"] = before["offset"];
                    }
                    Assert.Equal(Json(before), Json(after));
                }
            }
        }

        [Fact]
        public void S3_AFieldChangedInBothPlacesIsHeldAndTheTsValueSurvives() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var gear = TsRowScenarios.GearV3();
                var run = TsRowScenarios.GearS3(db, gear);

                Assert.Equal("10", Cell(db, "SELECT offset FROM exposuretemplate WHERE Id = 12"));
                Assert.Contains("Ha: offset is 25 in ACP and 10 in TS. Left alone until you choose in the gear editor.", run.Notes);
                var result = run.ResultFor(TsRowScenarios.GearTemplateNamed(gear, "Ha").Id);
                Assert.Equal("conflict", result.Action);
                Assert.Empty(result.FieldsWritten);
                Assert.Equal("offset", Assert.Single(result.Conflicts).Field);
                // The base keeps the old value, so the hold persists until the user chooses.
                Assert.Equal(-1, result.Base.Offset);
            }
        }

        [Fact]
        public void S4_AFieldChangedOnlyInTsIsLeftForThePull() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var gear = TsRowScenarios.GearV3();
                var run = TsRowScenarios.GearS4(db, gear);

                Assert.Equal("10", Cell(db, "SELECT offset FROM exposuretemplate WHERE Id = 12"));
                var result = run.ResultFor(TsRowScenarios.GearTemplateNamed(gear, "Ha").Id);
                Assert.Equal("unchanged", result.Action);
                Assert.Equal(-1, result.Base.Offset);
                Assert.DoesNotContain(run.Notes, n => n.Contains("Left alone"));
            }
        }

        [Fact]
        public void S5_ARenameIsWrittenToTheSameRowAndInsertsNothing() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var gear = TsRowScenarios.GearV3();
                var fixtureMax = FixturePlanMax(db);
                var run = TsRowScenarios.GearS5(db, gear);

                Assert.Equal("Ha 300", Cell(db, "SELECT name FROM exposuretemplate WHERE Id = 12"));
                Assert.Equal("20", Cell(db, "SELECT COUNT(*) FROM exposuretemplate"));
                var result = run.ResultFor(TsRowScenarios.GearTemplateNamed(gear, "Ha 300").Id);
                Assert.Equal("updated", result.Action);
                Assert.Equal(new[] { "name" }, result.FieldsWritten);
                Assert.Equal("Ha 300", result.Base.Name);
                // The 23 plans already on the template are still on it.
                Assert.Equal("23", Cell(db, $"SELECT COUNT(*) FROM exposureplan WHERE exposureTemplateId = 12 AND Id <= {fixtureMax}"));
            }
        }

        [Fact]
        public void S6_ARenameToANameAnotherTemplateHoldsIsSkippedWithANote() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var gear = TsRowScenarios.GearV3();
                var run = TsRowScenarios.GearS6(db, gear);

                Assert.Equal("Ha", Cell(db, "SELECT name FROM exposuretemplate WHERE Id = 12"));
                Assert.Contains(run.Notes, n => n.Contains("the name was skipped") && n.Contains("(id 11)"));
            }
        }

        [Fact]
        public void S7_ANewGearTemplateIsInsertedUnderTheSlotWithAGuidFromItsId() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var gear = TsRowScenarios.GearV3();
                var run = TsRowScenarios.GearS7(db, gear);

                Assert.Equal("21", Cell(db, "SELECT COUNT(*) FROM exposuretemplate"));
                Assert.Equal("H", Cell(db, "SELECT filtername FROM exposuretemplate WHERE name = 'Ha 600'"));
                Assert.Equal("600", Cell(db, "SELECT CAST(defaultexposure AS INTEGER) FROM exposuretemplate WHERE name = 'Ha 600'"));
                Assert.Equal("25", Cell(db, "SELECT offset FROM exposuretemplate WHERE name = 'Ha 600'"));
                Assert.Equal(
                    TsGuid.GearTemplate(TsRowScenarios.GearProfile, "11111111-1111-4111-8111-111111111111"),
                    Cell(db, "SELECT guid FROM exposuretemplate WHERE name = 'Ha 600'"));
                Assert.Equal("0", Cell(db, "SELECT COUNT(*) FROM exposureplan WHERE exposureTemplateId = 21"));
                Assert.Equal("inserted", run.ResultFor("11111111-1111-4111-8111-111111111111").Action);
            }
        }

        [Fact]
        public void S8_AFilterNotOnTheWheelIsSkippedUntilTheSlotAppears() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var gear = TsRowScenarios.GearV3();
                var rowBefore = Json(RowOf(db, 13));
                var run = TsRowScenarios.GearS8(db, gear, NoS);

                Assert.Equal(rowBefore, Json(RowOf(db, 13)));
                Assert.Equal("244", Cell(db, "SELECT COUNT(*) FROM exposureplan"));
                Assert.Contains(run.Notes, n => n.Contains("SII is not on the wheel of Foo yet, so 1 row was skipped"));
                Assert.Equal("skipped", run.ResultFor(TsRowScenarios.GearTemplateNamed(gear, "SII").Id).Action);

                TsRowScenarios.GearS8(db, gear, TsRowScenarios.WheelSlots);
                Assert.Equal("245", Cell(db, "SELECT COUNT(*) FROM exposureplan"));
                Assert.Equal("4", Cell(db, "SELECT COUNT(*) FROM exposureplan WHERE exposureTemplateId = 13"));
            }
        }

        [Fact]
        public void S9_AnUnknownWheelUpdatesExistingRowsWithoutTheirFilternameAndInsertsNothing() {
            string rowsEmpty;
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var gear = TsRowScenarios.GearV3();
                var run = TsRowScenarios.GearS9(db, gear, new string[0]);

                foreach (var id in new[] { 12, 11, 13 }) {
                    Assert.Equal("25", Cell(db, $"SELECT offset FROM exposuretemplate WHERE Id = {id}"));
                }
                Assert.Equal("H", Cell(db, "SELECT filtername FROM exposuretemplate WHERE Id = 12"));
                Assert.Equal("O", Cell(db, "SELECT filtername FROM exposuretemplate WHERE Id = 11"));
                Assert.Equal("S", Cell(db, "SELECT filtername FROM exposuretemplate WHERE Id = 13"));
                Assert.Equal("20", Cell(db, "SELECT COUNT(*) FROM exposuretemplate"));
                Assert.Contains(run.Notes, n => n.Contains("the wheel of Foo is unknown, so 1 row was skipped"));
                rowsEmpty = Rows(db);
            }
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                TsRowScenarios.GearS9(db, TsRowScenarios.GearV3(), null);
                Assert.Equal(rowsEmpty, Rows(db));
            }
        }

        [Fact]
        public void S10_TemplatesNoGearTemplateStandsForAreListedAsStray() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var run = TsRowScenarios.GearS10(db, TsRowScenarios.GearV3());
                Assert.Equal(
                    new[] { 1, 2, 3, 4, 8, 9, 10, 15, 17, 20 },
                    run.Outcome.StrayTemplateIds.OrderBy(i => i).ToArray());
            }
        }

        [Fact]
        public void S11_ASubChangeWritesTheDefaultAndPutsFollowingPlansBackOnIt() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                Plan plan;
                var fixtureMax = FixturePlanMax(db);
                TsRowScenarios.GearS11(db, TsRowScenarios.GearV3(), out plan);

                Assert.Equal("600", Cell(db, "SELECT CAST(defaultexposure AS INTEGER) FROM exposuretemplate WHERE Id = 12"));
                var guid = TsRowScenarios.PlanGuidOf(plan, "row-ha");
                Assert.Equal("-1", Cell(db, $"SELECT CAST(exposure AS INTEGER) FROM exposureplan WHERE guid = '{guid}'"));
                // Plans of other projects, not in the push, keep their explicit exposure.
                Assert.Equal("23", Cell(db, $"SELECT COUNT(*) FROM exposureplan WHERE exposureTemplateId = 12 AND Id <= {fixtureMax} AND exposure = 300"));
            }
        }

        [Fact]
        public void S12_AGainOfZeroStaysZeroInTheBase() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var gear = TsRowScenarios.GearV3();
                var run = TsRowScenarios.GearS12(db, gear);
                var result = run.ResultFor(TsRowScenarios.GearTemplateNamed(gear, "Ha").Id);
                Assert.Equal(0, result.Base.Gain);
            }
        }

        [Fact]
        public void S13_ARowWhosePlanSitsOnAnotherTemplateByHandIsNotMoved() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                Plan plan;
                TsRowScenarios.GearS13(db, TsRowScenarios.GearV3(), out plan);
                var guid = TsRowScenarios.PlanGuidOf(plan, "row-ha");
                Assert.Equal("4", Cell(db, $"SELECT exposureTemplateId FROM exposureplan WHERE guid = '{guid}'"));
            }
        }

        [Fact]
        public void S14_ARowChangedToAnotherTemplateMovesItsPlanAndWritesTheDefault() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                Plan plan;
                TsRowScenarios.GearS14(db, TsRowScenarios.GearV3(), out plan);
                var guid = TsRowScenarios.PlanGuidOf(plan, "row-ha");
                Assert.Equal("11", Cell(db, $"SELECT exposureTemplateId FROM exposureplan WHERE guid = '{guid}'"));
                Assert.Equal("-1", Cell(db, $"SELECT CAST(exposure AS INTEGER) FROM exposureplan WHERE guid = '{guid}'"));
                Assert.Equal("245", Cell(db, "SELECT COUNT(*) FROM exposureplan"));
            }
        }

        [Fact]
        public void S16_TwoCamerasWantingOneNameInsertTheFirstAndSayWhyForTheSecond() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var run = TsRowScenarios.GearS16(db, TsRowScenarios.GearV3());
                Assert.Contains(
                    "Template 'Ha' of ZWO ASI2600MM Pro was not created in profile Foo, because ZWO ASI6200MM Pro's 'Ha' already uses that name.",
                    run.Notes);
                // The 2600's other templates are new and go in; its "Ha" does not.
                Assert.Equal("0", Cell(db, "SELECT COUNT(*) FROM exposuretemplate WHERE Id > 20 AND filtername = 'H'"));
                Assert.Equal("20", Cell(db, "SELECT COUNT(*) FROM exposuretemplate WHERE Id <= 20"));
            }
        }

        [Fact]
        public void S17_TwoTemplatesOfOneNameAreWrittenByIdAndNeverLinkedByName() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var id1 = Json(RowOf(db, 1));
                TsRowScenarios.GearS17(db, TsRowScenarios.GearV3(), linked: true);
                Assert.Equal("25", Cell(db, "SELECT offset FROM exposuretemplate WHERE Id = 18"));
                Assert.Equal(id1, Json(RowOf(db, 1)));
            }
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var before = Rows(db);
                var gear = TsRowScenarios.GearV3();
                var run = TsRowScenarios.GearS17(db, gear, linked: false);
                Assert.Equal(before, Rows(db));
                Assert.Contains("Template 'L' matches 2 templates in Foo (ids 1, 18), left for adoption.", run.Notes);
                Assert.Equal("skipped", run.ResultFor(TsRowScenarios.GearTemplateNamed(gear, "L").Id).Action);
            }
        }

        [Fact]
        public void S18_ANewTemplateWhoseNameTwoRowsShareWaitsForAdoption() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var before = Json(TsRowScenarios.TemplateRows(db.Connection));
                var run = TsRowScenarios.GearS18(db, TsRowScenarios.GearV3());
                // Nothing of the profile's 20 changed, and the 2600's "B" made no 21st named "B".
                Assert.Equal(before, Json(TsRowScenarios.TemplateRows(db.Connection).Where(r => (long)r["Id"] <= 20).ToList()));
                Assert.Equal("2", Cell(db, "SELECT COUNT(*) FROM exposuretemplate WHERE name = 'B'"));
                Assert.Contains("Template 'B' matches 2 templates in Foo (ids 4, 14), left for adoption.", run.Notes);
            }
        }

        [Fact]
        public void S19_ATemplateFoundByNameWithNoBaseTakesTsValuesAndWritesNothing() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var before = Rows(db);
                var gear = TsRowScenarios.GearV3();
                var run = TsRowScenarios.GearS19(db, gear);
                Assert.Equal(before, Rows(db));
                Assert.Contains("Ha already exists in profile Foo, so ACP took its values.", run.Notes);
                var result = run.ResultFor(TsRowScenarios.GearTemplateNamed(gear, "Ha").Id);
                Assert.Equal("adopted", result.Action);
                Assert.Equal(12, result.TsId);
                Assert.Equal(-1, result.Base.Offset);
            }
        }

        [Fact]
        public void S20_AnAliasNamingARowOfAnotherGroupClaimsNothing() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var id2 = Json(RowOf(db, 2));
                TsRowScenarios.GearS20(db, TsRowScenarios.GearV3());
                Assert.Equal(id2, Json(RowOf(db, 2)));
                Assert.Equal("21", Cell(db, "SELECT COUNT(*) FROM exposuretemplate"));
                Assert.Equal("R", Cell(db, "SELECT filtername FROM exposuretemplate WHERE name = 'R Stars 2'"));
                Assert.Equal("15", Cell(db, "SELECT CAST(defaultexposure AS INTEGER) FROM exposuretemplate WHERE name = 'R Stars 2'"));
            }
        }

        [Fact]
        public void S24_ARowSubChangedInAcpOnADeviatingRowWritesAnExplicitExposure() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                Plan plan;
                TsRowScenarios.GearS24(db, TsRowScenarios.GearV3(), out plan);
                var guid = TsRowScenarios.PlanGuidOf(plan, "row-ha");
                Assert.Equal("450", Cell(db, $"SELECT CAST(exposure AS INTEGER) FROM exposureplan WHERE guid = '{guid}'"));
                Assert.Equal("245", Cell(db, "SELECT COUNT(*) FROM exposureplan"));
            }
        }

        [Fact]
        public void S30_NamesInTsNamesNeverFindARow() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var id2 = Json(RowOf(db, 2));
                TsRowScenarios.GearS30(db, TsRowScenarios.GearV3());
                Assert.Equal(id2, Json(RowOf(db, 2)));
                Assert.Equal("20", Cell(db, "SELECT COUNT(*) FROM exposuretemplate"));
                Assert.Equal("1", Cell(db, "SELECT COUNT(*) FROM exposuretemplate WHERE name = 'R (ZWO ASI6200MM Pro)' AND Id > 20"));
            }
        }

        [Fact]
        public void S31_AnAliasFindsTheRowAndTheRenameIsWrittenToIt() {
            using (var tmp = new TempDir())
            using (var db = Open(tmp)) {
                var gear = TsRowScenarios.GearV3();
                var run = TsRowScenarios.GearS31(db, gear);
                Assert.Equal("Ha 300", Cell(db, "SELECT name FROM exposuretemplate WHERE Id = 12"));
                Assert.Equal("20", Cell(db, "SELECT COUNT(*) FROM exposuretemplate"));
                var result = run.ResultFor(TsRowScenarios.GearTemplateNamed(gear, "Ha 300").Id);
                Assert.Equal(12, result.TsId);
                Assert.Equal(new[] { "name" }, result.FieldsWritten);
            }
        }
    }
}
