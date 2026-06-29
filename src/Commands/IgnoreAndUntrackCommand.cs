using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    /// <summary>
    /// "Ignore and Untrack Item" command. Adds the selected solution items to the
    /// repository's .loreignore so they are no longer tracked, mirroring the Git
    /// provider's equivalent command. Only shown when the solution is controlled.
    /// </summary>
    [Command(PackageIds.IgnoreAndUntrackCommand)]
    internal sealed class IgnoreAndUntrackCommand : BaseCommand<IgnoreAndUntrackCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            Command.Visible = Command.Enabled = (Package as LoreVSPackage)?.IsSolutionControlled == true;
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!(Package is LoreVSPackage package))
            {
                return;
            }

            IEnumerable<SolutionItem> items = await VS.Solutions.GetActiveItemsAsync();
            ILoreClient client = package.Client;
            var changed = new HashSet<string>();

            foreach (SolutionItem item in items)
            {
                string fullPath = item?.FullPath ?? string.Empty;
                if (string.IsNullOrEmpty(fullPath))
                {
                    continue;
                }

                string? root = client.FindRepositoryRoot(fullPath);
                if (root == null)
                {
                    continue;
                }

                string? pattern = ToIgnorePattern(root, fullPath);
                if (pattern == null)
                {
                    continue;
                }

                if (LoreIgnoreFile.AddPatterns(root, pattern))
                {
                    changed.Add(root);
                    await LoreLog.WriteLineAsync($"Ignored '{pattern}' in {LoreIgnoreFile.FileName}.");
                }
            }

            if (changed.Count > 0)
            {
                package.SccService?.RefreshAllGlyphs();
                await VS.StatusBar.ShowMessageAsync("Added to .loreignore.");
            }
        }

        /// <summary>
        /// Builds a forward-slash, repo-relative ignore pattern for <paramref name="fullPath"/>,
        /// appending a trailing slash for directories. Returns null when outside the root.
        /// </summary>
        private static string? ToIgnorePattern(string root, string fullPath)
        {
            string relative = fullPath.Substring(root.Length).TrimStart('\\', '/');
            if (string.IsNullOrEmpty(relative))
            {
                return null;
            }

            relative = relative.Replace('\\', '/');
            if (Directory.Exists(fullPath) && !relative.EndsWith("/"))
            {
                relative += "/";
            }

            return relative;
        }
    }
}