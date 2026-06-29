using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.Merge.VsPackage;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace LoreVS.UI
{
    /// <summary>
    /// Drives Visual Studio's native 3-way merge window to resolve the conflicts a Lore branch merge
    /// leaves behind. Each conflicted working file is written by the SDK with diff3 markers
    /// (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; ours</c> / <c>||||||| original</c> / <c>=======</c> /
    /// <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt; theirs</c>); those sections are split back into full base/ours/theirs
    /// files, fed to <see cref="IModernMergeService"/>, and the accepted result is written back over the
    /// working file. Once every file is resolved the merge is committed via
    /// <see cref="ILoreClient.ResolveMerge"/>; if the user cancels they are offered an abort.
    /// </summary>
    internal static class LoreMergePresenter
    {
        private const string TempPrefix = "lore-merge-";

        /// <summary>The Visual Studio merge package that provides <see cref="SModernMergeService"/>.</summary>
        private static readonly Guid MergePackageId = new Guid("BF0F8831-2CA2-4057-B64E-FF1CED3CEFA2");

        /// <summary>
        /// Resolves the conflicted files from an in-progress merge of <paramref name="sourceBranch"/>.
        /// Returns <see langword="true"/> when every conflict was resolved and the merge committed,
        /// <see langword="false"/> when the user cancelled (and the merge was aborted or left pending).
        /// </summary>
        public static async Task<bool> ResolveAsync(
            ILoreClient client,
            string repositoryRoot,
            string sourceBranch,
            IReadOnlyList<string> conflictPaths,
            string identity)
        {
            if (client == null || conflictPaths == null || conflictPaths.Count == 0)
            {
                return false;
            }

            SweepStaleTempFiles();

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                IModernMergeService? mergeService = await GetMergeServiceAsync();
                if (mergeService == null)
                {
                    await VS.MessageBox.ShowErrorAsync("Lore",
                        "The Visual Studio merge tool is unavailable, so the conflicts could not be opened. " +
                        "Resolve them with the Lore CLI, or abort the merge.");
                    return false;
                }

                var windows = new List<Task<bool>>(conflictPaths.Count);
                foreach (string conflictPath in conflictPaths)
                {
                    windows.Add(OpenMergeWindowAsync(mergeService, conflictPath, sourceBranch));
                }

                bool[] outcomes = await Task.WhenAll(windows);

                bool allAccepted = true;
                foreach (bool accepted in outcomes)
                {
                    allAccepted &= accepted;
                }

                if (allAccepted)
                {
                    string message = $"Merge branch '{sourceBranch}'";
                    var paths = new string[conflictPaths.Count];
                    for (int i = 0; i < conflictPaths.Count; i++)
                    {
                        paths[i] = conflictPaths[i];
                    }

                    LoreCommandResult result = await Task.Run(() => client.ResolveMerge(repositoryRoot, paths, message, identity));
                    await LoreLog.WriteCommandAsync($"branch merge resolve \"{sourceBranch}\"", result.CombinedText);

                    if (!result.Success)
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        await VS.MessageBox.ShowErrorAsync("Lore", "The merge could not be committed:\n\n" + result.CombinedText);
                        return false;
                    }

                    return true;
                }

                // The user closed at least one merge window without accepting. Offer to abort so the
                // working tree is restored; otherwise the merge is left in progress for the CLI.
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                bool abort = await VS.MessageBox.ShowConfirmAsync("Lore",
                    "The merge was not completed. Abort it and restore the working tree?");
                if (abort)
                {
                    LoreCommandResult result = await Task.Run(() => client.AbortMerge(repositoryRoot));
                    await LoreLog.WriteCommandAsync("branch merge abort", result.CombinedText);
                }

                return false;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Lore", "Could not open the merge tool:\n\n" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Splits one conflicted file into temporary base/ours/theirs files, opens the merge window, and
        /// completes with whether the user accepted the merge (writing the result back when they did).
        /// </summary>
        private static Task<bool> OpenMergeWindowAsync(IModernMergeService mergeService, string conflictPath, string sourceBranch)
        {
            string fileName = Path.GetFileName(conflictPath);
            string oursPath = MakeTempPath(fileName, "ours");
            string basePath = MakeTempPath(fileName, "base");
            string theirsPath = MakeTempPath(fileName, "theirs");
            string resultPath = MakeTempPath(fileName, "result");

            SplitConflict(conflictPath, oursPath, basePath, theirsPath);

            // Seed the result with the local (ours) side so the merge tool opens with a sensible baseline.
            File.Copy(oursPath, resultPath, true);

            var tcs = new TaskCompletionSource<bool>();

            mergeService.OpenAndRegisterMergeWindow(
                fileName: fileName,
                leftFilePath: oursPath,
                rightFilePath: theirsPath,
                baseFilePath: basePath,
                resultFilePath: resultPath,
                leftFileTag: "Current",
                rightFileTag: "Incoming",
                baseFileTag: "Base",
                resultFileTag: "Result",
                leftFileTitle: fileName + " (current)",
                rightFileTitle: $"{fileName} (from '{sourceBranch}')",
                baseFileTitle: fileName + " (base)",
                resultFileTitle: fileName,
                callbackParam: null,
                onMergeComplete: (MergeResults r) =>
                {
                    try
                    {
                        if (r.MergeAccepted)
                        {
                            File.Copy(resultPath, conflictPath, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        ex.LogAsync().FireAndForget();
                    }
                    finally
                    {
                        tcs.TrySetResult(r.MergeAccepted);
                    }
                });

            return tcs.Task;
        }

        /// <summary>
        /// Reconstructs the three full-file versions (<paramref name="oursPath"/>,
        /// <paramref name="basePath"/>, <paramref name="theirsPath"/>) from the diff3 conflict markers in
        /// <paramref name="conflictPath"/>. Lines outside conflict regions are common to all three.
        /// </summary>
        private static void SplitConflict(string conflictPath, string oursPath, string basePath, string theirsPath)
        {
            var ours = new StringBuilder();
            var theBase = new StringBuilder();
            var theirs = new StringBuilder();

            // ' ' = common (outside conflict), 'o' = ours, 'b' = base/original, 't' = theirs.
            char section = ' ';

            foreach (string line in File.ReadLines(conflictPath))
            {
                if (line.StartsWith("<<<<<<<", StringComparison.Ordinal))
                {
                    section = 'o';
                    continue;
                }

                if (section != ' ' && line.StartsWith("|||||||", StringComparison.Ordinal))
                {
                    section = 'b';
                    continue;
                }

                if (section != ' ' && line.StartsWith("=======", StringComparison.Ordinal))
                {
                    section = 't';
                    continue;
                }

                if (section != ' ' && line.StartsWith(">>>>>>>", StringComparison.Ordinal))
                {
                    section = ' ';
                    continue;
                }

                switch (section)
                {
                    case 'o':
                        ours.AppendLine(line);
                        break;
                    case 'b':
                        theBase.AppendLine(line);
                        break;
                    case 't':
                        theirs.AppendLine(line);
                        break;
                    default:
                        ours.AppendLine(line);
                        theBase.AppendLine(line);
                        theirs.AppendLine(line);
                        break;
                }
            }

            File.WriteAllText(oursPath, ours.ToString());
            File.WriteAllText(basePath, theBase.ToString());
            File.WriteAllText(theirsPath, theirs.ToString());
        }

        private static async Task<IModernMergeService?> GetMergeServiceAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            IVsShell shell = await VS.Services.GetShellAsync();
            Guid packageId = MergePackageId;
            shell.LoadPackage(ref packageId, out _);

            return await VS.GetServiceAsync<SModernMergeService, IModernMergeService>();
        }

        private static string MakeTempPath(string fileName, string role) =>
            Path.Combine(Path.GetTempPath(), $"{TempPrefix}{Guid.NewGuid():N}-{role}-{fileName}");

        // Best-effort cleanup of merge scratch files older than a day; a file still open in a merge
        // window stays and is swept on a later pass.
        private static void SweepStaleTempFiles()
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
                    catch { /* still in use; swept next time */ }
                }
            }
            catch (Exception ex)
            {
                ex.LogAsync().FireAndForget();
            }
        }
    }
}
