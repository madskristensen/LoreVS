using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;

namespace LoreVS.UI
{
    /// <summary>
    /// Themed modal "Create a new branch" dialog modeled on the built-in Git dialog: a required
    /// branch-name box, a read-only "Based on" field (Lore always branches from the current
    /// revision, so the source branch is informational), and a "Checkout branch" toggle.
    /// </summary>
    public sealed class LoreCreateBranchDialog : DialogWindow
    {
        private readonly TextBox _nameBox;
        private readonly CheckBox _checkout;
        private readonly Button _create;

        public LoreCreateBranchDialog(string currentBranch)
        {
            Title = "Create a new branch";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            HasMaximizeButton = false;
            HasMinimizeButton = false;

            ThemedDialogHelper.Apply(this);

            var grid = new Grid { Margin = new Thickness(12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 4; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            // Row 0: Branch name.
            var nameLabel = new TextBlock
            {
                Text = "Branch name:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 8),
            };
            Grid.SetRow(nameLabel, 0);
            Grid.SetColumn(nameLabel, 0);
            grid.Children.Add(nameLabel);

            _nameBox = new TextBox
            {
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(4, 4, 4, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
                MinHeight = 26,
            };
            _nameBox.TextChanged += (s, e) => UpdateCreateEnabled();
            Grid.SetRow(_nameBox, 0);
            Grid.SetColumn(_nameBox, 1);
            grid.Children.Add(_nameBox);

            // Row 1: Based on (read-only; Lore branches from the current revision).
            var basedOnLabel = new TextBlock
            {
                Text = "Based on:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 8),
            };
            Grid.SetRow(basedOnLabel, 1);
            Grid.SetColumn(basedOnLabel, 0);
            grid.Children.Add(basedOnLabel);

            var basedOn = new TextBox
            {
                Text = string.IsNullOrEmpty(currentBranch) ? "(current revision)" : currentBranch,
                IsReadOnly = true,
                IsEnabled = false,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(4, 4, 4, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
                MinHeight = 26,
                ToolTip = "Lore creates the branch from the current revision.",
            };
            Grid.SetRow(basedOn, 1);
            Grid.SetColumn(basedOn, 1);
            grid.Children.Add(basedOn);

            // Row 2: Checkout branch.
            _checkout = new CheckBox
            {
                Content = "Checkout branch",
                IsChecked = true,
                Margin = new Thickness(0, 4, 0, 12),
            };
            Grid.SetRow(_checkout, 2);
            Grid.SetColumn(_checkout, 1);
            grid.Children.Add(_checkout);

            // Row 3: Buttons.
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            _create = new Button
            {
                Content = "Create",
                IsDefault = true,
                IsEnabled = false,
                MinWidth = 75,
                Margin = new Thickness(0, 0, 8, 0),
            };
            _create.Click += (s, e) => { DialogResult = true; Close(); };

            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75 };
            cancel.Click += (s, e) => { DialogResult = false; Close(); };

            buttons.Children.Add(_create);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 3);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            Content = grid;

            Loaded += (s, e) => _nameBox.Focus();
        }

        /// <summary>The branch name entered by the user (trimmed).</summary>
        public string BranchName => _nameBox.Text.Trim();

        /// <summary>True when the working tree should be switched to the new branch.</summary>
        public bool Checkout => _checkout.IsChecked == true;

        private void UpdateCreateEnabled() =>
            _create.IsEnabled = !string.IsNullOrWhiteSpace(_nameBox.Text);
    }
}
