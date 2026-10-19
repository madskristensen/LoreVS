using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    [Command(PackageIds.UnlockCommand)]
    internal sealed class UnlockCommand : BaseCommand<UnlockCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            LoreChangesControl? context = LoreChangesControl.ContextMenuOwner;
            Command.Enabled = context == null || (context.ViewModel.CanInteract && context.ContextPaths.Length > 0);
        }

        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) =>
            LoreLockActions.ExecuteAsync((LoreVSPackage)Package, release: true);
    }
}