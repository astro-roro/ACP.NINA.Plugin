using ACP.NINA.Plugin.Services;
using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Sequencer {

    /// The start of the night in one instruction. Optionally slew, capture and
    /// solve a frame, work out what gear is actually connected from the solve,
    /// correct the profile focal length if it is wrong, and ask ACP which plans
    /// fit.
    ///
    /// Loading the matching plans into Target Scheduler is the next version.
    /// This one reports what fits and leaves TS alone, which is worth having on
    /// its own: it tells you whether the rig is what you think it is before you
    /// commit the night to it.
    [ExportMetadata("Name", "ACP: Sync for tonight")]
    [ExportMetadata(
        "Description",
        "Solves a frame, works out the connected gear from it, and asks ACP which plans fit tonight. " +
        "Updates the profile focal length and focal ratio when the solve says they are more than 5 percent out, " +
        "which you can switch off below. Loading the matching plans into Target Scheduler arrives in the next version."
    )]
    [ExportMetadata("Icon", "PlatesolveSVG")]
    [ExportMetadata("Category", "ACP")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SyncForTonight : SequenceItem, IValidatable {

        private readonly IProfileService profileService;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly ICameraMediator cameraMediator;
        private readonly IAcpPlateSolver plateSolver;
        private readonly ISyncForTonightRunner runner;

        [ImportingConstructor]
        public SyncForTonight(
            IProfileService profileService,
            ITelescopeMediator telescopeMediator,
            ICameraMediator cameraMediator,
            IAcpPlateSolver plateSolver,
            ISyncForTonightRunner runner
        ) {
            this.profileService = profileService;
            this.telescopeMediator = telescopeMediator;
            this.cameraMediator = cameraMediator;
            this.plateSolver = plateSolver;
            this.runner = runner;
            Coordinates = new InputCoordinates();
        }

        private SyncForTonight(SyncForTonight cloneMe) : this(
            cloneMe.profileService,
            cloneMe.telescopeMediator,
            cloneMe.cameraMediator,
            cloneMe.plateSolver,
            cloneMe.runner
        ) {
            CopyMetaData(cloneMe);
            SlewFirst = cloneMe.SlewFirst;
            ExposureTime = cloneMe.ExposureTime;
            UpdateProfileFocalLength = cloneMe.UpdateProfileFocalLength;
            Coordinates = cloneMe.Coordinates?.Clone() ?? new InputCoordinates();
        }

        public override object Clone() {
            return new SyncForTonight(this);
        }

        // -- Settings on the instruction ---------------------------------------

        private bool slewFirst;

        /// Off by default. The common case is running this after polar
        /// alignment and autofocus, where the mount is already somewhere with
        /// stars in the frame and moving is a waste of a minute.
        [JsonProperty]
        public bool SlewFirst {
            get => slewFirst;
            set { slewFirst = value; RaisePropertyChanged(); }
        }

        [JsonProperty]
        public InputCoordinates Coordinates { get; set; }

        private double exposureTime = 10;

        [JsonProperty]
        public double ExposureTime {
            get => exposureTime;
            set { exposureTime = value; RaisePropertyChanged(); }
        }

        private bool updateProfileFocalLength = true;

        /// On by default, per the spec. Turning it off still solves, still
        /// fingerprints and still matches; it only stops the profile being
        /// written.
        [JsonProperty]
        public bool UpdateProfileFocalLength {
            get => updateProfileFocalLength;
            set { updateProfileFocalLength = value; RaisePropertyChanged(); }
        }

        // -- Validation ---------------------------------------------------------

        private IList<string> issues = new List<string>();

        public IList<string> Issues {
            get => issues;
            set { issues = value; RaisePropertyChanged(); }
        }

        public bool Validate() {
            var found = new List<string>();
            if (!cameraMediator.GetInfo().Connected) {
                found.Add("Camera not connected. The gear fingerprint needs a frame to solve.");
            }
            if (SlewFirst && !telescopeMediator.GetInfo().Connected) {
                found.Add("Mount not connected, so this instruction cannot slew first.");
            }
            if (ExposureTime <= 0) {
                found.Add("Exposure time must be more than zero.");
            }
            Issues = found;
            return found.Count == 0;
        }

        // -- Execution ----------------------------------------------------------

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (SlewFirst) {
                var target = Coordinates?.Coordinates;
                if (target == null) {
                    throw new SequenceEntityFailedException(
                        "Sync for tonight is set to slew first but has no coordinates."
                    );
                }
                Logger.Info($"ACP: slewing to {target} before the sync solve.");
                await telescopeMediator.SlewToCoordinatesAsync(target, token);
            }

            // The instruction always solves. That is decision 1 in the v3 spec:
            // reusing an old solve is the dock button's job, because the button
            // is pressed by someone who can see how long ago it was.
            var solve = await plateSolver.SolveAsync(ExposureTime, progress, token);
            if (solve == null) {
                throw new SequenceEntityFailedException(
                    "The plate solve failed, so ACP cannot tell what gear is connected."
                );
            }

            var outcome = await runner.RunAsync(solve, UpdateProfileFocalLength, token);

            // The sequencer's own log is the NINA log, so one line each and the
            // user can read the whole run back afterwards.
            foreach (var line in outcome.Lines) {
                Logger.Info($"ACP: Sync for tonight: {line}");
            }

            if (!outcome.Success) {
                throw new SequenceEntityFailedException(outcome.Failure ?? "Sync for tonight did not finish.");
            }
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(SyncForTonight)}, " +
                   $"Exposure: {ExposureTime}s, Slew first: {SlewFirst}, " +
                   $"Update profile focal length: {UpdateProfileFocalLength}";
        }
    }
}
