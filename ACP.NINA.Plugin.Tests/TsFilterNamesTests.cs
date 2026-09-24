using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// Writing plan filters under the filter wheel's own names.
    ///
    /// Target Scheduler matches a template to a wheel slot by exact name. A rig
    /// whose wheel says H, O and S loaded 47 ACP plans on 2026-09-24 with most
    /// templates asking for Ha, OIII and SII, none of which would ever have run.
    public class TsFilterNamesTests {

        private static readonly string[] SingleLetterWheel = { "L", "R", "G", "B", "H", "O", "S" };

        [Theory]
        [InlineData("OIII", "O")]
        [InlineData("Ha", "H")]
        [InlineData("SII", "S")]
        [InlineData("O3", "O")]
        [InlineData("h-alpha", "H")]
        [InlineData("Lum", "L")]
        [InlineData("L", "L")]
        public void APlanFilterResolvesToTheWheelsSpelling(string plan, string slot) {
            Assert.Equal(slot, TsFilterNames.ResolveToWheel(plan, SingleLetterWheel));
        }

        [Fact]
        public void AnExactSlotBeatsACanonicalOne() {
            // A wheel with both spellings: each plan gets its own slot.
            var wheel = new[] { "H", "Ha" };
            Assert.Equal("Ha", TsFilterNames.ResolveToWheel("Ha", wheel));
            Assert.Equal("H", TsFilterNames.ResolveToWheel("H", wheel));
        }

        [Fact]
        public void CaseIsIgnoredBeforeAliasesAreTried() {
            Assert.Equal("oiii", TsFilterNames.ResolveToWheel("OIII", new[] { "O", "oiii" }));
        }

        [Fact]
        public void AFilterTheWheelDoesNotHaveResolvesToNull() {
            Assert.Null(TsFilterNames.ResolveToWheel("OIII", new[] { "L", "R", "G", "B" }));
            Assert.Null(TsFilterNames.ResolveToWheel("Duo", SingleLetterWheel));
        }

        [Fact]
        public void TheTemplateIsWrittenUnderTheWheelNameAndKeepsItsIdentity() {
            var withWheel = Build(SingleLetterWheel);
            var without = Build(null);

            var byGuidWith = withWheel.Templates.ToDictionary(t => t.Guid);
            var byGuidWithout = without.Templates.ToDictionary(t => t.Guid);

            // Same rows, found by the same guids, so a re-push updates in place.
            Assert.Equal(byGuidWithout.Keys.OrderBy(k => k), byGuidWith.Keys.OrderBy(k => k));
            foreach (var guid in byGuidWith.Keys) {
                Assert.Equal(byGuidWithout[guid].Name, byGuidWith[guid].Name);
            }

            Assert.Equal(new[] { "H", "O" }, withWheel.Templates.Select(t => t.FilterName).OrderBy(n => n));
            Assert.Equal(new[] { "Ha", "OIII" }, without.Templates.Select(t => t.FilterName).OrderBy(n => n));
            Assert.Equal("O", withWheel.FilterRenames["OIII"]);
            Assert.Equal("H", withWheel.FilterRenames["Ha"]);
            Assert.Empty(withWheel.FiltersNotOnWheel);
        }

        [Fact]
        public void AFilterMissingFromTheWheelIsKeptAndReported() {
            var payload = Build(new[] { "L", "R", "G", "B", "H" });

            Assert.Contains("OIII", payload.FiltersNotOnWheel);
            Assert.Contains(payload.Templates, t => t.FilterName == "OIII");
            Assert.Equal("H", payload.FilterRenames["Ha"]);
        }

        [Fact]
        public void NoWheelNamesMeansNoChange() {
            var payload = Build(new string[0]);
            Assert.Empty(payload.FilterRenames);
            Assert.Empty(payload.FiltersNotOnWheel);
        }

        [Fact]
        public void TheSummaryNamesTheRenamesAndTheGaps() {
            var result = new TsPushResult { Success = true, Outcome = new TsSyncOutcome() };
            result.Pushed.Add("NGC 7000");
            result.FilterRenames["OIII"] = "O";
            result.FiltersNotOnWheel.Add("Duo");

            var line = result.Summary();
            Assert.Contains("OIII as O", line);
            Assert.Contains("no slot for Duo", line);
        }

        private static TsSyncPayload Build(IReadOnlyList<string> wheel) {
            return TsConvert.BuildPayload(
                TsTestPlans.ThreePlans(), TsTestPlans.Gear(), TsTestPlans.ProfileId,
                TsTestPlans.FrozenNow, wheel);
        }
    }
}
