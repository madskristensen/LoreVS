using System.IO;
using System.Threading.Tasks;
using LoreVS.Options;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.Shell.Interop;

namespace LoreVS.Commands
{
    /// <summary>
    /// "Add to Lore Source Control" command. Onboards the open solution/folder to Lore by
    /// creating a repository on the configured server (running <c>lore repository create</c>
    /// in the solution directory) and binding the loaded projects to the provider.
    /// </summary>
    [Command(PackageIds.AddToLoreCommand)]
    internal sealed class AddToLoreCommand : BaseCommand<AddToLoreCommand>
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
                await VS.MessageBox.ShowWarningAsync("Lore", "Open a solution or folder before adding it to Lore source control.");
                return;
            }

            ILoreClient client = package.Client;

            string existingRoot = client.FindRepositoryRoot(solutionDir);
            if (existingRoot != null)
            {
                await VS.MessageBox.ShowAsync("Lore", $"This location is already under Lore source control:\n{existingRoot}");
                return;
            }

            if (!client.IsAvailable)
            {
                await VS.MessageBox.ShowErrorAsync("Lore",
                    "The Lore worker could not be started, so the SDK is unavailable. Reinstall the " +
                    "extension so the worker payload is deployed, then try again.");
                return;
            }

            General options = await General.GetLiveInstanceAsync();
            string repoName = Path.GetFileName(solutionDir);

            // The target is the solution/folder the user invoked the command on, so there is
            // nothing to ask: create the repository at the default URL on the configured server.
            string url = LoreVSPackage.GetServerEndpoint(options).RepositoryUrl(repoName);

            // Seed a .loreignore so Visual Studio's locked .vs folder, build output, and user
            // files are not staged/committed (otherwise the commit fails writing locked files).
            if (LoreIgnoreFile.EnsureDefault(solutionDir))
            {
                await LoreLog.WriteLineAsync($"Created default {LoreIgnoreFile.FileName}.");
            }

            await VS.StatusBar.ShowMessageAsync("Creating Lore repository...");
            LoreCommandResult result = await Task.Run(() => client.CreateRepository(solutionDir, url, options.Identity));
            await LoreLog.WriteCommandAsync($"repository create {url}", result.CombinedText);

            if (!result.Success)
            {
                await VS.StatusBar.ShowMessageAsync("Lore repository creation failed.");
                await VS.MessageBox.ShowErrorAsync("Lore", "Failed to create the Lore repository:\n\n" + result.CombinedText);
                return;
            }

            int bound = package.OnboardAfterCreate(solution, solutionDir);
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
