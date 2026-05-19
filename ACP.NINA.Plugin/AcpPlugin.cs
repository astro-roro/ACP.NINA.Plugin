using ACP.NINA.Plugin.Services;
using NINA.Core.Utility;
using RelayCommand = CommunityToolkit.Mvvm.Input.RelayCommand;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

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

            TestConnectionCommand = new RelayCommand(TestConnection);
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

        private void TestConnection() {
            // Iteration 3: replace with AcpApiClient.GetVersionAsync()
            ConnectionTestResult = $"Test stub — HTTP wiring lands in iteration 3. URL: {settings.ServerUrl}";
        }

        // ── INotifyPropertyChanged plumbing ───────────────────────────────────

        public event PropertyChangedEventHandler PropertyChanged;

        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
