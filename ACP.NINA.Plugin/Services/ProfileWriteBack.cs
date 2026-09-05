using ACP.NINA.Plugin.Models;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;

namespace ACP.NINA.Plugin.Services {

    /// What a write-back attempt did, so the sequencer log, the dock and the
    /// NINA log can all say the same thing without recomputing it.
    public class WriteBackResult {

        public bool Written { get; set; }

        /// Present when nothing was written, in words a user can act on.
        public string Reason { get; set; }

        public double OldFocalLengthMm { get; set; }
        public double NewFocalLengthMm { get; set; }
        public double? OldFocalRatio { get; set; }
        public double? NewFocalRatio { get; set; }

        /// One line, the same text in every place it is reported.
        public string Summary {
            get {
                if (!Written) {
                    return $"Profile focal length left at {OldFocalLengthMm:F1} mm ({Reason}).";
                }
                var line = $"Profile focal length {OldFocalLengthMm:F1} mm to {NewFocalLengthMm:F1} mm";
                if (NewFocalRatio.HasValue) {
                    var oldRatio = OldFocalRatio.HasValue ? $"f/{OldFocalRatio.Value:F1}" : "unset";
                    line += $", focal ratio {oldRatio} to f/{NewFocalRatio.Value:F1}";
                }
                return line + ".";
            }
        }
    }

    /// Writes the solved focal length back into the active NINA profile.
    ///
    /// Behind an interface for one reason above testability: there must be
    /// exactly one place in the plugin that touches TelescopeSettings, and it
    /// must be reachable from exactly two callers, the Sync for tonight
    /// instruction and the Sync for tonight dock button. No other plate solve,
    /// not NINA's centring, not a manual solve, not another plugin, ever gets
    /// near it. That is the decision recorded in the v3 spec on 2026-09-04.
    public interface IProfileWriteBack {

        /// Apply the write-back if the solved focal length is far enough out.
        /// Pass enabled false to evaluate and report without writing, which is
        /// what the instruction's unticked checkbox does.
        WriteBackResult Apply(Fingerprint fingerprint, IEnumerable<Telescope> acpTelescopes, bool enabled);
    }

    [Export(typeof(IProfileWriteBack))]
    public class ProfileWriteBack : IProfileWriteBack {

        private readonly IProfileService profileService;

        [ImportingConstructor]
        public ProfileWriteBack(IProfileService profileService) {
            this.profileService = profileService;
        }

        public WriteBackResult Apply(Fingerprint fingerprint, IEnumerable<Telescope> acpTelescopes, bool enabled) {
            var settings = profileService?.ActiveProfile?.TelescopeSettings;
            var solved = fingerprint?.FocalLengthMm?.Solved;
            var oldFocalLength = settings?.FocalLength ?? 0;
            var oldFocalRatio = settings?.FocalRatio;

            var result = new WriteBackResult {
                OldFocalLengthMm = oldFocalLength,
                NewFocalLengthMm = oldFocalLength,
                OldFocalRatio = oldFocalRatio,
                NewFocalRatio = oldFocalRatio,
            };

            if (settings == null) {
                result.Reason = "no active NINA profile";
                return result;
            }
            if (!solved.HasValue) {
                result.Reason = "nothing was solved, so there is no measured focal length to compare";
                return result;
            }
            if (!FingerprintMath.ShouldWriteBackFocalLength(oldFocalLength, solved.Value)) {
                result.Reason =
                    $"the solve says {solved.Value:F1} mm, within 5 percent of the profile";
                return result;
            }
            if (!enabled) {
                result.Reason =
                    $"the solve says {solved.Value:F1} mm but the update is switched off";
                return result;
            }

            // Aperture is optional. When ACP does not know it, the focal length
            // is written on its own and the focal ratio is left as it was,
            // rather than being invented from a stale aperture.
            var aperture = ChooseApertureMm(solved.Value, acpTelescopes);
            var newRatio = FingerprintMath.FocalRatio(solved.Value, aperture);

            settings.FocalLength = solved.Value;
            result.NewFocalLengthMm = solved.Value;
            if (newRatio.HasValue) {
                settings.FocalRatio = newRatio.Value;
                result.NewFocalRatio = newRatio.Value;
            }
            result.Written = true;

            // One line, with the old and the new values, every time. The spec
            // requires the user be told, and this is the third of the three
            // places, after the instruction's description and the button's
            // tooltip.
            Logger.Info("ACP: " + result.Summary);
            return result;
        }

        /// Pick the aperture to write the focal ratio from.
        ///
        /// The spec says "the aperture of the matched telescope", but the write
        /// back happens before the match call, so the telescope is matched here
        /// on the one thing already known: the solved focal length. The nearest
        /// ACP telescope within 15 percent wins, which is the same tolerance
        /// the ACP side uses for pixel scale, and nothing outside that is close
        /// enough to lend its aperture to a rig it is not.
        ///
        /// Static and taking plain ACP models so the choice can be tested.
        public static double? ChooseApertureMm(double solvedFocalLengthMm, IEnumerable<Telescope> telescopes) {
            if (telescopes == null || solvedFocalLengthMm <= 0) return null;

            var best = telescopes
                .Where(t => t != null
                            && t.ApertureMm.HasValue && t.ApertureMm.Value > 0
                            && t.FocalLengthMm.HasValue && t.FocalLengthMm.Value > 0)
                .Select(t => new {
                    Telescope = t,
                    Error = Math.Abs(t.FocalLengthMm.Value - solvedFocalLengthMm) / solvedFocalLengthMm,
                })
                .Where(x => x.Error <= 0.15)
                .OrderBy(x => x.Error)
                .FirstOrDefault();

            return best?.Telescope.ApertureMm;
        }
    }
}
