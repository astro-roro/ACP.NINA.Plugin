using ACP.NINA.Plugin.Services;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
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
                : "ACP will not hear what tonight acquired until something else tells it. Worth knowing: the Target Scheduler sync writes ACP's view of the counts back into Target Scheduler, so if ACP's hours go stale, a later sync can walk the real counts backwards.";

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
                : "Every plan in ACP is loaded. The gear fingerprint is still built and shown, and the focal length is still corrected, but nothing is filtered out. Anything that does not suit tonight gets one warning line and you adjust in Target Scheduler.";

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
