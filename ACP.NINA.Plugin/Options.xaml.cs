using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;

namespace ACP.NINA.Plugin {

    [Export(typeof(ResourceDictionary))]
    public partial class Options : ResourceDictionary {

        public Options() {
            InitializeComponent();
        }

        /// PasswordBox.Password is deliberately not a dependency property, so
        /// there is no way to bind it. This handler is the standard workaround:
        /// push the typed value straight into the plugin, which stores it in
        /// Windows Credential Manager and never hands it back to the UI.
        private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e) {
            if (sender is PasswordBox box && box.DataContext is AcpPlugin plugin) {
                plugin.SetApiToken(box.Password);
            }
        }
    }
}
