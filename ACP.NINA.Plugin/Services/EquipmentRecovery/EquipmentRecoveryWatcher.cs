using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services.EquipmentRecovery {

    /// One piece of equipment the watcher looks after: the camera, the filter
    /// wheel, the focuser, the rotator, the flat device or the switch. The
    /// mount, dome, safety monitor and weather unit are never wrapped in one
    /// of these, so the watcher cannot touch them.
    public interface IRecoveryDevice {
        /// Plain name for the log, such as "camera".
        string Kind { get; }

        bool IsConnected();

        /// NINA's own disconnect then connect, the same as pressing the two
        /// buttons in the Equipment tab. True when the device says it is
        /// connected afterwards.
        Task<bool> ReconnectAsync(CancellationToken ct);

        /// The focuser's position, read only. Null for everything else, and
        /// for a focuser that cannot say.
        double? ReadPosition();
    }

    /// The camera's cooler, read while connected and restored after a drop.
    public interface IRecoveryCooler {
        /// The set point while the cooler is on, or null when it is off or
        /// the camera cannot cool.
        double? ActiveSetPoint();

        /// Sensor temperature in °C and cooler power in percent, each null
        /// when the camera does not report it.
        (double? Temperature, double? CoolerPower) ReadThermals();

        /// The set point saved in the NINA profile, or null when there is none.
        double? ProfileSetPoint();

        /// Turn the cooler on and head for this set point. Returns once the
        /// request is made; the camera ramps on its own time.
        Task RestoreAsync(double setPoint, CancellationToken ct);
    }

    /// Where the watcher's lines go. Error is used once per drop at most, for
    /// the final give up, because another plugin turns ERROR lines into chat
    /// alerts.
    public interface IRecoveryLog {
        void Info(string message);
        void Warning(string message);
        void Error(string message);
    }

    /// Brings equipment back after it drops off USB in the middle of a night.
    ///
    /// The whole USB hub can drop at once and come back a few seconds later.
    /// NINA then shows the devices as disconnected and leaves them that way,
    /// and the running sequence fails every filter change and exposure until
    /// someone presses Disconnect and Connect by hand. This does that for them.
    ///
    /// It is driven by TickAsync, which the glue calls every couple of
    /// seconds. Each tick reads who is connected, compares it with the last
    /// tick, and moves one step through a small state machine:
    ///
    /// Watching: no sequence running means nothing to do apart from noting
    /// who is connected. With a sequence running, a device that was connected
    /// on the last tick and is not now has dropped.
    ///
    /// Recovering: wait SettleDelay so every device that is going to drop has
    /// done so, log one line naming them, then reconnect each one that has not
    /// come back by itself. Retry every RetryInterval. The camera gets its
    /// cooler back once it reconnects.
    ///
    /// GaveUp: GiveUpAfter has passed with something still missing. One ERROR
    /// line, no more retries until everything is back or the sequence stops.
    ///
    /// A device disconnected while no sequence runs is never in the baseline,
    /// so starting a sequence later does not reconnect it.
    public class EquipmentRecoveryWatcher {

        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(10);

        /// A focuser that comes back more than this many steps from where it
        /// was gets a warning that focus may be off.
        public const double FocusShiftWarningSteps = 1;

        public enum State { Watching, Recovering, GaveUp }

        private readonly IReadOnlyList<IRecoveryDevice> devices;
        private readonly IRecoveryCooler cooler;
        private readonly IRecoveryLog log;
        private readonly Func<bool> isEnabled;
        private readonly Func<bool> isSequenceRunning;
        private readonly Func<DateTimeOffset> clock;

        private HashSet<IRecoveryDevice> connectedLastTick = new HashSet<IRecoveryDevice>();
        private readonly Dictionary<IRecoveryDevice, double?> positionBeforeDrop = new Dictionary<IRecoveryDevice, double?>();
        private double? lastCoolerSetPoint;
        private bool coolerReadOnce;
        private double? lastTemperature;
        private double? temperatureAtDrop;

        private readonly List<IRecoveryDevice> dropped = new List<IRecoveryDevice>();
        private readonly HashSet<IRecoveryDevice> stillMissing = new HashSet<IRecoveryDevice>();
        private DateTimeOffset dropAt;
        private DateTimeOffset? lastAttemptAt;
        private bool announced;
        private double? coolerSetPointAtDrop;
        private bool coolerKnownAtDrop;
        private IRecoveryDevice cameraDevice;
        private readonly IPowerBoxSource powerBox;
        private readonly TimeZoneInfo zone;
        private bool powerBoxLogged;

        public EquipmentRecoveryWatcher(
            IEnumerable<IRecoveryDevice> devices,
            IRecoveryCooler cooler,
            IRecoveryLog log,
            Func<bool> isEnabled,
            Func<bool> isSequenceRunning,
            Func<DateTimeOffset> clock,
            IRecoveryDevice camera = null,
            IPowerBoxSource powerBox = null,
            TimeZoneInfo zone = null
        ) {
            this.powerBox = powerBox;
            this.zone = zone ?? TimeZoneInfo.Local;
            this.devices = (devices ?? Enumerable.Empty<IRecoveryDevice>()).Where(d => d != null).ToList();
            this.cooler = cooler;
            this.log = log;
            this.isEnabled = isEnabled ?? (() => true);
            this.isSequenceRunning = isSequenceRunning ?? (() => false);
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
            cameraDevice = camera;
        }

        public State Current { get; private set; } = State.Watching;

        /// Devices still missing in the current drop, for the tests.
        public IReadOnlyCollection<IRecoveryDevice> Missing => stillMissing.ToList();

        public async Task TickAsync(CancellationToken ct = default) {
            var now = clock();
            var connected = new HashSet<IRecoveryDevice>(devices.Where(SafeIsConnected));
            var active = isEnabled() && isSequenceRunning();

            switch (Current) {
                case State.Watching:
                    if (active) {
                        var lost = devices.Where(d => connectedLastTick.Contains(d) && !connected.Contains(d)).ToList();
                        if (lost.Count > 0) {
                            BeginRecovery(lost, now);
                            break;
                        }
                    }
                    RememberHealthyState(connected);
                    break;

                case State.Recovering:
                    if (!active) {
                        log.Info(
                            $"ACP: equipment recovery stopped because {(isEnabled() ? "the sequence is no longer running" : "the option was turned off")}. "
                            + $"Still disconnected: {Names(stillMissing)}.");
                        Reset(connected);
                        break;
                    }
                    await RecoverStepAsync(connected, now, ct).ConfigureAwait(false);
                    break;

                case State.GaveUp:
                    // Re-arm once someone has sorted it out, or the night ended.
                    if (stillMissing.All(connected.Contains)) {
                        log.Info($"ACP: equipment is connected again after {FormatDuration(now - dropAt)}: {Names(dropped)}.");
                        Reset(connected);
                    } else if (!active) {
                        Reset(connected);
                    }
                    break;
            }
        }

        private void BeginRecovery(List<IRecoveryDevice> lost, DateTimeOffset now) {
            Current = State.Recovering;
            dropAt = now;
            lastAttemptAt = null;
            announced = false;
            dropped.Clear();
            stillMissing.Clear();
            coolerSetPointAtDrop = lastCoolerSetPoint;
            coolerKnownAtDrop = coolerReadOnce;
            temperatureAtDrop = lastTemperature;
            powerBoxLogged = false;
            foreach (var d in lost) AddDropped(d);
        }

        private void AddDropped(IRecoveryDevice d) {
            if (dropped.Contains(d)) return;
            dropped.Add(d);
            stillMissing.Add(d);
        }

        private async Task RecoverStepAsync(HashSet<IRecoveryDevice> connected, DateTimeOffset now, CancellationToken ct) {
            // A hub drop takes a second or two to reach every device, so
            // anything else from the baseline that goes before the first line
            // is part of the same drop.
            if (!announced) {
                foreach (var d in devices) {
                    if (connectedLastTick.Contains(d) && !connected.Contains(d)) AddDropped(d);
                }
                if (now - dropAt < SettleDelay) return;
                announced = true;
                log.Info($"ACP: equipment dropped while the sequence was running: {Names(dropped)}. Reconnecting every {RetryInterval.TotalSeconds:0} s for up to {GiveUpAfter.TotalMinutes:0} minutes.");
            }

            // Some come back by themselves: NINA retries ASCOM devices once.
            foreach (var d in stillMissing.Where(connected.Contains).ToList()) {
                await MarkBackAsync(d, ct).ConfigureAwait(false);
            }
            await TryLogPowerBoxAsync(ct).ConfigureAwait(false);

            if (stillMissing.Count == 0) {
                Finish(now);
                return;
            }

            if (now - dropAt >= GiveUpAfter) {
                Current = State.GaveUp;
                log.Error($"ACP: equipment did not come back after {GiveUpAfter.TotalMinutes:0} minutes and needs attention: {Names(stillMissing)} still disconnected. Stopped trying to reconnect.");
                return;
            }

            if (lastAttemptAt.HasValue && now - lastAttemptAt.Value < RetryInterval) return;
            lastAttemptAt = now;

            // Devices in the order they were listed, which puts the camera first.
            foreach (var d in devices.Where(stillMissing.Contains).ToList()) {
                if (ct.IsCancellationRequested) return;
                bool ok;
                try {
                    ok = await d.ReconnectAsync(ct).ConfigureAwait(false) && SafeIsConnected(d);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    log.Warning($"ACP: reconnecting the {d.Kind} failed, will try again: {ex.Message}");
                    ok = false;
                }
                if (ok) await MarkBackAsync(d, ct).ConfigureAwait(false);
            }

            if (stillMissing.Count == 0) {
                await TryLogPowerBoxAsync(ct).ConfigureAwait(false);
                Finish(clock());
            }
        }

        /// One line per drop saying when the power box was powered on, read
        /// from the Pegasus Unity server. Waits while the switch is still
        /// missing, since Unity may hold a stale report until the box is back.
        private async Task TryLogPowerBoxAsync(CancellationToken ct) {
            if (powerBox == null || powerBoxLogged) return;
            if (stillMissing.Any(d => d.Kind == "switch")) return;
            IReadOnlyList<PowerBoxReport> reports;
            try {
                reports = await powerBox.ReadAllAsync(ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception) {
                return;
            }
            var upTime = PowerBoxRestart.FirstUpTime(reports);
            if (!upTime.HasValue) return;
            powerBoxLogged = true;
            log.Info(PowerBoxRestart.Describe(clock(), upTime.Value, dropAt, zone));
        }

        private async Task MarkBackAsync(IRecoveryDevice d, CancellationToken ct) {
            if (!stillMissing.Remove(d)) return;
            log.Info($"ACP: the {d.Kind} is connected again.");

            if (ReferenceEquals(d, cameraDevice)) {
                await RestoreCoolerAsync(ct).ConfigureAwait(false);
            }

            if (positionBeforeDrop.TryGetValue(d, out var before) && before.HasValue) {
                var after = SafeReadPosition(d);
                if (after.HasValue && Math.Abs(after.Value - before.Value) >= FocusShiftWarningSteps) {
                    log.Warning($"ACP: the {d.Kind} came back at position {after.Value:0}, not {before.Value:0}. Focus may be off until the sequence's next autofocus. The plugin does not move the focuser.");
                }
            }
        }

        /// The set point to restore. When the cooler was read before the drop
        /// its state wins: on means its set point, off means leave it off.
        /// Only when it was never read does the profile's set point stand in.
        /// Null means do not touch the cooler.
        public static double? ChooseSetPoint(bool readBeforeDrop, double? beforeDrop, double? profile) =>
            readBeforeDrop ? beforeDrop : profile;

        private async Task RestoreCoolerAsync(CancellationToken ct) {
            if (cooler == null) return;
            LogThermalsOnReturn();
            double? profile = null;
            try { profile = cooler.ProfileSetPoint(); } catch (Exception) { }
            var target = ChooseSetPoint(coolerKnownAtDrop, coolerSetPointAtDrop, profile);
            if (!target.HasValue) {
                log.Info(coolerKnownAtDrop
                    ? "ACP: the camera's cooler was off before the drop, so it is left off."
                    : "ACP: the camera's cooler was not restored: no set point was known from before the drop or from the profile.");
                return;
            }
            try {
                await cooler.RestoreAsync(target.Value, ct).ConfigureAwait(false);
                log.Info(FormattableString.Invariant($"ACP: the camera's cooler is on again, heading for {target.Value:0.#} °C."));
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                log.Warning($"ACP: could not turn the camera's cooler back on: {ex.Message}");
            }
        }

        /// One line before the cooler is touched. A camera that comes back warm
        /// lost power along with USB; one that is still cold lost only USB.
        private void LogThermalsOnReturn() {
            (double? Temperature, double? CoolerPower) now = (null, null);
            try { now = cooler.ReadThermals(); } catch (Exception) { }
            log.Info(DescribeThermals(now.Temperature, now.CoolerPower, temperatureAtDrop));
        }

        public static string DescribeThermals(double? temperature, double? coolerPower, double? beforeDrop) {
            var text = "ACP: the camera came back with the sensor at "
                + (temperature.HasValue ? FormattableString.Invariant($"{temperature.Value:0.#} °C") : "an unknown temperature")
                + ", cooler power "
                + (coolerPower.HasValue ? FormattableString.Invariant($"{coolerPower.Value:0} %") : "unknown")
                + ", and "
                + (beforeDrop.HasValue ? FormattableString.Invariant($"{beforeDrop.Value:0.#} °C just before the drop.") : "no reading from before the drop.");
            return text;
        }

        private void Finish(DateTimeOffset now) {
            var lost = now - dropAt;
            log.Info($"ACP: equipment recovered: {Names(dropped)} back after {FormatDuration(lost)}.");
            Reset(new HashSet<IRecoveryDevice>(devices.Where(SafeIsConnected)));
        }

        private void Reset(HashSet<IRecoveryDevice> connected) {
            Current = State.Watching;
            dropped.Clear();
            stillMissing.Clear();
            lastAttemptAt = null;
            announced = false;
            RememberHealthyState(connected);
        }

        private void RememberHealthyState(HashSet<IRecoveryDevice> connected) {
            connectedLastTick = connected;
            foreach (var d in connected) {
                positionBeforeDrop[d] = SafeReadPosition(d);
            }
            if (cameraDevice != null && connected.Contains(cameraDevice) && cooler != null) {
                try {
                    lastCoolerSetPoint = cooler.ActiveSetPoint();
                    coolerReadOnce = true;
                } catch (Exception) {
                    // Keep the last good reading.
                }
                try {
                    var t = cooler.ReadThermals().Temperature;
                    if (t.HasValue) lastTemperature = t;
                } catch (Exception) {
                    // Keep the last good reading.
                }
            }
        }

        private static bool SafeIsConnected(IRecoveryDevice d) {
            try { return d.IsConnected(); } catch (Exception) { return false; }
        }

        private static double? SafeReadPosition(IRecoveryDevice d) {
            try { return d.ReadPosition(); } catch (Exception) { return null; }
        }

        private static string Names(IEnumerable<IRecoveryDevice> list) =>
            string.Join(", ", list.Select(d => d.Kind));

        public static string FormatDuration(TimeSpan t) {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalMinutes < 1) return $"{t.TotalSeconds:0} s";
            return $"{(int)t.TotalMinutes} min {t.Seconds} s";
        }
    }
}
