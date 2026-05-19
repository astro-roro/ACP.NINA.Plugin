using Newtonsoft.Json;
using System.Collections.Generic;

namespace ACP.NINA.Plugin.Models {

    /// Response from POST /api/ext/nina-ts-sync/sync. Wraps the SyncReport
    /// the Python extension produces, plus the sibling backup file paths it
    /// wrote (db backup + plans.json backup) so the user can recover if they
    /// regret the push.
    public class TsSyncResponse {
        [JsonProperty("db_path")] public string DbPath { get; set; }
        [JsonProperty("backup_path")] public string BackupPath { get; set; }
        [JsonProperty("plans_path")] public string PlansPath { get; set; }
        [JsonProperty("plans_backup_path")] public string PlansBackupPath { get; set; }
        [JsonProperty("report")] public SyncReport Report { get; set; }
    }

    public class SyncReport {
        [JsonProperty("exposuretemplate")] public TableCounts ExposureTemplate { get; set; }
        [JsonProperty("project")] public TableCounts Project { get; set; }
        [JsonProperty("target")] public TableCounts Target { get; set; }
        [JsonProperty("exposureplan")] public TableCounts ExposurePlan { get; set; }
        [JsonProperty("ruleweight_seeded_projects")] public int RuleWeightSeededProjects { get; set; }
        [JsonProperty("notes")] public List<string> Notes { get; set; } = new List<string>();

        /// Compact one-liner suitable for the dockable's footer.
        public string ToShortString() {
            return $"Synced: " +
                   $"{Project?.Inserted ?? 0}+{Project?.Updated ?? 0} projects, " +
                   $"{Target?.Inserted ?? 0}+{Target?.Updated ?? 0} targets, " +
                   $"{ExposurePlan?.Inserted ?? 0}+{ExposurePlan?.Updated ?? 0} exposure plans, " +
                   $"{ExposureTemplate?.Inserted ?? 0}+{ExposureTemplate?.Updated ?? 0} templates " +
                   $"(inserted+updated)";
        }
    }

    public class TableCounts {
        [JsonProperty("inserted")] public int Inserted { get; set; }
        [JsonProperty("updated")] public int Updated { get; set; }
        [JsonProperty("claimed")] public int Claimed { get; set; }
    }
}
