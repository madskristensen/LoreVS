using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace LoreVS.UI
{
    /// <summary>
    /// Applies the shared Visual Studio themed-dialog styling to a <see cref="DialogWindow"/> so its
    /// child controls (text boxes, buttons, labels) follow the IDE theme instead of rendering as
    /// default white WPF controls, and themes the native title bar to match the IDE theme.
    /// </summary>
    internal static class ThemedDialogHelper
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        /// <summary>
        /// Merges the VS themed-dialog default style dictionary into <paramref name="window"/>,
        /// paints its body with the themed window brushes, and themes the native title bar. Safe to
        /// call from a dialog constructor.
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

            window.SourceInitialized += (s, e) => ThemeTitleBar(window);
        }

        private static void ThemeTitleBar(DialogWindow window)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    return;
                }

                Color caption = ResolveColor(window, EnvironmentColors.ToolWindowBackgroundColorKey, SystemColors.WindowColor);
                Color text = ResolveColor(window, EnvironmentColors.ToolWindowTextColorKey, SystemColors.WindowTextColor);

                int dark = IsDark(caption) ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

                int captionRef = ToColorRef(caption);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionRef, sizeof(int));

                int textRef = ToColorRef(text);
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textRef, sizeof(int));
            }
            catch (Exception ex)
            {
                // Title-bar theming requires Windows 11; ignore on older OSes.
                ex.LogAsync().FireAndForget();
            }
        }

        private static Color ResolveColor(DialogWindow window, object key, Color fallback)
        {
            return window.TryFindResource(key) is Color color ? color : fallback;
        }

        private static bool IsDark(Color c)
        {
            double luminance = ((0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B)) / 255.0;
            return luminance < 0.5;
        }

        private static int ToColorRef(Color c)
        {
            return c.R | (c.G << 8) | (c.B << 16);
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}