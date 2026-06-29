using System;
using System.IO;
using System.Threading.Tasks;
using LoreVS.Options;
using LoreVS.Server;
using LoreVS.SourceControl;
using LoreVS.UI;

namespace LoreVS.Commands
{
    /// <summary>
    /// "Clone Lore Repository" command. Pulls an existing remote repository into a local folder,
    /// the entry point for the remote-first workflow (no offline repo needed first).
    /// </summary>
    [Command(PackageIds.CloneCommand)]
    internal sealed class CloneCommand : BaseCommand<CloneCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!(Package is LoreVSPackage package))
            {
                return;
            }

            string? repositoryUrl = LoreInputDialog.Prompt("Clone from Lore",
                "Lore repository URL to clone (e.g. lore://127.0.0.1:41337/my-project):",
                string.Empty);
            if (string.IsNullOrWhiteSpace(repositoryUrl) ||
                !LoreServerEndpoint.TryParse(repositoryUrl!, out LoreServerEndpoint endpoint))
            {
                if (repositoryUrl != null)
                {
                    await VS.MessageBox.ShowErrorAsync("Lore", "Enter a valid lore:// repository URL.");
                }

                return;
            }

            string trimmed = repositoryUrl!.TrimEnd('/');
            string repoName = trimmed.Substring(trimmed.LastIndexOf('/') + 1);
            string? parent = LoreInputDialog.Prompt("Clone from Lore",
                "Local folder to clone into (the repository folder is created inside it):",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos"));
            if (string.IsNullOrWhiteSpace(parent))
            {
                return;
            }

            string target = Path.Combine(parent!, repoName);

            ILoreClient client = package.Client;
            if (!await Task.Run(() => client.IsAvailable))
            {
                await VS.MessageBox.ShowErrorAsync("Lore", "The Lore worker could not be started, so the SDK is unavailable.");
                return;
            }

            if (!await LoreServerHealth.IsReachableAsync(endpoint))
            {
                await VS.MessageBox.ShowErrorAsync("Lore", $"No Lore server is reachable at {endpoint.ServerUrl}.");
                return;
            }

            General options = await General.GetLiveInstanceAsync();
            await VS.StatusBar.ShowMessageAsync("Cloning Lore repository...");
            LoreCommandResult result = await Task.Run(() => client.CloneRepository(repositoryUrl!, target, options.Identity));
            await LoreLog.WriteCommandAsync($"clone {repositoryUrl}", result.CombinedText);

            if (!result.Success)
            {
                await VS.StatusBar.ShowMessageAsync("Lore clone failed.");
                await VS.MessageBox.ShowErrorAsync("Lore", "Failed to clone the Lore repository:\n\n" + result.CombinedText);
                return;
            }

            await VS.StatusBar.ShowMessageAsync("Lore repository cloned.");
            await VS.MessageBox.ShowAsync("Lore", $"Cloned '{repoName}' to:\n{target}\n\nOpen this folder to start working.");
        }
    }
}