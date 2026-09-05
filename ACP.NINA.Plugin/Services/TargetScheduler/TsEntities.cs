using System.Collections.Generic;

namespace ACP.NINA.Plugin.Services.TargetScheduler {

    /// The four Target Scheduler rows the push writes, plus the ruleweight seed
    /// and the payload that carries a whole sync run.
    ///
    /// Ported from nina_ts_sync/schema.py. Property names match the column
    /// names in schedulerdb.sqlite so the mapping to SQL stays mechanical, and
    /// the defaults match the ones Target Scheduler's own constructors use, so
    /// a column ACP has no field for is written at the value TS would have
    /// picked anyway.
    ///
    /// The Python side gets its column order from dataclasses.fields(). C#
    /// gives no such guarantee over properties, so each entity states its
    /// column list explicitly and hands back a value per column. Explicit is
    /// also easier to check against docs/schema-history.md.
    public abstract class TsEntity {

        /// The deterministic UUIDv5 stamp. Every entity has one; it is the
        /// dedup key the upsert looks up on.
        public string Guid { get; set; }

        /// Column names in the order the Python dataclass declares its fields.
        /// `Id` is deliberately absent so SQLite's autoincrement is left alone.
        public abstract IReadOnlyList<string> Columns { get; }

        /// The value for one of `Columns`, boxed for parameter binding.
        public abstract object ValueOf(string column);
    }

    public class TsExposureTemplate : TsEntity {

        public string ProfileId { get; set; }
        public string Name { get; set; }
        public string FilterName { get; set; }
        public double DefaultExposure { get; set; } = 60.0;
        public int Gain { get; set; } = -1;
        public int Offset { get; set; } = -1;
        public int Bin { get; set; } = 1;
        public int ReadoutMode { get; set; } = -1;
        /// 1 = Astronomical in NINA's twilight enum.
        public int TwilightLevel { get; set; } = 1;
        public int MinutesOffset { get; set; }
        public double MaximumHumidity { get; set; } = 100.0;
        public int MoonAvoidanceEnabled { get; set; }
        public double MoonAvoidanceSeparation { get; set; }
        public int MoonAvoidanceWidth { get; set; }
        public double MoonRelaxScale { get; set; }
        public double MoonRelaxMaxAltitude { get; set; } = 5.0;
        public double MoonRelaxMinAltitude { get; set; } = -15.0;
        public int MoonDownEnabled { get; set; }
        public int DitherEvery { get; set; } = -1;

        private static readonly string[] cols = {
            "profileId", "name", "filtername", "guid", "defaultexposure", "gain",
            "offset", "bin", "readoutmode", "twilightlevel", "minutesOffset",
            "maximumhumidity", "moonavoidanceenabled", "moonavoidanceseparation",
            "moonavoidancewidth", "moonrelaxscale", "moonrelaxmaxaltitude",
            "moonrelaxminaltitude", "moondownenabled", "ditherevery",
        };

        public override IReadOnlyList<string> Columns => cols;

        public override object ValueOf(string column) {
            switch (column) {
                case "profileId": return ProfileId;
                case "name": return Name;
                case "filtername": return FilterName;
                case "guid": return Guid;
                case "defaultexposure": return DefaultExposure;
                case "gain": return Gain;
                case "offset": return Offset;
                case "bin": return Bin;
                case "readoutmode": return ReadoutMode;
                case "twilightlevel": return TwilightLevel;
                case "minutesOffset": return MinutesOffset;
                case "maximumhumidity": return MaximumHumidity;
                case "moonavoidanceenabled": return MoonAvoidanceEnabled;
                case "moonavoidanceseparation": return MoonAvoidanceSeparation;
                case "moonavoidancewidth": return MoonAvoidanceWidth;
                case "moonrelaxscale": return MoonRelaxScale;
                case "moonrelaxmaxaltitude": return MoonRelaxMaxAltitude;
                case "moonrelaxminaltitude": return MoonRelaxMinAltitude;
                case "moondownenabled": return MoonDownEnabled;
                case "ditherevery": return DitherEvery;
                default: return null;
            }
        }
    }

    public class TsProject : TsEntity {

        public string ProfileId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; } = string.Empty;
        /// 1 = Active.
        public int State { get; set; } = 1;
        /// 1 = Normal, matching PRIORITY_RANK in the Python converter.
        public int Priority { get; set; } = 1;
        /// Unix seconds. TsConvert fills it from the injected clock.
        public long CreateDate { get; set; }
        public long? ActiveDate { get; set; }
        public long? InactiveDate { get; set; }
        public int IsMosaic { get; set; }
        public int FlatsHandling { get; set; }
        public int MinimumTime { get; set; }
        public double MinimumAltitude { get; set; }
        /// The column really does use a capital A from v23 onwards.
        public double MaximumAltitude { get; set; }
        public int UseCustomHorizon { get; set; }
        public double HorizonOffset { get; set; }
        public int MeridianWindow { get; set; }
        public int FilterSwitchFrequency { get; set; }
        public int DitherEvery { get; set; }
        public int SmartExposureOrder { get; set; }
        public int EnableGrader { get; set; }

        private static readonly string[] cols = {
            "profileId", "name", "guid", "description", "state", "priority",
            "createdate", "activedate", "inactivedate", "isMosaic", "flatsHandling",
            "minimumtime", "minimumaltitude", "maximumAltitude", "usecustomhorizon",
            "horizonoffset", "meridianwindow", "filterswitchfrequency", "ditherevery",
            "smartexposureorder", "enablegrader",
        };

        public override IReadOnlyList<string> Columns => cols;

