using ACP.NINA.Plugin.Models;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// What one push did, in the shape both the sequencer log and the dock can
    /// render without recomputing anything.
    public class TsPushResult {

        public bool Success { get; set; }

        /// Set when the push did not run, in words the user can act on.
        public string Failure { get; set; }

        public string DbPath { get; set; }
        public string BackupPath { get; set; }
        public int UserVersion { get; set; }
        public TsSyncOutcome Outcome { get; set; }

        /// Plans that produced at least one exposure plan row.
        public List<string> Pushed { get; } = new List<string>();

        /// Plans the push itself could not use, and why, keyed by plan name.
        public List<string> LeftOut { get; } = new List<string>();

        /// The ts_refs and ts_base_snapshot captured for each plan, ready to go
        /// back to ACP so a later sync can tell an ACP edit from a Target
        /// Scheduler one.
        public List<TsPlanState> PlanStates { get; } = new List<TsPlanState>();

        /// How many attempts it took, which is 1 unless the database was locked.
        public int Attempts { get; set; } = 1;

        /// The line the spec asks for: N plans synced, M left out and why.
        public string Summary() {
            if (!Success) return Failure ?? "The Target Scheduler sync did not run.";

            var line = $"{Pushed.Count} {(Pushed.Count == 1 ? "plan" : "plans")} loaded into " +
                       $"Target Scheduler: {Outcome.ToShortString()}.";
            if (LeftOut.Count > 0) {
                var named = string.Join(", ", LeftOut.Take(5));
                var more = LeftOut.Count > 5 ? $", and {LeftOut.Count - 5} more" : string.Empty;
                line += $" {LeftOut.Count} left out: {named}{more}.";
            }
            if (Attempts > 1) {
                line += $" The database was locked, so it took {Attempts} attempts.";
            }
            return line;
        }
    }

    /// Loading ACP plans into Target Scheduler.
    public interface ITsPushService {

        /// Push `plans` into the Target Scheduler database for `profileId`.
        ///
        /// Never writes while a Target Scheduler container is running, takes a
        /// backup before the first write, and refuses a schema version outside
        /// the supported range before touching a row.
        Task<TsPushResult> PushAsync(
            IReadOnlyList<Plan> plans,
            GearResponse gear,
            string profileId,
            CancellationToken token = default
        );
    }

    /// The push, wired together.
    ///
    /// This class is only the sequencing: refuse when Target Scheduler is busy,
    /// open, back up, convert, write with a retry, read back, stamp. The rules
    /// about what a row should contain all live in TsConvert and TsUpsert,
    /// where the Python extension's logic was ported to.
    [Export(typeof(ITsPushService))]
    public class TsPushService : ITsPushService {

        private readonly ITsContainerWatch containerWatch;
        private readonly Func<string, TargetSchedulerDb> openDb;
        private readonly Func<long> clock;

        [ImportingConstructor]
        public TsPushService(ITsContainerWatch containerWatch)
            : this(containerWatch, null, null) { }

        /// The database factory and the clock are seams for the tests, which
        /// need a fixture file rather than the machine's real install and a
        /// frozen createdate rather than the wall clock.
        public TsPushService(
            ITsContainerWatch containerWatch,
            Func<string, TargetSchedulerDb> openDb,
            Func<long> clock
        ) {
            this.containerWatch = containerWatch;
            this.openDb = openDb ?? (path => TargetSchedulerDb.Open(path));
            this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        /// The path to push to. Null means the conventional install location.
        public string DbPathOverride { get; set; }

        /// Whether to copy the database before writing. Only a test has any
        /// business turning this off.
        public bool MakeBackup { get; set; } = true;

        public async Task<TsPushResult> PushAsync(
            IReadOnlyList<Plan> plans,
            GearResponse gear,
            string profileId,
            CancellationToken token = default
        ) {
            var result = new TsPushResult();
            plans = plans ?? new List<Plan>();

            if (string.IsNullOrWhiteSpace(profileId)) {
                result.Failure = "No NINA profile is active, so there is nothing to sync into.";
                return result;
            }

            // 1) Stay out of a running container's way. Target Scheduler reads
            // its database between scheduling decisions, so writing underneath
            // it means the night runs on a plan that no longer matches what is
            // on disk.
            if (containerWatch != null && containerWatch.IsRunning) {
                // One sentence: the reason, then the consequence. The
                // reason already says a container is running.
                result.Failure = containerWatch.Explain().TrimEnd('.') + ", so nothing was written.";
                return result;
            }

            if (plans.Count == 0) {
                result.Failure = "There were no plans to load, so Target Scheduler was left as it was.";
                return result;
            }

            // 2) Open, which is also where the schema version is checked. Both
            // of these failures are worth their own words: one is a missing
            // install, the other is a Target Scheduler this plugin does not
            // know how to write to yet.
            TargetSchedulerDb db;
            try {
                db = openDb(DbPathOverride);
            } catch (TsSchemaVersionException ex) {
                result.Failure = ex.Message;
                return result;
            } catch (FileNotFoundException ex) {
                result.Failure = ex.Message;
                return result;
            } catch (Exception ex) {
                result.Failure = $"The Target Scheduler database could not be opened: {ex.Message}";
                return result;
            }

            using (db) {
                result.DbPath = db.DbPath;
                result.UserVersion = db.UserVersion;

                // 3) Back up before the transaction, so a failed write leaves
                // the user with an exact copy of what they had.
                if (MakeBackup) {
                    try {
                        result.BackupPath = TargetSchedulerDb.BackupTo(db.DbPath);
                    } catch (Exception ex) {
                        result.Failure =
                            $"The Target Scheduler database could not be backed up, so nothing " +
                            $"was written: {ex.Message}";
                        return result;
                    }
                }

                TsSyncPayload payload;
                try {
                    payload = TsConvert.BuildPayload(plans, gear, profileId, clock());
                } catch (TsPushValidationException ex) {
                    // A plan that cannot be identified. The message says what to
                    // rename, and nothing was written.
                    result.Failure = ex.Message;
                    return result;
                }
                RecordWhatWasUsable(plans, payload, result);

                if (result.Pushed.Count == 0) {
                    result.Failure =
                        "None of the plans had a filter goal with hours on it, so there was " +
                        "nothing to write.";
                    return result;
                }

                // 4) The write, retried on a locked database.
                try {
                    var attempts = 0;
                    result.Outcome = await db.RunWriteAsync(
                        conn => {
                            attempts++;
                            return TsUpsert.Apply(conn, db.UserVersion, db.ColumnsByTable, payload);
                        },
                        token
                    ).ConfigureAwait(false);
                    result.Attempts = attempts;
                    if (result.Outcome.MigratedGuids > 0) {
                        Logger.Info(
                            $"ACP: migrated {result.Outcome.MigratedGuids} Target Scheduler " +
                            "row(s) onto the new ACP identity recipe.");
                    }
                } catch (TsPushValidationException ex) {
                    result.Failure = ex.Message;
                    return result;
                } catch (Microsoft.Data.Sqlite.SqliteException ex) when (TargetSchedulerDb.IsLocked(ex)) {
                    result.Failure =
                        "Target Scheduler's database stayed locked through three retries, so " +
                        "nothing was written. Close Target Scheduler's project view and try again.";
                    return result;
                } catch (Exception ex) {
                    result.Failure = $"The Target Scheduler write failed and was rolled back: {ex.Message}";
                    return result;
                }

                // 5) Read back what actually landed and stamp each plan, so the
                // next sync has a base to diff against.
                try {
                    var snapshot = db.ReadAll(profileId);
                    foreach (var plan in plans) {
                        result.PlanStates.Add(new TsPlanState {
                            PlanId = plan.Id,
                            Refs = TsState.BuildRefs(
                                snapshot, plan, profileId, db.UserVersion, TsState.OperationPush),
                            BaseSnapshot = TsState.BuildBaseSnapshot(snapshot, plan, profileId),
                        });
                    }
                } catch (Exception ex) {
                    // The rows are written and committed. Failing to describe
                    // them afterwards is worth a warning, not a failed sync.
                    Logger.Warning($"ACP: the Target Scheduler rows were written but could not be " +
                                   $"read back for the plan snapshot: {ex.Message}");
                }

                result.Success = true;
                return result;
            }
        }

        /// Work out which plans actually reached the database and which fell
        /// out on the way, so the log can say why rather than just how many.
        private static void RecordWhatWasUsable(
            IReadOnlyList<Plan> plans, TsSyncPayload payload, TsPushResult result
        ) {
            var targetsWithPlans = new HashSet<string>(
                payload.PlansByTargetGuid.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key));

            foreach (var plan in plans) {
                var name = PlanName(plan);
                var projectName = TsConvert.ProjectNameOf(plan);

                var goals = plan.FilterGoals ?? new Dictionary<string, FilterGoal>();
                if (goals.Count == 0) {
                    result.LeftOut.Add($"{name} (no filter goals)");
                    continue;
                }
                if (goals.Values.All(g => (g?.TargetHours ?? 0) <= 0)) {
                    result.LeftOut.Add($"{name} (no filter goal has any hours on it)");
                    continue;
                }

                // The converter names its targets from the project and the
                // panel, so a plan reached the database if any of its target
                // guids picked up exposure plans.
                var reached = payload.TargetsByProjectGuid
                    .SelectMany(kv => kv.Value)
                    .Any(t => targetsWithPlans.Contains(t.Guid) && BelongsTo(t.Name, plan));

                if (reached) result.Pushed.Add(name);
                else result.LeftOut.Add($"{name} (nothing to write for it)");
            }
        }

        /// Whether a converted target row came from this plan. The converter
        /// names a single panel after the target and a mosaic panel after the
        /// target plus a suffix, so the base name is the link.
        private static bool BelongsTo(string targetName, Plan plan) {
            var baseName = !string.IsNullOrWhiteSpace(plan?.Target?.Name)
                ? plan.Target.Name
                : (!string.IsNullOrWhiteSpace(plan?.Id) ? plan.Id : "Untitled");
            return targetName == baseName ||
                   targetName.StartsWith(baseName + " Panel ", StringComparison.Ordinal);
        }

        private static string PlanName(Plan plan) {
            if (!string.IsNullOrWhiteSpace(plan?.Target?.Name)) return plan.Target.Name;
            if (!string.IsNullOrWhiteSpace(plan?.ProjectName)) return plan.ProjectName;
            return plan?.Id ?? "an unnamed plan";
        }
    }
}
