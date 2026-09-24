using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Security.Cryptography;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// A consistent, read-only copy of Target Scheduler's database, made to be
    /// uploaded to ACP and deleted straight afterwards.
    ///
    /// Never a plain file copy. Target Scheduler may have the database open,
    /// and a raw copy can miss committed changes still in the WAL file or
    /// catch a page part way through a write. SQLite's online backup API reads
    /// through SQLite's own locking and copies pages exactly, so row ids stay
    /// the same, which the merge in ACP depends on: it stamps those ids into
    /// each plan's link, and the writer finds TS rows by them.
    ///
    /// (TargetSchedulerDb.BackupTo, the pre-push backup, still uses a raw
    /// stream copy with the same WAL exposure. That is a follow-up.)
    ///
    /// Dispose deletes the copy. The caller holds it in a using block, so it
    /// goes whether the upload worked or not.
    public sealed class TsUploadCopy : IDisposable {

        public string Path { get; }

        public string Sha256 { get; }

        public int UserVersion { get; }

        public long SizeBytes { get; }

        private TsUploadCopy(string path, string sha256, int userVersion, long sizeBytes) {
            Path = path;
            Sha256 = sha256;
            UserVersion = userVersion;
            SizeBytes = sizeBytes;
        }

        /// Copy the database at `dbPath` (null means the usual install path, or
        /// ACP_TS_DB_PATH) into `tempDir` (null means %TEMP%).
        ///
        /// Throws TsSchemaVersionException when the source is outside the
        /// supported versions, before anything is copied or sent, and
        /// FileNotFoundException when there is no database.
        public static TsUploadCopy Make(string dbPath = null, string tempDir = null) {
            var dir = string.IsNullOrWhiteSpace(tempDir) ? System.IO.Path.GetTempPath() : tempDir;
            var dest = System.IO.Path.Combine(dir, $"acp-ts-upload-{Guid.NewGuid():N}.sqlite");

            try {
                // Read-only open through the same gate the push uses: the
                // 10 second busy timeout and the user_version check.
                using (var source = TargetSchedulerDb.Open(dbPath, readOnly: true))
                using (var target = new SqliteConnection(new SqliteConnectionStringBuilder {
                    DataSource = dest,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                }.ToString())) {
                    target.Open();
                    source.Connection.BackupDatabase(target);

                    // The backup copies page 1 as it is, so a source in WAL
                    // mode gives a copy whose header says WAL. Put the copy
                    // back in rollback mode, so it is one self-contained file
                    // that ACP can open read-only with nothing beside it.
                    using (var cmd = target.CreateCommand()) {
                        cmd.CommandText = "PRAGMA journal_mode = DELETE";
                        cmd.ExecuteScalar();
                    }
                }

                int version;
                using (var check = new SqliteConnection(new SqliteConnectionStringBuilder {
                    DataSource = dest,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString())) {
                    check.Open();
                    version = TargetSchedulerDb.ReadUserVersion(check);
                }
                // The source passed the check a moment ago, so this only fails
                // if the copy is not what was copied.
                if (!TsSchema.IsSupported(version)) throw new TsSchemaVersionException(version);

                return new TsUploadCopy(dest, HashFile(dest), version, new FileInfo(dest).Length);
            } catch {
                DeleteQuietly(dest);
                throw;
            }
        }

        public static string HashFile(string path) {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                var bytes = sha.ComputeHash(stream);
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        public void Dispose() {
            DeleteQuietly(Path);
        }

        private static void DeleteQuietly(string path) {
            foreach (var p in new[] { path, path + "-wal", path + "-shm", path + "-journal" }) {
                try {
                    if (File.Exists(p)) File.Delete(p);
                } catch (IOException) {
                    // A temp file that will not delete is not worth failing
                    // the upload over. %TEMP% is cleaned eventually.
                } catch (UnauthorizedAccessException) {
                }
            }
        }
    }
}
