using System;
using System.IO;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// Where schedulerdb.sqlite lives.
    ///
    /// Ported from nina_ts_sync/paths.py, which is the only definition of "the
    /// way the extension finds it today" that exists: Target Scheduler keeps
    /// its database at a fixed path under NINA's local application data and
    /// has no setting that moves it, so the plugin looks in the same place.
    /// The environment override is kept because it is what makes the Python
    /// tests runnable off Windows, and the same reason applies here.
    public static class TsPaths {

        public const string EnvDbPath = "ACP_TS_DB_PATH";

        public const string DefaultDbFileName = "schedulerdb.sqlite";

        /// The conventional install path, or null when we cannot guess, which
        /// is every non-Windows machine.
        public static string DefaultDbPath() {
            var overridePath = Environment.GetEnvironmentVariable(EnvDbPath);
            if (!string.IsNullOrWhiteSpace(overridePath)) {
                return Environment.ExpandEnvironmentVariables(overridePath);
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) return null;

            return Path.Combine(local, "NINA", "SchedulerPlugin", DefaultDbFileName);
        }

        /// Pick the path, preferring an explicit one, then the environment,
        /// then the OS default. Throws rather than returning a path that is not
        /// there, so a typo can never end up creating an empty database
        /// somewhere unexpected and quietly pushing a night's plans into it.
        public static string ResolveDbPath(string explicitPath = null) {
            string candidate;
            if (!string.IsNullOrWhiteSpace(explicitPath)) {
                candidate = Environment.ExpandEnvironmentVariables(explicitPath);
            } else {
                candidate = DefaultDbPath();
                if (string.IsNullOrWhiteSpace(candidate)) {
                    throw new FileNotFoundException(
                        "No Target Scheduler database path supplied and " + EnvDbPath +
                        " is unset. On a machine without a NINA install you must set " +
                        EnvDbPath + " explicitly."
                    );
                }
            }

            if (!File.Exists(candidate)) {
                throw new FileNotFoundException(
                    DefaultDbFileName + " not found at " + candidate +
                    ". Make sure NINA and the Target Scheduler plugin have been started " +
                    "at least once on this machine, or set " + EnvDbPath +
                    " to the database you want.",
                    candidate
                );
            }
            return candidate;
        }
    }
}
