using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    [Command(PackageIds.OpenChangeFileCommand)]
    internal sealed class OpenChangeFileCommand : BaseCommand<OpenChangeFileCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            Command.Enabled = LoreChangesControl.ContextMenuOwner?.ContextPaths.Length == 1;
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            string path = LoreChangesControl.ContextMenuOwner!.ContextPaths[0];
            await VS.Documents.OpenAsync(path);
        }
    }
}