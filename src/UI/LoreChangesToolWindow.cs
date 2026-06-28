using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.UI
{
    /// <summary>
    /// The "Lore Changes" tool window - a Git Changes-style panel for committing, reviewing changed
    /// files, diffing, and pushing/pulling, all backed by the Lore source control provider.
    /// </summary>
    public class LoreChangesToolWindow : BaseToolWindow<LoreChangesToolWindow>
    {
        public override string GetTitle(int toolWindowId) => "Lore Changes";

        public override Type PaneType => typeof(Pane);

        public override Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            // Return the control immediately and let it initialize in the background. Resolving the
            // Lore binding can involve out-of-process worker calls, and blocking CreateAsync on them
            // would leave the window stuck on the "Working on it..." spinner.
            var viewModel = new LoreChangesViewModel();
            var control = new LoreChangesControl(viewModel);
            return Task.FromResult<FrameworkElement>(control);
        }

        [Guid("2a8de7f4-84d7-4bdb-a555-eeb320ebbc88")]
        internal class Pane : ToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.Changeset;
                ToolBar = new CommandID(PackageGuids.LoreVS, PackageIds.LoreChangesToolbar);
            }
        }
    }
}
