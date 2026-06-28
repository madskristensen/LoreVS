using System.Threading.Tasks;
using LoreVS.Options;
using LoreVS.SourceControl;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell.Interop;

namespace LoreVS.Commands
{
    /// <summary>
    /// "Commit to Lore..." command. Stages all changes under the repository
    /// (<c>lore stage --scan</c>), commits them with a user-supplied message, and
    /// optionally pushes — giving a full edit/commit loop to test from inside VS.
    /// </summary>
    [Command(PackageIds.CommitToLoreCommand)]
    internal sealed class CommitToLoreCommand : BaseCommand<CommitToLoreCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!(Package is LoreVSPackage package))
            {
                return;
            }

            IVsSolution solution = await VS.Services.GetSolutionAsync();
            string solutionDir = SolutionScc.GetSolutionDirectory(solution);
            if (string.IsNullOrEmpty(solutionDir))
            {
                await VS.MessageBox.ShowWarningAsync("Lore", "Open a solution or folder first.");
                return;
            }

            ILoreClient client = package.Client;
            string root = client.FindRepositoryRoot(solutionDir);
            if (root == null)
            {
                await VS.MessageBox.ShowWarningAsync("Lore",
                    "This location is not under Lore source control yet. Use 'Add to Lore Source Control' first.");
                return;
            }

            if (!client.IsAvailable)
            {
                await VS.MessageBox.ShowErrorAsync("Lore",
                    "The Lore worker could not be started, so the SDK is unavailable. Reinstall the " +
                    "extension so the worker payload is deployed, then try again.");
                return;
            }

            string message = LoreInputDialog.Prompt("Commit to Lore", "Commit message:", string.Empty, multiline: true);
            if (message == null)
            {
                return;
            }

            General options = await General.GetLiveInstanceAsync();

            // Ensure a .loreignore exists so VS's locked .vs folder and build output aren't
            // staged (covers repositories created before the ignore file was seeded).
            if (LoreIgnoreFile.EnsureDefault(root))
            {
                await LoreLog.WriteLineAsync($"Created default {LoreIgnoreFile.FileName}.");
            }

            await VS.StatusBar.ShowMessageAsync("Staging changes...");
            LoreCommandResult stage = await Task.Run(() => client.StageAll(root));
            await LoreLog.WriteCommandAsync("stage --scan .", stage.CombinedText);
            if (!stage.Success)
            {
                await VS.MessageBox.ShowErrorAsync("Lore", "Failed to stage changes:\n\n" + stage.CombinedText);
                return;
            }

            await VS.StatusBar.ShowMessageAsync("Committing...");
            LoreCommandResult commit = await Task.Run(() => client.Commit(root, message, options.Identity));
            await LoreLog.WriteCommandAsync($"commit \"{message}\"", commit.CombinedText);
            if (!commit.Success)
            {
                await VS.MessageBox.ShowErrorAsync("Lore", "Commit failed:\n\n" + commit.CombinedText);
                return;
            }

            if (options.AutoPushOnCommit)
            {
                await VS.StatusBar.ShowMessageAsync("Pushing...");
                LoreCommandResult push = await Task.Run(() => client.Push(root));
                await LoreLog.WriteCommandAsync("push", push.CombinedText);
                if (!push.Success)
                {
                    await VS.MessageBox.ShowWarningAsync("Lore",
                        "Commit succeeded but push failed:\n\n" + push.CombinedText);
                }
            }

            package.SccService?.RefreshAllGlyphs();
            await VS.StatusBar.ShowMessageAsync("Lore commit complete.");
        }
    }
}
