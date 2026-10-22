using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NINA.Core.Utility;
using System;
using System.IO;

namespace ACP.NINA.Plugin.Services {

    /// Persistent plugin settings. Stored as JSON at
    /// %localappdata%\NINA\ACP.NINA.Plugin\settings.json, beside NINA's own
    /// Profiles and Logs folders. It is deliberately outside
    /// %localappdata%\NINA\Plugins\, because installing or updating a plugin
    /// replaces its folder there and would wipe the file. Versions up to 3.2
    /// kept it in the plugin folder, so Load copies that file across once if
    /// the new one is missing. Pure POCO + static Load/Save so we can avoid the
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

        /// The binning plans are judged against: the pixel scale sent to ACP is
        /// worked out at this bin factor. It is a setting, not read from the
        /// camera, because the camera is left at whatever the last test frame
        /// used and that says nothing about how the night will be imaged.
        public int ImagingBinning { get; set; } = 1;

        /// The frame Sync for tonight takes to measure focal length. Every
        /// field is optional and null means unset: exposure, filter and
        /// binning then come from NINA's plate solve settings, and gain and
        /// offset from the camera's defaults. The Sync for tonight
        /// instruction's own exposure still wins when it is more than 0.
        public double? SolveExposureSeconds { get; set; }

        /// A filter name from the profile's filter wheel, or null for the
        /// plate solve setting's filter.
        public string SolveFilter { get; set; }

        public int? SolveBinning { get; set; }

        public int? SolveGain { get; set; }

        public int? SolveOffset { get; set; }

        public static int ClampBinning(int value) => Math.Max(1, Math.Min(8, value));

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
        /// Credential Manager, see TokenStore. Settings.json gets copied around
        /// when people move a NINA install between machines, so a token in here
        /// would be a token in a text file.

        private static readonly string LocalAppData =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        private static readonly string SettingsPath = Path.Combine(
            LocalAppData, "NINA", "ACP.NINA.Plugin", "settings.json"
        );

        /// Where 3.2 and earlier kept the file. Read only, for migration.
        private static readonly string LegacySettingsPath = Path.Combine(
            LocalAppData, "NINA", "Plugins", "3.0.0", "ACP.NINA.Plugin", "settings.json"
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

        private static AcpSettings LoadFromDisk() => LoadFromDisk(SettingsPath, LegacySettingsPath);

        /// If the new file exists it wins and the old one is ignored. If only
        /// the old one exists it is copied (not moved, so a rollback to 3.2
        /// still finds it) and then loaded. Any failure falls back to defaults.
        internal static AcpSettings LoadFromDisk(string path, string legacyPath) {
            try {
                if (!File.Exists(path) && !string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath)) {
                    var dir = Path.GetDirectoryName(path);
                    if (!Directory.Exists(dir)) {
                        Directory.CreateDirectory(dir);
                    }
                    File.Copy(legacyPath, path, false);
                    Logger.Info($"ACP: copied settings from {legacyPath} to {path}");
                }
                if (!File.Exists(path)) {
                    return new AcpSettings();
                }
                var json = File.ReadAllText(path);
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
