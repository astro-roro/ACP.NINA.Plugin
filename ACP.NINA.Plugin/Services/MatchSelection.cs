using ACP.NINA.Plugin.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ACP.NINA.Plugin.Services {

    /// What the two modes actually do with the match verdicts, and the words
    /// that get reported afterwards.
    ///
    /// Pure and static, with no NINA and no HTTP, because this is the decision
    /// the whole "Two modes, one switch" section of the spec comes down to and
    /// it should not need a telescope to check.
    public static class MatchSelection {

        /// The plans to load, given the mode.
        ///
        /// Everything takes the lot. Only what fits takes the fit verdicts plus
        /// the unconstrained ones, which are the plans with no gear recorded:
        /// the spec says a plan with no gear set is synced in both modes.
        ///
        /// An unrecognised verdict from a newer server is treated as not a fit,
        /// so a server that grows a fifth verdict cannot quietly widen what a
        /// user asked to narrow.
        public static List<MatchedPlan> SelectForMode(MatchResponse response, SyncMode mode) {
            var plans = response?.Plans ?? new List<MatchedPlan>();
            if (mode == SyncMode.Everything) {
                return plans.ToList();
            }
            return plans.Where(p => IsFit(p?.Match?.Verdict)).ToList();
        }

        public static bool IsFit(string verdict) {
            return verdict == MatchVerdict.Fit || verdict == MatchVerdict.Unconstrained;
        }

        /// The plans that do not suit tonight. In Everything mode these are
        /// still loaded, and the spec asks for one warning line naming them.
        public static List<MatchedPlan> Misfits(MatchResponse response) {
            var plans = response?.Plans ?? new List<MatchedPlan>();
            return plans.Where(p => !IsFit(p?.Match?.Verdict)).ToList();
        }

        /// The line the sequencer log and the dock both report. One sentence
        /// for what was loaded, one for what did not suit, and nothing at all
        /// about misfits when there are none.
        public static string Summarise(MatchResponse response, SyncMode mode) {
            var selected = SelectForMode(response, mode);
            var misfits = Misfits(response);

            if ((response?.Plans?.Count ?? 0) == 0) {
                return "ACP returned no plans to consider.";
            }

            string line;
            if (selected.Count == 0) {
                line = "Nothing in ACP fits tonight's gear, so Target Scheduler is left as it was.";
            } else if (mode == SyncMode.Everything) {
                line = $"{selected.Count} {Plural(selected.Count)} to load.";
            } else {
                line = $"{selected.Count} of {response.Plans.Count} {Plural(response.Plans.Count)} fit tonight's gear.";
            }

            if (misfits.Count > 0) {
                var named = string.Join(", ", misfits.Take(5).Select(Describe));
                var andMore = misfits.Count > 5 ? $", and {misfits.Count - 5} more" : string.Empty;
                var verb = mode == SyncMode.Everything
                    ? "Loaded anyway but not suited to tonight"
                    : "Left out";
                line += $" {verb}: {named}{andMore}.";
            }

            return line;
        }

        private static string Plural(int count) {
            return count == 1 ? "plan" : "plans";
        }

        /// A misfit named with the first reason ACP gave, so the user can see
        /// why without opening the web UI.
        private static string Describe(MatchedPlan plan) {
            var name = plan?.Target?.Name ?? plan?.ProjectName ?? plan?.Id ?? "an unnamed plan";
            var reason = plan?.Match?.Reasons?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(reason)) {
                var missing = plan?.Match?.FiltersMissing;
                if (missing != null && missing.Count > 0) {
                    reason = $"no {string.Join(" or ", missing)} filter";
                }
            }
            return string.IsNullOrWhiteSpace(reason) ? name : $"{name} ({reason})";
        }
    }
}
