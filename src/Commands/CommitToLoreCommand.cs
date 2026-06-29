using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    /// <summary>
    /// "Commit to Lore..." command. Opens (and focuses) the Lore Changes tool window,
    /// the single place from which revisions are committed. Only shown when the
    /// solution is under Lore source control.
    /// </summary>
    [Command(PackageIds.CommitToLoreCommand)]
    internal sealed class CommitToLoreCommand : BaseCommand<CommitToLoreCommand>
    {
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) =>
            LoreChangesToolWindow.ShowAsync();

        protected override void BeforeQueryStatus(EventArgs e)
        {
            Command.Visible = Command.Enabled = (Package as LoreVSPackage)?.IsSolutionControlled == true;
        }
    }
}