using Newtonsoft.Json;
using System.Collections.Generic;

namespace ACP.NINA.Plugin.Models {

    /// POST /api/ts-templates. Field names are the spec's
    /// (docs/specs/goal-panel-and-ts-order.md section 2 in ACP). ACP keeps the
    /// latest list per profile, so a new post replaces the last one.
    public class TsTemplatesRequest {

        [JsonProperty("profile_id")]
        public string ProfileId { get; set; }

        /// When the list was read, ISO 8601 in UTC.
        [JsonProperty("sent_at")]
        public string SentAt { get; set; }

        [JsonProperty("templates")]
        public List<TsTemplateInfo> Templates { get; set; } = new List<TsTemplateInfo>();
    }

    public class TsTemplateInfo {

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("filter")]
        public string Filter { get; set; }

        [JsonProperty("default_exposure_s")]
        public double DefaultExposureS { get; set; }

        [JsonProperty("gain")]
        public int Gain { get; set; }

        [JsonProperty("offset")]
        public int Offset { get; set; }

        [JsonProperty("bin")]
        public int Bin { get; set; }
    }
}
