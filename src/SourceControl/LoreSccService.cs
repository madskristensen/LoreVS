using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// The Lore source control provider service. A single instance is proffered by
    /// <see cref="LoreVSPackage"/> and registered with the Visual Studio SCC manager.
    /// For the read-only MVP it implements provider activation
    /// (<see cref="IVsSccProvider"/>) and status glyphs/tooltips
    /// (<see cref="IVsSccManager2"/>, <see cref="IVsSccManagerTooltip"/>). It also supplies a
    /// custom green-plus glyph for newly added (not yet committed) files via
    /// <see cref="IVsSccGlyphs2"/>.
    /// </summary>
    [Guid(LoreGuids.SccServiceString)]
    public sealed class LoreSccService : IVsSccProvider, IVsSccManager2, IVsSccManagerTooltip, IVsSccGlyphs2
    {
        // Subset of SCC_STATUS_* flags (sccex.h) reported via rgdwSccStatus.
        private const uint SccStatusNotControlled = 0x0000;
        private const uint SccStatusControlled = 0x0001;
        private const uint SccStatusCheckedOut = 0x0002;

        private readonly LoreVSPackage _package;
        private readonly ILoreClient _client;
        private readonly HashSet<IVsSccProject2> _registeredProjects = new HashSet<IVsSccProject2>();

        private bool _active;

        // Repository roots with a background status warm-up in flight, keyed by root. Prevents a
        // warm-up storm when Visual Studio asks for glyphs for many files at once, while still
        // letting distinct repositories warm concurrently.
        private readonly ConcurrentDictionary<string, byte> _warmingRoots =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // Index of the first custom SCC glyph in Visual Studio's merged image list, supplied via
        // IVsSccGlyphs2.GetCustomGlyphMonikerList. Custom glyphs are addressed as
        // (VsStateIcon)(_customGlyphBaseIndex + offset). Until VS asks for the custom list, added
        // files fall back to a built-in glyph.
        private uint _customGlyphBaseIndex;
        private bool _customGlyphsRegistered;

        public LoreSccService(LoreVSPackage package, ILoreClient client)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>Whether Lore is currently the active source control provider.</summary>
        public bool Active => _active;

        /// <summary>The underlying Lore client (the out-of-process worker / SDK).</summary>
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
                    ex.LogAsync().FireAndForget();
                }
            }

            RefreshAllGlyphs();
            DiagLog.Write($"[scc] OnboardSolution registered {count} project(s) for root '{repositoryRoot}'");
            return count;
        }

        #region IVsSccProvider

        public int SetActive()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _active = true;
            DiagLog.Write("[scc] SetActive - Lore is now the active provider");
            LoreLog.WriteLineAsync("[scc] SetActive - Lore is now the active source control provider.").FileAndForget("LoreVS/Scc");
            _package.OnActiveStateChange(this);
            return VSConstants.S_OK;
        }

        public int SetInactive()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _active = false;
            DiagLog.Write("[scc] SetInactive - Lore is no longer the active provider");
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
                LoreFileStatus status = GetStatusForGlyph(rgpszFullPaths[i]);
                rgsiGlyphs[i] = ToGlyph(status);

                if (rgdwSccStatus != null)
                {
                    rgdwSccStatus[i] = ToSccStatus(status);
                }
            }

            DiagLog.Write($"[scc] GetSccGlyph queried {cFiles} file(s); first='{(cFiles > 0 ? rgpszFullPaths[0] : "<none>")}'");
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
            pbstrDirectoryName = string.Empty;
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

            LoreFileStatus status = GetStatusForGlyph(path);
            pbstrTooltipText = DescribeStatus(status);
            return VSConstants.S_OK;
        }

        #endregion

        #region IVsSccGlyphs2

        // Offsets of each custom glyph within the list returned to Visual Studio. Only the
        // "added but not yet committed" state uses a custom (green-plus) glyph today.
        private const uint CustomGlyphAddedOffset = 0;

        private static readonly ImageMoniker[] CustomGlyphMonikers =
        {
            KnownMonikers.PendingAddNode,
        };

        public IVsImageMonikerImageList GetCustomGlyphMonikerList(uint baseIndex)
        {
            _customGlyphBaseIndex = baseIndex;
            _customGlyphsRegistered = true;
            return new LoreSccGlyphList(CustomGlyphMonikers);
        }

        #endregion

        /// <summary>
        /// Asks every registered project to re-query glyphs for all of its items. Called
        /// after activation and by the "Refresh Lore Status" command.
        /// </summary>
        public void RefreshAllGlyphs()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            (_client as LoreBrokeredClient)?.InvalidateCache();
            RaiseGlyphsChanged();
        }

        /// <summary>
        /// Re-raises the SCC glyph for every registered project WITHOUT invalidating the status
        /// cache, so the background warm-up can repaint once fresh status has been cached.
        /// </summary>
        private void RaiseGlyphsChanged()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DiagLog.Write($"[scc] RaiseGlyphsChanged for {_registeredProjects.Count} registered project(s)");
            foreach (IVsSccProject2 project in _registeredProjects)
            {
                project.SccGlyphChanged(0, null, null, null);
            }
        }

        /// <summary>
        /// Returns the Lore status for <paramref name="path"/> for glyph/tooltip rendering without
        /// ever blocking the UI thread on the out-of-process worker. Visual Studio invokes the SCC
        /// glyph/tooltip entry points on the UI thread, so on a cache miss this returns a neutral
        /// status and warms the cache on a background thread, repainting glyphs when the real status
        /// becomes available.
        /// </summary>
        private LoreFileStatus GetStatusForGlyph(string path)
        {
            if (_client is LoreBrokeredClient brokered && brokered.TryGetCachedStatus(path, out LoreFileStatus cached))
            {
                return cached;
            }

            // Warm the cache once per repository root rather than once globally, so glyph queries
            // for files in another repository are not starved while one root is being scanned.
            string? root = _client.FindRepositoryRoot(path);
            if (root != null && _warmingRoots.TryAdd(root, 0))
            {
                DiagLog.Write($"[scc] GetStatusForGlyph cache miss for '{path}' -> warming '{root}'");
                WarmStatusAsync(path, root).FileAndForget("LoreVS/WarmGlyph");
            }

            return LoreFileStatus.Unchanged;
        }

        /// <summary>
        /// Fetches status on a background thread (populating the client cache), then repaints glyphs
        /// on the UI thread. Keeps the UI thread free while the worker is contacted.
        /// </summary>
        private async Task WarmStatusAsync(string path, string root)
        {
            LoreFileStatus result = LoreFileStatus.NotControlled;
            try
            {
                await Task.Run(() => result = _client.GetStatus(path));
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[scc] WarmStatusAsync FAILED for '{path}': {ex.GetType().Name}: {ex.Message}");
                throw;
            }
            finally
            {
                _warmingRoots.TryRemove(root, out _);
            }

            DiagLog.Write($"[scc] WarmStatusAsync got {result} for '{path}' -> repaint");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            RaiseGlyphsChanged();
        }

        private VsStateIcon ToGlyph(LoreFileStatus status)
        {
            switch (status)
            {
                case LoreFileStatus.Unchanged:
                    return VsStateIcon.STATEICON_CHECKEDIN;
                case LoreFileStatus.Modified:
                case LoreFileStatus.Renamed:
                    return VsStateIcon.STATEICON_CHECKEDOUT;
                case LoreFileStatus.Added:
                    return _customGlyphsRegistered
                        ? (VsStateIcon)(_customGlyphBaseIndex + CustomGlyphAddedOffset)
                        : VsStateIcon.STATEICON_CHECKEDOUT;
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
                case LoreFileStatus.Renamed:
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
                case LoreFileStatus.Renamed:
                    return "Lore: renamed";
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
