using ACP.NINA.Plugin.Services;
using System;
using System.IO;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// Settings live outside the plugin folder so an upgrade cannot wipe them,
    /// and a file left in the old place is copied across on first load.
    public class SettingsMigrationTests : IDisposable {

        private readonly string root = Path.Combine(
            Path.GetTempPath(), "acp-settings-test-" + Guid.NewGuid().ToString("N"));

        private string NewPath => Path.Combine(root, "new", "ACP.NINA.Plugin", "settings.json");
        private string OldPath => Path.Combine(root, "old", "settings.json");

        public SettingsMigrationTests() {
            Directory.CreateDirectory(root);
        }

        public void Dispose() {
            try { Directory.Delete(root, true); } catch { }
        }

        private void WriteFile(string path, string json) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, json);
        }

        [Fact]
        public void OldFileIsCopiedToTheNewPlaceAndLoaded() {
            const string json = "{\"ServerUrl\":\"http://10.0.0.9:5555\",\"AutoRefreshSeconds\":77}";
            WriteFile(OldPath, json);

            var loaded = AcpSettings.LoadFromDisk(NewPath, OldPath);

            Assert.Equal("http://10.0.0.9:5555", loaded.ServerUrl);
            Assert.Equal(77, loaded.AutoRefreshSeconds);
            Assert.True(File.Exists(NewPath));
            Assert.Equal(json, File.ReadAllText(NewPath));
        }

        [Fact]
        public void TheOldFileIsLeftInPlaceForARollback() {
            const string json = "{\"ServerUrl\":\"http://10.0.0.9:5555\"}";
            WriteFile(OldPath, json);

            AcpSettings.LoadFromDisk(NewPath, OldPath);

            Assert.True(File.Exists(OldPath));
            Assert.Equal(json, File.ReadAllText(OldPath));
        }

        [Fact]
        public void NeitherFileMeansDefaultsAndNothingIsWritten() {
            var loaded = AcpSettings.LoadFromDisk(NewPath, OldPath);

            Assert.Equal(new AcpSettings().ServerUrl, loaded.ServerUrl);
            Assert.False(File.Exists(NewPath));
            Assert.False(File.Exists(OldPath));
        }

        [Fact]
        public void TheNewFileWinsAndTheOldOneIsIgnored() {
            WriteFile(NewPath, "{\"ServerUrl\":\"http://new-host:5555\"}");
            WriteFile(OldPath, "{\"ServerUrl\":\"http://old-host:5555\"}");

            var loaded = AcpSettings.LoadFromDisk(NewPath, OldPath);

            Assert.Equal("http://new-host:5555", loaded.ServerUrl);
            Assert.Contains("new-host", File.ReadAllText(NewPath));
        }

        [Fact]
        public void AnUnreadableOldFileFallsBackToDefaults() {
            WriteFile(OldPath, "this is not json {{{");

            var loaded = AcpSettings.LoadFromDisk(NewPath, OldPath);

            Assert.Equal(new AcpSettings().ServerUrl, loaded.ServerUrl);
        }

        [Fact]
        public void AFolderThatCannotBeCreatedFallsBackToDefaults() {
            WriteFile(OldPath, "{\"ServerUrl\":\"http://10.0.0.9:5555\"}");
            // A plain file where the new file's folder should go, so
            // CreateDirectory throws.
            var blocker = Path.Combine(root, "blocked");
            File.WriteAllText(blocker, "x");
            var impossible = Path.Combine(blocker, "ACP.NINA.Plugin", "settings.json");

            var loaded = AcpSettings.LoadFromDisk(impossible, OldPath);

            Assert.Equal(new AcpSettings().ServerUrl, loaded.ServerUrl);
        }
    }
}
