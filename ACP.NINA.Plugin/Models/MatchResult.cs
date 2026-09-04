using Newtonsoft.Json;
using System.Collections.Generic;

namespace ACP.NINA.Plugin.Models {

    /// Response from POST /api/plans/match. The shape is fixed by the v3 spec
    /// and by the ACP-side contract being built in parallel; do not reshape it
    /// without changing both sides.
    public class MatchResponse {

        [JsonProperty("fingerprint_id")]
        public string FingerprintId { get; set; }

        [JsonProperty("plans")]
        public List<MatchedPlan> Plans { get; set; } = new List<MatchedPlan>();

        [JsonProperty("summary")]
        public MatchSummary Summary { get; set; } = new MatchSummary();
    }

    /// A plan as returned by the match endpoint: every field GET /api/plans
    /// carries, plus the verdict and the numbers behind it.
    public class MatchedPlan : Plan {

        [JsonProperty("match")]
        public PlanMatch Match { get; set; }
    }

    public class PlanMatch {

        /// One of the four values in MatchVerdict.
        [JsonProperty("verdict")]
        public string Verdict { get; set; }

        [JsonProperty("pixel_scale_ratio")]
        public double? PixelScaleRatio { get; set; }

        [JsonProperty("fov_ratio")]
        public double? FovRatio { get; set; }

        [JsonProperty("filters_missing")]
        public List<string> FiltersMissing { get; set; } = new List<string>();

        /// Human-readable lines explaining the verdict, for the dock and the
        /// sequencer log.
        [JsonProperty("reasons")]
        public List<string> Reasons { get; set; } = new List<string>();
    }

    public class MatchSummary {

        [JsonProperty("fit")]
        public int Fit { get; set; }

        [JsonProperty("fit_with_warnings")]
        public int FitWithWarnings { get; set; }

        [JsonProperty("no_fit")]
        public int NoFit { get; set; }

        [JsonProperty("unconstrained")]
        public int Unconstrained { get; set; }
    }

    /// The four verdicts the ACP side returns. String constants rather than an
    /// enum because an unrecognised verdict from a newer server must not throw
    /// during deserialisation; it falls through to "not a fit" instead.
    public static class MatchVerdict {
        public const string Fit = "fit";
        public const string FitWithWarnings = "fit_with_warnings";
        public const string NoFit = "no_fit";
        public const string Unconstrained = "unconstrained";
    }
}
