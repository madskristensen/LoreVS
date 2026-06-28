using System.Threading.Tasks;

namespace LoreVS.Commands
{
    /// <summary>
    /// "Stop Local Lore Server" command. Stops the <c>loreserver</c> process the extension started.
    /// </summary>
    [Command(PackageIds.StopLoreServerCommand)]
    internal sealed class StopLoreServerCommand : BaseCommand<StopLoreServerCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!(Package is LoreVSPackage package))
            {
                return;
            }

            if (!package.ServerManager.IsManagingProcess)
            {
                await VS.MessageBox.ShowAsync("Lore",
                    "No local Lore server started by the extension is currently running.");
                return;
            }

            package.ServerManager.Stop();
            await LoreLog.WriteLineAsync("Stopped local Lore server.");
            await VS.StatusBar.ShowMessageAsync("Local Lore server stopped.");
        }
    }
}
