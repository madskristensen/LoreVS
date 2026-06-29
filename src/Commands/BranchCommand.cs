using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.UI;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.Commands
{
    /// <summary>
    /// Toolbar dropdown that shows the current branch name and opens the branch
    /// switch/create/merge menu. Replaces the in-content branch button so the menu has a
    /// large, easy-to-hit target on the tool window toolbar.
    /// </summary>
    [Command(PackageIds.BranchCommand)]
    internal sealed class BranchCommand : BaseCommand<BranchCommand>
    {
        protected override Task InitializeCompletedAsync()
        {
            Command.BeforeQueryStatus += OnBeforeQueryStatus;
            return base.InitializeCompletedAsync();
        }

        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            LoreChangesViewModel? viewModel = LoreChangesControl.Current?.ViewModel;
            Command.Enabled = viewModel?.CanInteract == true;

            // Show the branch name with a trailing down-triangle (padded with whitespace) so the
            // button reads like the Git Changes branch combo.
            string branch = string.IsNullOrEmpty(viewModel?.BranchText) ? "Branch" : viewModel!.BranchText;
            Command.Text = branch + "     \u25BE";
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            LoreChangesControl? control = LoreChangesControl.Current;
            if (control != null)
            {
                await control.ShowBranchMenuAsync();
            }
        }
    }
}
