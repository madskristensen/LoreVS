using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.SourceControl;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    /// <summary>
    /// "Compare with Unmodified" command. Opens the native diff viewer comparing the
    /// selected file against its committed version. Only shown when the solution is
    /// under Lore source control.
    /// </summary>
    [Command(PackageIds.CompareUnmodifiedCommand)]
    internal sealed class CompareUnmodifiedCommand : BaseCommand<CompareUnmodifiedCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            Command.Visible = Command.Enabled = (Package as LoreVSPackage)?.IsSolutionControlled == true;
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!(Package is LoreVSPackage package))
            {
                return;
            }

            ILoreClient client = package.Client;
            SolutionItem? item = await VS.Solutions.GetActiveItemAsync();
            string fullPath = item?.FullPath ?? string.Empty;
            if (string.IsNullOrEmpty(fullPath))
            {
                return;
            }

            string? root = client.FindRepositoryRoot(fullPath);
            if (root == null)
            {
                return;
            }

            LoreFileStatus status = client.GetStatus(fullPath);
            await LoreDiffPresenter.ShowAsync(client, root, new LoreChangeItem(fullPath, root, status));
        }
    }
}