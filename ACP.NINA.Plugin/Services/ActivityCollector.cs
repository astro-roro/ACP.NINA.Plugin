using ACP.NINA.Plugin.Models;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using System;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services {

    /// Listens to NINA for the things the Activity tab reports and turns each
    /// into an event: light frames, autofocus runs, meridian flips, park and
    /// unpark, the safety monitor, and the start and end of a sequence.
    ///
    /// The NINA handlers only unpack their arguments and call the On* methods,
    /// which hold every decision. That keeps the decisions testable without
    /// NINA running.
    ///
    /// Every handler swallows its own failures. An exception escaping an event
    /// callback is this plugin's bug landing in somebody else's imaging run.
    ///
    /// Every NINA mediator is optional. One that is missing, or that refuses
    /// the subscription, costs that one kind of event and nothing else.
    public class ActivityCollector : IFocuserConsumer {

        private readonly Action<ActivityEvent> post;
        private readonly Func<string> profileId;
        private readonly Func<string> profileName;
        private readonly Func<DateTimeOffset> clock;
        private readonly Func<string, int?> tsTargetIdByName;
        private readonly Func<int?, string, Task<string>> planIdFor;

        private readonly ImagingTracker imaging;
        private readonly AutofocusTracker autofocus;
        private readonly object gate = new object();

        private readonly Func<bool> isActive;
        private readonly Channel<Func<Task>> work =
            Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(2000) {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        private DateTimeOffset? sessionStart;
        private bool? lastSafe;

        private IImageSaveMediator imageSave;
        private ITelescopeMediator telescope;
        private ISafetyMonitorMediator safety;
        private IFocuserMediator focuser;
        private ISequenceMediator sequence;

        /// <param name="tsTargetIdByName">Target Scheduler's id for a target
        /// name, or null. Frames carry a name; the event key wants the id.</param>
        /// <param name="imagingSeeder">Frames already taken tonight, read back
        /// from Target Scheduler, so totals survive a NINA restart.</param>
        /// <param name="isActive">False while reporting is switched off or the
        /// reporter has disabled itself. Saved frames then cause no database
        /// reads and no ACP calls.</param>
        /// <param name="planIdFor">The ACP plan that owns a Target Scheduler
        /// target, or null.</param>
        public ActivityCollector(
            Action<ActivityEvent> post,
            Func<string> profileId,
            Func<string> profileName,
            Func<DateTimeOffset> clock,
            Func<string, int?> tsTargetIdByName,
            Func<int?, string, Task<string>> planIdFor,
            Func<ImagingFrame, System.Collections.Generic.IEnumerable<ImagingSeed>> imagingSeeder = null,
            Func<bool> isActive = null
        ) {
            this.isActive = isActive ?? (() => true);
            this.post = post ?? (e => { });
            this.profileId = profileId ?? (() => null);
            this.profileName = profileName ?? (() => null);
            this.clock = clock ?? (() => DateTimeOffset.Now);
            this.tsTargetIdByName = tsTargetIdByName ?? (n => null);
            this.planIdFor = planIdFor ?? ((id, n) => Task.FromResult<string>(null));
            imaging = new ImagingTracker(() => this.clock(), imagingSeeder);
            // The session start goes in the autofocus key (see AutofocusTracker).
            autofocus = new AutofocusTracker(() => this.clock(), this.clock());
            _ = Task.Run(ConsumeFramesAsync);
        }

        // -- Decisions ---------------------------------------------------------

        /// One saved light frame. Looks up the Target Scheduler id and the ACP
        /// plan, and carries on without them when either lookup fails.
        public async Task OnLightFrameAsync(string targetName, string filter, double exposureSeconds, DateTimeOffset at) {
            if (!isActive()) return;
            // The Target Scheduler id is looked up before the tracker picks a
            // key, so a frame is counted under the id whenever one resolves.
            int? tsId = null;
            string planId = null;
            try { tsId = tsTargetIdByName(targetName); } catch (Exception ex) {
                Logger.Debug($"ACP: could not find the Target Scheduler id for {targetName}: {ex.Message}");
            }
            try { planId = await planIdFor(tsId, targetName).ConfigureAwait(false); } catch (Exception ex) {
                Logger.Debug($"ACP: could not find the ACP plan for {targetName}: {ex.Message}");
            }

            post(imaging.AddFrame(new ImagingFrame {
                ProfileId = profileId(),
                ProfileName = profileName(),
                TsTargetId = tsId,
                TargetName = targetName,
                PlanId = planId,
                Filter = filter,
                ExposureSeconds = exposureSeconds,
                At = at,
            }));
        }

        /// Queue a frame for the one consumer that handles frames in arrival
        /// order. Lookups and the tracker then never run for two frames at once.
        public void EnqueueLightFrame(string targetName, string filter, double exposureSeconds, DateTimeOffset at) {
            work.Writer.TryWrite(() => OnLightFrameAsync(targetName, filter, exposureSeconds, at));
        }

        /// Completes when every frame queued before the call has been handled.
        public Task FlushFramesAsync() {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!work.Writer.TryWrite(() => { done.TrySetResult(true); return Task.CompletedTask; })) {
                done.TrySetResult(true);
            }
            return done.Task;
        }

        private async Task ConsumeFramesAsync() {
            var reader = work.Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false)) {
                while (reader.TryRead(out var job)) {
                    try {
                        await job().ConfigureAwait(false);
                    } catch (Exception ex) {
                        Logger.Debug($"ACP: could not record a light frame: {ex.Message}");
                    }
                }
            }
        }

        /// A saved image from NINA. Returns true when it was a light frame taken
        /// for reporting. Returns early, before any lookup, when reporting is off.
        public bool HandleSavedImage(string imageType, string targetName, string filter, double exposureSeconds, DateTime exposureStart) {
            if (!isActive()) return false;
            if (!string.Equals(imageType, "LIGHT", StringComparison.OrdinalIgnoreCase)) return false;
            var at = exposureStart.Year > 2000 ? new DateTimeOffset(exposureStart) : clock();
            EnqueueLightFrame(targetName, filter, exposureSeconds, at);
            return true;
        }

        public void OnAutofocus(double position, double temperatureC, DateTimeOffset at) {
            post(autofocus.AddRun(new AutofocusRun {
                ProfileId = profileId(), ProfileName = profileName(),
                At = at, Position = position, TemperatureC = temperatureC,
            }));
        }

        public void OnMeridianFlip(bool recentred) {
            post(ActivityEvents.MeridianFlip(clock(), profileId(), profileName(), recentred));
        }

        public void OnParked(bool parked) {
            post(ActivityEvents.Park(clock(), profileId(), profileName(), parked));
        }

        /// Only a change is reported. A safety monitor that re-announces the same
        /// reading every poll would otherwise fill the night with one row each.
        public void OnSafetyChanged(bool isSafe) {
            lock (gate) {
                if (lastSafe == isSafe) return;
                lastSafe = isSafe;
            }
            post(ActivityEvents.Safety(clock(), profileId(), profileName(), isSafe));
        }

        public void OnSequenceStarting() {
            var now = clock();
            lock (gate) {
                if (sessionStart.HasValue) return;
                sessionStart = now;
            }
            post(ActivityEvents.SessionStart(now, profileId(), profileName()));
        }

        public void OnSequenceFinished() {
            string reason;
            lock (gate) { reason = lastSafe == false ? "sequence finished while the safety monitor read unsafe" : "sequence finished"; }
            EndSession(reason);
        }

        /// NINA is closing: end a session that is still open, once.
        public void Close() {
            EndSession("NINA closed");
        }

        private void EndSession(string reason) {
            DateTimeOffset started;
            lock (gate) {
                if (!sessionStart.HasValue) return;
                started = sessionStart.Value;
                sessionStart = null;
            }
            post(ActivityEvents.SessionEnd(started, clock(), profileId(), profileName(), reason));
        }

        // -- NINA wiring ---------------------------------------------------------

        public void Subscribe(
            IImageSaveMediator imageSave,
            ITelescopeMediator telescope,
            ISafetyMonitorMediator safety,
            IFocuserMediator focuser,
            ISequenceMediator sequence
        ) {
            this.imageSave = imageSave;
            this.telescope = telescope;
            this.safety = safety;
            this.focuser = focuser;
            this.sequence = sequence;

            Try("image saved", () => { if (imageSave != null) imageSave.ImageSaved += OnImageSaved; });
            Try("meridian flip", () => { if (telescope != null) telescope.AfterMeridianFlip += OnAfterMeridianFlip; });
            Try("park", () => { if (telescope != null) telescope.Parked += OnTelescopeParked; });
            Try("unpark", () => { if (telescope != null) telescope.Unparked += OnTelescopeUnparked; });
            Try("safety", () => { if (safety != null) safety.IsSafeChanged += OnIsSafeChanged; });
            Try("autofocus", () => focuser?.RegisterConsumer(this));
            Try("sequence start", () => { if (sequence != null) sequence.SequenceStarting += OnSequenceStartingEvent; });
            Try("sequence end", () => { if (sequence != null) sequence.SequenceFinished += OnSequenceFinishedEvent; });
        }

        public void Unsubscribe() {
            Try("image saved", () => { if (imageSave != null) imageSave.ImageSaved -= OnImageSaved; });
            Try("meridian flip", () => { if (telescope != null) telescope.AfterMeridianFlip -= OnAfterMeridianFlip; });
            Try("park", () => { if (telescope != null) telescope.Parked -= OnTelescopeParked; });
            Try("unpark", () => { if (telescope != null) telescope.Unparked -= OnTelescopeUnparked; });
            Try("safety", () => { if (safety != null) safety.IsSafeChanged -= OnIsSafeChanged; });
            Try("autofocus", () => focuser?.RemoveConsumer(this));
            Try("sequence start", () => { if (sequence != null) sequence.SequenceStarting -= OnSequenceStartingEvent; });
            Try("sequence end", () => { if (sequence != null) sequence.SequenceFinished -= OnSequenceFinishedEvent; });
        }

        private static void Try(string what, Action action) {
            try {
                action();
            } catch (Exception ex) {
                Logger.Debug($"ACP: activity events for {what} are unavailable: {ex.Message}");
            }
        }

        private void OnImageSaved(object sender, ImageSavedEventArgs e) {
            try {
                var image = e?.MetaData?.Image;
                if (image == null) return;
                HandleSavedImage(image.ImageType, e.MetaData.Target?.Name, e.Filter, image.ExposureTime, image.ExposureStart);
            } catch (Exception ex) {
                Logger.Debug($"ACP: could not read a saved image: {ex.Message}");
            }
        }

        private Task OnAfterMeridianFlip(object sender, AfterMeridianFlipEventArgs e) {
            Try("meridian flip", () => OnMeridianFlip(e != null && e.Success));
            return Task.CompletedTask;
        }

        private Task OnTelescopeParked(object sender, EventArgs e) {
            Try("park", () => OnParked(true));
            return Task.CompletedTask;
        }

        private Task OnTelescopeUnparked(object sender, EventArgs e) {
            Try("unpark", () => OnParked(false));
            return Task.CompletedTask;
        }

        private void OnIsSafeChanged(object sender, IsSafeEventArgs e) {
            Try("safety", () => OnSafetyChanged(e.IsSafe));
        }

        private Task OnSequenceStartingEvent(object sender, EventArgs e) {
            Try("sequence start", OnSequenceStarting);
            return Task.CompletedTask;
        }

        private Task OnSequenceFinishedEvent(object sender, EventArgs e) {
            Try("sequence end", OnSequenceFinished);
            return Task.CompletedTask;
        }

        // -- IFocuserConsumer ----------------------------------------------------

        public void UpdateEndAutoFocusRun(AutoFocusInfo info) {
            Try("autofocus", () => {
                if (info == null) return;
                var at = info.Timestamp.Year > 2000 ? new DateTimeOffset(info.Timestamp) : clock();
                OnAutofocus(info.Position, info.Temperature, at);
            });
        }

        public void UpdateUserFocused(FocuserInfo info) { }

        public void UpdateDeviceInfo(FocuserInfo deviceInfo) { }

        public void Dispose() {
            Unsubscribe();
            work.Writer.TryComplete();
        }
    }
}
