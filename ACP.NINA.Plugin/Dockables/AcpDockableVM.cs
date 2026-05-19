using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RelayCommand = CommunityToolkit.Mvvm.Input.RelayCommand;

namespace ACP.NINA.Plugin.Dockables {

    /// Main dockable panel for the ACP plugin. Lists plans fetched from ACP,
    /// shows the currently-selected plan's geometry, and exposes the v1.0
    /// action buttons (Push to Framing, Sync All to TS).
    ///
    /// Iteration 3: real HTTP wiring — Refresh actually fetches /api/plans
    /// (joined with /api/gear for human-readable telescope/camera labels),
    /// connection status reflects probe results. Actions still stubs.
    /// Iteration 4: wire Push to Framing (IFramingAssistantVM.SetCoordinates)
    /// and Sync All to TS (POST /api/ext/nina-ts-sync/sync).
    [Export(typeof(IDockableVM))]
    public partial class AcpDockableVM : DockableVM {

        private readonly AcpSettings settings;

        [ImportingConstructor]
        public AcpDockableVM(IProfileService profileService) : base(profileService) {
            // `profileService` is exposed by the BaseVM via its protected field
            // of the same name; no need to keep a duplicate reference here.
            Title = "Astro Coverage Planner";

            var resourceDict = new ResourceDictionary();
            resourceDict.Source = new Uri(
                "ACP.NINA.Plugin;component/Dockables/AcpDockableIcon.xaml",
                UriKind.RelativeOrAbsolute
            );
            ImageGeometry = (GeometryGroup)resourceDict["AcpDockableIcon"];
            ImageGeometry.Freeze();

            settings = AcpSettings.Load();

            RefreshCommand = new RelayCommand(async () => await RefreshAsync());
            PushToFramingCommand = new RelayCommand(PushToFramingStub, () => SelectedPlan != null);
            SyncAllToTsCommand = new RelayCommand(SyncAllToTsStub);

            ActiveProfileName = profileService?.ActiveProfile?.Name ?? "(no active profile)";
            ConnectionStatus = "Probing...";
            IsConnected = false;

            // Kick off an initial probe + plans fetch on construction.
            // Fire-and-forget — UI updates via INotifyPropertyChanged as the
            // task completes; surfacing exceptions in LastActionResult.
            _ = RefreshAsync();
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

        private async Task RefreshAsync() {
            var url = settings.ServerUrl;
            var client = new AcpApiClient(url);
            try {
                // Probe + fetch in one shot — Probe already hits /api/plans,
                // so we can also use that response. Calling them separately
                // for clarity; the HttpClient cache is shared so this is one
                // round-trip in practice on a warm server.
                await client.ProbeAsync().ConfigureAwait(false);
                var plans = await client.GetPlansAsync().ConfigureAwait(false);
                var gear = await client.GetGearAsync().ConfigureAwait(false);

                var rows = BuildPlanRows(plans.Plans, gear);

                Application.Current?.Dispatcher.Invoke(() => {
                    Plans.Clear();
                    foreach (var r in rows) Plans.Add(r);
                    IsConnected = true;
                    ConnectionStatus = $"Connected — {url}";
                    LastActionResult = $"Loaded {rows.Count} plans from ACP.";
                });
                Logger.Info($"ACP: refreshed {rows.Count} plans from {url}");
            } catch (Exception ex) {
                Application.Current?.Dispatcher.Invoke(() => {
                    Plans.Clear();
                    IsConnected = false;
                    ConnectionStatus = $"Not connected — {url}";
                    LastActionResult = $"Failed to reach ACP: {ex.Message}";
                });
                Logger.Warning($"ACP: refresh failed against {url}: {ex.Message}");
            }
        }

        // ── Plan-row composition (joins plans with gear for display) ──────────

        private static List<PlanRowVM> BuildPlanRows(IEnumerable<Plan> plans, GearResponse gear) {
            var scopesById = (gear?.Telescopes ?? new List<Telescope>())
                .Where(t => t?.Id != null)
                .ToDictionary(t => t.Id, t => t);
            var camsById = (gear?.Cameras ?? new List<Camera>())
                .Where(c => c?.Id != null)
                .ToDictionary(c => c.Id, c => c);

            var rows = new List<PlanRowVM>();
            foreach (var p in plans ?? Enumerable.Empty<Plan>()) {
                var tg = p.Target;
                var mosaic = tg?.Mosaic ?? new Mosaic();
                var scope = (p.TelescopeId != null && scopesById.TryGetValue(p.TelescopeId, out var s)) ? s : null;
                var cam = (p.CameraId != null && camsById.TryGetValue(p.CameraId, out var c)) ? c : null;

                rows.Add(new PlanRowVM {
                    ProjectName = p.ProjectName ?? p.Id ?? "(unnamed)",
                    TargetName = tg?.Name ?? "(no target)",
                    State = p.State ?? "",
                    FilterSummary = FormatFilters(p.FilterGoals),
                    MosaicShort = $"{mosaic.Rows}×{mosaic.Cols}",
                    GearShort = FormatGear(scope, cam),
                    CoordinatesShort = FormatCoords(tg?.CenterRaDeg ?? 0, tg?.CenterDecDeg ?? 0),
                    RotationDeg = tg?.RotationDeg ?? 0,
                });
            }
            return rows;
        }

        private static string FormatFilters(Dictionary<string, FilterGoal> goals) {
            if (goals == null || goals.Count == 0) return "(no filter goals)";
            // Sort high → low hours so the dominant filter leads.
            var parts = goals
                .Where(kv => kv.Value != null && kv.Value.TargetHours > 0)
                .OrderByDescending(kv => kv.Value.TargetHours)
                .Select(kv => $"{kv.Key} {kv.Value.TargetHours:0.#}h");
            return string.Join(", ", parts);
        }

        private static string FormatGear(Telescope scope, Camera cam) {
            var s = scope?.Name ?? "(no scope)";
            var c = cam?.Name ?? "(no cam)";
            return $"{s} + {c}";
        }

        private static string FormatCoords(double raDeg, double decDeg) {
            // RA: degrees → hours (HMS), Dec: degrees (DMS, signed).
            var raHours = raDeg / 15.0;
            var raH = (int)raHours;
            var raMins = (raHours - raH) * 60.0;
            var raM = (int)raMins;
            var raS = (raMins - raM) * 60.0;

            var sign = decDeg >= 0 ? "+" : "-";
            var absDec = Math.Abs(decDeg);
            var decD = (int)absDec;
            var decMins = (absDec - decD) * 60.0;
            var decM = (int)decMins;
            var decS = (decMins - decM) * 60.0;

            return $"{raH:00}h {raM:00}m {raS:00}s · {sign}{decD:00}° {decM:00}' {decS:00}\"";
        }

        // ── Action stubs (iteration 4) ────────────────────────────────────────

        private void PushToFramingStub() {
            if (SelectedPlan == null) return;
            LastActionResult = $"Push to Framing stub — would push '{SelectedPlan.TargetName}'. (Iteration 4.)";
            Logger.Info($"ACP: PushToFraming stub for {SelectedPlan.TargetName}");
        }

        private void SyncAllToTsStub() {
            LastActionResult = $"Sync All to TS stub — would sync {Plans.Count} plans to profile '{ActiveProfileName}'. (Iteration 4.)";
            Logger.Info($"ACP: SyncAllToTs stub");
        }
    }

    /// Per-row view-model for the plans list. Flattened from the full Plan
    /// model so XAML bindings don't have to chase nested target/mosaic objects.
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
