using ACP.NINA.Plugin.Services.TargetScheduler;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The version gate: 23 through 28 open, anything else is refused before a
    /// row is written. Mirrors tests/test_schema_versions.py in the Python
    /// extension, because the two tools have to agree on what they will touch.
    public class TsSchemaVersionTests {

        [Fact]
        public void SupportedRangeIs23Through28() {
            Assert.Equal(new[] { 23, 24, 25, 26, 27, 28 }, TsSchema.SupportedUserVersions);
        }

        [Theory]
        [InlineData(23)]
        [InlineData(24)]
        [InlineData(25)]
        [InlineData(26)]
        [InlineData(27)]
        [InlineData(28)]
        public void SupportedVersionOpensAndReportsItself(int version) {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(version, tmp.File("schedulerdb.sqlite"));
                using (var db = TargetSchedulerDb.Open(path)) {
                    Assert.Equal(version, db.UserVersion);
                }
            }
        }

        [Theory]
        [InlineData(22)]
        [InlineData(29)]
        public void UnsupportedVersionIsRefused(int version) {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(version, tmp.File("schedulerdb.sqlite"));
                var ex = Assert.Throws<TsSchemaVersionException>(() => TargetSchedulerDb.Open(path));

                Assert.Equal(version, ex.Found);
                Assert.Equal(new[] { 23, 24, 25, 26, 27, 28 }, ex.Supported);
                // The same message shape the Python extension raises, so a user
                // who has seen one tool refuse recognises the other.
                Assert.StartsWith(
                    $"Target Scheduler DB is at PRAGMA user_version={version}; " +
                    "this extension supports [23, 24, 25, 26, 27, 28].",
                    ex.Message);
                Assert.Contains("Refusing to write", ex.Message);
            }
        }

        [Fact]
        public void RefusingAnUnsupportedVersionWritesNothing() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(29, tmp.File("schedulerdb.sqlite"));
                Assert.Throws<TsSchemaVersionException>(() => TargetSchedulerDb.Open(path));

                var builder = new SqliteConnectionStringBuilder {
                    DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
                };
                using (var conn = new SqliteConnection(builder.ToString())) {
                    conn.Open();
                    using (var cmd = conn.CreateCommand()) {
                        cmd.CommandText = "SELECT COUNT(*) FROM project";
                        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
                    }
                }
            }
        }

        [Fact]
        public void TargetPriorityIsAbsentAt23AndPresentFrom24() {
            using (var tmp = new TempDir()) {
                var at23 = TsFixtures.TableColumns(
                    TsFixtures.MakeDb(23, tmp.File("v23.sqlite")), "target");
                var at24 = TsFixtures.TableColumns(
                    TsFixtures.MakeDb(24, tmp.File("v24.sqlite")), "target");

                Assert.DoesNotContain("priority", at23);
                Assert.Contains("priority", at24);
            }
        }

        [Fact]
        public void ColumnGateDropsTargetPriorityBelow24Only() {
            var columns = new[] { "name", "guid", "priority" };

            Assert.Equal(new[] { "name", "guid" }, TsSchema.ColumnsForVersion("target", columns, 23));
            Assert.Equal(columns, TsSchema.ColumnsForVersion("target", columns, 24));
            Assert.Equal(columns, TsSchema.ColumnsForVersion("target", columns, 28));

            // The gate is per table. Nothing else declares a minimum, so every
            // other table keeps every column at every supported version.
            Assert.Equal(columns, TsSchema.ColumnsForVersion("project", columns, 23));
        }

        [Fact]
        public void ResolveDbPathRefusesAPathThatIsNotThere() {
            using (var tmp = new TempDir()) {
                var missing = tmp.File("not-here.sqlite");
                var ex = Assert.Throws<FileNotFoundException>(() => TsPaths.ResolveDbPath(missing));
                Assert.Contains("schedulerdb.sqlite not found", ex.Message);
            }
        }

        [Fact]
        public void ResolveDbPathAcceptsAFileThatExists() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                Assert.Equal(path, TsPaths.ResolveDbPath(path));
            }
        }

        [Fact]
        public void BackupSitsBesideTheDatabaseAndMatchesItByteForByte() {
            using (var tmp = new TempDir()) {
                var path = TsFixtures.MakeDb(28, tmp.File("schedulerdb.sqlite"));
                var backup = TargetSchedulerDb.BackupTo(path);

                Assert.True(File.Exists(backup));
                Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(backup));
                Assert.Contains("-acpsync-", Path.GetFileName(backup));
                Assert.EndsWith("-backup.sqlite", backup);
                Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(backup));

                // A second backup in the same second gets its own name rather
                // than overwriting the first.
                var again = TargetSchedulerDb.BackupTo(path);
                Assert.NotEqual(backup, again);
                Assert.True(File.Exists(backup));
                Assert.True(File.Exists(again));
            }
        }
    }

    /// The deterministic stamps, checked against values the Python extension
    /// produced for the same names. If these drift, the two tools stop
    /// recognising each other's rows and every re-sync duplicates.
    public class TsGuidTests {

        [Fact]
        public void NamespaceIsTheOneTheExtensionUses() {
            Assert.Equal("c4b6f1ee-1f9e-5e4b-9a7a-7e1d2c3a4b5c", TsGuid.AcpNamespace.ToString());
        }

        [Theory]
        // Values from Python's uuid.uuid5 over the same namespace and names.
        [InlineData("profile-under-test/project/Mosaic", "0486698e-dc5b-5712-b127-4642c11dc7b2")]
        [InlineData("profile-under-test/target/Mosaic/M31 Panel 1 (R1C1)", "08b18079-6fbf-5167-b3d2-01b362dc407b")]
        [InlineData("x/template/Ha/cam-1", "27892c9c-3c91-5560-9dd0-777f49287095")]
        public void StableMatchesUuidV5(string name, string expected) {
            Assert.Equal(expected, TsGuid.Stable(name));
        }

        [Fact]
        public void TheFourRecipesUseTheDocumentedNames() {
            Assert.Equal(TsGuid.Stable("p/project/Orion"), TsGuid.Project("p", "Orion"));
            Assert.Equal(TsGuid.Stable("p/target/Orion/M42"), TsGuid.Target("p", "Orion", "M42"));
            Assert.Equal(TsGuid.Stable("p/template/Ha/cam"), TsGuid.Template("p", "Ha", "cam"));
            Assert.Equal(TsGuid.Stable("p/plan/tguid/Ha"), TsGuid.ExposurePlan("p", "tguid", "Ha"));
        }

        [Fact]
        public void StampsAreVersion5AndRfc4122() {
            var bytes = Guid.Parse(TsGuid.Project("p", "Orion")).ToByteArray();
            // Guid.ToByteArray is little-endian over the first three fields, so
            // the version nibble lands in byte 7 and the variant stays in 8.
            Assert.Equal(0x50, bytes[7] & 0xF0);
            Assert.Equal(0x80, bytes[8] & 0xC0);
        }

        [Fact]
        public void DifferentNamesDoNotCollide() {
            var stamps = new[] {
                TsGuid.Project("p", "A"),
                TsGuid.Project("p", "B"),
                TsGuid.Project("q", "A"),
                TsGuid.Target("p", "A", "A"),
            };
            Assert.Equal(stamps.Length, stamps.Distinct().Count());
        }
    }
}
