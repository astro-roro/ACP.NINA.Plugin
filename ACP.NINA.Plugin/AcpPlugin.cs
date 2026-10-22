using ACP.NINA.Plugin.Services;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using RelayCommand = CommunityToolkit.Mvvm.Input.RelayCommand;

namespace ACP.NINA.Plugin {

    /// Plugin entry point. NINA discovers this via the [Export(typeof(IPluginManifest))]
    /// attribute, instantiates it with the dependencies declared in
    /// [ImportingConstructor], and binds it as the DataContext of the
    /// Options.xaml DataTemplate (key "Astro Coverage Planner (ACP)_Options").
    ///
    /// Properties exposed here are the Options-page surface: server URL,
    /// auto-refresh settings, etc. Daily-driver UI lives in AcpDockableVM.
    [Export(typeof(IPluginManifest))]
    public class AcpPlugin : PluginBase, INotifyPropertyChanged {

        private readonly IProfileService profileService;
        private readonly IFramingAssistantVM framingAssistantVM;
        private AcpSettings settings;

        [ImportingConstructor]
        public AcpPlugin(IProfileService profileService, IFramingAssistantVM framingAssistantVM) {
            this.profileService = profileService;
            this.framingAssistantVM = framingAssistantVM;

            settings = AcpSettings.Load();

            TestConnectionCommand = new RelayCommand(async () => await TestConnectionAsync());
            ClearTokenCommand = new RelayCommand(ClearToken);

            tokenStatus = DescribeTokenState();
        }

        public override Task Initialize() {
            Logger.Info($"ACP plugin initialized — server URL: {settings.ServerUrl}");
            return Task.CompletedTask;
        }

        public override Task Teardown() {
            settings.Save();
            // End an open session and flush queued activity events before NINA
            // goes. The dock's Dispose does the same; it is safe to do twice.
            ActivityHub.Shutdown();
            Logger.Info("ACP plugin teardown");
            return Task.CompletedTask;
        }

        // ── Options-page bindings ─────────────────────────────────────────────

        public string ServerUrl {
            get => settings.ServerUrl;
            set {
                if (settings.ServerUrl == value) return;
                settings.ServerUrl = value;
                settings.Save();
                RaisePropertyChanged();
            }
        }

        public bool AutoRefreshEnabled {
            get => settings.AutoRefreshEnabled;
            set {
                if (settings.AutoRefreshEnabled == value) return;
                settings.AutoRefreshEnabled = value;
                settings.Save();
                RaisePropertyChanged();
            }
        }

        public int AutoRefreshSeconds {
            get => settings.AutoRefreshSeconds;
            set {
                if (settings.AutoRefreshSeconds == value) return;
                settings.AutoRefreshSeconds = value;
                settings.Save();
                RaisePropertyChanged();
            }
        }

        public bool ConfirmBeforeTsSync {
            get => settings.ConfirmBeforeTsSync;
            set {
                if (settings.ConfirmBeforeTsSync == value) return;
                settings.ConfirmBeforeTsSync = value;
                settings.Save();
                RaisePropertyChanged();
            }
        }

        public int SolveAttempts {
            get => settings.SolveAttempts;
            set {
                var clamped = AcpSettings.ClampSolveAttempts(value);
                if (settings.SolveAttempts == clamped) return;
                settings.SolveAttempts = clamped;
                settings.Save();
                RaisePropertyChanged();
            }
        }

        public int ActionTimeoutMinutes {
            get => settings.ActionTimeoutMinutes;
            set {
                var clamped = AcpSettings.ClampTimeoutMinutes(value);
                if (settings.ActionTimeoutMinutes == clamped) return;
                settings.ActionTimeoutMinutes = clamped;
                settings.Save();
                RaisePropertyChanged();
            }
        }

        public bool ProfileWriteBackEnabled {
            get => settings.ProfileWriteBackEnabled;
            set {
                if (settings.ProfileWriteBackEnabled == value) return;
                settings.ProfileWriteBackEnabled = value;
                settings.Save();
                RaisePropertyChanged();
            }
        }

