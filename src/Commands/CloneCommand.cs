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

            string defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
            var dialog = new LoreCloneDialog(defaultPath);
            if (dialog.ShowModal() != true)
            {
                return;
            }

            string repositoryUrl = dialog.RepositoryUrl;
            if (string.IsNullOrWhiteSpace(repositoryUrl) ||
                !LoreServerEndpoint.TryParse(repositoryUrl, out LoreServerEndpoint endpoint))
            {
                await VS.MessageBox.ShowErrorAsync("Lore", "Enter a valid lore:// repository URL.");
                return;
            }

            if (string.IsNullOrWhiteSpace(dialog.DestinationPath))
            {
                await VS.MessageBox.ShowErrorAsync("Lore", "Choose a local path to clone into.");
                return;
            }

            string trimmed = repositoryUrl.TrimEnd('/');
            string repoName = trimmed.Substring(trimmed.LastIndexOf('/') + 1);
            string target = Path.Combine(dialog.DestinationPath, repoName);

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
            LoreCommandResult result = await Task.Run(() => client.CloneRepository(repositoryUrl, target, options.Identity));
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