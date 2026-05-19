using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using RelayCommand = CommunityToolkit.Mvvm.Input.RelayCommand;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ACP.NINA.Plugin.Dockables {

    /// Main dockable panel for the ACP plugin. Shows connection status, the
    /// list of plans fetched from ACP, the currently-selected plan's geometry,
    /// and the two v1.0 action buttons (Push to Framing, Sync All to TS).
    ///
    /// Iteration 2: layout + placeholder plans + non-functional buttons.
    /// Iteration 3 wires real HTTP and the Framing push action.
    /// Iteration 4 wires the TS sync action.
    [Export(typeof(IDockableVM))]
    public partial class AcpDockableVM : DockableVM {

        private AcpSettings settings;

        [ImportingConstructor]
        public AcpDockableVM(IProfileService profileService) : base(profileService) {
            Title = "Astro Coverage Planner";

            var resourceDict = new ResourceDictionary();
            resourceDict.Source = new Uri(
                "ACP.NINA.Plugin;component/Dockables/AcpDockableIcon.xaml",
                UriKind.RelativeOrAbsolute
            );
            ImageGeometry = (GeometryGroup)resourceDict["AcpDockableIcon"];
            ImageGeometry.Freeze();

            settings = AcpSettings.Load();

            RefreshCommand = new RelayCommand(RefreshPlans);
            PushToFramingCommand = new RelayCommand(PushToFraming, () => SelectedPlan != null);
            SyncAllToTsCommand = new RelayCommand(SyncAllToTs);

            ActiveProfileName = profileService?.ActiveProfile?.Name ?? "(no active profile)";
            ConnectionStatus = "Not yet connected";
            IsConnected = false;

            // Placeholder data so the layout is testable before HTTP wiring lands.
            // Iteration 3 replaces this with a real fetch.
            LoadPlaceholderPlans();
        }

        // ── Connection status ─────────────────────────────────────────────────

        private bool isConnected;
        public bool IsConnected {
            get => isConnected;
            set { isConnected = value; RaisePropertyChanged(nameof(IsConnected)); }
        }

        private string connectionStatus;
        public string ConnectionStatus {
            get => connectionStatus;
            set { connectionStatus = value; RaisePropertyChanged(nameof(ConnectionStatus)); }
        }

        public string ServerUrl => settings?.ServerUrl ?? "(not configured)";

        // ── Plans list ────────────────────────────────────────────────────────

        public ObservableCollection<PlanRowVM> Plans { get; } = new ObservableCollection<PlanRowVM>();

        private PlanRowVM selectedPlan;
        public PlanRowVM SelectedPlan {
            get => selectedPlan;
            set {
                selectedPlan = value;
                RaisePropertyChanged(nameof(SelectedPlan));
                RaisePropertyChanged(nameof(HasSelection));
                RaisePropertyChanged(nameof(SelectedPlanSummary));
                RaisePropertyChanged(nameof(SelectedPlanCoordinates));
                ((RelayCommand)PushToFramingCommand).NotifyCanExecuteChanged();
            }
        }

        public bool HasSelection => SelectedPlan != null;

        public string SelectedPlanSummary => SelectedPlan == null
            ? string.Empty
            : $"{SelectedPlan.ProjectName} — {SelectedPlan.TargetName}";

        public string SelectedPlanCoordinates => SelectedPlan == null
            ? string.Empty
            : $"{SelectedPlan.CoordinatesShort} · rot {SelectedPlan.RotationDeg}° · {SelectedPlan.MosaicShort}";

        // ── Profile (for TS sync display) ─────────────────────────────────────

        private string activeProfileName;
        public string ActiveProfileName {
            get => activeProfileName;
            set { activeProfileName = value; RaisePropertyChanged(nameof(ActiveProfileName)); }
        }

        // ── Last action result ────────────────────────────────────────────────

        private string lastActionResult;
        public string LastActionResult {
            get => lastActionResult;
            set { lastActionResult = value; RaisePropertyChanged(nameof(LastActionResult)); }
        }

        // ── Commands ──────────────────────────────────────────────────────────

        public ICommand RefreshCommand { get; }
        public ICommand PushToFramingCommand { get; }
        public ICommand SyncAllToTsCommand { get; }

        private void RefreshPlans() {
            // Iteration 3: replace with AcpApiClient.GetPlansAsync()
            LastActionResult = $"Refresh stub — ACP HTTP wiring lands in iteration 3.";
            Logger.Info("ACP: RefreshPlans (stub)");
        }

        private void PushToFraming() {
            if (SelectedPlan == null) return;
            // Iteration 3: call IFramingAssistantVM.SetCoordinates() etc.
            LastActionResult = $"Push to Framing stub — would push '{SelectedPlan.TargetName}'.";
            Logger.Info($"ACP: PushToFraming stub for {SelectedPlan.TargetName}");
        }

        private void SyncAllToTs() {
            // Iteration 4: POST to /api/ext/nina-ts-sync/sync
            LastActionResult = $"Sync All to TS stub — would sync {Plans.Count} plans to profile '{ActiveProfileName}'.";
            Logger.Info($"ACP: SyncAllToTs stub");
        }

        // ── Placeholder data ──────────────────────────────────────────────────

        private void LoadPlaceholderPlans() {
            Plans.Clear();
            Plans.Add(new PlanRowVM {
                ProjectName = "Fesen SNR",
                TargetName = "G7.7-3.7",
                State = "Active",
                FilterSummary = "Ha 30h, OIII 30h, SII 15h",
                MosaicShort = "1×1",
                GearShort = "RedCat 51 + ASI6200MM Pro",
                CoordinatesShort = "18h 17m 30s · -24° 04' 59\"",
                RotationDeg = 0,
            });
            Plans.Add(new PlanRowVM {
                ProjectName = "Helix Nebula",
                TargetName = "NGC 7293",
                State = "Active",
                FilterSummary = "OIII 35h, Ha 5h, SII 4h",
                MosaicShort = "1×1",
                GearShort = "RedCat 51 + ASI2600MM Pro",
                CoordinatesShort = "22h 29m 38s · -20° 50' 13\"",
                RotationDeg = 0,
            });
            Plans.Add(new PlanRowVM {
                ProjectName = "M 78",
                TargetName = "M 78",
                State = "Draft",
                FilterSummary = "Ha 5h, OIII 5h",
                MosaicShort = "1×1",
                GearShort = "190MN + ASI6200MM Pro",
                CoordinatesShort = "05h 46m 46s · +00° 00' 50\"",
                RotationDeg = 15,
            });
            Plans.Add(new PlanRowVM {
                ProjectName = "Scorpii Reflection",
                TargetName = "IC 4628",
                State = "Active",
                FilterSummary = "Ha 32h, L 7h, G 6h, B 5h",
                MosaicShort = "2×2",
                GearShort = "RedCat 51 + ASI6200MM Pro",
                CoordinatesShort = "16h 57m 00s · -40° 20' 00\"",
                RotationDeg = 0,
            });
        }
    }

    /// Per-row view-model for the plans list. Flattened from the full Plan model
    /// to keep XAML bindings simple — the ItemTemplate doesn't have to know
    /// about nested target/mosaic objects.
    public class PlanRowVM {
        public string ProjectName { get; set; }
        public string TargetName { get; set; }
        public string State { get; set; }
        public string FilterSummary { get; set; }
        public string MosaicShort { get; set; }
        public string GearShort { get; set; }
        public string CoordinatesShort { get; set; }
        public double RotationDeg { get; set; }
    }
}
