using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;

namespace LoreVS.UI
{
    /// <summary>
    /// Themed modal dialog for cloning a Lore repository: a repository URL plus a destination path
    /// with a Browse button (mirrors the built-in Git clone dialog). Built in code to avoid a
    /// separate XAML compile unit, and styled with the shared VS themed-dialog resources so it
    /// matches the IDE theme.
    /// </summary>
    public sealed class LoreCloneDialog : DialogWindow
    {
        private readonly TextBox _urlBox;
        private readonly TextBox _pathBox;

        public LoreCloneDialog(string initialPath)
        {
            Title = "Clone a Lore repository";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            HasMaximizeButton = false;
            HasMinimizeButton = false;

            this.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogDefaultStylesKey);
            this.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            this.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var root = new Grid { Margin = new Thickness(12) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < 5; i++)
            {
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            TextBlock Label(string text) => new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4) };

            var urlLabel = Label("Lore repository URL (e.g. lore://127.0.0.1:41337/my-project):");
            Grid.SetRow(urlLabel, 0);
            Grid.SetColumnSpan(urlLabel, 2);
            root.Children.Add(urlLabel);

            _urlBox = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
            Grid.SetRow(_urlBox, 1);
            Grid.SetColumnSpan(_urlBox, 2);
            root.Children.Add(_urlBox);

            var pathLabel = Label("Local path (the repository folder is created inside it):");
            Grid.SetRow(pathLabel, 2);
            Grid.SetColumnSpan(pathLabel, 2);
            root.Children.Add(pathLabel);

            _pathBox = new TextBox { Text = initialPath ?? string.Empty, Margin = new Thickness(0, 0, 8, 12) };
            Grid.SetRow(_pathBox, 3);
            Grid.SetColumn(_pathBox, 0);
            root.Children.Add(_pathBox);

            var browse = new Button { Content = "...", MinWidth = 32, Margin = new Thickness(0, 0, 0, 12) };
            browse.Click += (s, e) => BrowseForPath();
            Grid.SetRow(browse, 3);
            Grid.SetColumn(browse, 1);
            root.Children.Add(browse);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetRow(buttons, 4);
            Grid.SetColumnSpan(buttons, 2);

            var ok = new Button { Content = "Clone", IsDefault = true, MinWidth = 75, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (s, e) => { DialogResult = true; Close(); };

            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75 };
            cancel.Click += (s, e) => { DialogResult = false; Close(); };

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            Content = root;

            Loaded += (s, e) =>
            {
                _urlBox.Focus();
                _urlBox.SelectAll();
            };
        }

        /// <summary>The repository URL the user entered, trimmed.</summary>
        public string RepositoryUrl => _urlBox.Text?.Trim() ?? string.Empty;

        /// <summary>The destination parent path the user entered, trimmed.</summary>
        public string DestinationPath => _pathBox.Text?.Trim() ?? string.Empty;

        private void BrowseForPath()
        {
            IntPtr owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            string? selected = FolderPicker.Pick(owner, "Choose a location to clone into", _pathBox.Text);
            if (!string.IsNullOrEmpty(selected))
            {
                _pathBox.Text = selected;
            }
        }
    }
}