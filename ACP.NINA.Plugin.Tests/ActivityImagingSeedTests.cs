using ACP.NINA.Plugin.Services;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// After a NINA restart mid-night the imaging totals are rebuilt from Target
    /// Scheduler's acquiredimage table, so the count ACP shows does not fall back
    /// to zero. The database is only ever read.
    public class ActivityImagingSeedTests {

        private const string Profile = "11111111-2222-3333-4444-555555555555";
        private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 13, 20, 0, 0, TimeSpan.FromHours(11));

        private static void Insert(
            SqliteConnection conn, string profile, int target, string filter, DateTimeOffset when, string metadata
        ) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText =
                    "INSERT INTO acquiredimage (projectId, targetId, acquireddate, filtername, gradingStatus, metadata, profileId) " +
                    "VALUES (1, $t, $d, $f, 1, $m, $p)";
                cmd.Parameters.AddWithValue("$t", target);
                cmd.Parameters.AddWithValue("$d", when.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$f", filter);
                cmd.Parameters.AddWithValue("$m", metadata);
                cmd.Parameters.AddWithValue("$p", profile);
                cmd.ExecuteNonQuery();
            }
        }

        private static ActivityImagingSeeder SeederOver(string path) {
            return new ActivityImagingSeeder((profile, from, to) => {
                using (var db = TargetSchedulerDb.Open(path, readOnly: true)) {
                    return db.ReadAcquiredImages(profile, from, to);
                }
            });
        }

        private static ImagingFrame Frame(DateTimeOffset at, string filter = "Ha") {
            return new ImagingFrame {
                ProfileId = Profile, ProfileName = "Rig", TsTargetId = 17, TargetName = "Dolphin",
                Filter = filter, ExposureSeconds = 300, At = at,
            };
        }

        private static string BuildDb(TempDir tmp) {
            var path = TsFixtures.MakeDb(28, tmp.File("seed.sqlite"));
            using (var db = TargetSchedulerDb.Open(path)) {
                var c = db.Connection;
                // Five frames earlier tonight.
                for (var i = 0; i < 5; i++) Insert(c, Profile, 17, "Ha", Start.AddMinutes(i * 6), "{\"ExposureDuration\":300}");
                // Last night, which must not count: 08:00 on the 13th is night 2026-10-12.
                Insert(c, Profile, 17, "Ha", new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(11)), "{\"ExposureDuration\":300}");
                Insert(c, Profile, 17, "Ha", new DateTimeOffset(2026, 10, 12, 23, 0, 0, TimeSpan.FromHours(11)), "{\"ExposureDuration\":300}");
                // Another profile and another target.
                Insert(c, "other-profile", 17, "Ha", Start.AddMinutes(1), "{\"ExposureDuration\":300}");
                Insert(c, Profile, 99, "Ha", Start.AddMinutes(2), "{\"ExposureDuration\":300}");
            }
            return path;
        }

        [Fact]
        public void The_night_window_runs_from_local_noon_to_local_noon() {
            ActivityNight.Window(Start, out var from, out var to);
            Assert.Equal(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.FromHours(11)), from);
            Assert.Equal(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(11)), to);
        }

        [Fact]
        public void Only_this_profile_and_this_nights_frames_are_read() {
            using (var tmp = new TempDir()) {
                var path = BuildDb(tmp);
                var seeds = SeederOver(path).Seed(Frame(Start.AddMinutes(60))).ToList();

                Assert.Equal(5, seeds.Count);
                Assert.All(seeds, s => Assert.Equal(300.0, s.ExposureSeconds));
                Assert.All(seeds, s => Assert.Equal("Ha", s.Filter));
            }
        }

        // The restart case: five frames already on disk, two more after startup.
        [Fact]
        public void Five_earlier_frames_and_two_new_ones_make_seven_without_counting_last_night() {
            using (var tmp = new TempDir()) {
                var path = BuildDb(tmp);
                var seeder = SeederOver(path);
                var tracker = new ImagingTracker(() => Start, seeder.Seed);

                var first = tracker.AddFrame(Frame(Start.AddMinutes(60)));
                var second = tracker.AddFrame(Frame(Start.AddMinutes(66)));

                var frames = (List<Dictionary<string, object>>)second.Data["frames"];
                Assert.Single(frames);
                Assert.Equal(7, Convert.ToInt32(frames[0]["count"]));
                Assert.Equal(6, Convert.ToInt32(((List<Dictionary<string, object>>)first.Data["frames"])[0]["count"]));
                Assert.Equal(ActivityNight.Iso(Start), (string)frames[0]["first_at"]);
                Assert.True(second.Seq > first.Seq);
            }
        }

        [Fact]
        public void A_row_already_written_for_the_triggering_frame_is_not_counted_twice() {
            using (var tmp = new TempDir()) {
                var path = BuildDb(tmp);
                // Target Scheduler has already saved the row for the frame that
                // wakes the tracker, a little after its exposure began.
                var trigger = Start.AddMinutes(60);
                using (var db = TargetSchedulerDb.Open(path)) {
                    Insert(db.Connection, Profile, 17, "Ha", trigger.AddSeconds(300), "{\"ExposureDuration\":300}");
                }
                var tracker = new ImagingTracker(() => Start, SeederOver(path).Seed);

                var e = tracker.AddFrame(Frame(trigger));

                var frames = (List<Dictionary<string, object>>)e.Data["frames"];
                Assert.Equal(6, Convert.ToInt32(frames[0]["count"]));
            }
        }

        [Fact]
        public void Exposure_comes_from_the_exposure_plan_when_the_metadata_has_none() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("plan.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    var c = db.Connection;
                    using (var cmd = c.CreateCommand()) {
                        // A bare exposure plan row, with no target or template behind it.
                        cmd.CommandText = "PRAGMA foreign_keys = OFF";
                        cmd.ExecuteNonQuery();
                        cmd.CommandText =
                            "INSERT INTO exposureplan (Id, profileId, exposure, desired, acquired, accepted, targetid) " +
                            "VALUES (500, $p, 240, 10, 1, 1, 17)";
                        cmd.Parameters.AddWithValue("$p", Profile);
                        cmd.ExecuteNonQuery();
                        cmd.CommandText =
                            "INSERT INTO acquiredimage (projectId, targetId, acquireddate, filtername, gradingStatus, metadata, profileId, exposureId) " +
                            "VALUES (1, 17, $d, 'OIII', 1, '{}', $p, 500)";
                        cmd.Parameters.AddWithValue("$d", Start.ToUnixTimeSeconds());
                        cmd.ExecuteNonQuery();
                    }
                }

                var seed = SeederOver(path).Seed(Frame(Start.AddMinutes(30))).Single();
                Assert.Equal(240.0, seed.ExposureSeconds);
                Assert.Equal("OIII", seed.Filter);
            }
        }

        [Fact]
        public void A_seeder_that_throws_costs_only_the_seed() {
            var tracker = new ImagingTracker(() => Start, f => throw new InvalidOperationException("locked"));
            var e = tracker.AddFrame(Frame(Start));
            var frames = (List<Dictionary<string, object>>)e.Data["frames"];
            Assert.Equal(1, Convert.ToInt32(frames[0]["count"]));
        }
    }
}
