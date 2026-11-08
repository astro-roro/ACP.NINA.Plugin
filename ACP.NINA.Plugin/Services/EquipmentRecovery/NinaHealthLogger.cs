using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services.EquipmentRecovery {

    /// Reads the switch and camera every 5 seconds and hands the reading to
    /// HealthLogWriter. Runs on a background task, never on NINA's UI thread,
    /// and only reads: it never sets a switch or touches the cooler.
    public sealed class NinaHealthLogger : IDisposable {

        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

        public static string LogDirectory => Path.Combine(AcpSettings.SettingsDirectory, "health");

        private readonly ICameraMediator camera;
        private readonly IFilterWheelMediator filterWheel;
        private readonly IFocuserMediator focuser;
        private readonly IRotatorMediator rotator;
        private readonly IFlatDeviceMediator flatDevice;
        private readonly ISwitchMediator switches;
        private readonly Func<bool> isEnabled;
        private readonly HealthLogWriter writer = new HealthLogWriter(LogDirectory);
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private int errorsReported;

        public NinaHealthLogger(
            ICameraMediator camera, IFilterWheelMediator filterWheel, IFocuserMediator focuser,
            IRotatorMediator rotator, IFlatDeviceMediator flatDevice, ISwitchMediator switches,
            Func<bool> isEnabled
        ) {
            this.camera = camera;
            this.filterWheel = filterWheel;
            this.focuser = focuser;
            this.rotator = rotator;
            this.flatDevice = flatDevice;
            this.switches = switches;
            this.isEnabled = isEnabled ?? (() => true);
        }

        public void Start() {
            _ = Task.Run(() => LoopAsync(cts.Token));
            Logger.Info($"ACP: health log writing to {LogDirectory} while the switch or camera is connected.");
        }

        private async Task LoopAsync(CancellationToken ct) {
            while (!ct.IsCancellationRequested) {
                try {
                    await Task.Delay(Interval, ct).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    return;
                }
                try {
                    if (!isEnabled()) continue;
                    var snapshot = Read();
                    if (snapshot == null || !snapshot.ShouldLog) continue;
                    if (!writer.TryWrite(snapshot)) ReportError();
                } catch (Exception ex) {
                    // Never Error: ERROR lines become chat alerts.
                    Logger.Debug($"ACP: health log reading failed: {ex.Message}");
                }
            }
        }

        /// Warns on the first failed write and every 720th after (once an hour
        /// at one row every 5 s), so a full disk does not flood the log.
        private void ReportError() {
            var count = writer.ErrorCount;
            if (count == 1 || count - errorsReported >= 720) {
                errorsReported = count;
                Logger.Warning($"ACP: health log write failed ({count} so far): {writer.LastError}");
            }
        }

        private HealthSnapshot Read() {
            var s = new HealthSnapshot { Utc = DateTimeOffset.UtcNow };
            var devices = new List<(string, bool)> {
                ("camera", Safe(() => camera?.GetInfo()?.Connected == true)),
                ("filter wheel", Safe(() => filterWheel?.GetInfo()?.Connected == true)),
                ("focuser", Safe(() => focuser?.GetInfo()?.Connected == true)),
                ("rotator", Safe(() => rotator?.GetInfo()?.Connected == true)),
                ("flat device", Safe(() => flatDevice?.GetInfo()?.Connected == true)),
                ("switch", Safe(() => switches?.GetInfo()?.Connected == true)),
            };
            s.Devices = devices;

            try {
                var info = camera?.GetInfo();
                if (info != null && info.Connected) {
                    s.CameraTemperature = Real(info.Temperature);
                    s.CoolerPower = Real(info.CoolerPower);
                    s.CoolerOn = info.CoolerOn;
                }
            } catch (Exception) {
                // Leave the camera columns empty for this row.
            }

            var gauges = new List<(string Name, double Value)>();
            try {
                var info = switches?.GetInfo();
                if (info != null && info.Connected) {
                    if (info.ReadonlySwitches != null) {
                        foreach (var sw in info.ReadonlySwitches) gauges.Add((sw.Name, sw.Value));
                    }
                    if (info.WritableSwitches != null) {
                        foreach (var sw in info.WritableSwitches) gauges.Add((sw.Name, sw.Value));
                    }
                }
            } catch (Exception) {
                // Skip the row rather than write one with columns missing.
                return null;
            }
            s.Gauges = HealthCsv.KeepGaugeColumns(gauges, lastGaugeNames);
            if (gauges.Count > 0) lastGaugeNames = gauges.ConvertAll(g => g.Name);
            return s;
        }

        private List<string> lastGaugeNames = new List<string>();

        private static bool Safe(Func<bool> read) {
            try { return read(); } catch (Exception) { return false; }
        }

        private static double? Real(double v) => double.IsNaN(v) || double.IsInfinity(v) ? (double?)null : v;

        public void Dispose() {
            try {
                cts.Cancel();
                cts.Dispose();
            } catch (Exception) {
                // Already gone.
            }
        }
    }
}
