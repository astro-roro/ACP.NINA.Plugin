using Newtonsoft.Json;
using System.Collections.Generic;

namespace ACP.NINA.Plugin.Models {

    /// The gear fingerprint from Part B of the v3 spec. Built fresh from the
    /// connected hardware every time it is used, never cached across sessions,
    /// and sent as the body of POST /api/plans/match.
    ///
    /// Property names are fixed by the spec's JSON, not by C# convention, so
    /// every one carries an explicit JsonProperty.
    public class Fingerprint {

        [JsonProperty("camera")]
        public FingerprintCamera Camera { get; set; }

        /// Filter wheel slot names in slot order, or empty when no wheel is
        /// connected. Empty plus a colour camera reads as OSC on the ACP side.
        [JsonProperty("filters")]
        public List<string> Filters { get; set; } = new List<string>();

        [JsonProperty("mount")]
        public FingerprintMount Mount { get; set; }

        [JsonProperty("site")]
        public FingerprintSite Site { get; set; }

        [JsonProperty("focal_length_mm")]
        public FingerprintFocalLength FocalLengthMm { get; set; }

        /// Arcseconds per pixel from the plate solve, null when nothing has
        /// been solved this session.
        [JsonProperty("pixel_scale_arcsec")]
        public double? PixelScaleArcsec { get; set; }

        /// Solved camera angle, used to preset the framing rotation.
        [JsonProperty("rotation_deg")]
        public double? RotationDeg { get; set; }

        [JsonProperty("profile_name")]
        public string ProfileName { get; set; }

        [JsonProperty("nina_version")]
        public string NinaVersion { get; set; }

        /// Set by the caller, not by the fingerprint builder: "everything" or
        /// "fit". Only meaningful on the match request.
        [JsonProperty("mode", NullValueHandling = NullValueHandling.Ignore)]
        public string Mode { get; set; }
    }

    public class FingerprintCamera {

        [JsonProperty("name")]
        public string Name { get; set; }

        /// Unbinned sensor size in pixels, width then height. The spec sends
        /// unbinned values plus the bin factor separately so the ACP side can
        /// reason about the sensor rather than tonight's binning choice.
        [JsonProperty("sensor_px")]
        public int[] SensorPx { get; set; }

        [JsonProperty("pixel_size_um")]
        public double PixelSizeUm { get; set; }

        /// True when the camera reports a Bayer pattern, meaning anything other
        /// than a monochrome sensor type.
        [JsonProperty("colour")]
        public bool Colour { get; set; }

        /// Current bin factor, 1 when unbinned.
        [JsonProperty("binning")]
        public int Binning { get; set; } = 1;
    }

    public class FingerprintMount {

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class FingerprintSite {

        [JsonProperty("lat")]
        public double Lat { get; set; }

        [JsonProperty("lon")]
        public double Lon { get; set; }

        [JsonProperty("elev_m")]
        public double ElevM { get; set; }
    }

    public class FingerprintFocalLength {

        /// What the active NINA profile says, which is the value the spec
        /// expects to be wrong.
        [JsonProperty("profile")]
        public double Profile { get; set; }

        /// Derived from the plate solve, null when nothing has been solved.
        [JsonProperty("solved")]
        public double? Solved { get; set; }

        /// "solved" or "profile", naming which of the two the rest of the
        /// fingerprint was computed from.
        [JsonProperty("source")]
        public string Source { get; set; }

        /// The value everything downstream should use.
        [JsonIgnore]
        public double Effective => Solved ?? Profile;
    }
}
