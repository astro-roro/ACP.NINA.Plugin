using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace ACP.NINA.Plugin.Models {

    /// One filter's worth of acquired progress, as ACP's
    /// POST /api/plans/<id>/progress wants it.
    ///
    /// ACP validates acquired_count but stores only the hours: its own
    /// /api/sync derives a sub count back from hours and the goal's sub
    /// exposure. We still send the count because it is what makes a
    /// disagreement between the two sides readable in a log.
    public class ProgressFilter {

        [JsonProperty("acquired_hours")]
        public double AcquiredHours { get; set; }

        [JsonProperty("acquired_count")]
        public int AcquiredCount { get; set; }
    }

    /// The request body for POST /api/plans/<id>/progress.
    ///
    /// "source" is always "ts" from this plugin. ACP uses it to tell plugin
    /// reports apart from the Python extension's sync-acquired, which can be
    /// running against the same plan on a same machine setup.
    public class ProgressRequest {

        [JsonProperty("filters")]
        public Dictionary<string, ProgressFilter> Filters { get; set; }
            = new Dictionary<string, ProgressFilter>(StringComparer.OrdinalIgnoreCase);

        [JsonProperty("source")]
        public string Source { get; set; } = "ts";

        [JsonProperty("at")]
        public string At { get; set; }

        /// Left out of the wire body unless it is actually set. ACP never
        /// lowers a stored actual_hours without it, and this plugin never
        /// sets it: a count that went backwards in TS is a culled frame or a
        /// reset project, and rewinding a plan the user has watched fill up
        /// is worse than being one session stale.
        [JsonProperty("force", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Force { get; set; }
    }

    /// The response ACP sends back. "updated" is what actually moved,
    /// "unknown_filters" is what ACP dropped because the plan has no goal for
    /// it, and "not_lowered" names goals whose stored value was already higher
    /// than what we reported.
    public class ProgressResponse {

        [JsonProperty("ok")]
        public bool Ok { get; set; }

        [JsonProperty("updated")]
        public Dictionary<string, double> Updated { get; set; }

        [JsonProperty("unknown_filters")]
        public List<string> UnknownFilters { get; set; }

        [JsonProperty("not_lowered")]
        public List<string> NotLowered { get; set; }

        /// A one line summary for the dock footer and the NINA log.
        public string ToShortString() {
            var updated = Updated == null || Updated.Count == 0
                ? "nothing changed"
                : string.Join(", ", Describe());
            var dropped = UnknownFilters == null || UnknownFilters.Count == 0
                ? string.Empty
                : $"; no goal for {string.Join(", ", UnknownFilters)}";
            var held = NotLowered == null || NotLowered.Count == 0
                ? string.Empty
                : $"; kept the higher stored hours for {string.Join(", ", NotLowered)}";
            return updated + dropped + held;
        }

        private IEnumerable<string> Describe() {
            foreach (var kv in Updated) {
                yield return $"{kv.Key} {kv.Value:0.##}h";
            }
        }
    }
}
