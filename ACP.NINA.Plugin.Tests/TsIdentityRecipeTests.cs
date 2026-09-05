using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services.TargetScheduler;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// The identity recipe, and the two payloads the push now refuses.
    ///
    /// The old recipe joined free-text name parts with a slash, so a target
    /// name holding a slash could reach the same UUID as a different project
    /// and target pair. Length-prefixing every part removes that by
    /// construction, whatever characters a name holds.
    ///
    /// Every expected value here was produced by the Python extension's
    /// tests/test_identity_recipe.py, which asserts the same things.
    public class TsIdentityRecipeTests {

        private const string Profile = "profile-1";

        [Fact]
        public void TheNamespaceUuidIsUnchanged() {
            // It is part of the on-disk identity of every row either tool stamps.
            Assert.Equal("c4b6f1ee-1f9e-5e4b-9a7a-7e1d2c3a4b5c", TsGuid.AcpNamespace.ToString());
        }

        [Fact]
        public void ASlashInANameNoLongerCollides() {
            Assert.NotEqual(
                TsGuid.Target(Profile, "M42", "M43/NGC1977"),
                TsGuid.Target(Profile, "M42/M43", "NGC1977"));
        }

        [Fact]
        public void TheOldRecipeDidCollide() {
            // The legacy methods are kept exactly as they were, for the migration.
            var a = TsGuid.LegacyTarget(Profile, "M42", "M43/NGC1977");
            var b = TsGuid.LegacyTarget(Profile, "M42/M43", "NGC1977");
            Assert.Equal(a, b);
            Assert.Equal("80e4a6ad-29a0-57ad-83b5-f0a07560b561", a);
        }

        [Fact]
        public void TheNewRecipeIsNotTheOldOne() {
            Assert.NotEqual(
                TsGuid.Project(Profile, "M42"), TsGuid.LegacyProject(Profile, "M42"));
        }

        [Fact]
        public void NamePartsAreLengthPrefixedInUtf8Bytes() {
            Assert.Equal("3:M42", TsGuid.NamePart("M42"));
            Assert.Equal("0:", TsGuid.NamePart(""));
            Assert.Equal("0:", TsGuid.NamePart(null));
            // Five bytes in UTF-8, three characters. Python counts bytes too,
            // because .NET would otherwise count UTF-16 units and disagree.
            Assert.Equal("5:µm°", TsGuid.NamePart("µm°"));
        }

        [Fact]
        public void EveryRecipeUsesThePrefixedForm() {
            Assert.Equal(
                TsGuid.Stable("9:profile-1/project/3:M42"), TsGuid.Project(Profile, "M42"));
            Assert.Equal(
                TsGuid.Stable("9:profile-1/target/1:P/1:T"), TsGuid.Target(Profile, "P", "T"));
            Assert.Equal(
                TsGuid.Stable("9:profile-1/template/2:Ha/5:cam-1"),
                TsGuid.Template(Profile, "Ha", "cam-1"));
            Assert.Equal(
                TsGuid.Stable("9:profile-1/plan/3:abc/2:Ha"),
                TsGuid.ExposurePlan(Profile, "abc", "Ha"));
        }

        [Fact]
        public void AmbiguityIsGoneForEveryPairOfSplitPoints() {
            // The one property the prefix buys: no two splits of a string agree.
            const string whole = "A/B/C";
            var seen = new HashSet<string>();
            for (var cut = 0; cut <= whole.Length; cut++) {
                seen.Add(TsGuid.Target(Profile, whole.Substring(0, cut), whole.Substring(cut)));
            }
            Assert.Equal(whole.Length + 1, seen.Count);
        }

        // -- Payloads the push refuses rather than writing --------------------

        [Fact]
        public void ATargetWithNoNameIsRefused() {
            var plans = new List<Plan> {
                TsTestPlans.Plan("blank", projectName: "P", targetName: "   "),
            };
            var ex = Assert.Throws<TsPushValidationException>(
                () => Build(plans));
            Assert.Contains("no name", ex.Message);
            Assert.Contains("'blank'", ex.Message);
        }

        [Fact]
        public void TwoPlansWithOneTargetNameInOneProjectAreRefused() {
            var plans = new List<Plan> {
                TsTestPlans.Plan("first", projectName: "Winter", targetName: "M31"),
                TsTestPlans.Plan("second", projectName: "Winter", targetName: "M31"),
            };
            var ex = Assert.Throws<TsPushValidationException>(() => Build(plans));
            Assert.Contains("'first'", ex.Message);
            Assert.Contains("'second'", ex.Message);
            Assert.Contains("'M31'", ex.Message);
            Assert.Contains("'Winter'", ex.Message);
        }

        [Fact]
        public void TheSameTargetNameInTwoProjectsIsFine() {
            var plans = new List<Plan> {
                TsTestPlans.Plan("first", projectName: "Winter", targetName: "M31"),
                TsTestPlans.Plan("second", projectName: "Summer", targetName: "M31"),
            };
            var payload = Build(plans);
            var guids = payload.TargetsByProjectGuid.Values.SelectMany(v => v)
                .Select(t => t.Guid).ToList();
            Assert.Equal(2, guids.Count);
            Assert.Equal(2, guids.Distinct().Count());
        }

        /// The upsert's own backstop, for a payload that did not come through
        /// the converter.
        [Fact]
        public void TheUpsertRefusesADuplicateItIsHandedDirectly() {
            var payload = Build(new List<Plan> {
                TsTestPlans.Plan("only", projectName: "P", targetName: "M31"),
            });
            var group = payload.TargetsByProjectGuid.Values.First();
            group.Add(new TsTarget { Name = group[0].Name, Guid = group[0].Guid });

            var ex = Assert.Throws<TsPushValidationException>(() => TsUpsert.Validate(payload));
            Assert.Contains("share the identity", ex.Message);
        }

        [Fact]
        public void TheUpsertRefusesABlankTargetNameItIsHandedDirectly() {
            var payload = Build(new List<Plan> {
                TsTestPlans.Plan("only", projectName: "P", targetName: "M31"),
            });
            payload.TargetsByProjectGuid.Values.First()[0].Name = "  ";

            var ex = Assert.Throws<TsPushValidationException>(() => TsUpsert.Validate(payload));
            Assert.Contains("no name", ex.Message);
        }

        private static TsSyncPayload Build(List<Plan> plans) {
            return TsConvert.BuildPayload(
                plans, TsTestPlans.Gear(), TsTestPlans.ProfileId, TsTestPlans.FrozenNow);
        }
    }
}
