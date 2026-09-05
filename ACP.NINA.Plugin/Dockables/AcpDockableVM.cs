using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using ACP.NINA.Plugin.Services.TargetScheduler;
using NINA.Astrometry;
using NINA.Core.MyMessageBox;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
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
    /// action buttons: Push to Framing (Framing Wizard) and Sync All to TS
    /// (POSTs to the private nina_ts_sync extension).
    ///
    /// Iteration 4 (this file): wires real Framing push + TS sync. Plans
    /// list and connection probing came in iteration 3.
    [Export(typeof(IDockableVM))]
    public partial class AcpDockableVM : DockableVM, IDisposable {

        private readonly IFramingAssistantVM framingAssistantVM;
        private readonly IAcpPlateSolver plateSolver;
        private readonly ISyncForTonightRunner syncRunner;
        private readonly AcpSettings settings;

        [ImportingConstructor]
        public AcpDockableVM(
            IProfileService profileService,
            IFramingAssistantVM framingAssistantVM,
            IAcpPlateSolver plateSolver,
            ISyncForTonightRunner syncRunner,
            [Import(AllowDefault = true)] IMessageBroker messageBroker,
            [Import(AllowDefault = true)] ITsContainerWatch containerWatch
        ) : base(profileService) {
            this.framingAssistantVM = framingAssistantVM;
            this.plateSolver = plateSolver;
            this.syncRunner = syncRunner;
            Title = "Astro Coverage Planner";

            var resourceDict = new ResourceDictionary();
            resourceDict.Source = new Uri(
                "ACP.NINA.Plugin;component/Dockables/AcpDockableIcon.xaml",
                UriKind.RelativeOrAbsolute
            );
            ImageGeometry = (GeometryGroup)resourceDict["AcpDockableIcon"];
            ImageGeometry.Freeze();

            settings = AcpSettings.Load();

            RefreshCommand = new RelayCommand(async () => await RefreshAsync(announce: true));
            PushToFramingCommand = new RelayCommand(
                async () => await PushToFramingAsync(),
                () => SelectedPlan != null && IsConnected
            );
            SyncAllToTsCommand = new RelayCommand(
                async () => await SyncAllToTsAsync(),
                () => IsConnected && Plans.Count > 0
            );
            SyncForTonightCommand = new RelayCommand(
                async () => await SyncForTonightAsync(),
                () => IsConnected && !IsSyncingForTonight
            );

            // The label under the sync button names the profile the sync will
            // write to. The sync itself reads the active profile at click time,
            // so the label has to follow profile switches or it lies.
            ActiveProfileName = profileService?.ActiveProfile?.Name ?? "(no active profile)";
            if (profileService != null) {
                profileService.ProfileChanged += OnProfileChanged;
            }
            ConnectionStatus = "Probing...";
            IsConnected = false;

            StartProgressReporting(messageBroker, containerWatch);

            _ = RefreshAsync();
            _ = PollForChangesAsync(pollCts.Token);
        }

        // -- Progress reporting (Part F) ---------------------------------------

        private ProgressReporter progressReporter;
        private TsSnapshotCache tsSnapshotCache;
        private TsPlanRefsSource planRefsSource;

        /// Build the reporter and let it run for the life of the dock.
        ///
        /// It lives here rather than behind another exported service because
        /// this is the one place that already has the profile, the settings and
        /// somewhere to show the result. Both new imports are AllowDefault, so
        /// a NINA that does not hand over a message broker or the container
        /// watch still composes the dock: progress reporting falls back to the
        /// timer, or to being off, rather than the whole panel failing to
        /// appear. That is the same call v3.1 made about its own broker import.
        private void StartProgressReporting(
            IMessageBroker messageBroker, ITsContainerWatch containerWatch
        ) {
            try {
                Func<string> profileId = () => profileService?.ActiveProfile?.Id.ToString();

                tsSnapshotCache = new TsSnapshotCache();
                planRefsSource = new TsPlanRefsSource(
                    tsSnapshotCache,
                    profileId,
                    async ct => (IReadOnlyList<Plan>)
                        (await new AcpApiClient(settings.ServerUrl).GetPlansAsync(ct)
                            .ConfigureAwait(false))?.Plans
                );

                progressReporter = new ProgressReporter(
                    messageBroker,
                    new TsDatabaseProgressSource(tsSnapshotCache, profileId),
                    planRefsSource,
                    new AcpApiClient(settings.ServerUrl),
                    containerWatch,
                    () => settings.ReportProgressToAcp,
                    profileId
                );
                progressReporter.StatusChanged += (s, e) =>
                    RaisePropertyChanged(nameof(ProgressStatusLine));
                progressReporter.Start();
            } catch (Exception ex) {
                // Nothing here is worth losing the dock over.
                Logger.Error($"ACP: could not start progress reporting: {ex}");
                progressReporter = null;
            }
        }

        /// A sync has just rewritten Target Scheduler, so the reporter's view
        /// of both the rows and the plan list is out of date. Dropping the two
        /// caches means the next report joins against what was actually
        /// written rather than what was there beforehand.
        private void InvalidateProgressCaches() {
            tsSnapshotCache?.Invalidate();
            planRefsSource?.Invalidate();
        }

        /// The footer line: "Progress sent 22 s ago", or the last error.
        public string ProgressStatusLine =>
            progressReporter?.StatusLine ?? ProgressStatus.Off;

        // -- Change polling ----------------------------------------------------

        /// GET /api/version once a minute and refetch plans only when
        /// plans_last_modified moves. A dock left open all night then costs one
        /// small request a minute instead of a full plans and gear fetch, and a
        /// plan edited in ACP's web UI shows up here without anyone pressing
        /// refresh.
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

        private readonly CancellationTokenSource pollCts = new CancellationTokenSource();
        private string lastSeenPlansModified;

        private async Task PollForChangesAsync(CancellationToken ct) {
            while (!ct.IsCancellationRequested) {
                try {
                    await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    return;
                }
                if (ct.IsCancellationRequested) return;

                try {
                    var client = new AcpApiClient(settings.ServerUrl);
                    var version = await client.GetVersionAsync(ct).ConfigureAwait(false);
                    var marker = version?.PlansLastModified;

                    if (!IsConnected) {
                        // Coming back after an outage. Refetch whatever the
                        // marker says, since the list on screen is only what was
                        // there when the connection dropped.
                        await RefreshAsync().ConfigureAwait(false);
                        continue;
                    }
                    if (marker != null && marker != lastSeenPlansModified) {
                        Logger.Info($"ACP: plans changed on the server ({lastSeenPlansModified} to {marker}), refetching.");
                        await RefreshAsync().ConfigureAwait(false);
                    }
                } catch (AcpUnauthorizedException ex) {
                    SetStatusOnUi(false, $"Not connected — {settings.ServerUrl}", ex.Message);
                } catch (OperationCanceledException) {
                    return;
                } catch (Exception ex) {
                    // A poll failure is not worth shouting about. The next one
                    // in a minute either recovers or the user presses refresh.
                    Logger.Debug($"ACP: version poll failed: {ex.Message}");
                }
            }
        }

        /// NINA's DockableVM has no Dispose to override, so this is an
        /// explicit interface implementation: it cannot collide with anything
        /// the base class grows later, and it stops the poll loop for anything
        /// that does dispose the view model. If nothing ever does, the loop
        /// simply runs for the life of the application, which is what a 60
        /// second poll is for.
        void IDisposable.Dispose() {
            if (profileService != null) profileService.ProfileChanged -= OnProfileChanged;
            try {
                progressReporter?.Dispose();
            } catch (Exception) {
                // Already gone, nothing useful to do.
            }
            try {
                pollCts.Cancel();
                pollCts.Dispose();
            } catch (Exception) {
                // Nothing useful to do if the token source is already gone.
            }
        }

        private void SetStatusOnUi(bool connected, string status, string result) {
            Application.Current?.Dispatcher.Invoke(() => {
                IsConnected = connected;
                ConnectionStatus = status;
                LastActionResult = result;
            });
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

        private void OnProfileChanged(object sender, EventArgs e) {
            var name = profileService?.ActiveProfile?.Name ?? "(no active profile)";
            Application.Current?.Dispatcher.Invoke(() => ActiveProfileName = name);
        }

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
        public ICommand SyncForTonightCommand { get; }

        // -- Action: Sync for tonight ------------------------------------------

        private bool isSyncingForTonight;

        public bool IsSyncingForTonight {
            get => isSyncingForTonight;
            set {
                isSyncingForTonight = value;
                RaisePropertyChanged(nameof(IsSyncingForTonight));
                ((RelayCommand)SyncForTonightCommand).NotifyCanExecuteChanged();
            }
        }

        /// The other half of Part E. Same steps as the sequencer instruction
        /// from the solve onwards, but from wherever the mount is already
        /// pointing, and reusing a solve from the last hour rather than taking
        /// another one. Decision 1 in the v3 spec: a button is pressed by
        /// someone who can see how long ago the last solve was, so it is
        /// allowed to reuse it as long as it says so.
        private async Task SyncForTonightAsync() {
            if (IsSyncingForTonight) return;
            IsSyncingForTonight = true;
            try {
                var reused = LastSolve.GetIfFresh();
                var solve = reused;
                if (solve == null) {
                    LastActionResult = "Sync for tonight: capturing and solving a frame...";
                    solve = await plateSolver.SolveAsync(0, null, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (solve == null) {
                        SetResultOnUi(
                            "✗ The plate solve failed, so ACP cannot tell what gear is connected."
                        );
                        return;
                    }
                } else {
                    var minutes = (int)Math.Round(reused.Age.TotalMinutes);
                    LastActionResult =
                        $"Sync for tonight: reusing the solve from {minutes} minutes ago.";
                }

                var outcome = await syncRunner
                    .RunAsync(solve, settings.ProfileWriteBackEnabled, CancellationToken.None)
                    .ConfigureAwait(false);

                // The dock is a small box, so the one line names the plan count
                // and the focal length change and the rest goes to the NINA log,
                // which the runner has already written. The refresh runs first
                // and quietly, so its own "Loaded N plans" line cannot replace
                // the result the user is waiting for.
                if (outcome.Success) {
                    InvalidateProgressCaches();
                    await RefreshAsync().ConfigureAwait(false);
                }

                var prefix = reused != null
                    ? $"Reused the solve from {(int)Math.Round(reused.Age.TotalMinutes)} minutes ago. "
                    : string.Empty;
                SetResultOnUi(
                    outcome.Success
                        ? "✓ " + prefix + outcome.ShortResult
                        : "✗ " + prefix + outcome.ShortResult
                );
            } catch (AcpUnauthorizedException ex) {
                SetResultOnUi("✗ " + ex.Message);
                Logger.Warning($"ACP: Sync for tonight rejected: {ex.Message}");
            } catch (Exception ex) {
                SetResultOnUi($"✗ Sync for tonight failed: {ex.Message}");
                Logger.Error($"ACP: Sync for tonight failed: {ex}");
            } finally {
                Application.Current?.Dispatcher.Invoke(() => IsSyncingForTonight = false);
            }
        }

        private void SetResultOnUi(string result) {
            Application.Current?.Dispatcher.Invoke(() => LastActionResult = result);
        }

        /// Refetch plans and gear. Only the refresh button announces itself
        /// on the result line; the timer and the sync paths stay quiet so the
        /// last thing the user did is still readable a minute later.
        private async Task RefreshAsync(bool announce = false) {
            var url = settings.ServerUrl;
            var client = new AcpApiClient(url);
            try {
                await client.ProbeAsync().ConfigureAwait(false);
                var plans = await client.GetPlansAsync().ConfigureAwait(false);
                var gear = await client.GetGearAsync().ConfigureAwait(false);

                // Stamp the change marker after a successful fetch, so a failed
                // fetch cannot make the poller think it is already up to date.
                try {
                    var version = await client.GetVersionAsync().ConfigureAwait(false);
                    lastSeenPlansModified = version?.PlansLastModified;
                } catch (Exception) {
                    // An ACP too old for /api/version polls without a marker.
                    // The poller then never sees a change and the refresh button
                    // stays the way to update.
                    lastSeenPlansModified = null;
                }

                var rows = BuildPlanRows(plans.Plans, gear);

                Application.Current?.Dispatcher.Invoke(() => {
                    Plans.Clear();
                    foreach (var r in rows) Plans.Add(r);
                    IsConnected = true;
                    ConnectionStatus = $"Connected — {url}";
                    if (announce) LastActionResult = $"Loaded {rows.Count} plans from ACP.";
                });
                Logger.Info($"ACP: refreshed {rows.Count} plans from {url}");
            } catch (AcpUnauthorizedException ex) {
                // The one failure the user can fix themselves, so it says what
                // is wrong rather than looking like the network is down.
                Application.Current?.Dispatcher.Invoke(() => {
                    Plans.Clear();
                    IsConnected = false;
                    ConnectionStatus = $"Not connected — {url}";
                    LastActionResult = ex.Message;
                });
                Logger.Warning($"ACP: {ex.Message} ({url})");
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
                // InvokeAsync with an async lambda completes when the lambda
                // yields at its first await, not when its work is done. Await the
                // inner task too, or the optics, mosaic and rotation steps race
                // NINA's own image load and lose.
                await await Application.Current.Dispatcher.InvokeAsync(async () => {
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
                    // NINA rebuilds the framing rectangle from the object's own
                    // rotation after the sky image loads, so a value poked into the
                    // view model afterwards does not survive. Give the object the
                    // rotation up front, the way NINA's own target import does.
                    dso.RotationPositionAngle = target.RotationDeg;
                    dso.Rotation = target.RotationDeg;
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
                    // NINA stores OverlapPercentage as a fraction 0.0-1.0
                    // (default 0.2 = 20% in the UI). ACP stores 0-99 as a
                    // percentage integer (per its plan validator). Without
                    // the /100 conversion, NINA reads e.g. 15 as 1500%
                    // overlap, which silently produces unrendered rectangles
                    // until the user nudges the slider.
                    framingAssistantVM.OverlapPercentage = m.OverlapPct / 100.0;
                    // Make sure the percent unit is selected, and re-select it so
                    // the slider re-reads the value. NINA raises no change event
                    // for OverlapValue when OverlapPercentage is set directly, so
                    // the slider can otherwise keep showing its previous number
                    // while the rectangles already use the new one.
                    // The view rebinds the stepper only when the unit selection
                    // changes, so go via the other unit and back to make the
                    // displayed number match the value just set.
                    var units = framingAssistantVM.OverlapUnits;
                    var pctUnit = units?.FirstOrDefault(u => u != null && u.Contains("%"));
                    var otherUnit = units?.FirstOrDefault(u => u != null && !u.Contains("%"));
                    if (pctUnit != null) {
                        if (otherUnit != null) framingAssistantVM.SelectedOverlapUnit = otherUnit;
                        framingAssistantVM.SelectedOverlapUnit = pctUnit;
                        framingAssistantVM.OverlapPercentage = m.OverlapPct / 100.0;
                    }
                });

                // No second LoadImage here. SetCoordinates already awaits the
                // image load itself, and a reload resets the rotation to the
                // profile's last remembered angle and rebuilds the rectangles
                // underneath the overlap change, which is what made rotated
                // pushes land on 0 and mosaic panels vanish until nudged.

                // Belt and braces after the load: the object carries the rotation,
                // and this pins the view model's field to the same value. Always
                // runs, including zero, so a stale angle from an earlier push is
                // never left behind.
                await ApplyRotationSettledAsync(target.RotationDeg);

                LastActionResult = $"✓ Pushed '{target.Name}' to Framing Wizard.";
                Logger.Info($"ACP: pushed '{target.Name}' to Framing — RA {target.CenterRaDeg:F4}° Dec {target.CenterDecDeg:F4}° rot {target.RotationDeg}° mosaic {target.Mosaic?.Rows}×{target.Mosaic?.Cols}");
            } catch (Exception ex) {
                LastActionResult = $"✗ Push failed: {ex.Message}";
                Logger.Error($"ACP: PushToFraming failed for '{target?.Name}': {ex}");
            }
        }

        /// Apply the rotation and make sure it holds. NINA's CameraWidth and
        /// CameraHeight setters defer their rectangle rebuild, and each rebuild
        /// that lands after a rotation set resets it. So set the value, wait,
        /// read it back, and repeat until it reads the same twice in a row.
        ///
        /// ACP stores the sky position angle. Framing's stored angle runs the
        /// other way: NINA's own plate solve path writes 360 minus the angle
        /// into RectangleTotalRotation, and the on screen box shows it back as
        /// the position angle. So an ACP plan at 45 is stored as 315 and shows
        /// as 45.
        private async Task ApplyRotationSettledAsync(double rotationDeg) {
            for (var i = 0; i < 40 && !framingAssistantVM.RectangleCalculated; i++) {
                await Task.Delay(250);
            }
            if (!framingAssistantVM.RectangleCalculated) {
                Logger.Warning("ACP: Rectangle not calculated after 10 s; rotation skipped");
                LastActionResult = "Pushed, but rotation was skipped: the Framing image did not load in time. Push again.";
                return;
            }

            var vmType = framingAssistantVM.GetType();
            var total = vmType.GetProperty("RectangleTotalRotation");
            var wanted = (360.0 - rotationDeg) % 360.0;
            if (wanted < 0) wanted += 360.0;

            if (total == null || !total.CanWrite) {
                if (framingAssistantVM.Rectangle != null) {
                    framingAssistantVM.Rectangle.TotalRotation = wanted;
                    Logger.Warning("ACP: no RectangleTotalRotation property; used Rectangle.TotalRotation fallback");
                }
                return;
            }

            double Read() {
                var v = total.GetValue(framingAssistantVM);
                return v is double d ? d : double.NaN;
            }
            double Diff(double a, double b) {
                var d = Math.Abs(((a - b) % 360.0 + 360.0) % 360.0);
                return Math.Min(d, 360.0 - d);
            }

            var stable = 0;
            var attempts = 0;
            for (; attempts < 8 && stable < 2; attempts++) {
                if (Diff(Read(), wanted) >= 0.5) {
                    await Application.Current.Dispatcher.InvokeAsync(() => total.SetValue(framingAssistantVM, wanted));
                    stable = 0;
                }
                await Task.Delay(300);
                stable = Diff(Read(), wanted) < 0.5 ? stable + 1 : 0;
            }
            var final = Read();
            if (Diff(final, wanted) < 0.5) {
                Logger.Info($"ACP: rotation {rotationDeg} held as stored {wanted} after {attempts} checks");
            } else {
                Logger.Warning($"ACP: rotation {rotationDeg} did not hold; stored value is {final} after {attempts} checks");
                LastActionResult = $"Pushed, but Framing kept resetting the rotation (now {final}). Set it by hand.";
            }
        }

        // ── Action: Sync All to TS ────────────────────────────────────────────

        private async Task SyncAllToTsAsync() {
            var profile = profileService?.ActiveProfile;
            if (profile == null) {
                LastActionResult = "✗ No active NINA profile — can't sync to TS.";
                return;
            }
            var profileId = profile.Id.ToString();

            // The options page saves through its own copy of the settings, so
            // read the flag fresh rather than trusting the one loaded at
            // startup. A file read per click is nothing next to the sync.
            if (AcpSettings.Load().ConfirmBeforeTsSync) {
                var answer = Application.Current?.Dispatcher.Invoke(() =>
                    MyMessageBox.Show(
                        $"Send {Plans.Count} plans to Target Scheduler for profile \"{profile.Name}\"?",
                        "Sync All to TS",
                        MessageBoxButton.YesNo,
                        MessageBoxResult.No
                    )
                );
                if (answer != MessageBoxResult.Yes) {
                    LastActionResult = "Sync to TS cancelled.";
                    return;
                }
            }

            try {
                LastActionResult = $"Syncing {Plans.Count} plans to TS (profile: {profile.Name})...";

                var client = new AcpApiClient(settings.ServerUrl);
                var resp = await client.SyncToTsAsync(profileId).ConfigureAwait(false);
                InvalidateProgressCaches();

                Application.Current?.Dispatcher.Invoke(() => {
                    LastActionResult = "✓ " + (resp?.Report?.ToShortString() ?? "Sync complete.");
                });
                Logger.Info($"ACP: TS sync OK — {resp?.Report?.ToShortString()}");
            } catch (AcpUnauthorizedException ex) {
                Application.Current?.Dispatcher.Invoke(() => {
                    LastActionResult = $"✗ {ex.Message}";
                });
                Logger.Warning($"ACP: TS sync rejected: {ex.Message}");
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
