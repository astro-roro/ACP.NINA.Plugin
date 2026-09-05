using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Core.Enum;
using System.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// Whether a Target Scheduler container is running right now, so the push
    /// can stay out of its way.
    public interface ITsContainerWatch {

        /// True while Target Scheduler looks to be running a container.
        bool IsRunning { get; }

        /// Why IsRunning says what it says, in words for the sequencer log.
        string Explain();
    }

    /// Listens to Target Scheduler's pub/sub topics to know when it is running.
    ///
    /// Target Scheduler publishes but never subscribes, and there is no
    /// ContainerStarted topic: the topics it emits are WaitStart,
    /// NewTargetStart and TargetStart during a run, and ContainerStopped and
    /// TargetComplete at the end. So a run is inferred rather than announced.
    /// Any of the three start topics means a container is going, and
    /// ContainerStopped means it has finished.
    ///
    /// The topics alone leave a window at the start of a run. The container
    /// publishes nothing until its planning engine has finished, and that
    /// engine reads this same database for twenty seconds or more. The bench
    /// on 2026-09-05 pushed three times inside that window. So the sequencer
    /// itself is asked too: when an advanced sequence is running and one of
    /// its target containers is Target Scheduler's and is in the RUNNING
    /// state, that counts as running whatever the topics say.
    ///
    /// That leaves one gap. If NINA is killed mid-run, or a container ends in a
    /// way that publishes nothing, the last thing this ever heard is a start
    /// and the push would refuse for the rest of the session. StaleAfter is the
    /// guard: a run with no event at all for that long is treated as over. It
    /// is long enough that a single target imaged all night still counts as
    /// running, because Target Scheduler emits a TargetStart every time it
    /// picks the next thing to shoot.
    [Export(typeof(ITsContainerWatch))]
    public class TsContainerWatch : ITsContainerWatch, ISubscriber, IDisposable {

        /// Target Scheduler's own MessageSenderId. Checked on every message so
        /// another plugin publishing on the same topic name cannot decide
        /// whether this one writes.
        public static readonly Guid TargetSchedulerSenderId =
            new Guid("B4541BA9-7B07-4D71-B8E1-6C73D4933EA0");

        public const string TopicWaitStart = "TargetScheduler-WaitStart";
        public const string TopicNewTargetStart = "TargetScheduler-NewTargetStart";
        public const string TopicTargetStart = "TargetScheduler-TargetStart";
        public const string TopicContainerStopped = "TargetScheduler-ContainerStopped";
        public const string TopicTargetComplete = "TargetScheduler-TargetComplete";

        /// The topics that mean a container is under way.
        public static readonly string[] StartTopics =
            { TopicWaitStart, TopicNewTargetStart, TopicTargetStart };

        /// The topics that mean it has finished. TargetComplete only says one
        /// target is done and the container may well carry on, so it is not
        /// here; only ContainerStopped clears the flag.
        public static readonly string[] StopTopics = { TopicContainerStopped };

        public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(3);

        private readonly IMessageBroker broker;
        private readonly ISequenceMediator sequenceMediator;
        private readonly Func<DateTime> clock;
        private readonly object gate = new object();

        private bool running;
        private DateTime lastEventUtc = DateTime.MinValue;
        private string lastTopic;
        private bool subscribed;

        /// The broker is optional. When NINA does not hand one over there is no
        /// way to know what Target Scheduler is doing, and refusing every push
        /// on that basis would break the feature for everyone rather than
        /// protect anyone. The push goes ahead and says the guard is off.
        [ImportingConstructor]
        public TsContainerWatch(
            [Import(AllowDefault = true)] IMessageBroker broker,
            [Import(AllowDefault = true)] ISequenceMediator sequenceMediator
        ) : this(broker, () => DateTime.UtcNow, sequenceMediator) { }

        public TsContainerWatch(IMessageBroker broker, Func<DateTime> clock)
            : this(broker, clock, null) { }

        public TsContainerWatch(IMessageBroker broker, Func<DateTime> clock, ISequenceMediator sequenceMediator) {
            this.broker = broker;
            this.sequenceMediator = sequenceMediator;
            this.clock = clock ?? (() => DateTime.UtcNow);
            Subscribe();
        }

        /// Ask the sequencer directly. Covers the planning window before the
        /// first topic, and a container that was already running when NINA
        /// composed this plugin. Any failure here means "cannot tell", which
        /// falls through to the topics.
        private bool SequencerShowsContainerRunning() {
            try {
                if (sequenceMediator == null || !sequenceMediator.IsAdvancedSequenceRunning()) return false;
                var targets = sequenceMediator.GetAllTargetsInAdvancedSequence();
                return targets != null && targets.Any(t =>
                    t != null
                    && t.GetType().Name == "TargetSchedulerContainer"
                    && t.Status == SequenceEntityStatus.RUNNING);
            } catch (Exception ex) {
                Logger.Debug($"ACP: could not read the sequencer for a Target Scheduler container: {ex.Message}");
                return false;
            }
        }

        public bool BrokerAvailable => broker != null;

        private void Subscribe() {
            if (broker == null) return;
            try {
                foreach (var topic in AllTopics()) broker.Subscribe(topic, this);
                subscribed = true;
            } catch (Exception ex) {
                // A broker that will not take a subscription is a reason to
                // fall back to "cannot tell", not a reason to fail to load.
                Logger.Warning($"ACP: could not subscribe to Target Scheduler's topics: {ex.Message}");
            }
        }

        private static IEnumerable<string> AllTopics() {
            foreach (var t in StartTopics) yield return t;
            foreach (var t in StopTopics) yield return t;
            yield return TopicTargetComplete;
        }

        public Task OnMessageReceived(IMessage message) {
            if (message == null) return Task.CompletedTask;
            if (message.SenderId != TargetSchedulerSenderId) return Task.CompletedTask;

            lock (gate) {
                lastEventUtc = clock();
                lastTopic = message.Topic;

                if (Array.IndexOf(StopTopics, message.Topic) >= 0) {
                    running = false;
                } else if (Array.IndexOf(StartTopics, message.Topic) >= 0) {
                    running = true;
                }
                // TargetComplete only refreshes the clock. One target finishing
                // does not mean the container has.
            }
            return Task.CompletedTask;
        }

        public bool IsRunning {
            get {
                if (SequencerShowsContainerRunning()) return true;
                lock (gate) {
                    if (!running) return false;
                    if (clock() - lastEventUtc > StaleAfter) {
                        // Nothing heard for hours. Treat the run as over rather
                        // than refusing to sync for the rest of the session.
                        running = false;
                        return false;
                    }
                    return true;
                }
            }
        }

        public string Explain() {
            if (SequencerShowsContainerRunning()) {
                return "Target Scheduler is running a container in the current sequence.";
            }
            lock (gate) {
                if (broker == null || !subscribed) {
                    return "NINA did not offer a message broker, so whether Target Scheduler is " +
                           "running could not be checked.";
                }
                if (!running) {
                    return lastTopic == null
                        ? "No Target Scheduler activity has been seen this session."
                        : $"Target Scheduler is idle, last heard on {lastTopic}.";
                }
                var age = clock() - lastEventUtc;
                return $"Target Scheduler is running a container, last heard on {lastTopic} " +
                       $"{(int)age.TotalMinutes} minutes ago.";
            }
        }

        public void Dispose() {
            if (broker == null || !subscribed) return;
            try {
                foreach (var topic in AllTopics()) broker.Unsubscribe(topic, this);
            } catch (Exception ex) {
                Logger.Warning($"ACP: could not unsubscribe from Target Scheduler's topics: {ex.Message}");
            }
            subscribed = false;
        }
    }
}
