using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace ACP.NINA.Plugin.Models {

    /// The `meta` part of POST /api/ext/nina-ts-sync/import/uploads. Field
    /// names are the spec's (docs/specs/ts-upload-import.md in ACP).
    public class TsUploadMeta {

        /// The NINA profile the preview is for. TS stores every row under the
        /// profile GUID, so this names the rig.
        [JsonProperty("profile_id")]
        public string ProfileId { get; set; }

        /// Every NINA profile on this machine, so ACP can name the profiles it
        /// finds in the file. Brutix has no NINA profile files to read.
        [JsonProperty("profiles")]
        public List<TsUploadProfile> Profiles { get; set; } = new List<TsUploadProfile>();

        /// SHA-256 of the file, lowercase hex. ACP refuses the upload when the
        /// bytes it received do not hash to this.
        [JsonProperty("sha256")]
        public string Sha256 { get; set; }

        [JsonProperty("user_version")]
        public int UserVersion { get; set; }

        [JsonProperty("plugin_version")]
        public string PluginVersion { get; set; }

        [JsonProperty("machine")]
        public string Machine { get; set; }
    }

    public class TsUploadProfile {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    /// ACP's 202 answer to an upload.
    public class TsUploadAccepted {
        [JsonProperty("upload_id")]
        public string UploadId { get; set; }

        [JsonProperty("status_url")]
        public string StatusUrl { get; set; }

        [JsonProperty("review_url")]
        public string ReviewUrl { get; set; }
    }

    /// GET /import/uploads/<id>, reduced to what the dock shows.
    ///
    /// The fields agreed with the extension are `state`, `error`,
    /// `review_url` and `counts` holding `new`, `updated` and `conflicts`.
    /// A reply with no counts still gives a state, and the dock then shows
    /// the button without numbers.
    public class TsUploadStatus {

        public const string Checking = "checking";
        public const string Ready = "ready";
        public const string Failed = "failed";
        public const string Superseded = "superseded";
        public const string Applied = "applied";

        public string State { get; set; }
        public string Error { get; set; }
        public string ReviewUrl { get; set; }
        public int? New { get; set; }
        public int? Updated { get; set; }
        public int? Conflicts { get; set; }

        public bool HasCounts => New.HasValue || Updated.HasValue || Conflicts.HasValue;

        public static TsUploadStatus Parse(string json) {
            var status = new TsUploadStatus();
            JObject root;
            try {
                root = JObject.Parse(json ?? "{}");
            } catch (JsonException) {
                return status;
            }

            status.State = ((string)(root["state"] as JValue))?.Trim().ToLowerInvariant();
            status.Error = (string)(root["error"] as JValue);
            status.ReviewUrl = (string)(root["review_url"] as JValue);

            var counts = root["counts"] as JObject;
            status.New = IntOrNull(counts?["new"]);
            status.Updated = IntOrNull(counts?["updated"]);
            status.Conflicts = IntOrNull(counts?["conflicts"]);
            return status;
        }

        private static int? IntOrNull(JToken v) {
            return v != null && v.Type == JTokenType.Integer ? (int?)(int)v : null;
        }
    }

    /// POST /api/ext/nina-ts-sync/links.
    public class TsLinksRequest {
        [JsonProperty("profile_id")]
        public string ProfileId { get; set; }

        [JsonProperty("machine")]
        public string Machine { get; set; }

        [JsonProperty("states")]
        public List<TsLinkState> States { get; set; } = new List<TsLinkState>();
    }

    public class TsLinkState {
        [JsonProperty("plan_id")]
        public string PlanId { get; set; }

        [JsonProperty("refs")]
        public JObject Refs { get; set; }

        [JsonProperty("base_snapshot")]
        public JObject BaseSnapshot { get; set; }
    }
}
