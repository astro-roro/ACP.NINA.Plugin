using Newtonsoft.Json;
using System.Collections.Generic;

namespace ACP.NINA.Plugin.Models {

    /// Plan record from ACP's GET /api/plans response. JSON shape mirrors the
    /// schema documented in docs/nina-plugin-api-audit.md (mixed int/float
    /// numerics, optional fields, ts_base_snapshot + ts_refs only when synced).
    public class Plan {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("guid")]
        public string Guid { get; set; }

        [JsonProperty("project_name")]
        public string ProjectName { get; set; }

        [JsonProperty("state")]
        public string State { get; set; }

        [JsonProperty("priority")]
        public string Priority { get; set; }

        [JsonProperty("min_altitude_deg")]
        public double? MinAltitudeDeg { get; set; }

        [JsonProperty("meridian_window_min")]
        public int? MeridianWindowMin { get; set; }

        [JsonProperty("telescope_id")]
        public string TelescopeId { get; set; }

        [JsonProperty("camera_id")]
        public string CameraId { get; set; }

        [JsonProperty("target")]
        public PlanTarget Target { get; set; }

        [JsonProperty("filter_goals")]
        public Dictionary<string, FilterGoal> FilterGoals { get; set; }

        [JsonProperty("last_synced_at")]
        public string LastSyncedAt { get; set; }
    }

    public class PlanTarget {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("center_ra_deg")]
        public double CenterRaDeg { get; set; }

        [JsonProperty("center_dec_deg")]
        public double CenterDecDeg { get; set; }

        [JsonProperty("rotation_deg")]
        public double RotationDeg { get; set; }

        [JsonProperty("mosaic")]
        public Mosaic Mosaic { get; set; }
    }

    public class Mosaic {
        [JsonProperty("rows")]
        public int Rows { get; set; } = 1;

        [JsonProperty("cols")]
        public int Cols { get; set; } = 1;

        [JsonProperty("overlap_pct")]
        public double OverlapPct { get; set; }
    }

    public class FilterGoal {
        [JsonProperty("target_hours")]
        public double TargetHours { get; set; }

        [JsonProperty("actual_hours")]
        public double? ActualHours { get; set; }

        [JsonProperty("sub_exposure_s")]
        public double? SubExposureS { get; set; }
    }

    public class PlansResponse {
        [JsonProperty("version")]
        public int Version { get; set; }

        [JsonProperty("plans")]
        public List<Plan> Plans { get; set; } = new List<Plan>();
    }
}
