using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// Restamp rows that still carry the pre-length-prefix ACP identity.
    /// Ported from nina_ts_sync/migrate.py, statement for statement.
    ///
    /// The identity recipe used to join free-text name parts with a slash, so
    /// "M42" plus "M43/NGC1977" hashed to the same UUID as "M42/M43" plus
    /// "NGC1977". Length-prefixing each part fixes that, and changes every guid
    /// ACP has ever written.
    ///
    /// If the new recipe simply went live, the next push would find none of its
    /// own rows, insert a fresh tree beside them, and leave the old tree active
    /// and still being imaged. So the push migrates as it goes: for each row in
    /// the four tables it writes, recompute the old recipe from that row's
    /// natural key, and where the stored guid matches, rewrite it to the new
    /// recipe. A row whose guid does not match the old recipe was stamped by
    /// Target Scheduler or by somebody else and is left exactly as it is.
    ///
    /// This runs inside the push's own BEGIN IMMEDIATE transaction, after the
    /// backup, so a failure anywhere in the push takes the restamping back with
    /// it. It is idempotent: on a database that has already been migrated
    /// nothing matches the old recipe and nothing is written.
    ///
    /// Three of the four tables carry their whole natural key on the row, or on
    /// the row plus a join to its parent. exposuretemplate does not: its key
    /// includes the camera id, which Target Scheduler has no column for. Those
    /// legacy guids come from the payload instead, via
    /// TsSyncPayload.LegacyTemplateGuidByGuid, which the converter fills in.
    /// The consequence is that a template row for a camera no longer in ACP's
    /// gear is not migrated. It gets picked up the next time that camera is
    /// pushed, and until then it is a row nothing was going to update anyway.
    public static class TsMigration {

        public class Result {
            /// How many rows were restamped. Zero on an already migrated database.
            public int Rewritten { get; set; }

            public List<string> Notes { get; } = new List<string>();
        }

        /// Rewrite every old recipe guid onto the new one. The caller owns the
        /// transaction.
        public static Result MigrateLegacyGuids(SqliteConnection conn, TsSyncPayload payload) {
            var result = new Result();
            MigrateTemplates(conn, payload, result);
            MigrateProjects(conn, result);
            MigrateTargets(conn, result);
            MigrateExposurePlans(conn, result);
            return result;
        }

        private static void MigrateTemplates(
            SqliteConnection conn, TsSyncPayload payload, Result result
        ) {
            foreach (var tpl in payload.Templates) {
                string old;
                if (!payload.LegacyTemplateGuidByGuid.TryGetValue(tpl.Guid, out old)) continue;
                if (string.IsNullOrEmpty(old) || old == tpl.Guid) continue;

                using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText = "SELECT Id FROM exposuretemplate WHERE guid = $g";
                    cmd.Parameters.AddWithValue("$g", old);
                    var value = cmd.ExecuteScalar();
                    if (value == null || value == DBNull.Value) continue;
                    Rewrite(conn, "exposuretemplate",
                        Convert.ToInt32(value, CultureInfo.InvariantCulture), tpl.Guid, result);
                }
            }
        }

        private static void MigrateProjects(SqliteConnection conn, Result result) {
            var rows = Read(conn, "SELECT Id, profileId, name, guid FROM project");
            foreach (var row in rows) {
                var id = Convert.ToInt32(row[0], CultureInfo.InvariantCulture);
                var profileId = Text(row[1]);
                var name = Text(row[2]);
                if (Text(row[3]) != TsGuid.LegacyProject(profileId, name)) continue;
                Rewrite(conn, "project", id, TsGuid.Project(profileId, name), result);
            }
        }

        private static void MigrateTargets(SqliteConnection conn, Result result) {
            var rows = Read(conn,
                "SELECT t.Id, t.name, t.guid, p.profileId, p.name " +
                "FROM target t JOIN project p ON p.Id = t.projectid");
            foreach (var row in rows) {
                var id = Convert.ToInt32(row[0], CultureInfo.InvariantCulture);
                var targetName = Text(row[1]);
                var profileId = Text(row[3]);
                var projectName = Text(row[4]);
                if (Text(row[2]) != TsGuid.LegacyTarget(profileId, projectName, targetName)) {
                    continue;
                }
                Rewrite(conn, "target", id,
                    TsGuid.Target(profileId, projectName, targetName), result);
            }
        }

        /// A plan's identity embeds its target's, so both recipes are
        /// recomputed here. The target guid used comes from the target's
        /// natural key rather than the guid stored on the target row, which
        /// means the order the four tables are migrated in makes no difference.
        private static void MigrateExposurePlans(SqliteConnection conn, Result result) {
            var rows = Read(conn,
                "SELECT e.Id, e.guid, e.profileId, p.profileId, p.name, t.name, x.filtername " +
                "FROM exposureplan e " +
                "JOIN target t ON t.Id = e.targetid " +
                "JOIN project p ON p.Id = t.projectid " +
                "JOIN exposuretemplate x ON x.Id = e.exposureTemplateId");
            foreach (var row in rows) {
                var id = Convert.ToInt32(row[0], CultureInfo.InvariantCulture);
                var planProfile = Text(row[2]);
                var profileId = Text(row[3]);
                var projectName = Text(row[4]);
                var targetName = Text(row[5]);
                var filterName = Text(row[6]);

                var oldTarget = TsGuid.LegacyTarget(profileId, projectName, targetName);
                if (Text(row[1]) != TsGuid.LegacyExposurePlan(planProfile, oldTarget, filterName)) {
                    continue;
                }
                var newTarget = TsGuid.Target(profileId, projectName, targetName);
                Rewrite(conn, "exposureplan", id,
                    TsGuid.ExposurePlan(planProfile, newTarget, filterName), result);
            }
        }

        /// Stamp `newGuid` on one row, unless another row already holds it.
        ///
        /// The clash cannot happen on first contact, because the new recipe has
        /// never been written to this database. It could happen on a database
        /// that was half migrated by a run that died between the commit and the
        /// next push, and a duplicate guid is worse than an unmigrated row, so
        /// the row is left alone and the push says so.
        private static void Rewrite(
            SqliteConnection conn, string table, int rowId, string newGuid, Result result
        ) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = $"SELECT Id FROM {table} WHERE guid = $g AND Id <> $id";
                cmd.Parameters.AddWithValue("$g", newGuid);
                cmd.Parameters.AddWithValue("$id", rowId);
                var clash = cmd.ExecuteScalar();
                if (clash != null && clash != DBNull.Value) {
                    result.Notes.Add(
                        $"{table} row {rowId} still carries the old ACP identity, but row " +
                        $"{Convert.ToInt32(clash, CultureInfo.InvariantCulture)} already holds " +
                        "the new one, so it was left alone");
                    return;
                }
            }
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = $"UPDATE {table} SET guid = $g WHERE Id = $id";
                cmd.Parameters.AddWithValue("$g", newGuid);
                cmd.Parameters.AddWithValue("$id", rowId);
                cmd.ExecuteNonQuery();
            }
            result.Rewritten++;
        }

        /// Read the whole result set up front. The rewrites run on the same
        /// connection, and Microsoft.Data.Sqlite will not have a second command
        /// executing while a reader is open on it.
        private static List<object[]> Read(SqliteConnection conn, string sql) {
            var rows = new List<object[]>();
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                using (var reader = cmd.ExecuteReader()) {
                    while (reader.Read()) {
                        var values = new object[reader.FieldCount];
                        reader.GetValues(values);
                        rows.Add(values);
                    }
                }
            }
            return rows;
        }

        /// A NULL column is an empty name, which is how the recipes read it.
        private static string Text(object value) {
            return value == null || value == DBNull.Value ? string.Empty : value.ToString();
        }
    }
}
