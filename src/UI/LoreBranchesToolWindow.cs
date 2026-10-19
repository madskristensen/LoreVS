using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.UI
{
    public sealed class LoreBranchesToolWindow : BaseToolWindow<LoreBranchesToolWindow>
    {
        public override string GetTitle(int toolWindowId) => "Lore Branches";
        public override Type PaneType => typeof(Pane);

        public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            ToolkitPackage package = Package ?? throw new InvalidOperationException("The Lore package has not initialized its tool windows.");
            return new LoreBranchesControl(package.JoinableTaskFactory);
        }

        [Guid("20189c30-2f97-4a69-96ad-762a23898106")]
        internal sealed class Pane : ToolWindowPane
        {
            public Pane() => BitmapImageMoniker = LoreMonikers.LoreLogo;
        }
    }
}