namespace LoreVS
{
    /// <summary>
    /// "Refresh Lore Status" command. Invalidates the cached Lore status and asks
    /// Visual Studio to re-query glyphs for all source-controlled items.
    /// </summary>
    [Command(PackageIds.MyCommand)]
    internal sealed class MyCommand : BaseCommand<MyCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            (Package as LoreVSPackage)?.SccService?.RefreshAllGlyphs();
        }
    }
}
