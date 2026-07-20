using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    /// <summary>Lore menu command that opens the read-only Lore History tool window.</summary>
    [Command(PackageIds.ShowHistoryCommand)]
    internal sealed class ShowHistoryCommand : BaseCommand<ShowHistoryCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await LoreHistoryToolWindow.ShowAsync();
        }
    }
}