using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NINA.Core.Utility;
using System;
using System.IO;

namespace ACP.NINA.Plugin.Services {

    /// Persistent plugin settings. Stored as JSON under
    /// %localappdata%\NINA\Plugins\3.0.0\ACP.NINA.Plugin\settings.json — same
    /// directory NINA deploys the plugin DLL to, so it travels with the
    /// installation. Pure POCO + static Load/Save so we can avoid the
    /// auto-generated Properties\Settings.Designer.cs dance (which requires
    /// Visual Studio's SettingsSingleFileGenerator).
    public class AcpSettings {
        public string ServerUrl { get; set; } = "http://127.0.0.1:5555";
        public bool AutoRefreshEnabled { get; set; } = false;
        public int AutoRefreshSeconds { get; set; } = 30;
        public bool ConfirmBeforeTsSync { get; set; } = false;

        /// Which plans to load into Target Scheduler. Everything by default,
        /// per the v3 spec: the fingerprint is still built and the focal length
        /// write-back still runs, nothing is filtered.
        [JsonConverter(typeof(StringEnumConverter))]
        public SyncMode SyncMode { get; set; } = SyncMode.Everything;

        /// Write the solved focal length back into the active profile when it
        /// differs from the profile by more than the threshold. The sequencer
        /// instruction has its own per-instruction checkbox; this is the
        /// default that checkbox starts from and what the dock button uses.
        public bool ProfileWriteBackEnabled { get; set; } = true;

        /// Deliberately absent: the bearer token. It lives in Windows
        /// Credential Manager, see TokenStore. Settings.json travels with the
        /// plugin folder, so a token in here would be a token in a text file.

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "Plugins", "3.0.0", "ACP.NINA.Plugin", "settings.json"
        );

        public static AcpSettings Load() {
            try {
                if (!File.Exists(SettingsPath)) {
                    return new AcpSettings();
                }
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonConvert.DeserializeObject<AcpSettings>(json);
                return loaded ?? new AcpSettings();
            } catch (Exception ex) {
                Logger.Error($"ACP: failed to load settings, using defaults: {ex.Message}");
                return new AcpSettings();
            }
        }

        public void Save() {
            try {
                var dir = Path.GetDirectoryName(SettingsPath);
                if (!Directory.Exists(dir)) {
                    Directory.CreateDirectory(dir);
                }
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(SettingsPath, json);
            } catch (Exception ex) {
                Logger.Error($"ACP: failed to save settings: {ex.Message}");
            }
        }
    }
}
