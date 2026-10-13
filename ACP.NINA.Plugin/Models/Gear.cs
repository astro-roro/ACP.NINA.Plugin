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

        /// The filter's gear templates, from an ACP that has them
        /// (docs/specs/gear-templates.md in ACP). Null or empty means today's
        /// ACP: the scalar properties above describe the one template, and the
        /// push looks it up by name. Only a filter with templates is written
        /// by the gear template writer.
        [JsonProperty("templates", NullValueHandling = NullValueHandling.Ignore)]
        public List<GearTemplate> Templates { get; set; }
    }

    /// One gear template: ACP's record of a Target Scheduler exposure
    /// template (docs/specs/gear-templates.md section 3).
    public class GearTemplate {

        /// Stable UUID, never reused, survives renames.
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        /// The default sub in seconds, written as defaultexposure.
        [JsonProperty("sub_s")]
        public double SubS { get; set; }

        [JsonProperty("gain")]
        public int Gain { get; set; } = -1;

        [JsonProperty("offset")]
        public int Offset { get; set; } = -1;

        [JsonProperty("bin")]
        public int Bin { get; set; } = 1;

        [JsonProperty("starred")]
        public bool Starred { get; set; }

        /// Earlier names, newest first. Used to find this template's TS row
        /// after a rename.
        [JsonProperty("aliases")]
        public List<string> Aliases { get; set; } = new List<string>();

        /// Names of other TS templates in this template's group. Never used
        /// to look up a TS row.
        [JsonProperty("ts_names")]
        public List<string> TsNames { get; set; } = new List<string>();

        /// Keyed by profile id.
        [JsonProperty("ts")]
        public Dictionary<string, GearTemplateLink> Ts { get; set; } = new Dictionary<string, GearTemplateLink>();

        /// The link for a profile, matched ignoring case, or null.
        public GearTemplateLink LinkFor(string profileId) {
            if (Ts == null || string.IsNullOrWhiteSpace(profileId)) return null;
            foreach (var pair in Ts) {
                if (string.Equals(pair.Key, profileId, System.StringComparison.OrdinalIgnoreCase)) return pair.Value;
            }
            return null;
        }
    }

    /// `template.ts[P]`: what ACP knows about a gear template's counterpart
    /// in one profile.
    public class GearTemplateLink {

        [JsonProperty("id")]
        public int? Id { get; set; }

        [JsonProperty("guid")]
        public string Guid { get; set; }

        [JsonProperty("pushed_iso")]
        public string PushedIso { get; set; }

        /// The values TS held at the last push, pull or adoption.
        [JsonProperty("base")]
        public GearTemplateBase Base { get; set; }

        /// TS ids whose ACP-linked plans the next push moves onto this
        /// template.
        [JsonProperty("repoint_from")]
        public List<int> RepointFrom { get; set; } = new List<int>();
    }

    public class GearTemplateBase {

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("filtername")]
        public string FilterName { get; set; }

        [JsonProperty("defaultexposure")]
        public double? DefaultExposure { get; set; }

        [JsonProperty("gain")]
        public int? Gain { get; set; }

        [JsonProperty("offset")]
        public int? Offset { get; set; }

        [JsonProperty("bin")]
        public int? Bin { get; set; }
    }
}
