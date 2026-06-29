using System;
using System.IO;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.UI
{
    /// <summary>
    /// Opens a diff for a changed file in the native Visual Studio difference viewer. The committed
    /// (base) version of the file is materialized to a temporary file via the Lore worker
    /// (<see cref="ILoreClient.WriteFileAtRevision"/>) and compared against the working copy on disk.
    /// </summary>
    internal static class LoreDiffPresenter
    {
        /// <summary>
        /// Shows a diff for <paramref name="item"/>. Added files open directly (no base), deleted
        /// files show their last committed content, and everything else is shown as base vs working.
        /// </summary>
        public static async Task ShowAsync(ILoreClient client, string repositoryRoot, LoreChangeItem item)
        {
            if (client == null || item == null)
            {
                return;
            }

            try
            {
                if (item.Status == LoreFileStatus.Added)
                {
                    await OpenDocumentAsync(item.FullPath);
                    return;
                }

                string basePath = await Task.Run(() => MaterializeBase(client, repositoryRoot, item));

                if (basePath == null)
                {
                    // The base could not be produced (worker unavailable or unsupported); fall back
                    // to simply opening the working file so the gesture still does something useful.
                    await OpenDocumentAsync(item.FullPath);
                    return;
                }

                if (item.Status == LoreFileStatus.Deleted || !File.Exists(item.FullPath))
                {
                    await OpenDocumentAsync(basePath);
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Use the IDE's built-in Tools.DiffFiles command (via DTE) to open the comparison
                // window, since IVsDifferenceService.OpenComparisonWindow2 silently no-ops here. The
                // arguments are "<left> <right> [<leftLabel>] [<rightLabel>]", space-delimited and quoted.
                string arguments = Quote(basePath) + " " + Quote(item.FullPath) + " " +
                    Quote(item.FileName + " (committed)") + " " + Quote(item.FileName + " (working)");

                EnvDTE.DTE dte = await VS.GetRequiredServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
                dte.ExecuteCommand("Tools.DiffFiles", arguments);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Lore", "Could not open the diff:\n\n" + ex.Message);
            }
        }

        /// <summary>
        /// Writes the committed version of <paramref name="item"/> to a temp file and returns its
        /// path, or <see langword="null"/> when the base could not be produced.
        /// </summary>
        private static string MaterializeBase(ILoreClient client, string repositoryRoot, LoreChangeItem item)
        {
            string tempPath = Path.Combine(
                Path.GetTempPath(),
                "lore-" + Guid.NewGuid().ToString("N") + "-" + item.FileName);

            // Materialize the committed base at the local tip revision: an empty revision resolves
            // to the working copy (identical to the file on disk), which would show no differences.
            LoreRepositoryInfo info = client.GetRepositoryInfo(repositoryRoot);
            string revision = info?.LocalRevisionHash ?? string.Empty;

            // Pass the absolute working path; the SDK resolves the in-repo file from it.
            LoreCommandResult result = client.WriteFileAtRevision(repositoryRoot, item.FullPath, revision, tempPath);
            if (result.Success && File.Exists(tempPath))
            {
                return tempPath;
            }

            return null;
        }

        // Quotes a path/label so it survives the space-delimited Tools.DiffFiles argument string,
        // escaping any embedded double quotes.
        private static string Quote(string value) =>
            "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

        private static async Task OpenDocumentAsync(string path)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                await VS.Documents.OpenAsync(path);
            }
        }
    }
}
