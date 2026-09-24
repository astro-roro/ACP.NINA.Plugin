using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// Push a set of ACP plans into a copy of a real Target Scheduler database
    /// with the plugin's own writer, and leave the result on disk to diff.
    ///
    /// This is how the import round trip was checked against Voyager's
    /// database: import the TS projects into ACP plans with the Python
    /// extension, then push those plans back with TsPushService and compare
    /// every row with the original. The diff itself is a script outside this
    /// repo; this test only does the push, so the answer comes from the real
    /// writer rather than a model of it.
    ///
    /// Set all four variables to run it. When any is unset the test passes
    /// without doing anything, like TsLiveDatabaseTests.
    ///
    ///   ACP_TS_HARNESS_DB     a schedulerdb.sqlite to start from; it is copied, never opened
    ///   ACP_TS_HARNESS_PLANS  a plans.json ({"plans": [...]}) as ACP would serve it
    ///   ACP_TS_HARNESS_GEAR   a gear.json as ACP would serve it
    ///   ACP_TS_HARNESS_OUT    a directory for pushed.sqlite and outcome.json
    public class TsImportRoundTripHarness {

        private const string ProfileId = "9d3a61fd-9f35-42ae-a911-bd6e4b0f76a7";

        /// Voyager's filter wheel.
        private static readonly string[] WheelFilters = { "L", "R", "G", "B", "H", "O", "S" };

        [Fact]
        public async Task PushTheImportedPlansIntoACopyOfTheDatabase() {
            var source = Environment.GetEnvironmentVariable("ACP_TS_HARNESS_DB");
            var plansPath = Environment.GetEnvironmentVariable("ACP_TS_HARNESS_PLANS");
            var gearPath = Environment.GetEnvironmentVariable("ACP_TS_HARNESS_GEAR");
            var outDir = Environment.GetEnvironmentVariable("ACP_TS_HARNESS_OUT");
            if (new[] { source, plansPath, gearPath, outDir }.Any(string.IsNullOrWhiteSpace)) return;

            Directory.CreateDirectory(outDir);
            var pushed = Path.Combine(outDir, "pushed.sqlite");
            File.Copy(source, pushed, overwrite: true);

            var plans = JsonConvert.DeserializeObject<PlansResponse>(File.ReadAllText(plansPath)).Plans;
            var gear = JsonConvert.DeserializeObject<GearResponse>(File.ReadAllText(gearPath));

            var service = new TsPushService(
                new FakeContainerWatch(),
                path => TargetSchedulerDb.Open(pushed),
                () => 1790300000L
            ) {
                DbPathOverride = pushed,
                MakeBackup = false,
            };
            var result = await service.PushAsync(plans, gear, ProfileId, default, WheelFilters);

            var outcome = new JObject {
                ["success"] = result.Success,
                ["failure"] = result.Failure,
                ["summary"] = result.Summary(),
                ["pushed"] = result.Pushed.Count,
                ["left_out"] = new JArray(result.LeftOut),
                ["filter_renames"] = JObject.FromObject(result.FilterRenames),
                ["filters_not_on_wheel"] = new JArray(result.FiltersNotOnWheel),
            };
            if (result.Outcome != null) {
                outcome["counts"] = JObject.FromObject(new Dictionary<string, TsTableCounts> {
                    { "exposuretemplate", result.Outcome.ExposureTemplate },
                    { "project", result.Outcome.Project },
                    { "target", result.Outcome.Target },
                    { "exposureplan", result.Outcome.ExposurePlan },
                });
                outcome["notes"] = new JArray(result.Outcome.Notes);
            }
            File.WriteAllText(Path.Combine(outDir, "outcome.json"), outcome.ToString());

            Assert.True(result.Success, result.Failure);
        }
    }
}
