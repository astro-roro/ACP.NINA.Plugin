using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ACP.NINA.Plugin.Tests {

    /// Build an empty Target Scheduler database at a requested schema version.
    ///
    /// A port of tests/make_db.py from the acp-nina-ts-sync repo, sharing its
    /// three fixture files verbatim: 28 was dumped from a real install, 23 and
    /// 25 were reconstructed by replaying Target Scheduler's own migration
    /// scripts. The versions in between differ from the nearest lower fixture
    /// by a handful of additive ALTER TABLE statements, kept inline here rather
    /// than as three more near-identical schema dumps.
    ///
    /// Every statement below is copied from the matching
    /// NINA.Plugin.TargetScheduler/Database/Migrate/N.sql. See
    /// docs/schema-history.md in that repo for the citations.
    public static class TsFixtures {

        /// The versions there is a complete schema file for.
        public static readonly int[] BaseVersions = { 23, 25, 28 };

        /// The full range this suite can build, which is the supported range.
        public static readonly int[] BuildableVersions = { 23, 24, 25, 26, 27, 28 };

        /// Version to the ALTER statements Migrate/<version>.sql applies. Only
        /// the versions that have to be synthesised are listed.
        private static readonly Dictionary<int, string[]> MigrationSteps =
            new Dictionary<int, string[]> {
                { 24, new[] { "ALTER TABLE target ADD COLUMN priority INTEGER DEFAULT -1;" } },
                { 26, new[] { "ALTER TABLE profilepreference ADD COLUMN enablePlannerReports INTEGER DEFAULT 0;" } },
                { 27, new[] { "ALTER TABLE profilepreference ADD COLUMN enableClientUpdatesExposurePlan INTEGER DEFAULT 1;" } },
            };

        public static string FixturesDir {
            get {
                return Path.Combine(AppContext.BaseDirectory, "Fixtures");
            }
        }

        public static string ReadFixture(string name) {
            return File.ReadAllText(Path.Combine(FixturesDir, name));
        }

        /// Create an empty Target Scheduler database at `version` on disk.
        ///
        /// 22 and 29 are deliberately buildable so the refusal path can be
        /// tested: 22 is the 23 schema with the pragma wound back and 29 is the
        /// 28 schema wound forward. Neither is claimed to be the real schema at
        /// that version; they exist only to carry the pragma.
        public static string MakeDb(int version, string path) {
            int baseVersion;
            var steps = new List<string>();

            if (BuildableVersions.Contains(version)) {
                baseVersion = BaseVersions.Where(v => v <= version).Max();
                for (var v = baseVersion + 1; v <= version; v++) {
                    string[] stmts;
                    if (MigrationSteps.TryGetValue(v, out stmts)) steps.AddRange(stmts);
                }
            } else if (version == 22) {
                baseVersion = 23;
            } else if (version == 29) {
                baseVersion = 28;
            } else {
                throw new ArgumentOutOfRangeException(
                    nameof(version), $"no recipe for schema version {version}");
            }

            if (File.Exists(path)) File.Delete(path);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var builder = new SqliteConnectionStringBuilder {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                // Same reason as TargetSchedulerDb: a pooled handle outlives
                // Dispose and would keep the fixture file locked.
                Pooling = false,
            };
            using (var conn = new SqliteConnection(builder.ToString())) {
                conn.Open();
                Exec(conn, ReadFixture($"ts-schema-{baseVersion}.sql"));
                foreach (var stmt in steps) Exec(conn, stmt);
                Exec(conn, $"PRAGMA user_version = {version}");
            }
            return path;
        }

        public static List<string> TableColumns(string path, string table) {
            var names = new List<string>();
            var builder = new SqliteConnectionStringBuilder {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            using (var conn = new SqliteConnection(builder.ToString())) {
                conn.Open();
                using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText = $"PRAGMA table_info({table})";
                    using (var reader = cmd.ExecuteReader()) {
                        while (reader.Read()) names.Add(reader.GetString(1));
                    }
                }
            }
            return names;
        }

        private static void Exec(SqliteConnection conn, string sql) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }
    }

    /// A temporary directory that cleans itself up, so each test gets its own
    /// database file and nothing leaks between them.
    public sealed class TempDir : IDisposable {

        public string Path { get; }

        public TempDir() {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "acp-ts-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string name) {
            return System.IO.Path.Combine(Path, name);
        }

        public void Dispose() {
            try {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            } catch (IOException) {
                // A file still held open by a connection the test did not
                // dispose is not worth failing a passing test over.
            }
        }
    }
}
