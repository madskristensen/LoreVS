using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    /// <summary>Opens (and focuses) the Lore Changes tool window.</summary>
    [Command(PackageIds.OpenLoreChangesCommand)]
    internal sealed class OpenLoreChangesCommand : BaseCommand<OpenLoreChangesCommand>
    {
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) =>
            LoreChangesToolWindow.ShowAsync();
    }
}
