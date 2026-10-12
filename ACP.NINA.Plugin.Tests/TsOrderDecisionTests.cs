using ACP.NINA.Plugin.Services.TargetScheduler;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The pieces of the order decision that do not need a database, as
    /// tests/test_order_decisions.py runs them in the extension. The golden
    /// scenarios x1 to x3b in TsPushGoldenTests cover the rest.
    public class TsOrderDecisionTests {

        [Fact]
        public void ARowAddedBeforeABaseRowIsAReorder() {
            Assert.True(TsOrder.SameOrder(new[] { "a", "b", "c" }, new[] { "a", "b" }));
            Assert.False(TsOrder.SameOrder(new[] { "c", "a", "b" }, new[] { "a", "b" }));
            Assert.False(TsOrder.SameOrder(new[] { "a", "c", "b" }, new[] { "a", "b" }));
            Assert.True(TsOrder.SameOrder(new[] { "a", "c" }, new[] { "a", "b", "c" }));
            Assert.True(TsOrder.SameOrder(new[] { "a", "b" }, new string[0]));
        }

        [Theory]
        [InlineData("\"x\"")]
        [InlineData("[[0, 1, 2]]")]
        [InlineData("[[0]]")]
        [InlineData("[[\"0\", 1]]")]
        [InlineData("[[0, 1.0]]")]
        [InlineData("[[true, 1]]")]
        [InlineData("[null]")]
        [InlineData("[[0, 2147483648]]")]
        [InlineData("{\"a\": 1}")]
        public void MalformedStoredRowsAreAbsent(string json) {
            Assert.Null(TsOrder.AsRows(JToken.Parse(json)));
        }

        [Fact]
        public void WellFormedStoredRowsAreRead() {
            var rows = TsOrder.AsRows(JToken.Parse("[[0, 1], [1, -1]]"));
            Assert.Equal(new[] { Tuple.Create(0, 1), Tuple.Create(1, -1) }, rows);
            Assert.Empty(TsOrder.AsRows(JToken.Parse("[]")));
            Assert.Null(TsOrder.AsRows(null));
        }
    }
}
