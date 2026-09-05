using Newtonsoft.Json;

namespace ACP.NINA.Plugin.Models {

    /// Response from GET /api/version. The dock polls this every 60 seconds and
    /// only refetches plans when plans_last_modified changes, so a dock left
    /// open all night costs one small request a minute rather than a full plan
    /// and gear fetch.
    public class VersionInfo {

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("api_version")]
        public int ApiVersion { get; set; }

        /// Opaque change marker. Compared as a string, never parsed; the server
        /// is free to make it a timestamp, an mtime or a hash.
        [JsonProperty("plans_last_modified")]
        public string PlansLastModified { get; set; }
    }
}
