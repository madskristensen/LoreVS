using System.Threading.Tasks;

namespace LoreVS
{
    /// <summary>
    /// Writes Lore CLI command output to a dedicated "Lore" Output window pane so the
    /// user can see exactly what ran when onboarding or committing.
    /// </summary>
    internal static class LoreLog
    {
        private static OutputWindowPane _pane;

        public static async Task WriteLineAsync(string text)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _pane ??= await VS.Windows.CreateOutputWindowPaneAsync("Lore");
            await _pane.WriteLineAsync(text ?? string.Empty);
        }

        public static async Task WriteCommandAsync(string command, string output)
        {
            await WriteLineAsync("> lore " + command);
            if (!string.IsNullOrWhiteSpace(output))
            {
                await WriteLineAsync(output.TrimEnd());
            }

            await WriteLineAsync(string.Empty);
        }
    }
}