        /// "Report progress to ACP while imaging", on by default.
        ///
        /// The reporter checks this on every event rather than at startup, so
        /// switching it here takes effect immediately and does not wait for a
        /// NINA restart.
        public bool ReportProgressToAcp {
            get => settings.ReportProgressToAcp;
            set {
                if (settings.ReportProgressToAcp == value) return;
                settings.ReportProgressToAcp = value;
                settings.Save();
                Logger.Info($"ACP: progress reporting turned {(value ? "on" : "off")}.");
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(ReportProgressExplanation));
            }
        }

        public string ReportProgressExplanation =>
            settings.ReportProgressToAcp
                ? "As Target Scheduler works through a target, the hours it has actually acquired are sent back to ACP so the coverage map and the hours remaining stay current. Hours only ever go up, so a report that arrives late or twice changes nothing."
                : "ACP will not hear what tonight acquired until something else tells it, so the coverage map and the hours remaining go stale. Syncs do not change the acquired or accepted counts on rows Target Scheduler already has. The one exception is AAC's accepted figures: a sync writes those, but only until Target Scheduler has saved frames since the night they cover.";

        // -- The two mode switch -----------------------------------------------

        /// Bound as plain strings rather than enum values because the Options
        /// page is a DataTemplate in a ResourceDictionary, where wiring up an
        /// ObjectDataProvider for the enum costs more than it saves.
        public string[] SyncModeOptions { get; } = new[] {
            SyncMode.Everything.ToLabel(),
            SyncMode.OnlyWhatFits.ToLabel(),
        };

