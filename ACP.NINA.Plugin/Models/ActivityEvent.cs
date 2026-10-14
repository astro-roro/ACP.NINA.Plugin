using Newtonsoft.Json;
using System.Collections.Generic;

namespace ACP.NINA.Plugin.Models {

    /// One event for ACP's Activity tab, in the shape POST /api/activity/events
    /// takes (docs/specs/activity-tab.md in ACP, "Event model").
    ///
    /// Optional fields are left out of the JSON when empty, so the body carries
    /// only what the plugin means to say. ACP fills the rest.
    public class ActivityEvent {

        public const string PluginSource = "plugin";

        /// ACP rejects any other source for a posted event, and "acp" is its own.
        [JsonProperty("source")]
        public string Source { get; set; } = PluginSource;

        /// Unique together with the source. Posting the same key again updates
        /// the event in place.
        [JsonProperty("key")]
        public string Key { get; set; }

        /// The sender's counter for this key. ACP ignores an update that carries
        /// a lower value than the one it stored, so a retry that arrives late
        /// cannot overwrite a newer running total.
        [JsonProperty("seq", NullValueHandling = NullValueHandling.Ignore)]
        public long? Seq { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        /// ISO 8601 with an offset. Required by ACP when needs_you is true or
        /// resolves is not empty, and the plugin sets it on every event.
        [JsonProperty("at", NullValueHandling = NullValueHandling.Ignore)]
        public string At { get; set; }

        /// The observing night, YYYY-MM-DD, local noon cut-over in
        /// Australia/Sydney. Always sent, never left for ACP to derive.
        [JsonProperty("night", NullValueHandling = NullValueHandling.Ignore)]
        public string Night { get; set; }

        /// One plain line, 200 characters at most.
        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("detail", NullValueHandling = NullValueHandling.Ignore)]
        public string Detail { get; set; }

        /// info, warn or error.
        [JsonProperty("severity")]
        public string Severity { get; set; } = SeverityInfo;

        public const string SeverityInfo = "info";
        public const string SeverityWarn = "warn";
        public const string SeverityError = "error";

        /// Only the keys ACP allows (plan_id, project, target, bucket, filter,
        /// ra_deg, dec_deg, aac_job, upload_id, profile, master). Others are
        /// dropped by ACP.
        [JsonProperty("refs", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, object> Refs { get; set; }

        /// Numbers for the UI. Over 8 KB serialised is rejected.
        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, object> Data { get; set; }

        [JsonProperty("needs_you", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool NeedsYou { get; set; }

        [JsonProperty("resolve_key", NullValueHandling = NullValueHandling.Ignore)]
        public string ResolveKey { get; set; }

        [JsonProperty("resolves", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Resolves { get; set; }

        /// True for the events the queue may drop first when it is full:
        /// plain information that nobody has to act on.
        [JsonIgnore]
        public bool IsDroppable =>
            Severity == SeverityInfo
            && !NeedsYou
            && (Resolves == null || Resolves.Count == 0);
    }

    /// ACP's answer to a batch: {stored, updated, unchanged, stale, rejected}.
    public class ActivityPostResult {

        [JsonProperty("stored")] public int Stored { get; set; }
        [JsonProperty("updated")] public int Updated { get; set; }
        [JsonProperty("unchanged")] public int Unchanged { get; set; }
        [JsonProperty("stale")] public int Stale { get; set; }

        /// Events ACP refused one by one. They are logged and never retried.
        [JsonProperty("rejected")]
        public List<ActivityRejection> Rejected { get; set; } = new List<ActivityRejection>();
    }

    public class ActivityRejection {
        [JsonProperty("key")] public string Key { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
    }
}
