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

        /// exposuretemplate.Id, and the guid on the row. Both are what ACP
        /// needs to link a gear template to its TS row. A list whose
        /// templates carry no id comes from an older plugin and never
        /// triggers adoption or Tidy.
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("guid")]
        public string Guid { get; set; }

        /// The columns Tidy compares when it shows what a choice drops.
        [JsonProperty("moonavoidanceenabled")]
        public int MoonAvoidanceEnabled { get; set; }

        [JsonProperty("moonavoidanceseparation")]
        public double MoonAvoidanceSeparation { get; set; }

        [JsonProperty("moonavoidancewidth")]
        public int MoonAvoidanceWidth { get; set; }

        [JsonProperty("twilightlevel")]
        public int TwilightLevel { get; set; }

        [JsonProperty("maximumhumidity")]
        public double MaximumHumidity { get; set; }

        [JsonProperty("ditherevery")]
        public int DitherEvery { get; set; }

        /// Every exposure plan using the template, enabled or not, at most
        /// 5000. ACP reads `exposure` to tell a plan that follows the
        /// template's default (-1) from one with its own.
        [JsonProperty("plans")]
        public List<TsTemplatePlan> Plans { get; set; } = new List<TsTemplatePlan>();
    }

    public class TsTemplatePlan {

        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("exposure")]
        public double Exposure { get; set; }
    }
}
