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

        /// What ACP actually sends: a two element [width, height] array. The
        /// scalar properties below were written from the API audit and never
        /// appear in a real /api/gear response, so anything reading them alone
        /// got null. Both shapes are read now and TryGetSensorPx prefers this
        /// one.
        [JsonProperty("sensor_px")]
        public int[] SensorPx { get; set; }

        [JsonProperty("sensor_width_px")]
        public int? SensorWidthPx { get; set; }

        [JsonProperty("sensor_height_px")]
        public int? SensorHeightPx { get; set; }

        [JsonProperty("pixel_size_um")]
        public double? PixelSizeUm { get; set; }

        /// Per filter capture settings, keyed by the filter name ACP uses.
        /// The Target Scheduler push reads the exposure template name, default
        /// sub length, gain, offset and binning from here.
        [JsonProperty("filters")]
        public Dictionary<string, CameraFilter> Filters { get; set; }

        /// Sensor size in pixels, from whichever shape the server sent.
        /// False when neither is usable, which the field of view maths reads as
        /// "single panel only".
        public bool TryGetSensorPx(out int width, out int height) {
            if (SensorPx != null && SensorPx.Length >= 2) {
                width = SensorPx[0];
                height = SensorPx[1];
                return width > 0 && height > 0;
            }
            width = SensorWidthPx ?? 0;
            height = SensorHeightPx ?? 0;
            return width > 0 && height > 0;
        }
    }

    /// One filter's capture settings on a camera, from
    /// /api/gear -> cameras[].filters.&lt;name&gt;.
    public class CameraFilter {

        /// The Target Scheduler exposure template to name. Null means the push
        /// makes one up from the filter and camera names.
        [JsonProperty("ts_template_name")]
        public string TsTemplateName { get; set; }

        [JsonProperty("ts_template_id")]
        public int? TsTemplateId { get; set; }

        [JsonProperty("default_sub_s")]
        public double? DefaultSubS { get; set; }

        /// -1 means "leave it to the camera default", which is what Target
        /// Scheduler stores for an unset value.
        [JsonProperty("gain")]
        public int? Gain { get; set; }

        [JsonProperty("offset")]
        public int? Offset { get; set; }

        [JsonProperty("bin")]
        public int? Bin { get; set; }
    }
}
