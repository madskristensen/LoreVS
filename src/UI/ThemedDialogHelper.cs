using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;

namespace LoreVS.UI
{
    /// <summary>
    /// Applies the shared Visual Studio themed-dialog styling to a <see cref="DialogWindow"/> so its
    /// child controls (text boxes, buttons, labels) follow the IDE theme instead of rendering as
    /// default white WPF controls.
    /// </summary>
    internal static class ThemedDialogHelper
    {
        /// <summary>
        /// Merges the VS themed-dialog default style dictionary into <paramref name="window"/> and
        /// paints its body with the themed window brushes. Safe to call from a dialog constructor.
        /// </summary>
        public static void Apply(DialogWindow window)
        {
            if (window == null)
            {
                return;
            }

            try
            {
                // ThemedDialogDefaultStylesKey resolves to a ResourceDictionary of implicit styles;
                // it must be merged into Resources, not assigned to the Style property.
                if (window.TryFindResource(VsResourceKeys.ThemedDialogDefaultStylesKey) is ResourceDictionary styles)
                {
                    window.Resources.MergedDictionaries.Add(styles);
                }
            }
            catch (Exception ex)
            {
                ex.LogAsync().FireAndForget();
            }

            window.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            window.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        }
    }
}