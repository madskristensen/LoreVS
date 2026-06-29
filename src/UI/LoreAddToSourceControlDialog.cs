using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;

namespace LoreVS.UI
{
    /// <summary>
    /// Themed modal dialog for "Add to Lore Source Control" that presents a clear choice between
    /// creating a fully local (offline) repository and creating the repository on a Lore server.
    /// Local is the default so onboarding an existing solution never binds a remote unless the user
    /// explicitly opts in. Built in code to avoid a separate XAML compile unit and styled with the
    /// shared VS themed-dialog resources so it matches the IDE theme.
    /// </summary>
    public sealed class LoreAddToSourceControlDialog : DialogWindow
    {
        private readonly RadioButton _localRadio;
        private readonly RadioButton _serverRadio;
        private readonly TextBox _urlBox;

        public LoreAddToSourceControlDialog(string repositoryName, string defaultServerUrl)
        {
            Title = "Add to Lore Source Control";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            HasMaximizeButton = false;
            HasMinimizeButton = false;

            ThemedDialogHelper.Apply(this);

            var root = new StackPanel { Margin = new Thickness(14) };

            root.Children.Add(new TextBlock
            {
                Text = $"Create a Lore repository for '{repositoryName}':",
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
            });

            _localRadio = new RadioButton
            {
                Content = "Local repository (offline, no server)",
                IsChecked = true,
                Margin = new Thickness(0, 0, 0, 2),
            };
            root.Children.Add(_localRadio);

            root.Children.Add(new TextBlock
            {
                Text = "All content stays on this machine. You can branch, commit, and switch branches " +
                    "without a server. You can publish to a server later.",
                Margin = new Thickness(20, 0, 0, 12),
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
            });

            _serverRadio = new RadioButton
            {
                Content = "Create on a Lore server",
                Margin = new Thickness(0, 0, 0, 6),
            };
            root.Children.Add(_serverRadio);

            _urlBox = new TextBox
            {
                Text = defaultServerUrl ?? string.Empty,
                Padding = new Thickness(4, 4, 4, 4),
                MinHeight = 26,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 0, 4),
                IsEnabled = false,
            };
            root.Children.Add(_urlBox);

            root.Children.Add(new TextBlock
            {
                Text = "The server must be reachable. Content is fetched from the server on demand, so " +
                    "switching branches may require the server to be online.",
                Margin = new Thickness(20, 0, 0, 16),
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
            });

            _serverRadio.Checked += (s, e) =>
            {
                _urlBox.IsEnabled = true;
                _urlBox.Focus();
                _urlBox.SelectAll();
            };
            _localRadio.Checked += (s, e) => _urlBox.IsEnabled = false;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var ok = new Button { Content = "Create", IsDefault = true, MinWidth = 75, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (s, e) => { DialogResult = true; Close(); };

            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75 };
            cancel.Click += (s, e) => { DialogResult = false; Close(); };

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            Content = root;
        }

        /// <summary>True when the user chose to create a fully local (offline) repository.</summary>
        public bool IsLocal => _localRadio.IsChecked == true;

        /// <summary>The Lore server URL the user entered, trimmed (only meaningful when not local).</summary>
        public string ServerUrl => _urlBox.Text?.Trim() ?? string.Empty;
    }
}
