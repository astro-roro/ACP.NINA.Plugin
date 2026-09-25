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
        /// project.flatsHandling is whether Target Scheduler takes flats for a
        /// project. ACP has no setting for it and TsConvert only ever wrote the
        /// constructor default, which was 0 (flats off), so every sync reset a
        /// project back to flats off even after Rohan turned them on in Target
        /// Scheduler. The constructor default is now 1 (flats on), matching
        /// what Rohan's own projects use, and a new project still gets that
        /// default on insert.
        ///
        /// The rest of the project row, per docs/specs/ts-project-settings.md
        /// section 4: custom horizon and its offset, filter switch frequency,
        /// dither, smart exposure order, the grader, maximum altitude, flats
        /// handling (above) and the description are all rig tuning Rohan does
        /// in Target Scheduler's own screens. ACP has no settings for any of
        /// them, so writing them on update reset that tuning on every push,
        /// which is the bug this list fixes. A new project still gets Target
        /// Scheduler's own defaults on insert.
        ///
        /// exposuretemplate's moon, twilight, humidity and dither columns are
        /// tuning Rohan does in Target Scheduler's own screens. ACP has no
        /// settings for them and TsConvert only ever writes the constructor
        /// defaults, so leaving them updatable meant every sync reset his
        /// tuning to those defaults. The rule: ACP owns the columns it has
        /// settings for (profileId, name, filtername, guid, defaultexposure,
        /// gain, offset, bin, readoutmode) and those still update on every
        /// sync. Target Scheduler owns the rest, and a new template still gets
        /// the defaults on insert.
        private static readonly Dictionary<string, HashSet<string>> insertOnlyColumns =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase) {
                { "exposureplan", new HashSet<string>(
                    new[] { "acquired", "accepted" }, StringComparer.OrdinalIgnoreCase) },
                { "project", new HashSet<string>(
                    new[] {
                        "createdate", "flatsHandling", "usecustomhorizon", "horizonoffset",
                        "filterswitchfrequency", "ditherevery", "smartexposureorder",
                        "enablegrader", "maximumAltitude", "description",
                    }, StringComparer.OrdinalIgnoreCase) },
                { "exposuretemplate", new HashSet<string>(
                    new[] {
                        "twilightlevel", "minutesOffset", "maximumhumidity",
                        "moonavoidanceenabled", "moonavoidanceseparation",
                        "moonavoidancewidth", "moonrelaxscale", "moonrelaxmaxaltitude",
                        "moonrelaxminaltitude", "moondownenabled", "ditherevery",
                    }, StringComparer.OrdinalIgnoreCase) },
            };

        /// The project columns ACP owns and syncs both ways: state, priority
        /// and minimum time, plus the two dates that follow a written state.
        /// Per docs/specs/ts-project-settings.md section 4, these are written
        /// on update only when ACP changed the value since the last sync on
        /// this rig, never unconditionally like the ACP-always-owns columns
        /// and never held back like the TS-owns columns above. TsConvert works
        /// out which of these a given push should write and records that on
        /// TsProject.ConditionalColumnsToWrite; TsUpsert reads it here.
        public static readonly HashSet<string> ConditionalProjectColumns =
            new HashSet<string>(
                new[] { "state", "priority", "minimumtime", "activedate", "inactivedate" },
                StringComparer.OrdinalIgnoreCase);

        /// Drop the insert-only columns from `columns`, order preserved.
        public static List<string> ColumnsForUpdate(string table, IEnumerable<string> columns) {
            HashSet<string> skip;
            if (!insertOnlyColumns.TryGetValue(table, out skip)) return columns.ToList();
            return columns.Where(c => !skip.Contains(c)).ToList();
        }

        /// Narrow `columns` further for an update to `entity`: a conditional
        /// project column (see ConditionalProjectColumns) is dropped unless
        /// the entity says this push should write it. Every other column
        /// passes through unchanged, so this is safe to call for any table.
        public static List<string> ColumnsForConditionalUpdate(string table, IEnumerable<string> columns, TsEntity entity) {
            var proj = entity as TsProject;
            if (proj == null) return columns.ToList();
            return columns.Where(c => !ConditionalProjectColumns.Contains(c) || proj.ConditionalColumnsToWrite.Contains(c)).ToList();
        }

        /// The same three column sets as an ordinary project update
        /// (ColumnsForUpdate plus ColumnsForConditionalUpdate), minus guid.
        /// A row found through ts_refs keeps the identity Target Scheduler
        /// gave it (see the note on pinnedUpdateColumns below), which the
        /// guid- and claim-found paths do not need to guard because writing
        /// their own guid back is either a no-op (guid match) or the
        /// intended stamp (a claim). Only the pinned path needs this.
        public static List<string> ColumnsForPinnedProjectUpdate(IEnumerable<string> updateColumns) {
            return updateColumns.Where(c => !string.Equals(c, "guid", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// The only columns written to a row found through a plan's ts_refs.
        ///
        /// A row found that way was usually made by hand in Target Scheduler
        /// and imported into ACP. ACP holds a value for every other column only
        /// because the entity classes carry Target Scheduler's defaults, so
        /// writing them would switch the grader off, clear the description,
        /// reset a target's epoch and reactivate a project the user parked.
        /// These are the fields ACP actually edits. The guid is never written
        /// either: the row keeps the identity Target Scheduler gave it.
        ///
        /// Templates are absent on purpose. A template found through ts_refs is
        /// shared with whatever else uses it, so it is pointed at, never
        /// rewritten.
        ///
        /// project has no entry here any more: per
        /// docs/specs/ts-project-settings.md section 4, a row found through
        /// ts_refs now follows the same three column sets as a row found by
        /// guid or claimed by name (see ColumnsForUpdate and
        /// ColumnsForConditionalUpdate). TsUpsert special-cases the project
        /// table to use that shared set instead of this dictionary.
        private static readonly Dictionary<string, HashSet<string>> pinnedUpdateColumns =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase) {
                { "target", new HashSet<string>(
                    new[] { "name", "ra", "dec", "rotation" }, StringComparer.OrdinalIgnoreCase) },
                { "exposureplan", new HashSet<string>(
                    new[] { "exposure", "desired" }, StringComparer.OrdinalIgnoreCase) },
            };

        /// Narrow `columns` to the ones a ts_refs update may write, order
        /// preserved. Empty for a table with no entry.
        public static List<string> ColumnsForPinnedUpdate(string table, IEnumerable<string> columns) {
            HashSet<string> keep;
            if (!pinnedUpdateColumns.TryGetValue(table, out keep)) return new List<string>();
            return columns.Where(keep.Contains).ToList();
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
