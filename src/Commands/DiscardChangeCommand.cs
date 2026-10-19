using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    [Command(PackageIds.DiscardChangeCommand)]
    internal sealed class DiscardChangeCommand : BaseCommand<DiscardChangeCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            LoreChangesControl? control = LoreChangesControl.ContextMenuOwner;
            Command.Enabled = control?.ViewModel.CanInteract == true && control.ContextDiscardItems.Count > 0;
        }

        protected override Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            LoreChangesControl control = LoreChangesControl.ContextMenuOwner!;
            return control.ViewModel.DiscardAsync(control.ContextDiscardItems);
        }
    }
}