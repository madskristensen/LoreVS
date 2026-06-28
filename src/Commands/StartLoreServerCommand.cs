using System.Threading.Tasks;
using LoreVS.Options;
using LoreVS.Server;

namespace LoreVS.Commands
{
    /// <summary>
    /// "Start Local Lore Server" command. Launches (or reuses) a local <c>loreserver</c> so the
    /// user never has to start one from a terminal.
    /// </summary>
    [Command(PackageIds.StartLoreServerCommand)]
    internal sealed class StartLoreServerCommand : BaseCommand<StartLoreServerCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!(Package is LoreVSPackage package))
            {
                return;
            }

            General o = await General.GetLiveInstanceAsync();
            await VS.StatusBar.ShowMessageAsync("Starting local Lore server...");
            EnsureServerResult result = await package.ServerManager.EnsureRunningAsync(
                LoreVSPackage.GetServerEndpoint(o), o.LoreServerExecutablePath, o.LocalServerStorePath);

            switch (result)
            {
                case EnsureServerResult.Started:
                    await LoreLog.WriteLineAsync("Started local Lore server.");
                    await VS.StatusBar.ShowMessageAsync("Local Lore server started.");
                    break;
                case EnsureServerResult.AlreadyRunning:
                    await VS.StatusBar.ShowMessageAsync("Local Lore server is already running.");
                    break;
                case EnsureServerResult.ExternalUnreachable:
                    await VS.MessageBox.ShowWarningAsync("Lore",
                        "The configured server uses non-default ports, so it is treated as an external " +
                        "server the extension does not launch. Start it yourself, or reset the ports in " +
                        "Tools > Options > Lore to use the managed demo server.");
                    break;
                case EnsureServerResult.MissingBinary:
                    await VS.MessageBox.ShowErrorAsync("Lore",
                        "'loreserver' could not be found. Run Tools > Install Lore Tools, or set its " +
                        "path in Tools > Options > Lore.");
                    break;
                default:
                    await VS.MessageBox.ShowErrorAsync("Lore",
                        "The local Lore server failed to start. See the server log under %TEMP%\\LoreVS.");
                    break;
            }
        }
    }
}
