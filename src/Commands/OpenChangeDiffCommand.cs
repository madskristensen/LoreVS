using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    [Command(PackageIds.OpenChangeDiffCommand)]
    internal sealed class OpenChangeDiffCommand : BaseCommand<OpenChangeDiffCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            LoreChangesControl? control = LoreChangesControl.ContextMenuOwner;
            Command.Visible = control?.ContextFile != null;
            Command.Enabled = control?.ViewModel.CanInteract == true;
        }

        protected override Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            LoreChangesControl control = LoreChangesControl.ContextMenuOwner!;
            return control.ViewModel.ShowDiffAsync(control.ContextFile!);
        }
    }
}