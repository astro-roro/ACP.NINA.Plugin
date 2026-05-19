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
