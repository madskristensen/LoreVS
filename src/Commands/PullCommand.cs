using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    /// <summary>Toolbar command that syncs (pulls) the latest revisions from the Lore remote.</summary>
    [Command(PackageIds.PullCommand)]
    internal sealed class PullCommand : BaseCommand<PullCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            LoreChangesViewModel viewModel = LoreChangesControl.Current?.ViewModel;
            if (viewModel != null)
            {
                await viewModel.PullAsync();
            }
        }
    }
}
