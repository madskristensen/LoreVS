using System;
using System.IO;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace LoreVS.UI
{
    /// <summary>
    /// Opens a diff for a changed file in the native Visual Studio difference viewer. The committed
    /// (base) version of the file is materialized to a temporary file via the Lore worker
    /// (<see cref="ILoreClient.WriteFileAtRevision"/>) and compared against the working copy on disk.
    /// </summary>
    internal static class LoreDiffPresenter
    {
        // __VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary - VS deletes the left file when the
        // comparison window closes, so the materialized base file is cleaned up automatically.
        private const uint LeftFileIsTemporary = 0x00000002;

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
                IVsDifferenceService diffService =
                    await VS.GetServiceAsync<SVsDifferenceService, IVsDifferenceService>();

                string caption = item.FileName + " (Lore diff)";
                diffService.OpenComparisonWindow2(
                    leftFileMoniker: basePath,
                    rightFileMoniker: item.FullPath,
                    caption: caption,
                    Tooltip: item.RelativePath,
                    leftLabel: item.FileName + " (committed)",
                    rightLabel: item.FileName + " (working)",
                    inlineLabel: null,
                    roles: null,
                    grfDiffOptions: LeftFileIsTemporary);
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

            // Lore stores repository-relative paths with forward slashes.
            string lorePath = item.RelativePath.Replace('\\', '/');

            LoreCommandResult result = client.WriteFileAtRevision(repositoryRoot, lorePath, string.Empty, tempPath);
            if (result.Success && File.Exists(tempPath))
            {
                return tempPath;
            }

            return null;
        }

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
