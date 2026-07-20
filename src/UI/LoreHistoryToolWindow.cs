using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.UI
{
    /// <summary>
    /// The "Lore History" tool window - a read-only, paged revision log for the Lore repository bound
    /// to the open solution or folder. Selecting a revision loads its changed files; double-clicking a
    /// file diffs it against the revision's direct parent.
    /// </summary>
    public class LoreHistoryToolWindow : BaseToolWindow<LoreHistoryToolWindow>
    {
        public override string GetTitle(int toolWindowId) => "Lore History";

        public override Type PaneType => typeof(Pane);

        public override Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            // Return the control immediately and let it initialize in the background. Resolving the
            // Lore binding can involve out-of-process worker calls, and blocking CreateAsync on them
            // would leave the window stuck on the "Working on it..." spinner.
            var viewModel = new LoreHistoryViewModel();
            var control = new LoreHistoryControl(viewModel);
            return Task.FromResult<FrameworkElement>(control);
        }

        [Guid("b7d2c9e1-4f8a-4c3b-9a6d-2e5f1c0a7b34")]
        internal class Pane : ToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = LoreMonikers.LoreLogo;
                ToolBar = new CommandID(PackageGuids.LoreVS, PackageIds.LoreHistoryToolbar);
            }
        }
    }
}