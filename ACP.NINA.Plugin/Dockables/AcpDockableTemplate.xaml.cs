using System.ComponentModel.Composition;
using System.Windows;

namespace ACP.NINA.Plugin.Dockables {
    [Export(typeof(ResourceDictionary))]
    public partial class AcpDockableTemplate : ResourceDictionary {
        public AcpDockableTemplate() {
            InitializeComponent();
        }
    }
}
