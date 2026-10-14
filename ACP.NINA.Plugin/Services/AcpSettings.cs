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

        /// How many frames a Sync for tonight solve takes before giving up.
        /// NINA's own count is meant for centring, where ten tries under
        /// passing cloud is right. Here a failed solve usually means the cover
        /// is closed or the sky is gone, and ten tries just holds the camera.
        public int SolveAttempts { get; set; } = 3;

        /// The longest any ACP action may run, in minutes, before it is
        /// stopped as if Stop had been pressed: the dock buttons and the
        /// sequence instruction alike. 0 means no limit.
        public int ActionTimeoutMinutes { get; set; } = 10;

        public static int ClampSolveAttempts(int value) => Math.Max(1, Math.Min(20, value));

        public static int ClampTimeoutMinutes(int value) => Math.Max(0, Math.Min(240, value));

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

        /// Send acquired hours back to ACP while Target Scheduler is imaging.
        ///
        /// On by default, per the v3 spec. It is the thing that keeps ACP's
        /// actual_hours from going stale. Turning it off costs Target Scheduler
        /// nothing: ordinary pushes never change acquired or accepted on an
        /// existing row, and the one write, AAC's accepted figures, is skipped
        /// once Target Scheduler has saved frames since the night they cover.
        ///
        /// Reasons to turn it off are having ACP on a machine this one cannot
        /// reach, or already running the Python extension's sync-acquired
        /// against the same plans. The two can coexist, since ACP takes the
        /// higher number either way, but there is no point paying for both.
        public bool ReportProgressToAcp { get; set; } = true;

        /// Deliberately absent: the bearer token. It lives in Windows
        /// Credential Manager, see TokenStore. Settings.json travels with the
        /// plugin folder, so a token in here would be a token in a text file.

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "Plugins", "3.0.0", "ACP.NINA.Plugin", "settings.json"
        );

        /// The most recently loaded or saved instance. The options page and
        /// the dock each load their own copy at startup, and the options page
        /// is the one that saves, so anything in the dock that wants the
        /// current value reads this rather than its startup copy.
        public static AcpSettings Current { get; private set; }

        /// Raised after Save() so the dock can refresh what it shows.
        public static event Action Saved;

        public static AcpSettings Load() {
            var loaded = LoadFromDisk();
            Current = loaded;
            return loaded;
        }

        private static AcpSettings LoadFromDisk() {
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
            Current = this;
            try {
                Saved?.Invoke();
            } catch (Exception ex) {
                Logger.Warning($"ACP: a settings listener failed: {ex.Message}");
            }
        }
    }
}
