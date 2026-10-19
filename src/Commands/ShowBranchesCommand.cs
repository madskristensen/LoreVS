using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    [Command(PackageIds.ShowBranchesCommand)]
    internal sealed class ShowBranchesCommand : BaseCommand<ShowBranchesCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e) => await LoreBranchesToolWindow.ShowAsync();
    }
}