using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// The Target Scheduler database, opened for one profile.
    ///
    /// Ported from nina_ts_sync/db.py and reader.py. Three responsibilities:
    /// open the connection the way the Python extension does, refuse a schema
    /// version outside the supported range before anything is written, and
    /// expose the reads and writes the push needs.
    ///
    /// Foreign key enforcement is deliberately left off, because Target
    /// Scheduler itself does not turn it on. Turning it on here would make
    /// inserts in a particular order start failing against databases that work
    /// fine for the plugin that owns them.
    public class TargetSchedulerDb : IDisposable {

        /// Matches DEFAULT_BUSY_TIMEOUT_MS in the Python extension. This is
        /// SQLite's own wait; the 2, 4, 8 second retry in RunWriteAsync sits on
        /// top of it for a lock that outlasts ten seconds.
        public const int BusyTimeoutMs = 10000;

        /// The backoff the spec asks for, in seconds. Four attempts in total:
        /// the first, then one after each wait.
        private static readonly int[] RetrySecondsDefault = { 2, 4, 8 };

        private readonly SqliteConnection connection;
        private bool disposed;

        public string DbPath { get; }

        public int UserVersion { get; }

        /// What PRAGMA table_info actually reports, lowercased, for the four
        /// tables the push writes. The declared user_version says which
        /// columns should be there; this is the backstop for a database that
        /// has drifted from the migration history.
        public IReadOnlyDictionary<string, HashSet<string>> ColumnsByTable { get; }

        private TargetSchedulerDb(
            SqliteConnection connection,
            string dbPath,
            int userVersion,
            Dictionary<string, HashSet<string>> columnsByTable
        ) {
            this.connection = connection;
            DbPath = dbPath;
            UserVersion = userVersion;
            ColumnsByTable = columnsByTable;
        }

        /// The live connection, for the upsert to run statements on. Kept
        /// internal to the assembly so nothing outside can bypass the version
        /// gate this class enforces on the way in.
        internal SqliteConnection Connection => connection;

        // -- Opening --------------------------------------------------------

        /// Open the database at `dbPath`, or at the conventional location when
        /// that is null, and verify the schema version before handing it back.
        ///
        /// Throws TsSchemaVersionException when user_version is outside 23 to
        /// 28. The check happens on open rather than on first write so a caller
        /// that only wanted to read acquired counts still finds out early
        /// rather than reading columns it has no business trusting.
        public static TargetSchedulerDb Open(string dbPath = null, bool readOnly = false) {
            var path = TsPaths.ResolveDbPath(dbPath);

            var builder = new SqliteConnectionStringBuilder {
                DataSource = path,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
                // Shared cache would make two connections in one process fight
                // over table locks instead of file locks, which is not how the
                // Target Scheduler plugin opens it.
                Cache = SqliteCacheMode.Private,
                // Pooling keeps the file handle open after Dispose. This is
                // somebody else's database, sitting under a plugin that opens
                // and closes it around every operation, so holding a handle
                // after we are finished is the wrong default here: it blocks
                // the backup copy and would leave a handle on the live
                // database for the rest of the NINA session.
                Pooling = false,
            };

            var conn = new SqliteConnection(builder.ToString());
            try {
                conn.Open();
                Execute(conn, $"PRAGMA busy_timeout = {BusyTimeoutMs}");

                var version = ReadUserVersion(conn);
                if (!TsSchema.IsSupported(version)) {
                    throw new TsSchemaVersionException(version);
                }

                return new TargetSchedulerDb(conn, path, version, ProbeColumns(conn));
            } catch {
                conn.Dispose();
                throw;
            }
        }

        public static int ReadUserVersion(SqliteConnection conn) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "PRAGMA user_version";
                var value = cmd.ExecuteScalar();
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }

        private static Dictionary<string, HashSet<string>> ProbeColumns(SqliteConnection conn) {
            var byTable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in new[] { "exposuretemplate", "project", "target", "exposureplan" }) {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText = $"PRAGMA table_info({table})";
                    using (var reader = cmd.ExecuteReader()) {
                        while (reader.Read()) {
                            names.Add(reader.GetString(1));
                        }
                    }
                }
                if (names.Count > 0) byTable[table] = names;
            }
            return byTable;
        }

        // -- Backup ---------------------------------------------------------

        /// Copy the database beside itself before the first write of a run, the
        /// way the Python extension does. A plain file copy rather than
        /// SQLite's backup API, because the file is small and Target Scheduler
        /// closes its connection between operations.
        ///
        /// The copy goes through an explicit stream rather than File.Copy,
        /// which opens the source with FileShare.Read and therefore fails the
        /// moment anything else has the database open for writing. A backup
        /// that refuses to run whenever Target Scheduler happens to be looking
        /// at the file is worse than no backup at all, because it stops the
        /// sync. Taking the copy while another process could be mid-write is
        /// the same exposure the Python extension has, and the container watch
        /// is what keeps a real imaging run out of the way.
        public static string BackupTo(string dbPath) {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            var dir = Path.GetDirectoryName(dbPath) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(dbPath);
            var ext = Path.GetExtension(dbPath);
            var dst = Path.Combine(dir, $"{stem}-acpsync-{stamp}-backup{ext}");

            using (var src = new FileStream(
                       dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var out_ = new FileStream(
                       dst, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                src.CopyTo(out_);
            }
            return dst;
        }

        // -- Writing --------------------------------------------------------

        /// Run `body` inside a BEGIN IMMEDIATE transaction, retrying the whole
        /// attempt on "database is locked" after 2, then 4, then 8 seconds.
        ///
        /// The transaction is immediate so the write lock is taken up front
        /// rather than part way through, which is what turns a mid-sync clash
        /// with Target Scheduler into a clean retry instead of a half-written
        /// project. Anything the body throws rolls the transaction back.
        public async Task<T> RunWriteAsync<T>(
            Func<SqliteConnection, T> body,
            CancellationToken ct = default,
            int[] retrySeconds = null
        ) {
            var waits = retrySeconds ?? RetrySecondsDefault;

            for (var attempt = 0; ; attempt++) {
                ct.ThrowIfCancellationRequested();
                try {
                    return RunWriteOnce(body);
                } catch (SqliteException ex) when (IsLocked(ex) && attempt < waits.Length) {
                    await Task.Delay(TimeSpan.FromSeconds(waits[attempt]), ct).ConfigureAwait(false);
                }
            }
        }

        private T RunWriteOnce<T>(Func<SqliteConnection, T> body) {
            Execute(connection, "BEGIN IMMEDIATE");
            var committed = false;
            try {
                var result = body(connection);
                Execute(connection, "COMMIT");
                committed = true;
                return result;
            } finally {
                if (!committed) {
                    try {
                        Execute(connection, "ROLLBACK");
                    } catch (SqliteException) {
                        // A failed BEGIN leaves nothing to roll back, and the
                        // original exception is the one worth reporting.
                    }
                }
            }
        }

        /// SQLITE_BUSY and SQLITE_LOCKED both surface as "database is locked"
        /// or "database table is locked". Matching on the numeric codes rather
        /// than the message keeps this working under a localised SQLite.
        public static bool IsLocked(SqliteException ex) {
            return ex != null && (ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6);
        }

        // -- Reading --------------------------------------------------------

        /// Every row for a profile across the four tables the push writes, with
        /// the on-disk Ids preserved. Ported from reader.read_all; the base
        /// snapshot needs it after a push to record what actually landed.
        public TsSnapshot ReadAll(string profileId) {
            var snap = new TsSnapshot { ProfileId = profileId };

            Query(
                "SELECT * FROM exposuretemplate WHERE profileId = $p",
                new Dictionary<string, object> { { "$p", profileId } },
                r => {
                    var id = ReadInt(r, "Id");
                    var tpl = new TsExposureTemplate {
                        ProfileId = ReadString(r, "profileId"),
                        Name = ReadString(r, "name"),
                        FilterName = ReadString(r, "filtername"),
                        Guid = ReadString(r, "guid"),
                        DefaultExposure = ReadDouble(r, "defaultexposure", 60.0),
                        Gain = ReadInt(r, "gain", -1),
                        Offset = ReadInt(r, "offset", -1),
                        Bin = ReadInt(r, "bin", 1),
                    };
                    snap.TemplatesById[id] = tpl;
                    if (!string.IsNullOrEmpty(tpl.Guid)) snap.TemplateIdByGuid[tpl.Guid] = id;
                });

            Query(
                "SELECT * FROM project WHERE profileId = $p",
                new Dictionary<string, object> { { "$p", profileId } },
                r => {
                    var id = ReadInt(r, "Id");
                    var proj = new TsProject {
                        ProfileId = ReadString(r, "profileId"),
                        Name = ReadString(r, "name"),
                        Guid = ReadString(r, "guid"),
                        Priority = ReadInt(r, "priority", 1),
                        MinimumAltitude = ReadDouble(r, "minimumaltitude"),
                        MeridianWindow = ReadInt(r, "meridianwindow"),
                        EnableGrader = ReadInt(r, "enablegrader"),
                        State = ReadInt(r, "state", 1),
                    };
                    snap.ProjectsById[id] = proj;
                    if (!string.IsNullOrEmpty(proj.Guid)) snap.ProjectIdByGuid[proj.Guid] = id;
                });

            if (snap.ProjectsById.Count > 0) {
                // Targets carry no profileId of their own, so they are scoped
                // through their parent project the way the Python reader does.
                var ids = new List<string>();
                var args = new Dictionary<string, object>();
                var n = 0;
                foreach (var id in snap.ProjectsById.Keys) {
                    var key = "$id" + n++;
                    ids.Add(key);
                    args[key] = id;
                }
                Query(
                    $"SELECT * FROM target WHERE projectid IN ({string.Join(",", ids)})",
                    args,
                    r => {
                        var id = ReadInt(r, "Id");
                        var tgt = new TsTarget {
                            ProjectId = ReadInt(r, "projectid"),
                            Name = ReadString(r, "name"),
                            Guid = ReadString(r, "guid"),
                            Ra = ReadDouble(r, "ra"),
                            Dec = ReadDouble(r, "dec"),
                            Rotation = ReadDouble(r, "rotation"),
                            // Absent at user_version 23; the Target Scheduler
                            // default stands in, exactly as the Python reader's
                            // dataclass default does.
                            Priority = ReadInt(r, "priority", -1),
                        };
                        snap.TargetsById[id] = tgt;
                        if (!string.IsNullOrEmpty(tgt.Guid)) snap.TargetIdByGuid[tgt.Guid] = id;
                    });
            }

            Query(
                "SELECT * FROM exposureplan WHERE profileId = $p",
                new Dictionary<string, object> { { "$p", profileId } },
                r => {
                    var id = ReadInt(r, "Id");
                    var plan = new TsExposurePlan {
                        ProfileId = ReadString(r, "profileId"),
                        TargetId = ReadInt(r, "targetid"),
                        ExposureTemplateId = ReadInt(r, "exposureTemplateId"),
                        Guid = ReadString(r, "guid"),
                        Exposure = ReadDouble(r, "exposure", -1.0),
                        Desired = ReadInt(r, "desired"),
                        Acquired = ReadInt(r, "acquired"),
                        Accepted = ReadInt(r, "accepted"),
                        Enabled = ReadInt(r, "enabled", 1),
                    };
                    snap.PlansById[id] = plan;
                    if (!string.IsNullOrEmpty(plan.Guid)) snap.PlanIdByGuid[plan.Guid] = id;
                });

            return snap;
        }

        /// Good subs and the exposure length per exposure plan guid, ported
        /// from reader.read_acquired.
        ///
        /// "Good" prefers `accepted` over `acquired` when the parent project
        /// has the grader on, so frames the grader rejected do not count
        /// towards ACP's hours remaining. Plans with no guid are skipped: they
        /// were made in the Target Scheduler UI and are nobody's to claim.
        public Dictionary<string, TsAcquired> ReadAcquired(string profileId) {
            var result = new Dictionary<string, TsAcquired>();
            Query(
                "SELECT ep.guid AS guid, ep.acquired AS acquired, " +
                "       ep.accepted AS accepted, ep.exposure AS exposure, " +
                "       p.enablegrader AS enablegrader " +
                "FROM exposureplan ep " +
                "JOIN target t ON ep.targetid = t.Id " +
                "JOIN project p ON t.projectid = p.Id " +
                "WHERE ep.profileId = $p AND ep.guid IS NOT NULL AND ep.guid != ''",
                new Dictionary<string, object> { { "$p", profileId } },
                r => {
                    var graded = ReadInt(r, "enablegrader") == 1;
                    result[ReadString(r, "guid")] = new TsAcquired {
                        Count = graded ? ReadInt(r, "accepted") : ReadInt(r, "acquired"),
                        ExposureSeconds = ReadDouble(r, "exposure"),
                    };
                });
            return result;
        }

        private void Query(
            string sql, Dictionary<string, object> args, Action<SqliteDataReader> onRow
        ) {
            using (var cmd = connection.CreateCommand()) {
                cmd.CommandText = sql;
                if (args != null) {
                    foreach (var kv in args) cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                }
                using (var reader = cmd.ExecuteReader()) {
                    while (reader.Read()) onRow(reader);
                }
            }
        }

        // -- Row helpers ----------------------------------------------------
        //
        // A column a newer schema added is simply absent on an older database,
        // and the caller's default stands in, which is the same tolerance the
        // Python reader gets from its dataclass defaults.

        private static int Ordinal(SqliteDataReader r, string name) {
            for (var i = 0; i < r.FieldCount; i++) {
                if (string.Equals(r.GetName(i), name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        public static string ReadString(SqliteDataReader r, string name, string fallback = null) {
            var i = Ordinal(r, name);
            if (i < 0 || r.IsDBNull(i)) return fallback;
            return r.GetValue(i)?.ToString();
        }

        public static int ReadInt(SqliteDataReader r, string name, int fallback = 0) {
            var i = Ordinal(r, name);
            if (i < 0 || r.IsDBNull(i)) return fallback;
            return Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);
        }

        public static double ReadDouble(SqliteDataReader r, string name, double fallback = 0.0) {
            var i = Ordinal(r, name);
            if (i < 0 || r.IsDBNull(i)) return fallback;
            return Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
        }

        private static void Execute(SqliteConnection conn, string sql) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            connection?.Dispose();
        }
    }

    /// Good subs and exposure length for one exposure plan.
    public struct TsAcquired {
        public int Count { get; set; }
        public double ExposureSeconds { get; set; }
    }

    /// Everything read for a profile, with the on-disk Ids kept so callers can
    /// follow the foreign keys. Ported from reader.TSSnapshot.
    public class TsSnapshot {

        public string ProfileId { get; set; }

        public Dictionary<int, TsExposureTemplate> TemplatesById { get; } =
            new Dictionary<int, TsExposureTemplate>();

        public Dictionary<int, TsProject> ProjectsById { get; } =
            new Dictionary<int, TsProject>();

        public Dictionary<int, TsTarget> TargetsById { get; } =
            new Dictionary<int, TsTarget>();

        public Dictionary<int, TsExposurePlan> PlansById { get; } =
            new Dictionary<int, TsExposurePlan>();

        public Dictionary<string, int> TemplateIdByGuid { get; } = new Dictionary<string, int>();

        public Dictionary<string, int> ProjectIdByGuid { get; } = new Dictionary<string, int>();

        public Dictionary<string, int> TargetIdByGuid { get; } = new Dictionary<string, int>();

        public Dictionary<string, int> PlanIdByGuid { get; } = new Dictionary<string, int>();
    }
}
