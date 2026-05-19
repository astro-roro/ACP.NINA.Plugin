using Newtonsoft.Json;
using System.Collections.Generic;

namespace ACP.NINA.Plugin.Models {

    /// Gear records from ACP's GET /api/gear response. Plans reference these by
    /// opaque IDs (`telescope_id`, `camera_id`); plugin joins client-side.
    public class GearResponse {
        [JsonProperty("version")]
        public int Version { get; set; }

        [JsonProperty("telescopes")]
        public List<Telescope> Telescopes { get; set; } = new List<Telescope>();

        [JsonProperty("cameras")]
        public List<Camera> Cameras { get; set; } = new List<Camera>();
    }

    public class Telescope {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("focal_length_mm")]
        public double? FocalLengthMm { get; set; }

        [JsonProperty("aperture_mm")]
        public double? ApertureMm { get; set; }
    }

    public class Camera {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("sensor_width_px")]
        public int? SensorWidthPx { get; set; }

        [JsonProperty("sensor_height_px")]
        public int? SensorHeightPx { get; set; }

        [JsonProperty("pixel_size_um")]
        public double? PixelSizeUm { get; set; }
    }
}
