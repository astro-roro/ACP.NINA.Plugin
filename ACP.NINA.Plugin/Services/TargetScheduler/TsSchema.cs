using System;
using System.Collections.Generic;
using System.Linq;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// Which Target Scheduler schema versions the push understands, and which
    /// columns exist at which version.
    ///
    /// Ported from nina_ts_sync/db.py and the COLUMN_MIN_VERSION table in
    /// nina_ts_sync/schema.py. Target Scheduler builds its database from
    /// initial_schema.sql and then applies every Migrate/N.sql above the
    /// current user_version, so a database at N is the initial schema plus
    /// scripts 1 to N. Every step from 23 to 28 is an additive ALTER TABLE ADD
    /// COLUMN and only one of them touches a table this code writes. See
    /// docs/schema-history.md in the acp-nina-ts-sync repo for the citations.
    public static class TsSchema {

        /// The versions this plugin is known to be compatible with. Bump only
        /// after testing against a newer Target Scheduler schema, and keep it
        /// in step with SUPPORTED_USER_VERSIONS in the Python extension.
        public static readonly int[] SupportedUserVersions = { 23, 24, 25, 26, 27, 28 };

        /// (table, column) to the first user_version that has the column.
        /// Anything not listed has existed since 23, the floor of the range.
        private static readonly Dictionary<string, int> columnMinVersion =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
                // Migrate/24.sql, INTEGER DEFAULT -1.
                { "target.priority", 24 },
            };

        public static bool IsSupported(int userVersion) {
            return Array.IndexOf(SupportedUserVersions, userVersion) >= 0;
        }

        public static bool ColumnExistsAt(string table, string column, int userVersion) {
            int min;
            if (!columnMinVersion.TryGetValue($"{table}.{column}", out min)) return true;
            return userVersion >= min;
        }

        /// Narrow `columns` to the ones the schema at `userVersion` has, order
        /// preserved so callers can zip the result against their values.
        public static List<string> ColumnsForVersion(
            string table, IEnumerable<string> columns, int userVersion
        ) {
            return columns.Where(c => ColumnExistsAt(table, c, userVersion)).ToList();
        }

        /// Columns written when a row is created and never again. Matches
        /// INSERT_ONLY_COLUMNS in nina_ts_sync/schema.py.
        ///
        /// exposureplan.acquired is how many subs the camera actually took and
        /// accepted is the grader's verdict on them. Neither is ACP's to state.
        /// ACP recomputes both from its own ActualHours, which at best
        /// round-trips the acquired count and at worst marks every frame the
        /// grader rejected as good. On a project with the grader on, Target
        /// Scheduler reads completeness from accepted, so overwriting it makes
        /// Target Scheduler believe a target is finished when it is not.
        ///
        /// project.createdate is the date the project was created. Rewriting it
        /// on every push makes it the date of the last push instead.
        ///
        /// Everything else the push writes is still overwritten on update. That
        /// is a wider question about settings columns, deliberately left open.
        private static readonly Dictionary<string, HashSet<string>> insertOnlyColumns =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase) {
                { "exposureplan", new HashSet<string>(
                    new[] { "acquired", "accepted" }, StringComparer.OrdinalIgnoreCase) },
                { "project", new HashSet<string>(
                    new[] { "createdate" }, StringComparer.OrdinalIgnoreCase) },
            };

        /// Drop the insert-only columns from `columns`, order preserved.
        public static List<string> ColumnsForUpdate(string table, IEnumerable<string> columns) {
            HashSet<string> skip;
            if (!insertOnlyColumns.TryGetValue(table, out skip)) return columns.ToList();
            return columns.Where(c => !skip.Contains(c)).ToList();
        }

        /// The message shape the Python extension raises, word for word, so a
        /// user who has seen one tool refuse recognises the other.
        public static string UnsupportedMessage(int found) {
            var list = string.Join(", ", SupportedUserVersions);
            return $"Target Scheduler DB is at PRAGMA user_version={found}; " +
                   $"this extension supports [{list}]. " +
                   "Refusing to write, see the extension README for the compat matrix.";
        }
    }

    /// Raised when the live database's user_version falls outside the
    /// allowlist. Its own type because it is the one failure the user can act
    /// on: update the plugin, or do not update Target Scheduler yet.
    public class TsSchemaVersionException : Exception {

        public int Found { get; }

        public int[] Supported { get; }

        public TsSchemaVersionException(int found)
            : base(TsSchema.UnsupportedMessage(found)) {
            Found = found;
            Supported = TsSchema.SupportedUserVersions;
        }
    }

    /// Raised when the payload cannot be written back to safely, and the
    /// message says what the user has to change. The Python extension's
    /// PayloadError, with the same two causes: a target with no name at all,
    /// and two entities that would land on one identity. Both refuse the whole
    /// push rather than write something the next push cannot find again.
    public class TsPushValidationException : Exception {

        public TsPushValidationException(string message) : base(message) { }
    }
}
