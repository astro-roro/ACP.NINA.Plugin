using ACP.NINA.Plugin.Services.TargetScheduler;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// Dithers in a generated override order when the project's own "dither
    /// every" is off. Target Scheduler shoots an override exactly as written,
    /// so without these the PNe tiles (OIII first by override) never dithered
    /// although their templates say every 2 (14 Oct 2026).
    public class OverrideDitherTests {

        private static List<Tuple<int, int>> Block(params int[] refs) =>
            refs.Select(r => Tuple.Create(TsOrder.Exposure, r)).ToList();

        private static string Show(List<Tuple<int, int>> rows, IList<string> filterOf) =>
            string.Join(" ", rows.Select(r => r.Item1 == TsOrder.Exposure ? filterOf[r.Item2] : "D"));

        [Fact]
        public void TheSameCountForEveryFilterMatchesTheProjectRule() {
            var filterOf = new List<string> { "H", "O" };
            var items = Block(1, 1, 1, 1, 0, 0, 0, 0);
            var project = TsOrder.InjectDithers(items, filterOf, 2);
            var perFilter = TsOrder.InjectDithers(items, filterOf, _ => 2);
            Assert.Equal(Show(project, filterOf), Show(perFilter, filterOf));
            Assert.Contains("D", Show(perFilter, filterOf));
        }

        [Fact]
        public void AFilterWithNoCountNeverCallsForADither() {
            var filterOf = new List<string> { "H", "O" };
            var items = Block(1, 1, 0, 0, 0, 0, 0, 0);
            var rows = TsOrder.InjectDithers(items, filterOf, f => f == "O" ? 1 : 0);
            var shown = Show(rows, filterOf);
            // Every dither follows an O: the H run of six is never broken up.
            Assert.Contains("H H H H H H", shown);
            Assert.True(rows.Count(r => r.Item1 != TsOrder.Exposure) >= 1, shown);
        }

        [Fact]
        public void NoCountsAtAllLeavesTheOrderAlone() {
            var filterOf = new List<string> { "H", "O" };
            var items = Block(1, 0);
            Assert.Equal(items, TsOrder.InjectDithers(items, filterOf, _ => 0));
        }

        [Fact]
        public void AProjectCountOfZeroStillLeavesTheOrderAlone() {
            var filterOf = new List<string> { "H", "O" };
            var items = Block(1, 1, 1, 0);
            Assert.Equal(items, TsOrder.InjectDithers(items, filterOf, 0));
        }
    }
}
