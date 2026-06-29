using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    /// <summary>Toolbar command that pushes committed local revisions to the Lore remote.</summary>
    [Command(PackageIds.PushCommand)]
    internal sealed class PushCommand : BaseCommand<PushCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            LoreChangesViewModel? viewModel = LoreChangesControl.Current?.ViewModel;
            if (viewModel != null)
            {
                await viewModel.PushAsync();
            }
        }
    }
}
