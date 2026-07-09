using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace Astrovault
{
    /// <summary>
    /// Code-behind for Options.xaml.
    /// Exports ResourceDictionary for N.I.N.A. to discover the settings UI.
    /// Bridges PasswordBox (which doesn't support binding) to the ViewModel,
    /// handles show/hide toggle, and delegates button clicks to the plugin.
    /// </summary>
    [Export(typeof(ResourceDictionary))]
    public partial class Options : ResourceDictionary
    {
        public Options()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Bridges PasswordBox.Password to plugin.ApiKeyInput since WPF
        /// PasswordBox doesn't support data binding for security reasons.
        /// </summary>
        private void OnApiKeyPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb && pb.DataContext is AstrovaultPlugin plugin)
            {
                plugin.ApiKeyInput = pb.Password;
            }
        }

        /// <summary>
        /// Toggles between masked (PasswordBox) and revealed (TextBox) views.
        /// Syncs the text between the two controls and updates button label.
        /// </summary>
        private void OnToggleApiKeyVisibility(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not AstrovaultPlugin plugin)
            {
                return;
            }

            // Find the controls by walking the visual tree from the button's parent grid
            var parent = button.Parent as System.Windows.Controls.Grid;
            if (parent == null) return;

            PasswordBox passwordBox = null;
            TextBox textBox = null;
            foreach (UIElement child in parent.Children)
            {
                if (child is PasswordBox pb) passwordBox = pb;
                if (child is TextBox tb && tb.Name == "ApiKeyTextBox") textBox = tb;
            }

            if (passwordBox == null || textBox == null) return;

            plugin.IsApiKeyRevealed = !plugin.IsApiKeyRevealed;

            if (plugin.IsApiKeyRevealed)
            {
                // Switching to revealed: sync PasswordBox -> TextBox
                textBox.Text = passwordBox.Password;
                passwordBox.Visibility = Visibility.Collapsed;
                textBox.Visibility = Visibility.Visible;
                textBox.Focus();
                button.Content = "Hide";
            }
            else
            {
                // Switching to masked: sync TextBox -> PasswordBox
                passwordBox.Password = textBox.Text;
                textBox.Visibility = Visibility.Collapsed;
                passwordBox.Visibility = Visibility.Visible;
                passwordBox.Focus();
                button.Content = "Show";
            }
        }

        /// <summary>
        /// Handles "Test Connection" button click.
        /// Calls TestConnectionAsync on the plugin and clears the PasswordBox on BOTH
        /// the success and failure paths. The ViewModel clears ApiKeyInput and resets the
        /// reveal state on every connect branch (plan 01); this only syncs the un-bindable
        /// PasswordBox bridge so a typed key never lingers after a connect attempt (P19-06).
        /// </summary>
        private async void OnTestConnectionClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not AstrovaultPlugin plugin)
            {
                return;
            }

            await plugin.TestConnectionAsync();

            // The ViewModel cleared ApiKeyInput on success AND failure (plan 01); sync the
            // PasswordBox bridge to match regardless of the outcome so the key is never retained.
            ClearApiKeyPasswordBox(button);
        }

        /// <summary>
        /// Clears the API-key PasswordBox by walking the visual tree from one of the action
        /// buttons (Test Connection / Disconnect) up to the shared parent StackPanel, then into
        /// the Grid that holds the PasswordBox. Extracted into a single helper (LOW review item 7)
        /// so the visual-tree walk lives in exactly ONE place and is reused by every connect path.
        /// PasswordBox cannot be data-bound, so this code-behind sync is the only way to clear it.
        /// </summary>
        private void ClearApiKeyPasswordBox(Button button)
        {
            // Button row StackPanel -> outer (main) StackPanel that also holds the API-key Grid.
            var parent = button.Parent as StackPanel;
            var mainPanel = parent?.Parent as StackPanel;
            if (mainPanel == null)
            {
                return;
            }

            foreach (var child in mainPanel.Children)
            {
                if (child is System.Windows.Controls.Grid grid)
                {
                    foreach (UIElement gridChild in grid.Children)
                    {
                        if (gridChild is PasswordBox pb)
                        {
                            pb.Password = string.Empty;
                            break;
                        }
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// Handles "Disconnect" button click.
        /// Prompts the user (Yes/No, default Yes) whether to also turn off automatic upload, then
        /// clears the stored API key and resets to NotConfigured status. Passing the consent choice
        /// to DisconnectAsync(bool) closes the silent auto-resume gap (P19-05): choosing Yes disables
        /// auto-upload before the key is cleared; choosing No disconnects but leaves it enabled.
        ///
        /// The entire body is wrapped in try/catch because this is an `async void` UI handler -- an
        /// unobserved exception would otherwise fault NINA's UI thread (MAINT-05); failures are
        /// surfaced via Notification.ShowError instead.
        /// </summary>
        private async void OnDisconnectClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.DataContext is AstrovaultPlugin plugin)
                {
                    // MyMessageBox is the NINA-themed modal; it is synchronous and self-dispatches
                    // to the UI thread, returning the chosen result. Default = Yes (turn upload off).
                    var choice = NINA.Core.MyMessageBox.MyMessageBox.Show(
                        "Also turn off automatic upload? Choosing No disconnects but leaves auto-upload enabled.",
                        "Disconnect Astrovault",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxResult.Yes);

                    bool alsoDisableUpload = choice == System.Windows.MessageBoxResult.Yes;
                    await plugin.DisconnectAsync(alsoDisableUpload);
                }
            }
            catch (System.Exception ex)
            {
                NINA.Core.Utility.Notification.Notification.ShowError($"Disconnect failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Opens the hyperlink URI in the default browser.
        /// Standard NINA pattern from AboutNINAView.xaml.cs.
        /// </summary>
        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        /// <summary>
        /// Handles "Retry All" button click.
        /// Retries all failed upload jobs (batch operation).
        /// </summary>
        private async void OnRetryAllFailedClicked(object sender, RoutedEventArgs e)
        {
            // WR-04: an async void handler must not throw to NINA's UI thread (mirrors OnDisconnectClicked).
            try
            {
                if (sender is not Button button || button.DataContext is not AstrovaultPlugin plugin)
                    return;
                await plugin.RetryAllFailedAsync();
            }
            catch (System.Exception ex)
            {
                NINA.Core.Utility.Notification.Notification.ShowError($"Retry all failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles "Dismiss All" button click.
        /// Clears all failed jobs from the visible queue without deleting source files.
        /// </summary>
        private async void OnDismissAllFailedClicked(object sender, RoutedEventArgs e)
        {
            // WR-04: an async void handler must not throw to NINA's UI thread (mirrors OnDisconnectClicked).
            try
            {
                if (sender is not Button button || button.DataContext is not AstrovaultPlugin plugin)
                    return;
                await plugin.DismissAllFailedAsync();
            }
            catch (System.Exception ex)
            {
                NINA.Core.Utility.Notification.Notification.ShowError($"Dismiss all failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the "Reset Counts" button click.
        /// Zeroes the historical counters (Total uploaded + Failed); does not delete files or affect
        /// pending / in-progress uploads.
        /// </summary>
        private async void OnResetCountsClicked(object sender, RoutedEventArgs e)
        {
            // WR-04: an async void handler must not throw to NINA's UI thread (mirrors OnDisconnectClicked).
            try
            {
                if (sender is not Button button || button.DataContext is not AstrovaultPlugin plugin)
                    return;
                await plugin.ResetCountsAsync();
            }
            catch (System.Exception ex)
            {
                NINA.Core.Utility.Notification.Notification.ShowError($"Reset counts failed: {ex.Message}");
            }
        }
    }
}
