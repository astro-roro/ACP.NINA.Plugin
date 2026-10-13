using ACP.NINA.Plugin.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// The gear template writer: for a camera whose filters carry gear
    /// templates, settle each template's Target Scheduler row before any
    /// exposure plan is written (docs/specs/gear-templates.md section 8).
    ///
    /// Each field is decided by three values: ACP's A (from the gear
    /// template; for the filtername, the wheel slot), the base B (what TS held
    /// at the last push, pull or adoption) and TS's value now C. A change made
    /// only in ACP is written, a change made only in TS is left for the pull,
    /// and a change made in both is held and reported, never overwritten.
    /// A row is found by TS id first, so a rename updates the same row and
    /// never inserts a second one.
    public static partial class TsUpsert {

        private static readonly string[] GearFields = {
            "name", "filtername", "defaultexposure", "gain", "offset", "bin",
        };

        /// A template row as it stands in TS during the gear pass.
        private class GearTsRow {
            public int Id;
            public string Name;
            public string FilterName;
            public string Guid;
            public double Exposure;
            public int Gain;
            public int Offset;
            public int Bin;
        }

        private static List<GearTsRow> ReadGearRows(SqliteConnection conn, string profileId) {
            var rows = new List<GearTsRow>();
            using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = "SELECT * FROM exposuretemplate WHERE profileId = $p ORDER BY Id";
                cmd.Parameters.AddWithValue("$p", profileId);
                using (var r = cmd.ExecuteReader()) {
                    while (r.Read()) {
                        rows.Add(new GearTsRow {
                            Id = TargetSchedulerDb.ReadInt(r, "Id"),
                            Name = TargetSchedulerDb.ReadString(r, "name"),
                            FilterName = TargetSchedulerDb.ReadString(r, "filtername"),
                            Guid = TargetSchedulerDb.ReadString(r, "guid"),
                            Exposure = TargetSchedulerDb.ReadDouble(r, "defaultexposure", 60.0),
                            Gain = TargetSchedulerDb.ReadInt(r, "gain", -1),
                            Offset = TargetSchedulerDb.ReadInt(r, "offset", -1),
                            Bin = TargetSchedulerDb.ReadInt(r, "bin", 1),
                        });
                    }
                }
            }
            return rows;
        }

        private static object GearValueOfRow(GearTsRow row, string field) {
            switch (field) {
                case "name": return row.Name;
                case "filtername": return row.FilterName;
                case "defaultexposure": return row.Exposure;
                case "gain": return row.Gain;
                case "offset": return row.Offset;
                default: return row.Bin;
            }
        }

        private static object GearValueOfGear(TsGearWork work, string field) {
            var t = work.Template;
            switch (field) {
                case "name": return t.Name;
                case "filtername": return work.Slot;
                case "defaultexposure": return t.SubS;
                case "gain": return t.Gain;
                case "offset": return t.Offset;
                default: return t.Bin;
            }
        }

        private static object GearValueOfBase(GearTemplateBase b, string field) {
            if (b == null) return null;
            switch (field) {
                case "name": return b.Name;
                case "filtername": return b.FilterName;
                case "defaultexposure": return b.DefaultExposure;
                case "gain": return b.Gain;
                case "offset": return b.Offset;
                default: return b.Bin;
            }
        }

        private static bool GearSame(object x, object y) {
            if (x == null || y == null) return x == null && y == null;
            var a = x is string ? null : (double?)Convert.ToDouble(x, CultureInfo.InvariantCulture);
            var b = y is string ? null : (double?)Convert.ToDouble(y, CultureInfo.InvariantCulture);
            if (a.HasValue && b.HasValue) return Math.Abs(a.Value - b.Value) < 1e-9;
            return string.Equals(x.ToString(), y.ToString(), StringComparison.Ordinal);
        }

        private static string GearShow(object value) {
            if (value is double) return ((double)value).ToString("0.#####", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string GearFieldLabel(string field) {
            return field == "defaultexposure" ? "sub" : field;
        }

        private static void SetGearBase(GearTemplateBase b, string field, object value) {
            switch (field) {
                case "name": b.Name = (string)value; break;
                case "filtername": b.FilterName = (string)value; break;
                case "defaultexposure": b.DefaultExposure = Convert.ToDouble(value, CultureInfo.InvariantCulture); break;
                case "gain": b.Gain = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
                case "offset": b.Offset = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
                default: b.Bin = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            }
        }

        private static bool InGearGroup(GearTsRow row, string filter, double sub) {
            return TsRows.SameFilter(row.FilterName, filter) && ((row.Exposure < TsRows.FrameUnitBelowS) == (sub < TsRows.FrameUnitBelowS));
        }

        /// Settle every gear template's TS row. `idByGuid` gets the TS id for
        /// each template whose plans may be written, and `skipped` the gear
        /// guids whose rows must wait (not on the wheel, wheel unknown with no
        /// row, a name that waits for adoption).
        private static void ApplyGearTemplates(
            SqliteConnection conn, int userVersion,
            IReadOnlyDictionary<string, HashSet<string>> columnsByTable,
            TsSyncPayload payload, TsSyncOutcome outcome,
            Dictionary<string, int> idByGuid, HashSet<string> skipped,
            Dictionary<string, int> unknownWheelRows, List<string> unknownWheelOrder
        ) {
            var profile = payload.ProfileId;
            var profileLabel = string.IsNullOrWhiteSpace(payload.ProfileName) ? "this profile" : payload.ProfileName;
            var rows = ReadGearRows(conn, profile);
            var templateByGuid = payload.Templates.Where(t => t.Gear != null).ToDictionary(t => t.Guid, t => t);
            var claimedBy = new Dictionary<int, TsGearWork>();
            var foundRow = new Dictionary<TsGearWork, GearTsRow>();
            var viaLink = new HashSet<TsGearWork>();
            var viaAlias = new HashSet<TsGearWork>();
            var pending = new Dictionary<TsGearWork, string>();

            // Pass 1: the rows ACP already links to, by TS id and then guid.
            foreach (var work in payload.GearWorks) {
                var link = work.Template.LinkFor(profile);
                if (link == null) continue;
                GearTsRow row = null;
                if (link.Id.HasValue) row = rows.FirstOrDefault(r => r.Id == link.Id.Value);
                if (row == null && !string.IsNullOrEmpty(link.Guid)) {
                    row = rows.FirstOrDefault(r => string.Equals(r.Guid, link.Guid, StringComparison.Ordinal));
                }
                if (row == null || claimedBy.ContainsKey(row.Id)) continue;
                claimedBy[row.Id] = work;
                foundRow[work] = row;
                viaLink.Add(work);
            }

            // Pass 2: by name or an earlier name, for a template not linked.
            var insertedNames = new Dictionary<string, TsGearWork>(StringComparer.Ordinal);
            foreach (var work in payload.GearWorks) {
                if (work.OffWheel || foundRow.ContainsKey(work)) continue;
                var t = work.Template;
                var names = new List<string> { t.Name };
                if (t.Aliases != null) names.AddRange(t.Aliases.Where(a => !string.IsNullOrEmpty(a)));
                var candidates = rows
                    .Where(r => names.Contains(r.Name, StringComparer.Ordinal))
                    .ToList();
                if (candidates.Count > 1) {
                    var ids = string.Join(", ", candidates.Select(r => r.Id));
                    var line = $"Template '{t.Name}' matches {candidates.Count} templates in {profileLabel} (ids {ids}), left for adoption.";
                    outcome.SyncNotes.Add(line);
                    pending[work] = line;
                    continue;
                }
                if (candidates.Count == 1) {
                    var row = candidates[0];
                    var byName = string.Equals(row.Name, t.Name, StringComparison.Ordinal);
                    TsGearWork other;
                    if (claimedBy.TryGetValue(row.Id, out other)) {
                        var line = ClashNote(work, other, profileLabel);
                        outcome.SyncNotes.Add(line);
                        pending[work] = line;
                        continue;
                    }
                    if (InGearGroup(row, work.Filter, t.SubS)) {
                        claimedBy[row.Id] = work;
                        foundRow[work] = row;
                        if (!byName) viaAlias.Add(work);
                        continue;
                    }
                    if (byName) {
                        var line = $"Template '{t.Name}' exists in {profileLabel} (id {row.Id}) for another filter or sub class, so it was not touched or created.";
                        outcome.SyncNotes.Add(line);
                        pending[work] = line;
                        continue;
                    }
                    // An alias on a row of another group claims nothing.
                }
                // Nothing to find: an insert, unless another camera's template
                // of the same name is already taking that name in this push.
                TsGearWork holder;
                if (insertedNames.TryGetValue(t.Name, out holder) && holder.CameraId != work.CameraId) {
                    var line = ClashNote(work, holder, profileLabel);
                    outcome.SyncNotes.Add(line);
                    pending[work] = line;
                    continue;
                }
                insertedNames[t.Name] = work;
            }

            // Pass 3: decide and write, in gear order.
            foreach (var work in payload.GearWorks) {
                var t = work.Template;
                var link = work.Template.LinkFor(profile);
                var result = new TsTemplateResult { TemplateId = t.Id };
                outcome.GearResults.Add(result);

                if (work.OffWheel) {
                    result.Action = "skipped";
                    result.Note = $"{work.Filter} is not on the wheel of {profileLabel} yet";
                    result.TsId = link?.Id;
                    result.Guid = link?.Guid;
                    result.Base = link?.Base;
                    skipped.Add(work.Guid);
                    continue;
                }
                string pendingNote;
                if (pending.TryGetValue(work, out pendingNote)) {
                    result.Action = "skipped";
                    result.Note = pendingNote;
                    result.TsId = link?.Id;
                    result.Guid = link?.Guid;
                    result.Base = link?.Base;
                    skipped.Add(work.Guid);
                    continue;
                }

                GearTsRow row;
                if (!foundRow.TryGetValue(work, out row)) {
                    if (string.IsNullOrEmpty(work.Slot)) {
                        // The column is not null and a guess would be a wrong
                        // filtername, so nothing is inserted.
                        result.Action = "skipped";
                        result.Note = $"the wheel of {profileLabel} is unknown";
                        result.TsId = link?.Id;
                        result.Guid = link?.Guid;
                        result.Base = link?.Base;
                        skipped.Add(work.Guid);
                        CountUnknownWheel(unknownWheelRows, unknownWheelOrder, work.CameraName, work.RowLabels.Count);
                        continue;
                    }
                    var tpl = templateByGuid[work.Guid];
                    var newId = Insert(conn, "exposuretemplate", tpl,
                        WritableColumns("exposuretemplate", tpl.Columns, userVersion, columnsByTable),
                        outcome.ExposureTemplate);
                    var inserted = new GearTsRow {
                        Id = newId, Name = tpl.Name, FilterName = tpl.FilterName, Guid = tpl.Guid,
                        Exposure = tpl.DefaultExposure, Gain = tpl.Gain, Offset = tpl.Offset, Bin = tpl.Bin,
                    };
                    rows.Add(inserted);
                    claimedBy[newId] = work;
                    idByGuid[work.Guid] = newId;
                    result.Action = "inserted";
                    result.TsId = newId;
                    result.Guid = inserted.Guid;
                    result.Base = new GearTemplateBase {
                        Name = inserted.Name, FilterName = inserted.FilterName,
                        DefaultExposure = inserted.Exposure, Gain = inserted.Gain,
                        Offset = inserted.Offset, Bin = inserted.Bin,
                    };
                    result.FieldsWritten = new List<string>(GearFields);
                    continue;
                }

                DecideGearFields(
                    conn, userVersion, columnsByTable, payload, outcome, work, row, link,
                    viaLink.Contains(work), rows, profileLabel, result);
                idByGuid[work.Guid] = row.Id;
            }

            // Templates of the profile no gear template stands for.
            foreach (var row in rows) {
                if (claimedBy.ContainsKey(row.Id)) continue;
                outcome.StrayTemplates.Add(row.Name ?? string.Empty);
                outcome.StrayTemplateIds.Add(row.Id);
            }
        }

        private static string ClashNote(TsGearWork work, TsGearWork holder, string profileLabel) {
            return $"Template '{work.Template.Name}' of {work.CameraName} was not created in profile {profileLabel}, " +
                   $"because {holder.CameraName}'s '{holder.Template.Name}' already uses that name.";
        }

        private static void CountUnknownWheel(
            Dictionary<string, int> counts, List<string> order, string camera, int rows
        ) {
            var key = camera ?? "The camera";
            int n;
            if (!counts.TryGetValue(key, out n)) order.Add(key);
            counts[key] = n + rows;
        }

        /// The three way comparison for an existing row.
        private static void DecideGearFields(
            SqliteConnection conn, int userVersion, IReadOnlyDictionary<string, HashSet<string>> columnsByTable,
            TsSyncPayload payload, TsSyncOutcome outcome, TsGearWork work, GearTsRow row,
            GearTemplateLink link, bool linked, List<GearTsRow> allRows, string profileLabel, TsTemplateResult result
        ) {
            var t = work.Template;
            var newBase = new GearTemplateBase();
            var written = new List<string>();
            var conflicts = new List<TsTemplateConflict>();
            var writes = new Dictionary<string, object>();
            var existingBase = linked ? link?.Base : null;
            var adopted = !linked || existingBase == null;

            foreach (var field in GearFields) {
                var a = GearValueOfGear(work, field);
                var c = GearValueOfRow(row, field);
                var b = GearValueOfBase(existingBase, field);
                if (b == null) b = c;

                if (a == null) {
                    // A filtername with no known slot: left alone, not compared.
                    SetGearBase(newBase, field, b);
                    continue;
                }
                if (adopted && field != "name") {
                    // Found by name and never linked: take TS's values, write nothing.
                    SetGearBase(newBase, field, c);
                    continue;
                }
                if (GearSame(a, b)) {
                    SetGearBase(newBase, field, b);
                    continue;
                }
                if (GearSame(c, b)) {
                    if (field == "name") {
                        var clash = allRows.FirstOrDefault(r => r.Id != row.Id && string.Equals(r.Name, (string)a, StringComparison.Ordinal));
                        if (clash != null) {
                            outcome.SyncNotes.Add(
                                $"{t.Name}: the name was skipped, because {profileLabel} already has a template named '{t.Name}' (id {clash.Id}).");
                            SetGearBase(newBase, field, b);
                            continue;
                        }
                    }
                    writes[field] = a;
                    SetGearBase(newBase, field, a);
                    continue;
                }
                if (GearSame(c, a)) {
                    SetGearBase(newBase, field, c);
                    continue;
                }
                conflicts.Add(new TsTemplateConflict { Field = field, Acp = a, Ts = c });
                outcome.SyncNotes.Add(
                    $"{t.Name}: {GearFieldLabel(field)} is {GearShow(a)} in ACP and {GearShow(c)} in TS. " +
                    "Left alone until you choose in the gear editor.");
                SetGearBase(newBase, field, b);
            }

            if (writes.Count > 0) {
                var writable = WritableColumns("exposuretemplate", writes.Keys, userVersion, columnsByTable);
                if (writable.Count > 0) {
                    var setClause = string.Join(", ", writable.Select(col => $"{Quote(col)} = ${col}"));
                    using (var cmd = conn.CreateCommand()) {
                        cmd.CommandText = $"UPDATE exposuretemplate SET {setClause} WHERE Id = $__id";
                        foreach (var col in writable) cmd.Parameters.AddWithValue("$" + col, writes[col] ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("$__id", row.Id);
                        cmd.ExecuteNonQuery();
                    }
                    foreach (var col in writable) {
                        written.Add(col);
                        switch (col) {
                            case "name": row.Name = (string)writes[col]; break;
                            case "filtername": row.FilterName = (string)writes[col]; break;
                            case "defaultexposure": row.Exposure = Convert.ToDouble(writes[col], CultureInfo.InvariantCulture); break;
                            case "gain": row.Gain = Convert.ToInt32(writes[col], CultureInfo.InvariantCulture); break;
                            case "offset": row.Offset = Convert.ToInt32(writes[col], CultureInfo.InvariantCulture); break;
                            default: row.Bin = Convert.ToInt32(writes[col], CultureInfo.InvariantCulture); break;
                        }
                    }
                    outcome.ExposureTemplate.Updated++;
                }
            }

            if (adopted && written.Count == 0 && string.Equals(row.Name, t.Name, StringComparison.Ordinal)) {
                outcome.SyncNotes.Add($"{t.Name} already exists in profile {profileLabel}, so ACP took its values.");
            }

            result.TsId = row.Id;
            result.Guid = row.Guid;
            result.Base = newBase;
            result.FieldsWritten = written;
            if (conflicts.Count > 0) result.Conflicts = conflicts;
            if (written.Count > 0) result.Action = "updated";
            else if (conflicts.Count > 0) result.Action = "conflict";
            else if (adopted) result.Action = "adopted";
            else result.Action = "unchanged";
        }
    }
}
