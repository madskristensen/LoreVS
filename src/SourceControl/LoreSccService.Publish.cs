using System.Threading;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// <see cref="IVsSccPublish"/> implementation for the Lore provider. Enables Visual Studio's
    /// "publish"/"unpublished repository" affordance (for example the status bar entry shown when a
    /// solution is not yet under source control) to start Lore onboarding. The workflow itself lives
    /// in the "Add to Lore Source Control" command, so this simply invokes that command.
    /// </summary>
    public sealed partial class LoreSccService
    {
        async Task IVsSccPublish.BeginPublishWorkflowAsync(CancellationToken cancellationToken)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                DiagLog.Write("[scc] BeginPublishWorkflowAsync -> AddToLoreCommand");
                await VS.Commands.ExecuteAsync(PackageGuids.LoreVS, PackageIds.AddToLoreCommand);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }
    }
}