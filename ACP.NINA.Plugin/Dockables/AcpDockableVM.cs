using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RelayCommand = CommunityToolkit.Mvvm.Input.RelayCommand;

namespace ACP.NINA.Plugin.Dockables {

    /// Main dockable panel for the ACP plugin. Lists plans fetched from ACP,
    /// shows the currently-selected plan's geometry, and exposes the v1.0
    /// action buttons: Push to Framing (Framing Wizard) and Sync All to TS
    /// (POSTs to the private nina_ts_sync extension).
    ///
    /// Iteration 4 (this file): wires real Framing push + TS sync. Plans
    /// list and connection probing came in iteration 3.
    [Export(typeof(IDockableVM))]
    public partial class AcpDockableVM : DockableVM {

        private readonly IFramingAssistantVM framingAssistantVM;
        private readonly AcpSettings settings;

        [ImportingConstructor]
        public AcpDockableVM(
            IProfileService profileService,
            IFramingAssistantVM framingAssistantVM
        ) : base(profileService) {
            this.framingAssistantVM = framingAssistantVM;
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
            PushToFramingCommand = new RelayCommand(
                async () => await PushToFramingAsync(),
                () => SelectedPlan != null && IsConnected
            );
            SyncAllToTsCommand = new RelayCommand(
                async () => await SyncAllToTsAsync(),
                () => IsConnected && Plans.Count > 0
            );

            ActiveProfileName = profileService?.ActiveProfile?.Name ?? "(no active profile)";
            ConnectionStatus = "Probing...";
            IsConnected = false;

            _ = RefreshAsync();
        }

        // ── Connection status ─────────────────────────────────────────────────

