global using Community.VisualStudio.Toolkit;

global using Microsoft.VisualStudio.Shell;

global using System;

global using Task = System.Threading.Tasks.Task;

using LoreVS.Options;
using LoreVS.Server;
using LoreVS.SourceControl;

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;

using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LoreVS
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Proffer the private SCC provider service consumed by the Visual Studio SCC manager.
    // It must be SYNCHRONOUSLY queryable: IVsRegisterScciProvider.RegisterSourceControlProvider
    // resolves the provider service via a synchronous QueryService and QIs it for IVsSccProvider.
    // Registering it as async-queryable makes that synchronous resolve fail with E_NOINTERFACE.
    [ProvideService(typeof(LoreSccService), IsAsyncQueryable = false)]
    // Register Lore in Tools > Options > Source Control > Plug-in Selection. The three GUIDs
    // are: the SCC provider, this package, and the SCC provider service.
    [ProvideSourceControlProvider(
        LoreGuids.ProviderName,
        "#100",
        LoreGuids.SccProviderString,
        PackageGuids.LoreVSString,
        LoreGuids.SccServiceString)]
    [ProvideOptionPage(typeof(Options.OptionsProvider.GeneralOptions), "Lore", "General", 0, 0, supportsAutomation: true, SupportsProfiles = true)]
    [ProvideAutoLoad(LoreGuids.SccProviderString, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
    // Persist the Lore binding in the solution file. When a controlled solution is reopened the
    // shell sees this section, loads this package, and calls IVsPersistSolutionProps.ReadSolutionProps
    // so we can re-activate Lore as the source control provider (the same way VS persists Git/TFS).
    [ProvideSolutionProps(SolutionPersistenceKey)]
    // The Lore Changes tool window - a Git Changes-style panel for staging-free commit, diff,
    // and push/pull. Docked next to Solution Explorer by default.
    [ProvideToolWindow(typeof(UI.LoreChangesToolWindow.Pane), Style = VsDockStyle.Tabbed, Window = WindowGuids.SolutionExplorer)]
    [Guid(PackageGuids.LoreVSString)]
    public sealed partial class LoreVSPackage : ToolkitPackage
    {
        /// <summary>Solution-file section name used to persist the Lore binding (max 31 chars, no dots).</summary>
        internal const string SolutionPersistenceKey = "LoreSourceControl";
        private const string PropControlled = "LoreControlled";
        private const string PropRepositoryRoot = "LoreRepositoryRoot";

        private LoreSccService _sccService;

        // Solution-persistence state. _controlledRoot is the Lore repository root bound to the
        // currently open solution (null when not controlled); _solutionHasDirtyProps tracks whether
        // the binding still needs to be written to the .sln; _pendingControlledRoot carries the root
        // read from the .sln in ReadSolutionProps until the solution finishes opening and we bind it.
        private bool _solutionHasDirtyProps;
        private string _controlledRoot;
        private string _pendingControlledRoot;

        /// <summary>The active Lore SCC provider service, available once the package has loaded.</summary>
        internal LoreSccService SccService => _sccService;

        /// <summary>The Lore client (the out-of-process worker / SDK) backing the SCC provider.</summary>
        internal ILoreClient Client => _sccService.Client;

        /// <summary>Builds the Lore server endpoint (host/port) from the current options.</summary>
        internal static LoreServerEndpoint GetServerEndpoint(General options) =>
            new LoreServerEndpoint(LoreServerEndpoint.DefaultHost, options.ServerPort);

        /// <summary>
        /// Makes Lore the active source control provider, binds the solution's loaded projects, and
        /// persists the binding in the solution file so it is restored automatically on reopen.
        /// Returns the number of projects bound. Called after a repository has been created.
        /// </summary>
        internal int OnboardAfterCreate(IVsSolution solution, string repositoryRoot)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            int bound = ActivateAndBind(solution, repositoryRoot);

            // Record the binding and flush it to the .sln so reopening the solution reactivates Lore.
            _controlledRoot = repositoryRoot;
            PersistBinding(solution);
            return bound;
        }

        /// <summary>
        /// Registers Lore as the active SCC provider and binds the solution's loaded projects for
        /// glyphs. Does not touch the solution file. Returns the number of projects bound.
        /// </summary>
        private int ActivateAndBind(IVsSolution solution, string repositoryRoot)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (GetService(typeof(IVsRegisterScciProvider)) is IVsRegisterScciProvider register)
            {
                int hr = register.RegisterSourceControlProvider(LoreGuids.SccProvider);
                DiagLog.Write($"[scc] RegisterSourceControlProvider(Lore) hr=0x{hr:X8} active={_sccService.Active}");
                LoreLog.WriteLineAsync($"[scc] RegisterSourceControlProvider(Lore) hr=0x{hr:X8}; active={_sccService.Active}")
                    .FileAndForget("LoreVS/Onboard");
            }
            else
            {
                DiagLog.Write("[scc] IVsRegisterScciProvider not available - cannot activate Lore");
                LoreLog.WriteLineAsync("[scc] IVsRegisterScciProvider service was not available - cannot switch active provider to Lore.")
                    .FileAndForget("LoreVS/Onboard");
            }

            int bound = _sccService.OnboardSolution(solution, repositoryRoot);
            DiagLog.Write($"[scc] OnboardSolution bound {bound} project(s); active={_sccService.Active}");
            LoreLog.WriteLineAsync($"[scc] OnboardSolution bound {bound} project(s); active={_sccService.Active}")
                .FileAndForget("LoreVS/Onboard");
            return bound;
        }

        /// <summary>
        /// Marks the Lore binding dirty and saves the solution so the persistence section is written
        /// to the .sln (triggers QuerySaveSolutionProps -> SaveSolutionProps -> WriteSolutionProps).
        /// </summary>
        private void PersistBinding(IVsSolution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _solutionHasDirtyProps = true;
            solution?.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0);
        }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Create and proffer the SCC provider service. The brokered client runs the native
            // Lore .NET SDK in an out-of-process .NET worker over JSON-RPC.
            _sccService = new LoreSccService(this, new LoreBrokeredClient());
            ((IServiceContainer)this).AddService(typeof(LoreSccService), _sccService, promote: true);

            // Do NOT call IVsRegisterScciProvider.RegisterSourceControlProvider here. The provider
            // GUID is also the autoload UI context (see [ProvideAutoLoad(LoreGuids.SccProviderString)]),
            // so activating the provider while InitializeAsync is still running would request a load of
            // this very package and deadlock against the in-progress load. When Lore was the active
            // provider in the previous session, Visual Studio loads this package via that autoload
            // context and calls IVsSccProvider.SetActive on our service automatically.

            // Listen for solution open/close to restore and clear the Lore binding. The static
            // SolutionEvents helper manages the underlying IVsSolutionEvents advise/unadvise for us.
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenSolution += OnAfterOpenSolution;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseSolution += OnAfterCloseSolution;

            // Refresh glyphs after a document is saved so a file's icon flips to its new Lore
            // status (e.g. modified) without requiring a manual refresh.
            VS.Events.DocumentEvents.Saved += OnDocumentSaved;

            await this.RegisterCommandsAsync();
            this.RegisterToolWindows();

            // The package may have loaded after a solution was already opened
            // the MRU on startup), in which case OnAfterOpenSolution/ReadSolutionProps already ran
            // before we subscribed. Restore the Lore binding for any solution that is already open.
            // This is deferred until after InitializeAsync returns: activating the SCC provider
            // raises this package's autoload UI context, and doing that while the load is still in
            // progress would re-enter the load.
            JoinableTaskFactory.RunAsync(async () =>
            {
                await Task.Yield();
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                DiagLog.Write("[init] deferred restore -> ApplyControlledBinding");
                ApplyControlledBinding();
            }).FileAndForget("LoreVS/RestoreScc");
        }

        /// <summary>
        /// Called by <see cref="LoreSccService"/> when activation state changes. Updates SCC
        /// command visibility and refreshes glyphs.
        /// </summary>
        internal void OnActiveStateChange(LoreSccService service)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (GetService(typeof(IMenuCommandService)) is OleMenuCommandService mcs)
            {
                var id = new CommandID(PackageGuids.LoreVS, PackageIds.MyCommand);
                MenuCommand command = mcs.FindCommand(id);
                if (command != null)
                {
                    command.Supported = true;
                    command.Enabled = service.Active;
                    command.Visible = service.Active;
                }
            }

            if (service.Active)
            {
                service.RefreshAllGlyphs();
            }
        }

        /// <summary>
        /// Restores the Lore binding after a solution finishes opening. Prefers the binding read
        /// from the solution file (<see cref="ReadSolutionProps"/>); falls back to detecting a
        /// <c>.lore</c> repository on disk for solutions onboarded before solution-props persistence
        /// existed, upgrading them to persisted bindings on the next save.
        /// </summary>
        private void OnAfterOpenSolution(object sender, Microsoft.VisualStudio.Shell.Events.OpenSolutionEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DiagLog.Write("[evt] OnAfterOpenSolution -> ApplyControlledBinding");
            ApplyControlledBinding();
        }

        /// <summary>Clears solution-persistence state when the solution closes.</summary>
        private void OnAfterCloseSolution(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _controlledRoot = null;
            _pendingControlledRoot = null;
            _solutionHasDirtyProps = false;
        }

        /// <summary>
        /// Re-activates Lore and binds projects for the open solution when it is controlled. Uses
        /// the binding persisted in the .sln when present, otherwise detects a <c>.lore</c>
        /// repository on disk. Safe to call repeatedly and when no solution is open.
        /// </summary>
        private void ApplyControlledBinding()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_sccService == null || !(GetService(typeof(SVsSolution)) is IVsSolution solution))
            {
                DiagLog.Write($"[bind] ApplyControlledBinding skipped: sccService={(_sccService != null)} solutionService={GetService(typeof(SVsSolution)) is IVsSolution}");
                return;
            }

            // Binding read from the .sln takes precedence.
            if (!string.IsNullOrEmpty(_pendingControlledRoot))
            {
                _controlledRoot = _pendingControlledRoot;
            }

            _pendingControlledRoot = null;

            if (!string.IsNullOrEmpty(_controlledRoot))
            {
                DiagLog.Write($"[bind] using persisted/controlled root '{_controlledRoot}' -> ActivateAndBind");
                ActivateAndBind(solution, _controlledRoot);
                return;
            }

            // Fallback for repositories onboarded before solution-props persistence existed: detect
            // the .lore marker and persist the binding on the next save so future opens are native.
            string solutionDir = SolutionScc.GetSolutionDirectory(solution);
            if (string.IsNullOrEmpty(solutionDir))
            {
                DiagLog.Write("[bind] no solution directory; nothing to bind");
                return;
            }

            string detected = Client.FindRepositoryRoot(solutionDir);
            DiagLog.Write($"[bind] solutionDir='{solutionDir}' detectedRoot='{detected ?? "<null>"}'");
            if (detected != null)
            {
                _controlledRoot = detected;
                _solutionHasDirtyProps = true;
                ActivateAndBind(solution, detected);
            }
            else
            {
                DiagLog.Write("[bind] no .lore root detected -> RefreshAllGlyphs only (provider NOT activated)");
                _sccService.RefreshAllGlyphs();
            }
        }

        /// <summary>Refreshes Lore status glyphs after a document is saved so the file's icon
        /// reflects its new working-tree status (e.g. modified).</summary>
        private void OnDocumentSaved(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_sccService != null && _sccService.Active)
            {
                _sccService.RefreshAllGlyphs();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenSolution -= OnAfterOpenSolution;
                Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseSolution -= OnAfterCloseSolution;
                VS.Events.DocumentEvents.Saved -= OnDocumentSaved;

                // Tear down the brokered client so its out-of-process Lore worker is stopped.
                (_sccService?.Client as IDisposable)?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}