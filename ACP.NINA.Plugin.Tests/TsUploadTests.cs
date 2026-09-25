using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// A fake ACP that answers each request through a function, and keeps
    /// what it was sent: the path, the bearer header, and every multipart
    /// part by name. The upload's file part is a stream, so it is read here,
    /// once, as it goes past.
    public class ScriptedAcp : HttpMessageHandler {

        public class Seen {
            public HttpMethod Method;
            public string Path;
            public string Authorization;
            public string ContentType;
            public string Body;
            public Dictionary<string, byte[]> Parts = new Dictionary<string, byte[]>();
            public Dictionary<string, string> PartTypes = new Dictionary<string, string>();
        }

        private readonly Func<Seen, HttpResponseMessage> answer;

        public List<Seen> Requests { get; } = new List<Seen>();

        public ScriptedAcp(Func<Seen, HttpResponseMessage> answer) {
            this.answer = answer;
        }

        public static HttpResponseMessage Json(HttpStatusCode status, string json) {
            return new HttpResponseMessage(status) {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken
        ) {
            var seen = new Seen {
                Method = request.Method,
                Path = request.RequestUri.AbsolutePath,
                Authorization = request.Headers.Authorization?.ToString(),
                ContentType = request.Content?.Headers.ContentType?.MediaType,
            };
            if (request.Content is MultipartFormDataContent form) {
                foreach (var part in form) {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                    seen.Parts[name] = await part.ReadAsByteArrayAsync().ConfigureAwait(false);
                    seen.PartTypes[name] = part.Headers.ContentType?.MediaType;
                }
            } else if (request.Content != null) {
                seen.Body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            Requests.Add(seen);
            return answer(seen);
        }
    }

    public class TsUploadTests {

        private const string Profile = TsTestPlans.ProfileId;
        private const string Base = "http://acp.test";
        private const string StatusPath = "/api/ext/nina-ts-sync/import/uploads/abc123";
        private const string ReviewPath = "/ext/nina-ts-sync/import/uploads/abc123";

        private static readonly string Accepted = JsonConvert.SerializeObject(new {
            upload_id = "abc123", status_url = StatusPath, review_url = ReviewPath,
        });

        private static string ConnString(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWrite) {
            return new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();
        }

        private static void Exec(SqliteConnection conn, string sql) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        private static long Scalar(string path, string sql, SqliteOpenMode mode = SqliteOpenMode.ReadOnly) {
            using (var conn = new SqliteConnection(ConnString(path, mode))) {
                conn.Open();
                using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText = sql;
                    return Convert.ToInt64(cmd.ExecuteScalar());
                }
            }
        }

        private static void InsertProject(SqliteConnection conn, int id, string name) {
            Exec(conn, $"INSERT INTO project (Id, profileId, name, state, priority) " +
                       $"VALUES ({id}, '{Profile}', '{name}', 1, 1)");
        }

        private static List<string> Rows(string path, string table) {
            var rows = new List<string>();
            using (var conn = new SqliteConnection(ConnString(path, SqliteOpenMode.ReadOnly))) {
                conn.Open();
                using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText = $"SELECT * FROM {table} ORDER BY Id";
                    using (var r = cmd.ExecuteReader()) {
                        while (r.Read()) {
                            var cells = new List<string>();
                            for (var i = 0; i < r.FieldCount; i++) {
                                cells.Add(r.IsDBNull(i) ? "null" : Convert.ToString(r.GetValue(i),
                                    System.Globalization.CultureInfo.InvariantCulture));
                            }
                            rows.Add(string.Join("|", cells));
                        }
                    }
                }
            }
            return rows;
        }

        private static string Sha(byte[] bytes) {
            using (var sha = SHA256.Create()) {
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }

        // hasToken defaults to true: TsUploadService itself defaults to
        // TokenStore.HasToken, which is a real Windows Credential Manager
        // read. Every test here is about the ACP conversation, not about
        // what happens to be stored on the machine running the test, so the
        // seam is always overridden unless a test is specifically exercising
        // the no-token path.
        private static TsUploadService Service(ScriptedAcp acp, string dbPath, string tempDir,
                                               string token = "tok-123", List<TimeSpan> waits = null,
                                               bool hasToken = true) {
            var client = new AcpApiClient(Base, () => token, acp);
            return new TsUploadService(client, (t, ct) => {
                waits?.Add(t);
                return Task.CompletedTask;
            }, () => hasToken) {
                DbPathOverride = dbPath,
                TempDirOverride = tempDir,
            };
        }

        private static List<TsUploadProfile> Profiles() {
            return new List<TsUploadProfile> {
                new TsUploadProfile { Id = Profile, Name = "Voyager main" },
                new TsUploadProfile { Id = "9a01", Name = "Travel rig" },
            };
        }

        private static string[] TempCopies(string dir) {
            return Directory.GetFiles(dir, "acp-ts-upload-*");
        }

        // -- The consistent copy --------------------------------------------

        [Fact]
        public void TheCopyHoldsCommittedRowsThatAreStillOnlyInTheWal() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var copies = Directory.CreateDirectory(tmp.File("copies")).FullName;

                using (var writer = new SqliteConnection(ConnString(path))) {
                    writer.Open();
                    Exec(writer, "PRAGMA journal_mode = WAL");
                    // No checkpoint, and the writer stays open, so the rows
                    // are committed but live only in the -wal file.
                    Exec(writer, "PRAGMA wal_autocheckpoint = 0");
                    InsertProject(writer, 1, "Rosette");
                    InsertProject(writer, 2, "Horsehead");
                    InsertProject(writer, 3, "M31");
                    Assert.True(new FileInfo(path + "-wal").Length > 0);

                    // A plain copy of the main file alone misses them. That is
                    // the failure the backup API is there to avoid.
                    var raw = tmp.File("raw.sqlite");
                    using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var dst = File.Create(raw)) {
                        src.CopyTo(dst);
                    }
                    // Read-write, because its header still says WAL and there
                    // is no -wal file beside it for a read-only open to use.
                    Assert.Equal(0, Scalar(raw, "SELECT COUNT(*) FROM project", SqliteOpenMode.ReadWrite));

                    using (var copy = TsUploadCopy.Make(path, copies)) {
                        Assert.Equal(3, Scalar(copy.Path, "SELECT COUNT(*) FROM project"));
                        Assert.Equal(28, copy.UserVersion);
                        // One self-contained file: back in rollback mode.
                        using (var conn = new SqliteConnection(ConnString(copy.Path, SqliteOpenMode.ReadOnly))) {
                            conn.Open();
                            using (var cmd = conn.CreateCommand()) {
                                cmd.CommandText = "PRAGMA journal_mode";
                                Assert.Equal("delete", ((string)cmd.ExecuteScalar()).ToLowerInvariant());
                            }
                        }
                        Assert.Equal(TsUploadCopy.HashFile(copy.Path), copy.Sha256);
                    }
                    Assert.Empty(TempCopies(copies));
                }
            }
        }

        [Fact]
        public void TheCopyKeepsEveryRowIdInEveryTsTable() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                using (var conn = new SqliteConnection(ConnString(path))) {
                    conn.Open();
                    // Gaps in the ids, the way a database looks after deletes.
                    InsertProject(conn, 3, "A");
                    InsertProject(conn, 9, "B");
                    InsertProject(conn, 41, "C");
                    Exec(conn, "INSERT INTO target (Id, name, active, epochcode, projectid, guid) VALUES " +
                               "(5, 'T5', 1, 2, 3, 'g5'), (17, 'T17', 1, 2, 9, 'g17'), (230, 'T230', 1, 2, 41, NULL)");
                    Exec(conn, $"INSERT INTO exposuretemplate (Id, profileId, name, filtername) VALUES " +
                               $"(2, '{Profile}', 'Ha', 'Ha'), (11, '{Profile}', 'OIII', 'OIII')");
                    Exec(conn, $"INSERT INTO exposureplan (Id, profileId, exposure, desired, targetid, exposureTemplateId) VALUES " +
                               $"(7, '{Profile}', 300, 10, 5, 2), (70, '{Profile}', 600, 4, 17, 11), (700, '{Profile}', 120, 1, 230, 2)");
                    Exec(conn, "INSERT INTO ruleweight (Id, name, weight, projectid) VALUES (8, 'MeridianWindow', 50, 9)");
                }

                using (var copy = TsUploadCopy.Make(path, tmp.Path)) {
                    foreach (var table in new[] { "project", "target", "exposuretemplate", "exposureplan", "ruleweight" }) {
                        var expected = Rows(path, table);
                        Assert.NotEmpty(expected);
                        Assert.Equal(expected, Rows(copy.Path, table));
                    }
                }
            }
        }

        [Fact]
        public async Task AVersion22DatabaseIsRefusedBeforeTheUploadHttpCall() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(22, tmp.File("schedulerdb.sqlite"));
                var acp = new ScriptedAcp(_ => ScriptedAcp.Json(HttpStatusCode.Accepted, Accepted));

                var outcome = await Service(acp, path, tmp.Path).SendAsync(Profile, "Voyager main", Profiles(), null);

                // The token check still goes out, since it needs no copy of
                // the database, but the schema refusal stops the send before
                // the actual upload is ever attempted.
                Assert.Single(acp.Requests);
                Assert.Equal("/api/version", acp.Requests[0].Path);
                Assert.False(outcome.Uploaded);
                Assert.Equal("✗ " + TsSchema.UnsupportedMessage(22), outcome.Line);
                Assert.Empty(TempCopies(tmp.Path));
            }
        }

        // -- The token check, before anything is copied or sent -------------

        [Fact]
        public async Task NoStoredTokenSendsNoRequestAndMakesNoCopy() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var copies = Directory.CreateDirectory(tmp.File("copies")).FullName;
                var acp = new ScriptedAcp(_ => ScriptedAcp.Json(HttpStatusCode.Accepted, Accepted));
                var client = new AcpApiClient(Base, () => null, acp);
                var service = new TsUploadService(client, hasToken: () => false) {
                    DbPathOverride = path,
                    TempDirOverride = copies,
                };

                var outcome = await service.SendAsync(Profile, "Voyager main", Profiles(), null);

                Assert.Empty(acp.Requests);
                Assert.False(outcome.Uploaded);
                Assert.Equal(TsUploadService.NoTokenLine, outcome.Line);
                Assert.True(outcome.CopyDeleted);
                Assert.Empty(TempCopies(copies));
            }
        }

        [Fact]
        public async Task ARejectedTokenMakesNoCopy() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var copies = Directory.CreateDirectory(tmp.File("copies")).FullName;
                var acp = new ScriptedAcp(_ => ScriptedAcp.Json(
                    HttpStatusCode.Unauthorized, "{\"error\": \"unauthorized\"}"));

                var outcome = await Service(acp, path, copies).SendAsync(Profile, "Voyager main", Profiles(), null);

                Assert.Single(acp.Requests);
                Assert.Equal("/api/version", acp.Requests[0].Path);
                Assert.False(outcome.Uploaded);
                Assert.Equal("✗ ACP rejected the token", outcome.Line);
                Assert.True(outcome.CopyDeleted);
                Assert.Empty(TempCopies(copies));
            }
        }

        [Fact]
        public async Task AServerWithNoTokenSetMakesNoCopy() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var copies = Directory.CreateDirectory(tmp.File("copies")).FullName;
                var acp = new ScriptedAcp(_ => ScriptedAcp.Json(
                    HttpStatusCode.Forbidden,
                    "{\"error\": \"ACP needs an access token set before it accepts uploads\"}"));

                var outcome = await Service(acp, path, copies).SendAsync(Profile, "Voyager main", Profiles(), null);

                Assert.Single(acp.Requests);
                Assert.Equal("/api/version", acp.Requests[0].Path);
                Assert.False(outcome.Uploaded);
                Assert.Equal("✗ ACP needs an access token set before it accepts uploads", outcome.Line);
                Assert.True(outcome.CopyDeleted);
                Assert.Empty(TempCopies(copies));
            }
        }

        // -- The upload -----------------------------------------------------

        [Fact]
        public async Task TheUploadIsMultipartWithFileAndMetaAndTheBearerHeader() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                using (var conn = new SqliteConnection(ConnString(path))) {
                    conn.Open();
                    InsertProject(conn, 1, "Rosette");
                }
                var copies = Directory.CreateDirectory(tmp.File("copies")).FullName;
                var acp = new ScriptedAcp(seen => seen.Method == HttpMethod.Post
                    ? ScriptedAcp.Json(HttpStatusCode.Accepted, Accepted)
                    : ScriptedAcp.Json(HttpStatusCode.OK,
                        "{\"state\": \"ready\", \"counts\": {\"new\": 2, \"updated\": 5, \"conflicts\": 1}}"));
                var lines = new List<string>();

                var outcome = await Service(acp, path, copies)
                    .SendAsync(Profile, "Voyager main", Profiles(), new SyncProgress(lines));

                // First the token check, with the bearer header, before
                // anything is copied.
                Assert.Equal("/api/version", acp.Requests[0].Path);
                Assert.Equal("Bearer tok-123", acp.Requests[0].Authorization);

                var upload = acp.Requests[1];
                Assert.Equal(HttpMethod.Post, upload.Method);
                Assert.Equal(AcpApiClient.TsUploadPath, upload.Path);
                Assert.Equal("Bearer tok-123", upload.Authorization);
                Assert.Equal("multipart/form-data", upload.ContentType);
                Assert.Equal(new[] { "file", "meta" }, upload.Parts.Keys.OrderBy(k => k).ToArray());
                Assert.Equal("application/vnd.sqlite3", upload.PartTypes["file"]);

                var file = upload.Parts["file"];
                Assert.Equal("SQLite format 3\0", Encoding.ASCII.GetString(file, 0, 16));
                var meta = JObject.Parse(Encoding.UTF8.GetString(upload.Parts["meta"]));
                Assert.Equal(Sha(file), (string)meta["sha256"]);
                Assert.Equal(Profile, (string)meta["profile_id"]);
                Assert.Equal(28, (int)meta["user_version"]);
                Assert.Equal(Environment.MachineName, (string)meta["machine"]);
                Assert.Equal(2, ((JArray)meta["profiles"]).Count);
                Assert.Equal("Voyager main", (string)meta["profiles"][0]["name"]);
                Assert.False(string.IsNullOrEmpty((string)meta["plugin_version"]));

                // Then the status poll, with the same header.
                Assert.Equal(StatusPath, acp.Requests[2].Path);
                Assert.Equal("Bearer tok-123", acp.Requests[2].Authorization);

                Assert.True(outcome.Uploaded);
                Assert.Equal(
                    "✓ Voyager main: 2 new, 5 updated, 1 needs a choice. Review and apply them in ACP before anything changes.",
                    outcome.Line);
                Assert.Equal(Base + ReviewPath, outcome.ReviewUrl);
                Assert.True(outcome.CopyDeleted);
                Assert.Empty(TempCopies(copies));
                Assert.Contains(lines, l => l.Contains("copying"));
                Assert.Contains(lines, l => l.Contains("ACP is reading it"));
            }
        }

        public static IEnumerable<object[]> Refusals() {
            // 401 and 403 are token problems, so the pre-flight token check
            // catches them on its own request; the upload is never attempted.
            yield return new object[] {
                HttpStatusCode.Unauthorized, "{\"error\": \"unauthorized\"}", "✗ ACP rejected the token", 1,
            };
            yield return new object[] {
                HttpStatusCode.Forbidden,
                "{\"error\": \"ACP needs an access token set before it accepts uploads\"}",
                "✗ ACP needs an access token set before it accepts uploads", 1,
            };
            // 413 and 415 are about the file itself, not the token, so the
            // pre-flight check passes and the refusal comes from the actual
            // upload, the second request.
            yield return new object[] {
                HttpStatusCode.RequestEntityTooLarge,
                "{\"error\": \"The upload is over the 64 MB limit\"}",
                "✗ The upload is over the 64 MB limit", 2,
            };
            yield return new object[] {
                HttpStatusCode.UnsupportedMediaType,
                "{\"error\": \"not a SQLite database\"}",
                "✗ ACP says this is not a SQLite database: not a SQLite database", 2,
            };
        }

        [Theory]
        [MemberData(nameof(Refusals))]
        public async Task EachRefusalGivesItsDockLineAndTheCopyIsDeleted(
            HttpStatusCode status, string body, string expectedLine, int expectedRequestCount
        ) {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var copies = Directory.CreateDirectory(tmp.File("copies")).FullName;
                var acp = new ScriptedAcp(_ => ScriptedAcp.Json(status, body));

                var service = Service(acp, path, copies);
                var outcome = await service.SendAsync(Profile, "Voyager main", Profiles(), null);

                Assert.Equal(expectedRequestCount, acp.Requests.Count);
                Assert.False(outcome.Uploaded);
                Assert.Equal(expectedLine, outcome.Line);
                Assert.Null(outcome.ReviewUrl);
                Assert.True(outcome.CopyDeleted);
                Assert.False(File.Exists(service.LastCopyPath));
                Assert.Empty(TempCopies(copies));
            }
        }

        [Fact]
        public async Task ADroppedConnectionSaysNothingChangedAndTheCopyIsDeleted() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var copies = Directory.CreateDirectory(tmp.File("copies")).FullName;
                var acp = new ScriptedAcp(_ => throw new HttpRequestException("connection reset"));

                var service = Service(acp, path, copies);
                var outcome = await service.SendAsync(Profile, "Voyager main", Profiles(), null);

                Assert.Equal(TsUploadService.InterruptedLine, outcome.Line);
                Assert.True(outcome.CopyDeleted);
                Assert.Empty(TempCopies(copies));
            }
        }

        [Fact]
        public async Task AFailedReadShowsAcpsMessageAndNoButton() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var acp = new ScriptedAcp(seen => seen.Method == HttpMethod.Post
                    ? ScriptedAcp.Json(HttpStatusCode.Accepted, Accepted)
                    : ScriptedAcp.Json(HttpStatusCode.OK,
                        "{\"state\": \"failed\", \"error\": \"a SQLite file, but not a Target Scheduler database\"}"));

                var outcome = await Service(acp, path, tmp.Path).SendAsync(Profile, "Voyager main", Profiles(), null);

                Assert.Equal("✗ a SQLite file, but not a Target Scheduler database", outcome.Line);
                Assert.Null(outcome.ReviewUrl);
            }
        }

        [Fact]
        public async Task AfterTwoMinutesOfCheckingTheButtonShowsAnyway() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var acp = new ScriptedAcp(seen => seen.Method == HttpMethod.Post
                    ? ScriptedAcp.Json(HttpStatusCode.Accepted, Accepted)
                    : ScriptedAcp.Json(HttpStatusCode.OK, "{\"state\": \"checking\"}"));
                var waits = new List<TimeSpan>();

                var outcome = await Service(acp, path, tmp.Path, waits: waits)
                    .SendAsync(Profile, "Voyager main", Profiles(), null);

                Assert.StartsWith("ACP is still reading it", outcome.Line);
                Assert.Equal(Base + ReviewPath, outcome.ReviewUrl);
                Assert.All(waits, w => Assert.Equal(TimeSpan.FromSeconds(2), w));
                Assert.Equal(60, waits.Count);
                // One token check, one upload, then a poll before each wait
                // and one after the last.
                Assert.Equal(1 + 1 + 61, acp.Requests.Count);
            }
        }

        [Fact]
        public void AReviewUrlOnAnotherServerIsPinnedToTheConfiguredOne() {
            var client = new AcpApiClient(Base, () => null, new ScriptedAcp(_ => null));
            Assert.Equal(Base + "/x?y=1", client.Resolve("/x?y=1"));
            Assert.Equal(Base + "/x", client.Resolve(Base + "/x"));
            Assert.Equal(Base + "/steal", client.Resolve("http://evil.test/steal"));
            Assert.Null(client.Resolve(null));
        }

        // -- RefsFor --------------------------------------------------------

        private static JObject RefsBlock(string profileId, int projectId) {
            return new JObject {
                { "profile_id", profileId },
                { "project_id", projectId },
                { "target_ids_by_panel", new JObject { { "1,1", projectId * 10 } } },
                { "template_ids_by_filter", new JObject() },
                { "exposure_plan_ids", new JObject() },
            };
        }

        [Fact]
        public void RefsForReadsTheProfilesLinkFirst() {
            var plan = TsTestPlans.Plan("p1");
            plan.TsLinks = new JObject {
                { "other", new JObject { { "machine", "HAYABUSA" }, { "refs", RefsBlock("other", 9) } } },
                { Profile, new JObject { { "machine", "VOYAGER" }, { "refs", RefsBlock(Profile, 4) } } },
            };
            plan.TsRefs = RefsBlock(Profile, 1);

            Assert.Equal(4, TsConvert.RefsFor(plan, Profile).ProjectId);
            Assert.Equal(40, TsConvert.RefsFor(plan, Profile).TargetIdsByPanel["1,1"]);
            Assert.Equal(9, TsConvert.RefsFor(plan, "other").ProjectId);
        }

        [Fact]
        public void RefsForFallsBackToTsRefsWhenItsProfileMatches() {
            var plan = TsTestPlans.Plan("p1");
            plan.TsLinks = new JObject {
                { "other", new JObject { { "refs", RefsBlock("other", 9) } } },
            };
            plan.TsRefs = RefsBlock(Profile, 1);

            Assert.Equal(1, TsConvert.RefsFor(plan, Profile).ProjectId);
        }

        [Fact]
        public void RefsForGivesNothingWhenNeitherBelongsToTheProfile() {
            var plan = TsTestPlans.Plan("p1");
            plan.TsLinks = new JObject {
                { "other", new JObject { { "refs", RefsBlock("other", 9) } } },
            };
            plan.TsRefs = RefsBlock("other", 9);

            Assert.Null(TsConvert.RefsFor(plan, Profile));
            Assert.Null(TsConvert.RefsFor(TsTestPlans.Plan("bare"), Profile));
        }

        [Fact]
        public void TsLinksDeserialiseFromAcpsPlanJson() {
            var json = "{\"id\": \"p1\", \"ts_links\": {\"" + Profile + "\": {\"machine\": \"VOYAGER\", " +
                       "\"refs\": {\"profile_id\": \"" + Profile + "\", \"project_id\": 12}, " +
                       "\"base_snapshot\": {}, \"updated_iso\": \"2026-09-24T10:31:00Z\"}}}";
            var plan = JsonConvert.DeserializeObject<Plan>(json);
            Assert.Equal(12, TsConvert.RefsFor(plan, Profile).ProjectId);
        }

        // -- Push state sent back -------------------------------------------

        private static async Task<TsPushResult> PushThreePlansAndOneLeftOut(string dbPath) {
            var service = new TsPushService(
                new FakeContainerWatch(), p => TargetSchedulerDb.Open(dbPath), () => TsTestPlans.FrozenNow
            ) { DbPathOverride = dbPath, MakeBackup = false };
            var plans = TsTestPlans.ThreePlans();
            plans.Add(TsTestPlans.Plan("nogoals", filterGoals: new Dictionary<string, FilterGoal>()));
            return await service.PushAsync(plans, TsTestPlans.Gear(), Profile);
        }

        [Fact]
        public async Task AfterAPushOnePostToLinksCarriesAStatePerPushedPlan() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var push = await PushThreePlansAndOneLeftOut(path);
                Assert.True(push.Success, push.Failure);

                var acp = new ScriptedAcp(_ => ScriptedAcp.Json(HttpStatusCode.OK, "{\"updated\": 3}"));
                var client = new AcpApiClient(Base, () => "tok-123", acp);
                var warning = await TsLinksReporter.PostAsync(client, push, Profile);

                Assert.Null(warning);
                var post = Assert.Single(acp.Requests);
                Assert.Equal(HttpMethod.Post, post.Method);
                Assert.Equal(AcpApiClient.TsLinksPath, post.Path);
                Assert.Equal("Bearer tok-123", post.Authorization);

                var body = JObject.Parse(post.Body);
                Assert.Equal(Profile, (string)body["profile_id"]);
                Assert.Equal(Environment.MachineName, (string)body["machine"]);
                var states = (JArray)body["states"];
                Assert.Equal(new[] { "mosaic", "single", "twofilter" },
                    states.Select(s => (string)s["plan_id"]).OrderBy(x => x).ToArray());
                foreach (var s in states) {
                    Assert.Equal(Profile, (string)s["refs"]["profile_id"]);
                    Assert.Equal(JTokenType.Integer, s["refs"]["project_id"].Type);
                    Assert.NotNull(s["base_snapshot"]["project"]);
                }
                // The mosaic's four panels all have their target ids.
                var mosaic = states.First(s => (string)s["plan_id"] == "mosaic");
                Assert.Equal(4, ((JObject)mosaic["refs"]["target_ids_by_panel"]).Count);
            }
        }

        [Fact]
        public async Task WhenLinksFailsTheSyncStillCountsWithAWarning() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var push = await PushThreePlansAndOneLeftOut(path);

                var acp = new ScriptedAcp(_ => ScriptedAcp.Json(HttpStatusCode.InternalServerError, "{\"error\": \"boom\"}"));
                var warning = await TsLinksReporter.PostAsync(new AcpApiClient(Base, () => null, acp), push, Profile);

                Assert.Equal(TsLinksReporter.NotToldWarning, warning);
                Assert.True(push.Success);

                var outcome = new SyncOutcome {
                    Success = true, TsPush = push, LinksWarning = warning,
                    WriteBack = new WriteBackResult(),
                };
                Assert.False(outcome.PushRefused);
                Assert.EndsWith(TsLinksReporter.NotToldWarning, outcome.ShortResult);
                Assert.StartsWith("3 plans loaded into Target Scheduler", outcome.ShortResult);
            }
        }

        [Fact]
        public async Task AFailedPushSendsNoLinks() {
            var push = new TsPushResult { Success = false, Failure = "locked" };
            var acp = new ScriptedAcp(_ => ScriptedAcp.Json(HttpStatusCode.OK, "{}"));
            var warning = await TsLinksReporter.PostAsync(new AcpApiClient(Base, () => null, acp), push, Profile);
            Assert.Null(warning);
            Assert.Empty(acp.Requests);
        }

        [Fact]
        public void TheStatusReplyIsReadFromTheAgreedFields() {
            var s = TsUploadStatus.Parse(
                "{\"state\": \"ready\", \"review_url\": \"/r\", \"counts\": {\"new\": 1, \"updated\": 0, \"conflicts\": 2}}");
            Assert.Equal(TsUploadStatus.Ready, s.State);
            Assert.Equal("/r", s.ReviewUrl);
            // A zero category (updated, here) is left out of the line
            // entirely rather than printed as "0 updated".
            Assert.Equal(
                "Voyager main: 1 new, 2 need a choice. Review and apply them in ACP before anything changes.",
                TsUploadService.ReadyLine("Voyager main", s));

            var bare = TsUploadStatus.Parse("{\"state\": \"ready\"}");
            Assert.False(bare.HasCounts);
            Assert.Equal("Voyager main: ACP has read it. Review it in ACP.",
                TsUploadService.ReadyLine("Voyager main", bare));
        }

        [Fact]
        public void ReadyLineNamesEveryNonZeroCategoryAndSaysWhenThereIsNothingToReview() {
            var everything = TsUploadStatus.Parse(
                "{\"state\": \"ready\", \"counts\": {\"new\": 32, \"updated\": 5, \"conflicts\": 46}}");
            Assert.Equal(
                "Voyager main: 32 new, 5 updated, 46 need a choice. Review and apply them in ACP before anything changes.",
                TsUploadService.ReadyLine("Voyager main", everything));

            var onlyOneNeedingAChoice = TsUploadStatus.Parse(
                "{\"state\": \"ready\", \"counts\": {\"new\": 0, \"updated\": 0, \"conflicts\": 1}}");
            Assert.Equal(
                "Voyager main: 1 needs a choice. Review and apply them in ACP before anything changes.",
                TsUploadService.ReadyLine("Voyager main", onlyOneNeedingAChoice));

            var nothing = TsUploadStatus.Parse(
                "{\"state\": \"ready\", \"counts\": {\"new\": 0, \"updated\": 0, \"conflicts\": 0}}");
            Assert.Equal(
                "Voyager main: ACP found nothing new or changed. Nothing to review.",
                TsUploadService.ReadyLine("Voyager main", nothing));
        }

        /// IProgress that records synchronously, so the test sees every line
        /// without waiting on a synchronisation context.
        private class SyncProgress : IProgress<string> {
            private readonly List<string> into;
            public SyncProgress(List<string> into) { this.into = into; }
            public void Report(string value) { lock (into) into.Add(value); }
        }
    }
}
