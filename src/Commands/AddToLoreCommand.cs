using System.IO;
using System.Threading.Tasks;
using LoreVS.Options;
using LoreVS.Server;
using LoreVS.SourceControl;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell.Interop;

namespace LoreVS.Commands
{
    /// <summary>
    /// "Add to Lore Source Control" command. Onboards the open solution/folder to Lore by
    /// creating a repository (locally by default, or on a Lore server if the user opts in) and
    /// binding the loaded projects to the provider.
    /// </summary>
    [Command(PackageIds.AddToLoreCommand)]
    internal sealed class AddToLoreCommand : BaseCommand<AddToLoreCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            Command.Visible = Command.Enabled = (Package as LoreVSPackage)?.IsSolutionControlled != true;
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!(Package is LoreVSPackage package))
            {
                return;
            }

            IVsSolution solution = await VS.Services.GetSolutionAsync();
            string? solutionDir = SolutionScc.GetSolutionDirectory(solution);
            if (string.IsNullOrEmpty(solutionDir))
            {
                await VS.MessageBox.ShowWarningAsync("Lore", "Open a solution or folder before adding it to Lore source control.");
                return;
            }

            ILoreClient client = package.Client;

            string? existingRoot = client.FindRepositoryRoot(solutionDir!);
            if (existingRoot != null)
            {
                await VS.MessageBox.ShowAsync("Lore", $"This location is already under Lore source control:\n{existingRoot}");
                return;
            }

            if (!await Task.Run(() => client.IsAvailable))
            {
                await VS.MessageBox.ShowErrorAsync("Lore",
                    "The Lore worker could not be started, so the SDK is unavailable. Reinstall the " +
                    "extension so the worker payload is deployed, then try again.");
                return;
            }

            General options = await General.GetLiveInstanceAsync();
            string repoName = Path.GetFileName(solutionDir);

            // Ask how to create. Local (offline, no remote) is the default so onboarding never binds
            // a server unless the user explicitly opts in. A lore:// server URL creates the repo on
            // that server and binds a remote; a bare name creates a fully offline repo (git-init style).
            var dialog = new LoreAddToSourceControlDialog(repoName, LoreServerEndpoint.Default.ServerUrl);
            if (dialog.ShowModal() != true)
            {
                return;
            }

            string url = repoName;
            if (!dialog.IsLocal)
            {
                if (!LoreServerEndpoint.TryParse(dialog.ServerUrl, out LoreServerEndpoint endpoint))
                {
                    await VS.MessageBox.ShowErrorAsync("Lore", "Enter a valid lore:// server URL, or choose a local repository.");
                    return;
                }

                if (!await LoreServerHealth.IsReachableAsync(endpoint))
                {
                    await VS.MessageBox.ShowErrorAsync("Lore",
                        $"No Lore server is reachable at {endpoint.ServerUrl}.\n\nStart a server (e.g. the demo " +
                        "server on lore://127.0.0.1:41337), or choose a local repository.");
                    return;
                }

                url = endpoint.RepositoryUrl(repoName);
            }

            // Seed a .loreignore so Visual Studio's locked .vs folder, build output, and user
            // files are not staged/committed (otherwise the commit fails writing locked files).
            if (LoreIgnoreFile.EnsureDefault(solutionDir!))
            {
                await LoreLog.WriteLineAsync($"Created default {LoreIgnoreFile.FileName}.");
            }

            await VS.StatusBar.ShowMessageAsync("Creating Lore repository...");
            string authUrl = dialog.IsLocal ? string.Empty : url;
            LoreCommandResult result = await LoreAuthFlow.ExecuteAsync(
                client, solutionDir!, authUrl,
                () => client.CreateRepository(solutionDir!, url, options.Identity));
            await LoreLog.WriteCommandAsync($"repository create {url}", result.CombinedText);

            if (!result.Success)
            {
                await VS.StatusBar.ShowMessageAsync("Lore repository creation failed.");
                await VS.MessageBox.ShowErrorAsync("Lore", "Failed to create the Lore repository:\n\n" + result.CombinedText);
                return;
            }

            int bound = package.OnboardAfterCreate(solution, solutionDir!);
            await VS.StatusBar.ShowMessageAsync(
                bound > 0
                    ? $"Added to Lore source control ({bound} project(s) bound)."
                    : "Added to Lore source control.");

            string boundLine = bound > 0 ? $"{bound} project(s) bound.\n" : string.Empty;
            await VS.MessageBox.ShowAsync("Lore",
                $"'{repoName}' is now under Lore source control.\n{boundLine}\n" +
                "Edit files, then use Tools > Commit to Lore to record a revision.");
        }
    }
}