        private bool isConnected;
        public bool IsConnected {
            get => isConnected;
            set {
                isConnected = value;
                RaisePropertyChanged(nameof(IsConnected));
                ((RelayCommand)PushToFramingCommand).NotifyCanExecuteChanged();
                ((RelayCommand)SyncAllToTsCommand).NotifyCanExecuteChanged();
            }
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
                    // Underlying records — kept so Push to Framing has the
                    // raw RA/Dec/FOV data without a re-fetch.
                    Plan = p,
                    Telescope = scope,
                    Camera = cam,

                    // Display strings
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

        // ── Action: Push to Framing ───────────────────────────────────────────

        private async Task PushToFramingAsync() {
            if (SelectedPlan?.Plan?.Target == null) return;
            var plan = SelectedPlan.Plan;
            var target = plan.Target;

            try {
                LastActionResult = $"Pushing '{target.Name}' to Framing Wizard...";

                // The DeepSkyObject + Coordinates work happens on whatever thread
                // the command fires from, but property setters on the framing
                // VM need to land on the UI thread. Dispatcher.Invoke wraps the
                // whole sequence to keep it tidy.
                await Application.Current.Dispatcher.InvokeAsync(async () => {
                    var coords = new Coordinates(
                        Angle.ByDegree(target.CenterRaDeg),
                        Angle.ByDegree(target.CenterDecDeg),
                        Epoch.J2000
                    );
                    var dso = new DeepSkyObject(
                        target.Name ?? plan.ProjectName ?? plan.Id ?? "",
                        coords,
                        string.Empty,
                        null
                    );
                    var ok = await framingAssistantVM.SetCoordinates(dso);
                    if (!ok) {
                        LastActionResult = $"Framing rejected the coordinates for '{target.Name}'.";
                        return;
                    }

                    // Optics (only set when ACP has the data; partial pushes are
                    // valid — Framing falls back to the active NINA profile).
                    var cam = SelectedPlan.Camera;
                    var scope = SelectedPlan.Telescope;
                    if (cam?.SensorWidthPx is int w) framingAssistantVM.CameraWidth = w;
                    if (cam?.SensorHeightPx is int h) framingAssistantVM.CameraHeight = h;
                    if (cam?.PixelSizeUm is double px) framingAssistantVM.CameraPixelSize = px;
                    if (scope?.FocalLengthMm is double fl) framingAssistantVM.FocalLength = fl;

                    // Mosaic
                    var m = target.Mosaic ?? new Mosaic();
                    framingAssistantVM.HorizontalPanels = Math.Max(1, m.Cols);
                    framingAssistantVM.VerticalPanels = Math.Max(1, m.Rows);
                    framingAssistantVM.OverlapPercentage = m.OverlapPct;
                });

                // Phase 2 — explicitly trigger LoadImage and await it.
                // This is the pattern NINA's own DSO import path uses:
                //   await LoadImageCommand.ExecuteAsync(null);
                //   RectangleRotation = 360 - dso.RotationPositionAngle;
                // Waiting for LoadImage to fully complete means all the
                // cascading CalculateRectangle tasks queued by the camera/
                // mosaic setters have finished, the sky-survey image is
                // drawn, the viewport FoV is populated, and CameraRectangles
                // is properly populated with the mosaic panel outlines.
                // Setting rotation after this point sticks; no retry loop
                // needed (and a retry loop turned out to race against the
                // CameraRectangles build, hiding the mosaic outlines).
                await Application.Current.Dispatcher.InvokeAsync(async () => {
                    if (framingAssistantVM.LoadImageCommand?.CanExecute(null) == true) {
                        await framingAssistantVM.LoadImageCommand.ExecuteAsync(null);
                    }
                });

                if (Math.Abs(target.RotationDeg) > 0.001) {
                    await ApplyRotationOnceAsync(target.RotationDeg);
                }

                LastActionResult = $"✓ Pushed '{target.Name}' to Framing Wizard.";
                Logger.Info($"ACP: pushed '{target.Name}' to Framing — RA {target.CenterRaDeg:F4}° Dec {target.CenterDecDeg:F4}° rot {target.RotationDeg}° mosaic {target.Mosaic?.Rows}×{target.Mosaic?.Cols}");
            } catch (Exception ex) {
                LastActionResult = $"✗ Push failed: {ex.Message}";
                Logger.Error($"ACP: PushToFraming failed for '{target?.Name}': {ex}");
            }
        }

        /// Set rotation once. Caller is responsible for ensuring everything
        /// has settled (i.e. LoadImageCommand has been awaited) — the retry
        /// loop the previous version of this method ran turned out to race
        /// against the CameraRectangles build, hiding mosaic panel outlines.
        ///
        /// Uses the RectangleRotation proxy (Rectangle.Rotation, the
        /// user-rotation field NINA's own DSO load path writes to). Falls
        /// back to RectangleTotalRotation, then to the bare
        /// Rectangle.TotalRotation, in case a future NINA version renames
        /// the proxy.
        private async Task ApplyRotationOnceAsync(double rotationDeg) {
            if (!framingAssistantVM.RectangleCalculated) {
                Logger.Warning("ACP: Rectangle not calculated; rotation skipped");
                LastActionResult = "✓ Pushed (rotation skipped — Framing image didn't load in time).";
                return;
            }

            var vmType = framingAssistantVM.GetType();
            var proxy = vmType.GetProperty("RectangleRotation")
                     ?? vmType.GetProperty("RectangleTotalRotation");
            var inverted = 360 - rotationDeg;

            await Application.Current.Dispatcher.InvokeAsync(() => {
                if (proxy != null && proxy.CanWrite) {
                    proxy.SetValue(framingAssistantVM, inverted);
                    Logger.Info($"ACP: rotation {rotationDeg}° applied via {proxy.Name}");
                } else if (framingAssistantVM.Rectangle != null) {
                    framingAssistantVM.Rectangle.TotalRotation = inverted;
                    Logger.Warning("ACP: no rotation proxy property; used Rectangle.TotalRotation fallback");
                }
            });
        }

        // ── Action: Sync All to TS ────────────────────────────────────────────

        private async Task SyncAllToTsAsync() {
            var profile = profileService?.ActiveProfile;
            if (profile == null) {
                LastActionResult = "✗ No active NINA profile — can't sync to TS.";
                return;
            }
            var profileId = profile.Id.ToString();

            try {
                LastActionResult = $"Syncing {Plans.Count} plans to TS (profile: {profile.Name})...";

                var client = new AcpApiClient(settings.ServerUrl);
                var resp = await client.SyncToTsAsync(profileId).ConfigureAwait(false);

                Application.Current?.Dispatcher.Invoke(() => {
                    LastActionResult = "✓ " + (resp?.Report?.ToShortString() ?? "Sync complete.");
                });
                Logger.Info($"ACP: TS sync OK — {resp?.Report?.ToShortString()}");
            } catch (Exception ex) {
                Application.Current?.Dispatcher.Invoke(() => {
                    LastActionResult = $"✗ TS sync failed: {ex.Message}";
                });
                Logger.Error($"ACP: TS sync failed: {ex}");
            }
        }
    }

    /// Per-row view-model. Carries the underlying Plan + matched Telescope +
    /// Camera so Push to Framing has access to optics/sensor data without
    /// re-fetching. Display strings are pre-computed for the ItemTemplate.
    public class PlanRowVM {
        public Plan Plan { get; set; }
        public Telescope Telescope { get; set; }
        public Camera Camera { get; set; }

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
