using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services {

    /// What one "Send TS to ACP" did, for the dock.
    public class TsUploadOutcome {

        /// True when ACP took the file. The line may still say ACP found a
        /// problem with it, or is still reading it.
        public bool Uploaded { get; set; }

        /// The one line the dock shows, with its tick or cross.
        public string Line { get; set; }

        /// The review page to open, or null when there is nothing to open.
        public string ReviewUrl { get; set; }

        /// Whether the temp copy was gone by the time the send returned.
        public bool CopyDeleted { get; set; }
    }

    /// "Send TS to ACP": copy Target Scheduler's database consistently, upload
    /// it, and wait for ACP to read it. Nothing on the rig is written.
    ///
    /// Spec: docs/specs/ts-upload-import.md in ACP, Part A. This first version
    /// sends the whole file. TODO(ts-upload): send the four TS tables as JSON
    /// rows instead, under ACP's normal 1 MB limit, once the row format is a
    /// contract both repos test against. See "Later: send rows as JSON instead
    /// of the file" in the spec.
    public class TsUploadService {

        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan PollLimit = TimeSpan.FromMinutes(2);

        public const string InterruptedLine = "✗ Upload interrupted. Nothing changed in ACP. Try again.";

        private readonly AcpApiClient client;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;

        /// `delay` is the test seam for the two second poll.
        public TsUploadService(AcpApiClient client, Func<TimeSpan, CancellationToken, Task> delay = null) {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        }

        /// Where the database is. Null means the usual install path.
        public string DbPathOverride { get; set; }

        /// Where the copy goes. Null means %TEMP%.
        public string TempDirOverride { get; set; }

        /// The path of the last copy made, so a test can check it is gone.
        public string LastCopyPath { get; private set; }

        public async Task<TsUploadOutcome> SendAsync(
            string profileId,
            string profileName,
            IReadOnlyList<TsUploadProfile> profiles,
            IProgress<string> status,
            CancellationToken ct = default
        ) {
            var outcome = new TsUploadOutcome();
            var label = string.IsNullOrWhiteSpace(profileName) ? profileId : profileName;
            if (string.IsNullOrWhiteSpace(profileId)) {
                outcome.Line = "✗ No active NINA profile, so there is nothing to send.";
                outcome.CopyDeleted = true;
                return outcome;
            }

            status?.Report($"Sending profile {label}: copying Target Scheduler's database...");
            TsUploadCopy copy;
            try {
                copy = TsUploadCopy.Make(DbPathOverride, TempDirOverride);
            } catch (TsSchemaVersionException ex) {
                // Same words the push uses when it refuses this version.
                outcome.Line = "✗ " + ex.Message;
                outcome.CopyDeleted = true;
                return outcome;
            } catch (FileNotFoundException ex) {
                outcome.Line = "✗ " + ex.Message;
                outcome.CopyDeleted = true;
                return outcome;
            } catch (Exception ex) {
                outcome.Line = $"✗ Could not copy Target Scheduler's database, so nothing was sent: {ex.Message}";
                outcome.CopyDeleted = true;
                return outcome;
            }

            LastCopyPath = copy.Path;
            TsUploadAccepted accepted;
            try {
                using (copy) {
                    var meta = new TsUploadMeta {
                        ProfileId = profileId,
                        Profiles = new List<TsUploadProfile>(profiles ?? new List<TsUploadProfile>()),
                        Sha256 = copy.Sha256,
                        UserVersion = copy.UserVersion,
                        PluginVersion = typeof(TsUploadService).Assembly.GetName().Version?.ToString(),
                        Machine = Environment.MachineName,
                    };
                    var lastPct = -1;
                    var progress = new Progress<double>(f => {
                        var pct = (int)Math.Floor(f * 100);
                        if (pct == lastPct) return;
                        lastPct = pct;
                        status?.Report($"Sending profile {label}: uploading, {pct}%");
                    });
                    accepted = await client.UploadTsDatabaseAsync(copy.Path, meta, progress, ct)
                        .ConfigureAwait(false);
                }
            } catch (AcpUnauthorizedException ex) {
                return Failed(outcome, copy, "✗ " + ex.Message);
            } catch (AcpHttpException ex) {
                return Failed(outcome, copy, "✗ " + DescribeRefusal(ex));
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                // HttpClient's own timeout surfaces as a cancellation.
                return Failed(outcome, copy, InterruptedLine);
            } catch (HttpRequestException) {
                return Failed(outcome, copy, InterruptedLine);
            } catch (IOException) {
                return Failed(outcome, copy, InterruptedLine);
            }

            outcome.Uploaded = true;
            outcome.CopyDeleted = !File.Exists(copy.Path);
            outcome.ReviewUrl = client.Resolve(accepted?.ReviewUrl);

            if (string.IsNullOrWhiteSpace(accepted?.StatusUrl)) {
                outcome.Line = "ACP took the upload but gave no status to follow. Open it in ACP to review.";
                return outcome;
            }

            status?.Report($"Sending profile {label}: ACP is reading it...");
            var waited = TimeSpan.Zero;
            while (true) {
                TsUploadStatus s = null;
                try {
                    s = await client.GetTsUploadStatusAsync(accepted.StatusUrl, ct).ConfigureAwait(false);
                } catch (AcpUnauthorizedException ex) {
                    outcome.Line = "✗ " + ex.Message;
                    return outcome;
                } catch (AcpHttpException ex) when (ex.Status == HttpStatusCode.NotFound) {
                    outcome.Line = "✗ ACP no longer has this upload. Send it again.";
                    outcome.ReviewUrl = null;
                    return outcome;
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    throw;
                } catch (Exception) {
                    // One missed poll is not a failure. The limit below is.
                }

                if (s != null) {
                    if (!string.IsNullOrWhiteSpace(s.ReviewUrl)) outcome.ReviewUrl = client.Resolve(s.ReviewUrl);
                    if (s.State == TsUploadStatus.Ready) {
                        outcome.Line = "✓ " + ReadyLine(label, s);
                        return outcome;
                    }
                    if (s.State == TsUploadStatus.Failed) {
                        outcome.Line = "✗ " + (string.IsNullOrWhiteSpace(s.Error)
                            ? "ACP could not read the upload."
                            : s.Error);
                        outcome.ReviewUrl = null;
                        return outcome;
                    }
                    if (s.State == TsUploadStatus.Superseded) {
                        outcome.Line = "A newer upload for this profile replaced this one.";
                        return outcome;
                    }
                }

                if (waited >= PollLimit) {
                    outcome.Line = "ACP is still reading it. Open it in ACP to follow along.";
                    return outcome;
                }
                await delay(PollInterval, ct).ConfigureAwait(false);
                waited += PollInterval;
            }
        }

        /// "Voyager main: 2 new, 5 updated, 1 needs a choice".
        public static string ReadyLine(string label, TsUploadStatus s) {
            if (s == null || !s.HasCounts) return $"{label}: ACP has read it. Open it in ACP to review.";
            var conflicts = s.Conflicts ?? 0;
            return $"{label}: {s.New ?? 0} new, {s.Updated ?? 0} updated, " +
                   $"{conflicts} {(conflicts == 1 ? "needs" : "need")} a choice";
        }

        /// The dock's words for each refusal. ACP's own message leads when it
        /// sent one, because it names the limit or the missing setting.
        public static string DescribeRefusal(AcpHttpException ex) {
            var said = ex.ServerMessage;
            switch ((int)ex.Status) {
                case 403:
                    return said ?? "ACP refused the upload. It needs ACP_API_TOKEN set before it accepts uploads.";
                case 413:
                    return said ?? "The database is bigger than ACP accepts.";
                case 415:
                    return said != null
                        ? $"ACP says this is not a SQLite database: {said}"
                        : "ACP says this is not a SQLite database.";
                case 404:
                    return "ACP has no upload route. The nina-ts-sync extension on the server needs updating.";
                default:
                    return $"ACP refused the upload ({(int)ex.Status}): {said ?? "no reason given"}";
            }
        }

        private static TsUploadOutcome Failed(TsUploadOutcome outcome, TsUploadCopy copy, string line) {
            copy.Dispose();
            outcome.Line = line;
            outcome.CopyDeleted = !File.Exists(copy.Path);
            return outcome;
        }
    }
}
