using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    /// <summary>
    /// "Undo Changes" command. Discards working-tree edits for the selected modified
    /// files, resetting them to the current revision (lore file reset). Only shown when
    /// the solution is under Lore source control.
    /// </summary>
    [Command(PackageIds.UndoChangesCommand)]
    internal sealed class UndoChangesCommand : BaseCommand<UndoChangesCommand>
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

            ILoreClient client = package.Client;
            IEnumerable<SolutionItem> items = await VS.Solutions.GetActiveItemsAsync();

            string? root = null;
            var paths = new List<string>();
            foreach (SolutionItem item in items)
            {
                string fullPath = item?.FullPath ?? string.Empty;
                if (string.IsNullOrEmpty(fullPath))
                {
                    continue;
                }

                root = root ?? client.FindRepositoryRoot(fullPath);
                if (root == null || !fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                paths.Add(fullPath.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/'));
            }

            if (root == null || paths.Count == 0)
            {
                return;
            }

            string prompt = paths.Count == 1
                ? $"Undo changes to '{paths[0]}'? This cannot be undone."
                : $"Undo changes to {paths.Count} files? This cannot be undone.";
            if (!await VS.MessageBox.ShowConfirmAsync("Lore", prompt))
            {
                return;
            }

            await VS.StatusBar.ShowMessageAsync("Undoing changes...");
            LoreCommandResult result = await Task.Run(() => client.ResetFiles(root, paths.ToArray()));
            await LoreLog.WriteCommandAsync("file reset", result.CombinedText);

            if (!result.Success)
            {
                await VS.MessageBox.ShowErrorAsync("Lore", "Failed to undo changes:\n\n" + result.CombinedText);
                return;
            }

            package.SccService?.RefreshAllGlyphs();
            await VS.StatusBar.ShowMessageAsync("Changes undone.");
        }
    }
}