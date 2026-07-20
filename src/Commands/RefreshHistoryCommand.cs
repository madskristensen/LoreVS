using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    /// <summary>Toolbar command that reloads the Lore History revision log on demand.</summary>
    [Command(PackageIds.RefreshHistoryCommand)]
    internal sealed class RefreshHistoryCommand : BaseCommand<RefreshHistoryCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            LoreHistoryViewModel? viewModel = LoreHistoryControl.Current?.ViewModel;
            if (viewModel != null)
            {
                await viewModel.ReloadAsync();
            }
        }
    }
}