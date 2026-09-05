using ACP.NINA.Plugin.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services {

    /// The three things progress reporting talks to, named so the reporter can
    /// be tested without a database, a message broker or a socket.

    /// Acquired counts out of the Target Scheduler database.
    ///
    /// Narrow on purpose. The v3.1 sync opens the database for a whole push,
    /// with a backup and an immediate transaction; Part F only ever reads, so
    /// it asks for exactly what it reads and nothing about the writing path can
    /// leak into the reporting path.
    public interface ITsProgressSource {

        /// The exposure plan rows for one Target Scheduler target, joined to
        /// their template and their parent project. Empty when the target has
        /// no rows.
        IReadOnlyList<TsProgressRow> ReadRowsForTarget(int tsTargetId);
    }

    /// Which Target Scheduler rows belong to which ACP plan.
    ///
    /// Asynchronous because working this out means knowing what plans ACP has,
    /// and the honest way to know that is to ask ACP.
    public interface IPlanRefsSource {

        Task<IReadOnlyList<TsPlanRefs>> ReadPlanRefsAsync(CancellationToken ct);
    }

    /// Where a progress report goes. The real one is AcpApiClient; the tests
    /// use a fake so the reporter can be exercised without a socket.
    public interface IProgressSink {

        Task<ProgressResponse> ReportProgressAsync(
            string planId, ProgressRequest body, CancellationToken ct
        );
    }
}