        public override object ValueOf(string column) {
            switch (column) {
                case "profileId": return ProfileId;
                case "name": return Name;
                case "guid": return Guid;
                case "description": return Description;
                case "state": return State;
                case "priority": return Priority;
                case "createdate": return CreateDate;
                case "activedate": return ActiveDate;
                case "inactivedate": return InactiveDate;
                case "isMosaic": return IsMosaic;
                case "flatsHandling": return FlatsHandling;
                case "minimumtime": return MinimumTime;
                case "minimumaltitude": return MinimumAltitude;
                case "maximumAltitude": return MaximumAltitude;
                case "usecustomhorizon": return UseCustomHorizon;
                case "horizonoffset": return HorizonOffset;
                case "meridianwindow": return MeridianWindow;
                case "filterswitchfrequency": return FilterSwitchFrequency;
                case "ditherevery": return DitherEvery;
                case "smartexposureorder": return SmartExposureOrder;
                case "enablegrader": return EnableGrader;
                default: return null;
            }
        }
    }

    public class TsTarget : TsEntity {

        /// Filled in by the upsert once the parent project has a real Id.
        public int ProjectId { get; set; }
        public string Name { get; set; }
        public int Active { get; set; } = 1;
        /// Hours, which is what Target Scheduler stores.
        public double Ra { get; set; }
        /// Degrees.
        public double Dec { get; set; }
        /// 1 = J2000.
        public int EpochCode { get; set; } = 1;
        public double Rotation { get; set; }
        public double Roi { get; set; } = 100.0;
        /// Left behind by Migrate/17.sql when per-target exposure order moved
        /// to its own table. The plugin no longer reads it, so we leave it null.
        public string UnusedOeo { get; set; }
        /// Added by Migrate/24.sql, so absent at user_version 23. The write path
        /// drops it on a 23 database; see TsSchema.ColumnMinVersion.
        public int Priority { get; set; } = -1;

        private static readonly string[] cols = {
            "projectid", "name", "guid", "active", "ra", "dec", "epochcode",
            "rotation", "roi", "unusedOEO", "priority",
        };

        public override IReadOnlyList<string> Columns => cols;

        public override object ValueOf(string column) {
            switch (column) {
                case "projectid": return ProjectId;
                case "name": return Name;
                case "guid": return Guid;
                case "active": return Active;
                case "ra": return Ra;
                case "dec": return Dec;
                case "epochcode": return EpochCode;
                case "rotation": return Rotation;
                case "roi": return Roi;
                case "unusedOEO": return UnusedOeo;
                case "priority": return Priority;
                default: return null;
            }
        }
    }

    public class TsExposurePlan : TsEntity {

        public string ProfileId { get; set; }
        /// Filled in by the upsert once the parent target has a real Id.
        public int TargetId { get; set; }
        /// Filled in by the upsert once the template has a real Id.
        public int ExposureTemplateId { get; set; }
        /// -1 means "use the template's default exposure".
        public double Exposure { get; set; } = -1.0;
        public int Desired { get; set; }
        public int Acquired { get; set; }
        public int Accepted { get; set; }
        public int Enabled { get; set; } = 1;

        private static readonly string[] cols = {
            "profileId", "targetid", "exposureTemplateId", "guid", "exposure",
            "desired", "acquired", "accepted", "enabled",
        };

        public override IReadOnlyList<string> Columns => cols;

        public override object ValueOf(string column) {
            switch (column) {
                case "profileId": return ProfileId;
                case "targetid": return TargetId;
                case "exposureTemplateId": return ExposureTemplateId;
                case "guid": return Guid;
                case "exposure": return Exposure;
                case "desired": return Desired;
                case "acquired": return Acquired;
                case "accepted": return Accepted;
                case "enabled": return Enabled;
                default: return null;
            }
        }
    }

    /// One scoring rule weight on a project. Target Scheduler's own
    /// RepairAndUpdate() fills in anything missing on the next NINA start, so
    /// seeding these is a courtesy that makes a fresh project usable straight
    /// away rather than a correctness requirement.
    public class TsRuleWeight {
        public int ProjectId { get; set; }
        public string Name { get; set; }
        public double Weight { get; set; }
    }

    /// Everything one sync run wants to write.
    ///
    /// Insert order matters even though Target Scheduler does not enforce its
    /// foreign keys: templates first so exposure plans have an Id to point at,
    /// then projects with their rule weights, then targets, then plans.
    public class TsSyncPayload {

        public string ProfileId { get; set; }

        public List<TsExposureTemplate> Templates { get; } = new List<TsExposureTemplate>();

        public List<TsProject> Projects { get; } = new List<TsProject>();

        public Dictionary<string, List<TsRuleWeight>> RuleWeightsByProjectGuid { get; } =
            new Dictionary<string, List<TsRuleWeight>>();

        public Dictionary<string, List<TsTarget>> TargetsByProjectGuid { get; } =
            new Dictionary<string, List<TsTarget>>();

        public Dictionary<string, List<TsExposurePlan>> PlansByTargetGuid { get; } =
            new Dictionary<string, List<TsExposurePlan>>();

        /// Each exposure plan's guid to the template guid it should reference.
        /// The converter fills this in; the upsert reads it to resolve
        /// exposureTemplateId once both rows are on disk.
        public Dictionary<string, string> TemplateGuidByPlanGuid { get; } =
            new Dictionary<string, string>();

        /// Each exposure template's guid to the guid the same template carried
        /// under the pre-length-prefix recipe. A template's natural key
        /// includes the camera id, which is not a column on the row, so this is
        /// the one table whose old identity cannot be recomputed from the
        /// database alone. TsMigration reads it; the other three tables it
        /// derives from their own rows.
        public Dictionary<string, string> LegacyTemplateGuidByGuid { get; } =
            new Dictionary<string, string>();
    }
}
