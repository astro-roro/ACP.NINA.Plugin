using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ACP.NINA.Plugin.Services.EquipmentRecovery {

    /// The NINA side of equipment recovery, kept as thin as it can be. All of
    /// the deciding happens in EquipmentRecoveryWatcher, which never sees a
    /// NINA type; this file only reads connection state from the mediators and
    /// calls their Connect and Disconnect.
    public sealed class NinaEquipmentRecovery : IDisposable {

        /// The longest one device's disconnect and connect may take before the
        /// watcher stops waiting and moves on. The call itself carries on in
        /// NINA; the next retry finds out whether it worked.
        public static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(60);

        private readonly EquipmentRecoveryWatcher watcher;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private bool started;

        private NinaEquipmentRecovery(EquipmentRecoveryWatcher watcher) {
            this.watcher = watcher;
        }

        /// Every mediator may be null: NINA imports them with AllowDefault, and
        /// a missing one simply means that kind of device is not watched. The
        /// mount, dome, safety monitor and weather mediators are deliberately
        /// not parameters, so recovery cannot reach them.
        public static NinaEquipmentRecovery Create(
            ICameraMediator camera,
            IFilterWheelMediator filterWheel,
            IFocuserMediator focuser,
            IRotatorMediator rotator,
            IFlatDeviceMediator flatDevice,
            ISwitchMediator switches,
            ISequenceMediator sequence,
            IProfileService profileService,
            Func<bool> isEnabled,
            IPowerBoxSource powerBox = null
        ) {
            var devices = new List<IRecoveryDevice>();
            IRecoveryDevice cameraDevice = null;
            if (camera != null) {
                cameraDevice = new MediatorDevice("camera", () => camera.GetInfo()?.Connected == true, camera.Connect, camera.Disconnect);
                devices.Add(cameraDevice);
            }
            if (filterWheel != null) devices.Add(new MediatorDevice("filter wheel", () => filterWheel.GetInfo()?.Connected == true, filterWheel.Connect, filterWheel.Disconnect));
            if (focuser != null) {
                devices.Add(new MediatorDevice("focuser", () => focuser.GetInfo()?.Connected == true, focuser.Connect, focuser.Disconnect,
                    () => {
                        var info = focuser.GetInfo();
                        return info != null && info.Connected ? info.Position : (double?)null;
                    }));
            }
            if (rotator != null) devices.Add(new MediatorDevice("rotator", () => rotator.GetInfo()?.Connected == true, rotator.Connect, rotator.Disconnect));
            if (flatDevice != null) devices.Add(new MediatorDevice("flat device", () => flatDevice.GetInfo()?.Connected == true, flatDevice.Connect, flatDevice.Disconnect));
            if (switches != null) devices.Add(new MediatorDevice("switch", () => switches.GetInfo()?.Connected == true, switches.Connect, switches.Disconnect));

            var watcher = new EquipmentRecoveryWatcher(
                devices,
                camera != null ? new CameraCooler(camera, profileService) : null,
                new NinaLog(),
                isEnabled,
                () => sequence?.IsAdvancedSequenceRunning() == true,
                () => DateTimeOffset.UtcNow,
                cameraDevice,
                powerBox);
            return new NinaEquipmentRecovery(watcher);
        }

        public void Start() {
            if (started) return;
            started = true;
            _ = LoopAsync(cts.Token);
            Logger.Info("ACP: equipment recovery watcher started.");
        }

        private async Task LoopAsync(CancellationToken ct) {
            while (!ct.IsCancellationRequested) {
                try {
                    await Task.Delay(EquipmentRecoveryWatcher.PollInterval, ct).ConfigureAwait(false);
                    await watcher.TickAsync(ct).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    return;
                } catch (Exception ex) {
                    // Never Error here: ERROR lines become chat alerts.
                    Logger.Warning($"ACP: equipment recovery check failed: {ex.Message}");
                }
            }
        }

        public void Dispose() {
            try {
                cts.Cancel();
                cts.Dispose();
            } catch (Exception) {
                // Already gone.
            }
        }

        /// Runs a mediator call on NINA's UI thread, where the Equipment tab's
        /// own Connect and Disconnect buttons run it.
        private static Task<T> OnUiThread<T>(Func<Task<T>> call) {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) return call();
            return dispatcher.InvokeAsync(call).Task.Unwrap();
        }

        private sealed class MediatorDevice : IRecoveryDevice {
            private readonly Func<bool> isConnected;
            private readonly Func<Task<bool>> connect;
            private readonly Func<Task> disconnect;
            private readonly Func<double?> position;

            public MediatorDevice(string kind, Func<bool> isConnected, Func<Task<bool>> connect, Func<Task> disconnect, Func<double?> position = null) {
                Kind = kind;
                this.isConnected = isConnected;
                this.connect = connect;
                this.disconnect = disconnect;
                this.position = position;
            }

            public string Kind { get; }

            public bool IsConnected() => isConnected();

            public double? ReadPosition() => position?.Invoke();

            public async Task<bool> ReconnectAsync(CancellationToken ct) {
                var work = OnUiThread(async () => {
                    try {
                        await disconnect().ConfigureAwait(true);
                    } catch (Exception ex) {
                        Logger.Debug($"ACP: disconnecting the {Kind} before reconnecting it failed: {ex.Message}");
                    }
                    return await connect().ConfigureAwait(true);
                });
                var finished = await Task.WhenAny(work, Task.Delay(ReconnectTimeout, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (finished != work) {
                    Logger.Warning($"ACP: reconnecting the {Kind} took longer than {ReconnectTimeout.TotalSeconds:0} s, will check again on the next try.");
                    return false;
                }
                return await work.ConfigureAwait(false);
            }
        }

        private sealed class CameraCooler : IRecoveryCooler {
            private readonly ICameraMediator camera;
            private readonly IProfileService profileService;

            public CameraCooler(ICameraMediator camera, IProfileService profileService) {
                this.camera = camera;
                this.profileService = profileService;
            }

            public double? ActiveSetPoint() {
                var info = camera.GetInfo();
                if (info == null || !info.Connected || !info.CanSetTemperature || !info.CoolerOn) return null;
                return info.TemperatureSetPoint;
            }

            /// NINA reports NaN for a reading the camera does not give.
            public (double? Temperature, double? CoolerPower) ReadThermals() {
                var info = camera.GetInfo();
                if (info == null || !info.Connected) return (null, null);
                return (Real(info.Temperature), Real(info.CoolerPower));
            }

            private static double? Real(double v) => double.IsNaN(v) || double.IsInfinity(v) ? (double?)null : v;

            public double? ProfileSetPoint() {
                double? t = profileService?.ActiveProfile?.CameraSettings?.Temperature;
                return t;
            }

            /// Starts NINA's own cool camera routine with no ramp and returns
            /// straight away, so a slow cool down never holds up the watcher.
            public Task RestoreAsync(double setPoint, CancellationToken ct) {
                _ = Task.Run(async () => {
                    try {
                        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                            limit.CancelAfter(TimeSpan.FromMinutes(30));
                            await camera.CoolCamera(setPoint, TimeSpan.Zero, new Progress<ApplicationStatus>(_ => { }), limit.Token)
                                .ConfigureAwait(false);
                        }
                    } catch (OperationCanceledException) {
                    } catch (Exception ex) {
                        Logger.Warning($"ACP: turning the camera's cooler back on failed: {ex.Message}");
                    }
                });
                return Task.CompletedTask;
            }
        }

        private sealed class NinaLog : IRecoveryLog {
            public void Info(string message) => Logger.Info(message);
            public void Warning(string message) => Logger.Warning(message);
            public void Error(string message) => Logger.Error(message);
        }
    }
}
