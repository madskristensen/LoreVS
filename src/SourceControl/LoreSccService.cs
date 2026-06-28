using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// The Lore source control provider service. A single instance is proffered by
    /// <see cref="LoreVSPackage"/> and registered with the Visual Studio SCC manager.
    /// For the read-only MVP it implements provider activation
    /// (<see cref="IVsSccProvider"/>) and status glyphs/tooltips
    /// (<see cref="IVsSccManager2"/>, <see cref="IVsSccManagerTooltip"/>).
    /// </summary>
    [Guid(LoreGuids.SccServiceString)]
    public sealed class LoreSccService : IVsSccProvider, IVsSccManager2, IVsSccManagerTooltip
    {
        // Subset of SCC_STATUS_* flags (sccex.h) reported via rgdwSccStatus.
        private const uint SccStatusNotControlled = 0x0000;
        private const uint SccStatusControlled = 0x0001;
        private const uint SccStatusCheckedOut = 0x0002;

        private readonly LoreVSPackage _package;
        private readonly ILoreClient _client;
        private readonly HashSet<IVsSccProject2> _registeredProjects = new HashSet<IVsSccProject2>();

        private bool _active;

        public LoreSccService(LoreVSPackage package, ILoreClient client)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>Whether Lore is currently the active source control provider.</summary>
        public bool Active => _active;

        /// <summary>The underlying Lore client (CLI today, brokered service later).</summary>
        internal ILoreClient Client => _client;

        /// <summary>
        /// Binds every loaded source-controllable project in <paramref name="solution"/> to
        /// Lore: writes the SCC cookie strings into each project (via
        /// <see cref="IVsSccProject2.SetSccLocation"/> so Visual Studio re-binds on reopen)
        /// and tracks it for glyph refreshes. Returns the number of projects bound.
        /// </summary>
        internal int OnboardSolution(IVsSolution solution, string repositoryRoot)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int count = 0;
            foreach (IVsSccProject2 project in SolutionScc.GetSccProjects(solution))
            {
                string localPath = SolutionScc.GetProjectDirectory(project as IVsHierarchy, repositoryRoot);
                try
                {
                    project.SetSccLocation(repositoryRoot, repositoryRoot, localPath, LoreGuids.ProviderName);
                    _registeredProjects.Add(project);
                    count++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LoreVS] SetSccLocation failed: {ex.Message}");
                }
            }

            RefreshAllGlyphs();
            return count;
        }

        #region IVsSccProvider

        public int SetActive()
        {
            _active = true;
            LoreLog.WriteLineAsync("[scc] SetActive - Lore is now the active source control provider.").FileAndForget("LoreVS/Scc");
            _package.OnActiveStateChange(this);
            return VSConstants.S_OK;
        }

        public int SetInactive()
        {
            _active = false;
            LoreLog.WriteLineAsync("[scc] SetInactive - Lore is no longer the active source control provider.").FileAndForget("LoreVS/Scc");
            _package.OnActiveStateChange(this);
            return VSConstants.S_OK;
        }

        public int AnyItemsUnderSourceControl(out int pfResult)
        {
            pfResult = _active && _registeredProjects.Count > 0 ? 1 : 0;
            return VSConstants.S_OK;
        }

        #endregion

        #region IVsSccManager2

        public int RegisterSccProject(
            IVsSccProject2 pscp2Project,
            string pszSccProjectName,
            string pszSccAuxPath,
            string pszSccLocalPath,
            string pszProvider)
        {
            if (pscp2Project != null)
            {
                _registeredProjects.Add(pscp2Project);
            }

            return VSConstants.S_OK;
        }

        public int UnregisterSccProject(IVsSccProject2 pscp2Project)
        {
            if (pscp2Project != null)
            {
                _registeredProjects.Remove(pscp2Project);
            }

            return VSConstants.S_OK;
        }

        public int GetSccGlyph(
            int cFiles,
            string[] rgpszFullPaths,
            VsStateIcon[] rgsiGlyphs,
            uint[] rgdwSccStatus)
        {
            if (rgpszFullPaths == null || rgsiGlyphs == null)
            {
                return VSConstants.E_INVALIDARG;
            }

            for (int i = 0; i < cFiles; i++)
            {
                LoreFileStatus status = _client.GetStatus(rgpszFullPaths[i]);
                rgsiGlyphs[i] = ToGlyph(status);

                if (rgdwSccStatus != null)
                {
                    rgdwSccStatus[i] = ToSccStatus(status);
                }
            }

            return VSConstants.S_OK;
        }

        public int GetSccGlyphFromStatus(uint dwSccStatus, VsStateIcon[] psiGlyph)
        {
            if (psiGlyph == null || psiGlyph.Length == 0)
            {
                return VSConstants.E_INVALIDARG;
            }

            if ((dwSccStatus & SccStatusControlled) == 0)
            {
                psiGlyph[0] = VsStateIcon.STATEICON_BLANK;
            }
            else if ((dwSccStatus & SccStatusCheckedOut) != 0)
            {
                psiGlyph[0] = VsStateIcon.STATEICON_CHECKEDOUT;
            }
            else
            {
                psiGlyph[0] = VsStateIcon.STATEICON_CHECKEDIN;
            }

            return VSConstants.S_OK;
        }

        public int IsInstalled(out int pbInstalled)
        {
            // The provider is installed and able to service requests.
            pbInstalled = 1;
            return VSConstants.S_OK;
        }

        public int BrowseForProject(out string pbstrDirectoryName, out int pfOK)
        {
            // Browsing for a server-side project is not supported in the read-only MVP.
            pbstrDirectoryName = null;
            pfOK = 0;
            return VSConstants.E_NOTIMPL;
        }

        public int CancelAfterBrowseForProject() => VSConstants.S_OK;

        #endregion

        #region IVsSccManagerTooltip

        public int GetGlyphTipText(IVsHierarchy phierHierarchy, uint itemidNode, out string pbstrTooltipText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            pbstrTooltipText = string.Empty;

            if (phierHierarchy == null)
            {
                return VSConstants.S_OK;
            }

            if (phierHierarchy.GetCanonicalName(itemidNode, out string path) != VSConstants.S_OK ||
                string.IsNullOrEmpty(path))
            {
                return VSConstants.S_OK;
            }

            LoreFileStatus status = _client.GetStatus(path);
            pbstrTooltipText = DescribeStatus(status);
            return VSConstants.S_OK;
        }

        #endregion

        /// <summary>
        /// Asks every registered project to re-query glyphs for all of its items. Called
        /// after activation and by the "Refresh Lore Status" command.
        /// </summary>
        public void RefreshAllGlyphs()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            (_client as LoreCliClient)?.InvalidateCache();

            foreach (IVsSccProject2 project in _registeredProjects)
            {
                project.SccGlyphChanged(0, null, null, null);
            }
        }

        private static VsStateIcon ToGlyph(LoreFileStatus status)
        {
            switch (status)
            {
                case LoreFileStatus.Unchanged:
                    return VsStateIcon.STATEICON_CHECKEDIN;
                case LoreFileStatus.Modified:
                    return VsStateIcon.STATEICON_EDITABLE;
                case LoreFileStatus.Added:
                    return VsStateIcon.STATEICON_CHECKEDOUT;
                case LoreFileStatus.Deleted:
                    return VsStateIcon.STATEICON_EXCLUDEDFROMSCC;
                case LoreFileStatus.Conflicted:
                    return VsStateIcon.STATEICON_ORPHANED;
                case LoreFileStatus.Locked:
                    return VsStateIcon.STATEICON_CHECKEDOUTEXCLUSIVE;
                case LoreFileStatus.Ignored:
                case LoreFileStatus.NotControlled:
                default:
                    return VsStateIcon.STATEICON_BLANK;
            }
        }

        private static uint ToSccStatus(LoreFileStatus status)
        {
            switch (status)
            {
                case LoreFileStatus.Unchanged:
                    return SccStatusControlled;
                case LoreFileStatus.Modified:
                case LoreFileStatus.Added:
                case LoreFileStatus.Deleted:
                case LoreFileStatus.Conflicted:
                case LoreFileStatus.Locked:
                    return SccStatusControlled | SccStatusCheckedOut;
                case LoreFileStatus.Ignored:
                case LoreFileStatus.NotControlled:
                default:
                    return SccStatusNotControlled;
            }
        }

        private static string DescribeStatus(LoreFileStatus status)
        {
            switch (status)
            {
                case LoreFileStatus.Unchanged:
                    return "Lore: up to date";
                case LoreFileStatus.Modified:
                    return "Lore: modified";
                case LoreFileStatus.Added:
                    return "Lore: added";
                case LoreFileStatus.Deleted:
                    return "Lore: deleted";
                case LoreFileStatus.Conflicted:
                    return "Lore: conflicted";
                case LoreFileStatus.Locked:
                    return "Lore: locked";
                case LoreFileStatus.Ignored:
                    return "Lore: ignored";
                default:
                    return string.Empty;
            }
        }
    }
}
