using ACP.NINA.Plugin.Services.TargetScheduler;
using Microsoft.Data.Sqlite;
using System.Collections.Generic;

namespace ACP.NINA.Plugin.Tests {

    /// The two database states a second push has to get right, as fixed
    /// recipes both this suite and the Python extension's
    /// tests/dump_golden_rows.py run step for step. They have to stay in step:
    /// the golden comparison is only worth anything if both sides start from
    /// the same database.
    public static class TsScenarios {

        /// The createdate written by hand before the second push, standing in
        /// for a project created long before ACP ever pushed to it. Matches
        /// OLDER_CREATEDATE in tests/dump_golden_rows.py.
        public const long OlderCreateDate = 1600000000L;

        /// The camera every template in TsTestPlans.ThreePlans belongs to. The
        /// old identity recipe needs it and the row does not carry it.
        public const string CameraId = "cam-1";

        /// What a night of imaging and a grader leave behind: frames taken,
        /// frames kept, and one of them with every frame rejected.
        public static void ANightOfImaging(SqliteConnection conn) {
            Exec(conn, "UPDATE exposureplan SET acquired = 137, accepted = 120 WHERE Id = 1");
            Exec(conn, "UPDATE exposureplan SET acquired = 40, accepted = 0 WHERE Id = 2");
            Exec(conn, $"UPDATE project SET createdate = {OlderCreateDate} WHERE Id = 1");
        }

        /// Restamp every ACP row the way the pre-length-prefix code would have.
        ///
        /// The old recipe is written out here rather than called through
        /// TsGuid.Legacy*, so the fixture states the shape it is standing in
        /// for instead of trusting the code under test to describe it.
        public static void WindBackToTheOldRecipe(SqliteConnection conn, string profileId) {
            foreach (var row in Read(conn, "SELECT Id, filtername FROM exposuretemplate")) {
                Exec(conn,
                    "UPDATE exposuretemplate SET guid = '" +
                    TsGuid.Stable($"{profileId}/template/{row[1]}/{CameraId}") +
                    $"' WHERE Id = {row[0]}");
            }
            foreach (var row in Read(conn, "SELECT Id, name FROM project")) {
                Exec(conn,
                    "UPDATE project SET guid = '" +
                    TsGuid.Stable($"{profileId}/project/{row[1]}") +
                    $"' WHERE Id = {row[0]}");
            }

            var oldTargetGuidById = new Dictionary<string, string>();
            foreach (var row in Read(conn,
                "SELECT t.Id, t.name, p.name FROM target t " +
                "JOIN project p ON p.Id = t.projectid ORDER BY t.Id")) {
                var guid = TsGuid.Stable($"{profileId}/target/{row[2]}/{row[1]}");
                oldTargetGuidById[row[0]] = guid;
                Exec(conn, $"UPDATE target SET guid = '{guid}' WHERE Id = {row[0]}");
            }

            foreach (var row in Read(conn,
                "SELECT e.Id, e.targetid, x.filtername FROM exposureplan e " +
                "JOIN exposuretemplate x ON x.Id = e.exposureTemplateId ORDER BY e.Id")) {
                var parent = oldTargetGuidById[row[1]];
                Exec(conn,
                    "UPDATE exposureplan SET guid = '" +
                    TsGuid.Stable($"{profileId}/plan/{parent}/{row[2]}") +
                    $"' WHERE Id = {row[0]}");
            }
        }

        private static void Exec(SqliteConnection conn, string sql) {
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        /// Every column as a string, read up front so the rewrites below can
        /// run on the same connection.
        private static List<string[]> Read(SqliteConnection conn, string sql) {
            var rows = new List<string[]>();
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = sql;
                using (var reader = cmd.ExecuteReader()) {
                    while (reader.Read()) {
                        var values = new string[reader.FieldCount];
                        for (var i = 0; i < reader.FieldCount; i++) {
                            values[i] = reader.IsDBNull(i) ? string.Empty
                                : reader.GetValue(i).ToString();
                        }
                        rows.Add(values);
                    }
                }
            }
            return rows;
        }
    }
}
