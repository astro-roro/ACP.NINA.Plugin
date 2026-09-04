using System.ComponentModel.Composition;
using System.Windows;

namespace ACP.NINA.Plugin.Sequencer {

    /// NINA picks up exported ResourceDictionaries from a plugin and merges
    /// them, which is how the instruction gets a data template rather than the
    /// default property grid.
    [Export(typeof(ResourceDictionary))]
    public partial class SyncForTonightTemplate : ResourceDictionary {

        public SyncForTonightTemplate() {
            InitializeComponent();
        }
    }
}
