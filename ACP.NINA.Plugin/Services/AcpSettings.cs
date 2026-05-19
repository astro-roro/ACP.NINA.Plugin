using Newtonsoft.Json;
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
