using ACP.NINA.Plugin.Services.TargetScheduler;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The one filter vocabulary for filter rows. Fixtures/row_filter_cases.json
    /// is copied from the Python extension's tests/fixtures, and ACP's
    /// migration tests the same table, so a goal key or a TS filtername
    /// becomes the same row filter in every place that reads it.
    public class TsRowFilterTests {

        public static IEnumerable<object[]> Cases() {
            var root = JObject.Parse(TsFixtures.ReadFixture("row_filter_cases.json"));
            return ((JArray)root["cases"]).Select(c => new object[] { (string)c[0], (string)c[1] });
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void EveryCaseInTheSharedTable(string raw, string expected) {
            Assert.Equal(expected, TsRows.RowFilter(raw));
        }

        [Fact]
        public void ADifferenceOnlyCanonicalisationMakesIsTheSameFilter() {
            Assert.True(TsRows.SameFilter("H", "Ha"));
            Assert.True(TsRows.SameFilter("R Stars", "R"));
            Assert.False(TsRows.SameFilter("Antlia Ha", "Ha"));
        }
    }
}
