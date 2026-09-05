using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using ACP.NINA.Plugin.Services.TargetScheduler;
using NINA.Plugin.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Tests {

    /// Stand-ins for the four things the progress reporter talks to: the
    /// Target Scheduler database, the plan mapping, ACP, and the container
    /// watch. All in memory, so the whole of Part F runs with no sqlite file,
    /// no NINA and no socket.

    /// Rows as they would come back from a database, keyed by target id.
    public class FakeTsProgressSource : ITsProgressSource {

        public Dictionary<int, List<TsProgressRow>> RowsByTarget { get; }
            = new Dictionary<int, List<TsProgressRow>>();

        /// Set to have every read throw, which is how the "database is locked
        /// or missing" path is exercised.
        public Exception ThrowOnRead { get; set; }

        public List<int> TargetsRead { get; } = new List<int>();

        public FakeTsProgressSource With(int targetId, params TsProgressRow[] rows) {
            RowsByTarget[targetId] = rows.ToList();
            return this;
        }

        public IReadOnlyList<TsProgressRow> ReadRowsForTarget(int tsTargetId) {
            TargetsRead.Add(tsTargetId);
            if (ThrowOnRead != null) throw ThrowOnRead;
            return RowsByTarget.TryGetValue(tsTargetId, out var rows)
                ? rows
                : new List<TsProgressRow>();
        }
    }

    public class FakePlanRefsSource : IPlanRefsSource {

        public List<TsPlanRefs> Refs { get; } = new List<TsPlanRefs>();

        public Exception ThrowOnRead { get; set; }

        public int ReadCount { get; private set; }

        public Task<IReadOnlyList<TsPlanRefs>> ReadPlanRefsAsync(CancellationToken ct) {
            ReadCount++;
            if (ThrowOnRead != null) throw ThrowOnRead;
            return Task.FromResult((IReadOnlyList<TsPlanRefs>)Refs);
        }
    }

    /// Records every report rather than sending it.
    public class FakeProgressSink : IProgressSink {

        public List<Tuple<string, ProgressRequest>> Sent { get; }
            = new List<Tuple<string, ProgressRequest>>();

        public Exception ThrowOnSend { get; set; }

        public ProgressResponse Response { get; set; } = new ProgressResponse { Ok = true };

        public Task<ProgressResponse> ReportProgressAsync(
            string planId, ProgressRequest body, CancellationToken ct
        ) {
            if (ThrowOnSend != null) throw ThrowOnSend;
            Sent.Add(Tuple.Create(planId, body));
            return Task.FromResult(Response);
        }
    }

    /// A Target Scheduler pub/sub message. Every member of NINA's IMessage, so
    /// the reporter can be driven exactly as the broker would drive it.
    public class FakeMessage : IMessage {
        public Guid SenderId { get; set; }
        public string Sender { get; set; }
        public Guid MessageId { get; set; }
        public string Topic { get; set; }
        public object Content { get; set; }
        public DateTimeOffset SentAt { get; set; }
        public DateTimeOffset? Expiration { get; set; }
        public Guid? CorrelationId { get; set; }
        public int Version { get; set; }
        public IDictionary<string, object> CustomHeaders { get; set; }

        /// A message that passes the sender check, which is what Target
        /// Scheduler's own messages do.
        public static FakeMessage On(string topic, object content = null) {
            return new FakeMessage {
                Topic = topic,
                Content = content,
                SenderId = TsContainerWatch.TargetSchedulerSenderId,
                SentAt = DateTimeOffset.UtcNow,
                MessageId = Guid.NewGuid(),
                Version = 2,
            };
        }

        /// The same message from somebody else.
        public static FakeMessage FromStranger(string topic, object content = null) {
            var m = On(topic, content);
            m.SenderId = Guid.NewGuid();
            return m;
        }
    }

    /// The shape a TargetStart payload plausibly has. Only used to prove the
    /// reader finds an id on a payload object whose type it has never seen,
    /// which is the situation the plugin is actually in.
    public class FakeTsTargetPayload {
        public int TargetId { get; set; }
        public string TargetName { get; set; }
        public string ProjectName { get; set; }
    }
}
