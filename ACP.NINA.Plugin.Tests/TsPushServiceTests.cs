using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// A container watch a test can drive, standing in for the pub/sub one.
    public class FakeContainerWatch : ITsContainerWatch {
        public bool IsRunning { get; set; }
        public string Explain() {
            return IsRunning ? "Target Scheduler is running a container." : "Target Scheduler is idle.";
        }
    }

    public class TsPushServiceTests {

        private static TsPushService ServiceFor(string dbPath, ITsContainerWatch watch = null) {
            return new TsPushService(
                watch ?? new FakeContainerWatch(),
                path => TargetSchedulerDb.Open(dbPath),
                () => TsTestPlans.FrozenNow
            ) {
                DbPathOverride = dbPath,
            };
        }

        [Fact]
        public async Task APushWritesTheRowsAndReportsWhatItDid() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));

                var result = await ServiceFor(path).PushAsync(
                    TsTestPlans.ThreePlans(), TsTestPlans.Gear(), TsTestPlans.ProfileId);

                Assert.True(result.Success, result.Failure);
                Assert.Equal(28, result.UserVersion);
                Assert.Equal(3, result.Pushed.Count);
                Assert.Empty(result.LeftOut);
                Assert.Equal(7, result.Outcome.ExposurePlan.Inserted);
                Assert.Contains("3 plans loaded into Target Scheduler", result.Summary());
            }
        }

        [Fact]
        public async Task ABackupIsTakenBesideTheDatabaseBeforeAnythingIsWritten() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));

                var result = await ServiceFor(path).PushAsync(
                    TsTestPlans.ThreePlans(), TsTestPlans.Gear(), TsTestPlans.ProfileId);

                Assert.True(File.Exists(result.BackupPath));
                // The backup is the state before the push, so it has no rows.
                using (var db = TargetSchedulerDb.Open(result.BackupPath)) {
                    Assert.Empty(db.ReadAll(TsTestPlans.ProfileId).ProjectsById);
                }
                // And the live database does.
                using (var db = TargetSchedulerDb.Open(path)) {
                    Assert.Equal(3, db.ReadAll(TsTestPlans.ProfileId).ProjectsById.Count);
                }
            }
        }

        [Fact]
        public async Task NothingIsWrittenWhileATargetSchedulerContainerIsRunning() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var watch = new FakeContainerWatch { IsRunning = true };

                var result = await ServiceFor(path, watch).PushAsync(
                    TsTestPlans.ThreePlans(), TsTestPlans.Gear(), TsTestPlans.ProfileId);

                Assert.False(result.Success);
                Assert.Contains("running a container", result.Failure);
                using (var db = TargetSchedulerDb.Open(path)) {
                    Assert.Empty(db.ReadAll(TsTestPlans.ProfileId).ProjectsById);
                }
                // Not even a backup, because the push never got that far.
                Assert.Null(result.BackupPath);
            }
        }

        [Fact]
        public async Task AnUnsupportedSchemaIsRefusedInTheSameWordsAsTheExtension() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(29, tmp.File("schedulerdb.sqlite"));

                var result = await ServiceFor(path).PushAsync(
                    TsTestPlans.ThreePlans(), TsTestPlans.Gear(), TsTestPlans.ProfileId);

                Assert.False(result.Success);
                Assert.StartsWith("Target Scheduler DB is at PRAGMA user_version=29;", result.Failure);
                Assert.Contains("Refusing to write", result.Failure);
            }
        }

        [Fact]
        public async Task PlansWithNoHoursOnAnyGoalAreNamedInTheLeftOutList() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));

                var plans = TsTestPlans.ThreePlans();
                plans.Add(TsTestPlans.Plan(
                    "empty", projectName: "Nothing To Do", targetName: "Empty One",
                    filterGoals: new Dictionary<string, FilterGoal> {
                        { "Ha", new FilterGoal { TargetHours = 0 } },
                    }));

                var result = await ServiceFor(path).PushAsync(
                    plans, TsTestPlans.Gear(), TsTestPlans.ProfileId);

                Assert.True(result.Success, result.Failure);
                Assert.Equal(3, result.Pushed.Count);
                Assert.Single(result.LeftOut);
                Assert.Contains("Empty One", result.LeftOut[0]);
                Assert.Contains("no filter goal has any hours on it", result.LeftOut[0]);
                Assert.Contains("1 left out", result.Summary());
            }
        }

        [Fact]
        public async Task AnEmptyPlanListLeavesTargetSchedulerAlone() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));

                var result = await ServiceFor(path).PushAsync(
                    new List<Plan>(), TsTestPlans.Gear(), TsTestPlans.ProfileId);

                Assert.False(result.Success);
                Assert.Contains("left as it was", result.Failure);
                Assert.Null(result.BackupPath);
            }
        }

        [Fact]
        public async Task NoActiveProfileIsRefusedBeforeTheDatabaseIsOpened() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));

                var result = await ServiceFor(path).PushAsync(
                    TsTestPlans.ThreePlans(), TsTestPlans.Gear(), "  ");

                Assert.False(result.Success);
                Assert.Contains("No NINA profile is active", result.Failure);
            }
        }

        [Fact]
        public async Task EachPlanComesBackWithRefsAndABaseSnapshot() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));

                var result = await ServiceFor(path).PushAsync(
                    TsTestPlans.ThreePlans(), TsTestPlans.Gear(), TsTestPlans.ProfileId);

                Assert.Equal(3, result.PlanStates.Count);

                var mosaic = result.PlanStates.Single(s => s.PlanId == "mosaic");
                Assert.Equal(TsTestPlans.ProfileId, (string)mosaic.Refs["profile_id"]);
                Assert.NotNull((int?)mosaic.Refs["project_id"]);
                Assert.Equal(28, (int)mosaic.Refs["last_pushed_user_version"]);
                Assert.NotNull((string)mosaic.Refs["last_pushed_iso"]);
                // Four panels, each with an Id.
                Assert.Equal(4, mosaic.Refs["target_ids_by_panel"].Count());
                Assert.Equal(4, mosaic.BaseSnapshot["targets_by_panel"].Count());
                // The mosaic struct is recorded, so the next diff has a base
                // for it rather than seeing null and pulling over the user's
                // overlap.
                Assert.Equal(2, (int)mosaic.BaseSnapshot["mosaic"]["rows"]);
                Assert.Equal(15, (int)mosaic.BaseSnapshot["mosaic"]["overlap_pct"]);

                var single = result.PlanStates.Single(s => s.PlanId == "single");
                // A one by one mosaic records null, so the diff short circuits.
                Assert.Equal(
                    Newtonsoft.Json.Linq.JTokenType.Null, single.BaseSnapshot["mosaic"].Type);
                Assert.Equal(
                    "NGC 253", (string)single.BaseSnapshot["targets_by_panel"]["1,1"]["name"]);
            }
        }

        [Fact]
        public async Task PushingTwiceThroughTheServiceDoesNotDuplicate() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var service = ServiceFor(path);

                await service.PushAsync(TsTestPlans.ThreePlans(), TsTestPlans.Gear(), TsTestPlans.ProfileId);
                var second = await service.PushAsync(
                    TsTestPlans.ThreePlans(), TsTestPlans.Gear(), TsTestPlans.ProfileId);

                Assert.True(second.Success, second.Failure);
                Assert.Equal(0, second.Outcome.ExposurePlan.Inserted);
                Assert.Equal(7, second.Outcome.ExposurePlan.Updated);
            }
        }

        [Fact]
        public async Task AMissingDatabaseIsRefusedWithSomethingActionable() {
            using (var tmp = new TempDir()) {
                var missing = tmp.File("not-there.sqlite");
                var service = new TsPushService(
                    new FakeContainerWatch(),
                    path => TargetSchedulerDb.Open(missing),
                    () => TsTestPlans.FrozenNow);

                var result = await service.PushAsync(
                    TsTestPlans.ThreePlans(), TsTestPlans.Gear(), TsTestPlans.ProfileId);

                Assert.False(result.Success);
                Assert.Contains("schedulerdb.sqlite not found", result.Failure);
            }
        }
    }

    /// The retry, checked without waiting the real 2, 4 and 8 seconds.
    public class TsLockRetryTests {

        [Fact]
        public async Task AWriteThatSucceedsFirstTimeDoesNotWait() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("db.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var attempts = 0;
                    var watch = Stopwatch.StartNew();
                    var result = await db.RunWriteAsync(conn => { attempts++; return 42; });
                    watch.Stop();

                    Assert.Equal(42, result);
                    Assert.Equal(1, attempts);
                    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
                }
            }
        }

        [Fact]
        public async Task ALockedDatabaseIsRetriedAndThenSucceeds() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("db.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var attempts = 0;
                    var result = await db.RunWriteAsync(
                        conn => {
                            attempts++;
                            if (attempts < 3) throw Locked();
                            return attempts;
                        },
                        CancellationToken.None,
                        // The real backoff is 2, 4 and 8 seconds. The waits are
                        // injectable so the behaviour can be tested without
                        // fourteen seconds of a test run sitting idle.
                        retrySeconds: new[] { 0, 0, 0 });

                    Assert.Equal(3, result);
                    Assert.Equal(3, attempts);
                }
            }
        }

        [Fact]
        public async Task ADatabaseThatStaysLockedGivesUpAfterTheThirdWait() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("db.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var attempts = 0;

                    await Assert.ThrowsAsync<SqliteException>(() => db.RunWriteAsync<int>(
                        conn => { attempts++; throw Locked(); },
                        CancellationToken.None,
                        retrySeconds: new[] { 0, 0, 0 }));

                    // The first go plus one after each of the three waits.
                    Assert.Equal(4, attempts);
                }
            }
        }

        [Fact]
        public async Task AnythingOtherThanALockFailsStraightAwayAndRollsBack() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("db.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var attempts = 0;

                    await Assert.ThrowsAsync<InvalidOperationException>(() => db.RunWriteAsync<int>(
                        conn => {
                            attempts++;
                            using (var cmd = conn.CreateCommand()) {
                                cmd.CommandText =
                                    "INSERT INTO project (profileId, name, state, priority) " +
                                    "VALUES ('p', 'Half Written', 1, 1)";
                                cmd.ExecuteNonQuery();
                            }
                            throw new InvalidOperationException("no");
                        },
                        CancellationToken.None,
                        retrySeconds: new[] { 0, 0, 0 }));

                    Assert.Equal(1, attempts);
                    // The row went in and then came back out with the rollback.
                    using (var cmd = db.Connection.CreateCommand()) {
                        cmd.CommandText = "SELECT COUNT(*) FROM project";
                        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
                    }
                }
            }
        }

        [Fact]
        public void BusyAndLockedBothCountAsLocked() {
            Assert.True(TargetSchedulerDb.IsLocked(Locked()));
            Assert.True(TargetSchedulerDb.IsLocked(new SqliteException("table is locked", 6)));
            Assert.False(TargetSchedulerDb.IsLocked(new SqliteException("no such table", 1)));
            Assert.False(TargetSchedulerDb.IsLocked(null));
        }

        private static SqliteException Locked() {
            // 5 is SQLITE_BUSY, which is what "database is locked" reports as.
            return new SqliteException("database is locked", 5);
        }
    }

    /// The container watch's state machine, driven without a message broker.
    public class TsContainerWatchTests {

        // Qualified from the root: inside ACP.NINA.Plugin.Tests, a bare
        // NINA.Plugin binds to ACP.NINA.Plugin rather than NINA's own.
        private class FakeMessage : global::NINA.Plugin.Interfaces.IMessage {
            public Guid SenderId { get; set; } = TsContainerWatch.TargetSchedulerSenderId;
            public string Sender { get; set; } = "Target Scheduler";
            public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
            public Guid MessageId { get; set; } = Guid.NewGuid();
            public DateTimeOffset? Expiration { get; set; }
            public Guid? CorrelationId { get; set; }
            public int Version { get; set; } = 1;
            public IDictionary<string, object> CustomHeaders { get; set; }
            public string Topic { get; set; }
            public object Content { get; set; }
        }

        private DateTime now = new DateTime(2026, 9, 4, 20, 0, 0, DateTimeKind.Utc);

        private TsContainerWatch NewWatch() {
            // No broker, so nothing is subscribed and the messages are fed in
            // directly. That is the state machine under test, not the plumbing.
            return new TsContainerWatch(null, () => now);
        }

        private static async Task Send(TsContainerWatch watch, string topic) {
            await watch.OnMessageReceived(new FakeMessage { Topic = topic });
        }

        [Fact]
        public async Task NothingHeardMeansNotRunning() {
            var watch = NewWatch();
            Assert.False(watch.IsRunning);
            Assert.Contains("could not be checked", watch.Explain());
            await Task.CompletedTask;
        }

        [Theory]
        [InlineData(TsContainerWatch.TopicWaitStart)]
        [InlineData(TsContainerWatch.TopicNewTargetStart)]
        [InlineData(TsContainerWatch.TopicTargetStart)]
        public async Task AnyStartTopicMeansRunning(string topic) {
            var watch = NewWatch();
            await Send(watch, topic);
            Assert.True(watch.IsRunning);
        }

        [Fact]
        public async Task ContainerStoppedClearsIt() {
            var watch = NewWatch();
            await Send(watch, TsContainerWatch.TopicTargetStart);
            await Send(watch, TsContainerWatch.TopicContainerStopped);
            Assert.False(watch.IsRunning);
        }

        [Fact]
        public async Task TargetCompleteDoesNotClearIt() {
            var watch = NewWatch();
            await Send(watch, TsContainerWatch.TopicTargetStart);
            await Send(watch, TsContainerWatch.TopicTargetComplete);
            // One target finishing says nothing about the container, which may
            // have several more to get through.
            Assert.True(watch.IsRunning);
        }

        [Fact]
        public async Task ARunWithNoEventsForHoursIsTreatedAsOver() {
            var watch = NewWatch();
            await Send(watch, TsContainerWatch.TopicTargetStart);
            Assert.True(watch.IsRunning);

            // NINA was killed mid-run, so ContainerStopped never arrived. The
            // alternative to this guard is refusing every sync until restart.
            now = now.Add(TsContainerWatch.StaleAfter).AddMinutes(1);
            Assert.False(watch.IsRunning);
        }

        [Fact]
        public async Task AMessageFromAnotherPluginIsIgnored() {
            var watch = NewWatch();
            await watch.OnMessageReceived(new FakeMessage {
                Topic = TsContainerWatch.TopicTargetStart,
                SenderId = Guid.NewGuid(),
            });
            Assert.False(watch.IsRunning);
        }

        [Fact]
        public void NoBrokerIsReportedRatherThanGuessed() {
            var watch = NewWatch();
            Assert.False(watch.BrokerAvailable);
            Assert.Contains("did not offer a message broker", watch.Explain());
        }
    }
}
