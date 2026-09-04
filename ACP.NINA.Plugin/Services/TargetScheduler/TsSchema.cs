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
}
