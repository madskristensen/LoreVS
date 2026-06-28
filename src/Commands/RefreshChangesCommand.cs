using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    /// <summary>Toolbar command that re-resolves the Lore binding and refreshes the change list.</summary>
    [Command(PackageIds.RefreshChangesCommand)]
    internal sealed class RefreshChangesCommand : BaseCommand<RefreshChangesCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            LoreChangesViewModel viewModel = LoreChangesControl.Current?.ViewModel;
            if (viewModel != null)
            {
                await viewModel.ReloadAsync();
            }
        }
    }
}
