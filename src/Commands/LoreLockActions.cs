using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.SourceControl;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    internal static class LoreLockActions
    {
        public static async Task ExecuteAsync(LoreVSPackage package, bool release)
        {
            LoreChangesControl? context = LoreChangesControl.ContextMenuOwner;
            string[]? contextPaths = context?.ContextPaths;
            string? contextRoot = context?.ViewModel.RepositoryRoot;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string operation = release ? "Unlock" : "Lock";
            try
            {
                ILoreClient client = package.Client;
                string[] paths = contextPaths ?? (await VS.Solutions.GetActiveItemsAsync())
                    .Select(item => item.FullPath ?? string.Empty).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
                string? root = contextRoot;
                await Task.Run(() =>
                {
                    if (paths.Length == 0)
                    {
                        throw new InvalidOperationException("Select at least one file to " + operation.ToLowerInvariant() + ".");
                    }
                    foreach (string path in paths)
                    {
                        string? fileRoot = client.FindRepositoryRoot(path);
                        root ??= fileRoot;
                        if (root == null || !string.Equals(root, fileRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException("Select files from a single Lore repository.");
                        }
                    }
                    paths = ValidateSelection(root!, paths);
                });

                await VS.StatusBar.ShowMessageAsync(operation + "ing Lore files...");
                LoreCommandResult result = await LoreAuthFlow.ExecuteAsync(client, root!, string.Empty,
                    () => release ? client.ReleaseLock(root!, paths) : client.AcquireLock(root!, paths));
                await LoreLog.WriteCommandAsync(release ? "lock release" : "lock acquire", result.CombinedText);
                if (!result.Success)
                {
                    await VS.MessageBox.ShowErrorAsync("Lore", operation + " failed:\n\n" + result.CombinedText);
                }
                else
                {
                    await VS.StatusBar.ShowMessageAsync(release ? "Lore files unlocked." : "Lore files locked.");
                }

                if (LoreChangesControl.Current is LoreChangesControl control)
                {
                    await control.ViewModel.RefreshLocksAsync();
                }
                package.SccService?.RefreshAllGlyphs();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Lore", operation + " failed:\n\n" + ex.Message);
            }
        }

        internal static string[] ValidateSelection(string root, IEnumerable<string> paths)
        {
            string prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string[] files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0 || files.Any(path => !Path.IsPathRooted(path) ||
                !Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Directory.Exists(path)))
            {
                throw new InvalidOperationException("Select files inside a single Lore repository, not folders.");
            }
            return files;
        }
    }
}