        public string SelectedSyncModeOption {
            get => settings.SyncMode.ToLabel();
            set {
                var mode = value == SyncMode.OnlyWhatFits.ToLabel()
                    ? SyncMode.OnlyWhatFits
                    : SyncMode.Everything;
                if (settings.SyncMode == mode) return;
                settings.SyncMode = mode;
                settings.Save();
                Logger.Info($"ACP: plan matching mode set to {mode.ToWire()}");
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(SyncModeExplanation));
            }
        }

        public string SyncModeExplanation =>
            settings.SyncMode == SyncMode.OnlyWhatFits
                ? "Plans are matched against the gear the plate solve found and only the ones that fit are loaded. Plans with no gear set are always loaded. Pick this if you use more than one rig, site or computer."
                : "Every plan in ACP is loaded and nothing is filtered out. A frame is only solved when the focal length update below is on; with it off, no camera is needed. Anything that does not suit tonight gets one warning line and you adjust in Target Scheduler.";

        // -- Binning and the focal length solve frame --------------------------

        public int ImagingBinning {
            get => settings.ImagingBinning;
            set {
                var clamped = AcpSettings.ClampBinning(value);
                if (settings.ImagingBinning == clamped) return;
                settings.ImagingBinning = clamped;
                settings.Save();
                RaisePropertyChanged();
            }
        }

        /// The solve frame boxes bind to text so that an empty box can mean
        /// unset. Text that is not a valid number is ignored and the last
        /// saved value stays in force.
        public string SolveExposureText {
            get => settings.SolveExposureSeconds?.ToString(CultureInfo.InvariantCulture) ?? "";
            set {
                double? parsed = null;
                if (!string.IsNullOrWhiteSpace(value)) {
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                        || seconds <= 0 || double.IsInfinity(seconds)) return;
                    parsed = seconds;
                }
                if (settings.SolveExposureSeconds == parsed) return;
                settings.SolveExposureSeconds = parsed;
                settings.Save();
                RaisePropertyChanged();
            }
        }

        public string SolveBinningText {
            get => settings.SolveBinning?.ToString(CultureInfo.InvariantCulture) ?? "";
            set {
                var parsed = ParseOptionalInt(value, 1, 8, out var ok);
                if (!ok || settings.SolveBinning == parsed) return;
                settings.SolveBinning = parsed;
                settings.Save();
                RaisePropertyChanged();
            }
        }

        public string SolveGainText {
            get => settings.SolveGain?.ToString(CultureInfo.InvariantCulture) ?? "";
            set {
                var parsed = ParseOptionalInt(value, 0, int.MaxValue, out var ok);
                if (!ok || settings.SolveGain == parsed) return;
                settings.SolveGain = parsed;
                settings.Save();
                RaisePropertyChanged();
            }
        }

        public string SolveOffsetText {
            get => settings.SolveOffset?.ToString(CultureInfo.InvariantCulture) ?? "";
            set {
                var parsed = ParseOptionalInt(value, 0, int.MaxValue, out var ok);
                if (!ok || settings.SolveOffset == parsed) return;
                settings.SolveOffset = parsed;
                settings.Save();
                RaisePropertyChanged();
            }
        }

        /// Blank is a valid answer and means unset. Anything else must be a
        /// whole number in range, otherwise ok is false and nothing changes.
        private static int? ParseOptionalInt(string text, int min, int max, out bool ok) {
            ok = true;
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                && number >= min && number <= max) {
                return number;
            }
            ok = false;
            return null;
        }

        public const string SolveFilterUnsetLabel = "(use the plate solve setting)";

        /// The unset choice, then the active profile's filter wheel names. A
        /// saved name the profile no longer has is kept in the list so the
        /// box does not silently go blank.
        public string[] SolveFilterOptions {
            get {
                var names = new List<string> { SolveFilterUnsetLabel };
                try {
                    var filters = profileService?.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters;
                    if (filters != null) {
                        names.AddRange(filters.Select(f => f?.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
                    }
                } catch (Exception ex) {
                    Logger.Warning($"ACP: could not read the filter wheel names for the options page: {ex.Message}");
                }
                var saved = settings.SolveFilter;
                if (!string.IsNullOrWhiteSpace(saved) && !names.Contains(saved)) names.Add(saved);
                return names.ToArray();
            }
        }

        public string SelectedSolveFilterOption {
            get => string.IsNullOrWhiteSpace(settings.SolveFilter) ? SolveFilterUnsetLabel : settings.SolveFilter;
            set {
                // A ComboBox sends null while its list is being rebuilt.
                if (value == null) return;
                var filter = value == SolveFilterUnsetLabel ? null : value;
                if (settings.SolveFilter == filter) return;
                settings.SolveFilter = filter;
                settings.Save();
                RaisePropertyChanged();
            }
        }

        // -- API token ---------------------------------------------------------

        private string tokenStatus;
        public string TokenStatus {
            get => tokenStatus;
            set { tokenStatus = value; RaisePropertyChanged(); }
        }

        public ICommand ClearTokenCommand { get; }

        /// Called from the Options code-behind as the user types. Blank clears
        /// the stored token, which is how someone goes back to an ACP with no
        /// token set.
        public void SetApiToken(string token) {
            var ok = TokenStore.Write(token);
            TokenStatus = ok
                ? DescribeTokenState()
                : "Windows Credential Manager refused to store the token. See the NINA log.";
        }

        private void ClearToken() {
            TokenStore.Delete();
            TokenStatus = DescribeTokenState();
        }

        private static string DescribeTokenState() {
            return TokenStore.HasToken()
                ? "A token is stored in Windows Credential Manager. Type a new one to replace it."
                : "No token stored. Needed only when the ACP server sets ACP_API_TOKEN.";
        }

        private string connectionTestResult;
        public string ConnectionTestResult {
            get => connectionTestResult;
            set { connectionTestResult = value; RaisePropertyChanged(); }
        }

        public ICommand TestConnectionCommand { get; }

        private async Task TestConnectionAsync() {
            var url = settings.ServerUrl;
            ConnectionTestResult = $"Probing {url}...";
            try {
                var client = new AcpApiClient(url);
                var status = await client.ProbeAsync().ConfigureAwait(false);
                ConnectionTestResult = $"✓ {status}";
                Logger.Info($"ACP: test connection OK against {url}");
            } catch (AcpUnauthorizedException ex) {
                ConnectionTestResult = $"✗ {ex.Message}";
                Logger.Warning($"ACP: test connection rejected by {url}: {ex.Message}");
            } catch (Exception ex) {
                ConnectionTestResult = $"✗ Failed: {ex.Message}";
                Logger.Warning($"ACP: test connection failed against {url}: {ex.Message}");
            }
        }

        // ── INotifyPropertyChanged plumbing ───────────────────────────────────

        public event PropertyChangedEventHandler PropertyChanged;

        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
