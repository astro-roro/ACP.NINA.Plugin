using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using System.ComponentModel.Composition;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin {

    [Export(typeof(IPluginManifest))]
    public class AcpPlugin : PluginBase {

        private readonly IProfileService profileService;
        private readonly IFramingAssistantVM framingAssistantVM;

        [ImportingConstructor]
        public AcpPlugin(IProfileService profileService, IFramingAssistantVM framingAssistantVM) {
            this.profileService = profileService;
            this.framingAssistantVM = framingAssistantVM;
        }

        public override Task Initialize() {
            Logger.Info("ACP plugin initialized");
            return Task.CompletedTask;
        }

        public override Task Teardown() {
            Logger.Info("ACP plugin teardown");
            return Task.CompletedTask;
        }
    }
}
