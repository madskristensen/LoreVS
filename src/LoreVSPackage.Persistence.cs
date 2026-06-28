using LoreVS.SourceControl;

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;

namespace LoreVS
{
    /// <summary>
    /// Persists the Lore source-control binding in the solution file via
    /// <see cref="IVsPersistSolutionProps"/>. When a controlled solution is reopened the shell
    /// finds the <see cref="SolutionPersistenceKey"/> section, loads this package, and calls
    /// <see cref="ReadSolutionProps"/> so Lore can re-activate itself as the active provider —
    /// the same mechanism Visual Studio uses for Git/TFS bindings.
    /// </summary>
    public sealed partial class LoreVSPackage : IVsPersistSolutionProps
    {
        public int QuerySaveSolutionProps(IVsHierarchy pHierarchy, VSQUERYSAVESLNPROPS[] pqsspSave)
        {
            if (pqsspSave == null || pqsspSave.Length == 0)
            {
                return VSConstants.E_INVALIDARG;
            }

            if (string.IsNullOrEmpty(_controlledRoot))
            {
                pqsspSave[0] = VSQUERYSAVESLNPROPS.QSP_HasNoProps;
            }
            else
            {
                pqsspSave[0] = _solutionHasDirtyProps
                    ? VSQUERYSAVESLNPROPS.QSP_HasDirtyProps
                    : VSQUERYSAVESLNPROPS.QSP_HasNoDirtyProps;
            }

            return VSConstants.S_OK;
        }

        public int SaveSolutionProps(IVsHierarchy pHierarchy, IVsSolutionPersistence pPersistence)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Persist in the pre-load section so the binding is read back before projects open.
            pPersistence?.SavePackageSolutionProps(1, null, this, SolutionPersistenceKey);
            _solutionHasDirtyProps = false;
            return VSConstants.S_OK;
        }

        public int WriteSolutionProps(IVsHierarchy pHierarchy, string pszKey, Microsoft.VisualStudio.OLE.Interop.IPropertyBag pPropBag)
        {
            if (pPropBag == null)
            {
                return VSConstants.E_INVALIDARG;
            }

            object controlled = true.ToString();
            pPropBag.Write(PropControlled, ref controlled);

            object root = _controlledRoot ?? string.Empty;
            pPropBag.Write(PropRepositoryRoot, ref root);

            return VSConstants.S_OK;
        }

        public int ReadSolutionProps(IVsHierarchy pHierarchy, string pszProjectName, string pszProjectMk, string pszKey, int fPreLoad, Microsoft.VisualStudio.OLE.Interop.IPropertyBag pPropBag)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!string.Equals(pszKey, SolutionPersistenceKey, System.StringComparison.Ordinal) || pPropBag == null)
            {
                return VSConstants.S_OK;
            }

            // Re-activate Lore as the source control provider so the shell routes SCC to us for
            // this solution. The actual project binding happens once the solution finishes opening
            // (see ApplyControlledBinding), which needs the loaded project hierarchies.
            if (GetService(typeof(IVsRegisterScciProvider)) is IVsRegisterScciProvider register)
            {
                register.RegisterSourceControlProvider(LoreGuids.SccProvider);
            }

            try
            {
                pPropBag.Read(PropControlled, out object controlled, null, 0, null);
                if (controlled != null &&
                    string.Equals(controlled.ToString(), true.ToString(), System.StringComparison.OrdinalIgnoreCase))
                {
                    pPropBag.Read(PropRepositoryRoot, out object root, null, 0, null);
                    _pendingControlledRoot = root?.ToString();
                    _solutionHasDirtyProps = false;
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoreVS] ReadSolutionProps failed: {ex.Message}");
            }

            return VSConstants.S_OK;
        }

        public int OnProjectLoadFailure(IVsHierarchy pStubHierarchy, string pszProjectName, string pszProjectMk, string pszKey)
            => VSConstants.S_OK;

        // IVsPersistSolutionOpts (.suo) members. Lore stores no per-user solution options, so these
        // are no-ops; user settings are persisted via the options store, not the .suo file.
        public int SaveUserOptions(IVsSolutionPersistence pPersistence) => VSConstants.S_OK;

        public int LoadUserOptions(IVsSolutionPersistence pPersistence, uint grfLoadOpts) => VSConstants.S_OK;

        public int WriteUserOptions(Microsoft.VisualStudio.OLE.Interop.IStream pOptionsStream, string pszKey) => VSConstants.S_OK;

        public int ReadUserOptions(Microsoft.VisualStudio.OLE.Interop.IStream pOptionsStream, string pszKey) => VSConstants.S_OK;
    }
}
