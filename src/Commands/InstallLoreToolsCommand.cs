using System.Threading.Tasks;
using LoreVS.SourceControl;

namespace LoreVS.Commands
{
    /// <summary>
    /// "Install Lore Tools" command. Installs the <c>lore</c> CLI and <c>loreserver</c> binaries
    /// (for any that are missing) by running the official install script, so the user can onboard
    /// and run a local server without leaving Visual Studio.
    /// </summary>
    [Command(PackageIds.InstallLoreToolsCommand)]
    internal sealed class InstallLoreToolsCommand : BaseCommand<InstallLoreToolsCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!(Package is LoreVSPackage))
            {
                return;
            }

            LoreVS.Options.General options = await LoreVS.Options.General.GetLiveInstanceAsync();
            bool hasCli = LoreToolLocator.Exists(options.LoreExecutablePath);
            bool hasServer = LoreToolLocator.Exists(options.LoreServerExecutablePath);

            if (hasCli && hasServer)
            {
                await VS.MessageBox.ShowAsync("Lore",
                    "The Lore CLI and server are already installed.");
                return;
            }

            bool confirmed = await VS.MessageBox.ShowConfirmAsync("Lore",
                "This will download and run the official Lore install script from GitHub to install " +
                "the missing tools into %USERPROFILE%\\bin. Continue?");
            if (!confirmed)
            {
                return;
            }

            await LoreLog.WriteLineAsync("Installing Lore tools...");
            await VS.StatusBar.ShowMessageAsync("Installing Lore tools...");

            bool ok = await LoreToolInstaller.InstallAsync(installCli: !hasCli, installServer: !hasServer);

            if (ok)
            {
                await VS.StatusBar.ShowMessageAsync("Lore tools installed.");
                await VS.MessageBox.ShowAsync("Lore",
                    "Lore tools were installed into %USERPROFILE%\\bin. You can now add a solution to " +
                    "Lore source control.");
            }
            else
            {
                await VS.StatusBar.ShowMessageAsync("Lore tools installation failed.");
                await VS.MessageBox.ShowErrorAsync("Lore",
                    "Installation failed. See the 'Lore' Output pane for details.");
            }
        }
    }
}
