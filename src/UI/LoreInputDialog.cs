using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;

namespace LoreVS.UI
{
    /// <summary>
    /// Minimal themed modal input dialog (prompt label + single text box + OK/Cancel).
    /// Used to collect a commit message and to confirm/edit the repository URL when
    /// onboarding. Built in code to avoid a separate XAML compile unit.
    /// </summary>
    public sealed class LoreInputDialog : DialogWindow
    {
        private readonly TextBox _textBox;

        public LoreInputDialog(string title, string prompt, string initialValue, bool multiline = false)
        {
            Title = title ?? "Lore";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            HasMaximizeButton = false;
            HasMinimizeButton = false;

            var root = new StackPanel { Margin = new Thickness(12) };

            root.Children.Add(new TextBlock
            {
                Text = prompt ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });

            _textBox = new TextBox
            {
                Text = initialValue ?? string.Empty,
                Margin = new Thickness(0, 0, 0, 12),
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MinHeight = multiline ? 64 : 0,
                VerticalScrollBarVisibility = multiline
                    ? ScrollBarVisibility.Auto
                    : ScrollBarVisibility.Hidden,
            };
            root.Children.Add(_textBox);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 75, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (s, e) => { DialogResult = true; Close(); };

            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75 };
            cancel.Click += (s, e) => { DialogResult = false; Close(); };

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            Content = root;

            Loaded += (s, e) =>
            {
                _textBox.Focus();
                _textBox.SelectAll();
            };
        }

        /// <summary>The text entered by the user.</summary>
        public string Value => _textBox.Text;

        /// <summary>
        /// Shows the dialog modally and returns the entered text, or <see langword="null"/>
        /// if the user cancelled or left the box empty.
        /// </summary>
        public static string Prompt(string title, string prompt, string initialValue, bool multiline = false)
        {
            var dialog = new LoreInputDialog(title, prompt, initialValue, multiline);
            bool? result = dialog.ShowModal();
            if (result == true && !string.IsNullOrWhiteSpace(dialog.Value))
            {
                return dialog.Value.Trim();
            }

            return null;
        }
    }
}
