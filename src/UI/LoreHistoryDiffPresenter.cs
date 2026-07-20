using System;
using System.IO;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.UI
{
    /// <summary>
    /// Opens a read-only diff of a file changed by a revision against that file's state in the
    /// revision's direct parent, using the native Visual Studio difference viewer. Both sides are
    /// materialized to temporary files via <see cref="ILoreClient.WriteFileAtRevision"/>; the working
    /// tree is never touched. When the revision has no parent (a root revision) or the file was added,
    /// only the revision version is shown; a deleted file shows only the parent version.
    /// </summary>
    internal static class LoreHistoryDiffPresenter
    {
        private const string TempPrefix = "lore-hist-";

        /// <summary>
        /// Shows the diff for <paramref name="item"/> between <paramref name="revisionHash"/> and
        /// <paramref name="parentHash"/>.
        /// </summary>
        public static async Task ShowAsync(
            ILoreClient client,
            string repositoryRoot,
            LoreChangeItem item,
            string revisionHash,
            string parentHash)
        {
            if (client == null || item == null || string.IsNullOrEmpty(revisionHash))
            {
                return;
            }

            SweepStaleFiles();

            try
            {
                bool hasParent = !string.IsNullOrEmpty(parentHash);

                // Added file (or a root revision with no parent to compare against): show the file as
                // it exists in this revision only.
                if (item.Status == LoreFileStatus.Added || !hasParent)
                {
                    string? added = await Task.Run(() => Materialize(client, repositoryRoot, item.FullPath, revisionHash));
                    await OpenDocumentAsync(added ?? item.FullPath);
                    return;
                }

                // Deleted file: it does not exist in this revision, so show its last content from the
                // parent.
                if (item.Status == LoreFileStatus.Deleted)
                {
                    string? removed = await Task.Run(() => Materialize(client, repositoryRoot, item.FullPath, parentHash));
                    await OpenDocumentAsync(removed);
                    return;
                }

                string? parentPath = null;
                string? revisionPath = null;
                await Task.Run(() =>
                {
                    parentPath = Materialize(client, repositoryRoot, item.FullPath, parentHash);
                    revisionPath = Materialize(client, repositoryRoot, item.FullPath, revisionHash);
                });

                if (parentPath == null || revisionPath == null)
                {
                    // A side could not be produced (worker unavailable or unsupported); fall back to
                    // opening whichever version exists so the gesture still does something useful.
                    await OpenDocumentAsync(revisionPath ?? parentPath ?? item.FullPath);
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Use the IDE's built-in Tools.DiffFiles command (via DTE) to open the comparison
                // window, matching LoreDiffPresenter. Arguments are "<left> <right> [<leftLabel>]
                // [<rightLabel>]", space-delimited and quoted, with the parent on the left.
                string shortRev = Shorten(revisionHash);
                string shortParent = Shorten(parentHash);
                string arguments = Quote(parentPath) + " " + Quote(revisionPath) + " " +
                    Quote(item.FileName + " (" + shortParent + ")") + " " +
                    Quote(item.FileName + " (" + shortRev + ")");

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
        /// Writes the content of <paramref name="fullPath"/> at <paramref name="revision"/> to a temp
        /// file and returns its path, or <see langword="null"/> when it could not be produced (for
        /// example the file did not exist at that revision).
        /// </summary>
        private static string? Materialize(ILoreClient client, string repositoryRoot, string fullPath, string revision)
        {
            string tempPath = Path.Combine(
                Path.GetTempPath(),
                TempPrefix + Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(fullPath));

            // Pass the absolute working path; the SDK resolves the in-repo file from it.
            LoreCommandResult result = client.WriteFileAtRevision(repositoryRoot, fullPath, revision, tempPath);
            if (result.Success && File.Exists(tempPath))
            {
                return tempPath;
            }

            return null;
        }

        // Temp files created for diffing accumulate because the IDE owns the diff window lifetime;
        // best-effort sweep prior diffs older than a day so the temp folder does not grow unbounded.
        private static void SweepStaleFiles()
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(Path.GetTempPath(), TempPrefix + "*"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
                        {
                            File.Delete(file);
                        }
                    }
                    catch { /* a file still open in a diff window stays; swept next time */ }
                }
            }
            catch (Exception ex)
            {
                ex.LogAsync().FireAndForget();
            }
        }

        private static string Shorten(string hash) =>
            string.IsNullOrEmpty(hash) || hash.Length <= 8 ? hash ?? string.Empty : hash.Substring(0, 8);

        // Quotes a path/label so it survives the space-delimited Tools.DiffFiles argument string,
        // escaping any embedded double quotes.
        private static string Quote(string value) =>
            "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

        private static async Task OpenDocumentAsync(string? path)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                await VS.Documents.OpenAsync(path!);
            }
        }
    }
}
