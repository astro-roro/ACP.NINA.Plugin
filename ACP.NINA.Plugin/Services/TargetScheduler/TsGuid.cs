using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// Deterministic GUID stamping, ported from the Python extension's
    /// nina_ts_sync/schema.py.
    ///
    /// Target Scheduler's tables have no unique index on `guid`; uniqueness is
    /// convention only. Stamping our own UUIDv5 over a stable name means the
    /// same logical entity always hashes to the same value, so a re-sync can
    /// find the row it wrote last time with SELECT Id WHERE guid = ? instead of
    /// duplicating it.
    ///
    /// Every free-text part of a name is length-prefixed as
    /// "&lt;utf-8 byte length&gt;:&lt;value&gt;". Without that the parts were joined
    /// with a slash and could run into each other: "M42" plus "M43/NGC1977"
    /// hashed to the same UUID as "M42/M43" plus "NGC1977", and slashes in
    /// catalogue names are ordinary, so the collision was reachable with real
    /// input. Byte length rather than Length, because Python counts UTF-8
    /// bytes and .NET would otherwise count UTF-16 units and disagree.
    ///
    /// The namespace UUID and the four name recipes must stay byte-identical to
    /// the Python extension, because the two tools write to the same database
    /// and have to agree on what they wrote. Never change ACP_NS.
    ///
    /// The Legacy* methods are the recipe as it stood before the length prefix.
    /// TsMigration is their only caller: it uses them to recognise a row this
    /// plugin stamped under the old recipe so it can restamp it. Nothing else
    /// should identify a row with them.
    public static class TsGuid {

        /// The same literal as nina_ts_sync.schema.ACP_NS. Part of the on-disk
        /// identity of every row either tool stamps.
        public static readonly Guid AcpNamespace =
            new Guid("c4b6f1ee-1f9e-5e4b-9a7a-7e1d2c3a4b5c");

        /// RFC 4122 version 5: SHA-1 over the namespace bytes in network order
        /// followed by the UTF-8 name, with the version and variant bits forced.
        ///
        /// .NET has no UUIDv5 of its own, and Guid's own byte order is
        /// little-endian for the first three fields, so the namespace has to be
        /// flipped on the way in and the digest flipped on the way out.
        public static string Stable(string name) {
            var namespaceBytes = AcpNamespace.ToByteArray();
            SwapEndianness(namespaceBytes);

            var nameBytes = Encoding.UTF8.GetBytes(name ?? string.Empty);
            var input = new byte[namespaceBytes.Length + nameBytes.Length];
            Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
            Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);

            byte[] hash;
            using (var sha1 = SHA1.Create()) {
                hash = sha1.ComputeHash(input);
            }

            var guidBytes = new byte[16];
            Buffer.BlockCopy(hash, 0, guidBytes, 0, 16);
            // Version 5 in the high nibble of byte 6, RFC 4122 variant in byte 8.
            guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
            guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

            SwapEndianness(guidBytes);
            return new Guid(guidBytes).ToString();
        }

        /// One free-text component of an identity name, length-prefixed so two
        /// components cannot be confused for one. Matches name_part() in
        /// nina_ts_sync/schema.py, down to treating null as an empty string.
        public static string NamePart(string value) {
            var text = value ?? string.Empty;
            return Encoding.UTF8.GetByteCount(text).ToString(CultureInfo.InvariantCulture)
                   + ":" + text;
        }

        public static string Template(string profileId, string filterName, string cameraId) {
            return Stable(
                $"{NamePart(profileId)}/template/{NamePart(filterName)}/{NamePart(cameraId)}");
        }

        public static string Project(string profileId, string projectName) {
            return Stable($"{NamePart(profileId)}/project/{NamePart(projectName)}");
        }

        public static string Target(string profileId, string projectName, string targetName) {
            return Stable(
                $"{NamePart(profileId)}/target/{NamePart(projectName)}/{NamePart(targetName)}");
        }

        public static string ExposurePlan(string profileId, string targetGuid, string filterName) {
            return Stable(
                $"{NamePart(profileId)}/plan/{NamePart(targetGuid)}/{NamePart(filterName)}");
        }

        // -- The pre-length-prefix recipe, for the migration only -------------

        public static string LegacyTemplate(string profileId, string filterName, string cameraId) {
            return Stable($"{profileId}/template/{filterName}/{cameraId}");
        }

        public static string LegacyProject(string profileId, string projectName) {
            return Stable($"{profileId}/project/{projectName}");
        }

        public static string LegacyTarget(string profileId, string projectName, string targetName) {
            return Stable($"{profileId}/target/{projectName}/{targetName}");
        }

        public static string LegacyExposurePlan(
            string profileId, string targetGuid, string filterName
        ) {
            return Stable($"{profileId}/plan/{targetGuid}/{filterName}");
        }

        /// Guid stores Data1, Data2 and Data3 host-endian; RFC 4122 wants them
        /// big-endian. On a little-endian machine that means reversing the
        /// first four bytes, then the next two, then the next two. The last
        /// eight bytes are already in order.
        private static void SwapEndianness(byte[] bytes) {
            if (!BitConverter.IsLittleEndian) return;
            Array.Reverse(bytes, 0, 4);
            Array.Reverse(bytes, 4, 2);
            Array.Reverse(bytes, 6, 2);
        }
    }
}
