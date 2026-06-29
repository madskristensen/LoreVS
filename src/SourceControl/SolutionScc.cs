using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// Helpers for inspecting the open solution/folder and its source-controllable
    /// projects. Centralizes the <see cref="IVsSolution"/> interop used by the
    /// onboarding and commit commands.
    /// </summary>
    internal static class SolutionScc
    {
        /// <summary>
        /// Returns the directory of the open solution, or the root folder in Open Folder
        /// mode. Returns <see langword="null"/> when nothing is open.
        /// </summary>
        public static string? GetSolutionDirectory(IVsSolution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (solution == null)
            {
                return null;
            }

            if (solution.GetSolutionInfo(out string dir, out string slnFile, out _) != VSConstants.S_OK)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(dir))
            {
                return dir.TrimEnd('\\', '/');
            }

            if (!string.IsNullOrEmpty(slnFile))
            {
                return Path.GetDirectoryName(slnFile);
            }

            return null;
        }

        /// <summary>Enumerates the loaded projects in the solution that support source control.</summary>
        public static IEnumerable<IVsSccProject2> GetSccProjects(IVsSolution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var projects = new List<IVsSccProject2>();
            if (solution == null)
            {
                return projects;
            }

            Guid empty = Guid.Empty;
            if (solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, ref empty, out IEnumHierarchies enumerator)
                != VSConstants.S_OK || enumerator == null)
            {
                return projects;
            }

            var hierarchy = new IVsHierarchy[1];
            while (enumerator.Next(1, hierarchy, out uint fetched) == VSConstants.S_OK && fetched == 1)
            {
                if (hierarchy[0] is IVsSccProject2 sccProject)
                {
                    projects.Add(sccProject);
                }
            }

            return projects;
        }

        /// <summary>
        /// Returns the directory that contains the project backed by <paramref name="hierarchy"/>,
        /// falling back to <paramref name="fallback"/> when it cannot be determined.
        /// </summary>
        public static string GetProjectDirectory(IVsHierarchy? hierarchy, string fallback)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (hierarchy != null &&
                    hierarchy.GetCanonicalName(VSConstants.VSITEMID_ROOT, out string name) == VSConstants.S_OK &&
                    !string.IsNullOrEmpty(name))
                {
                    string dir = Directory.Exists(name) ? name : Path.GetDirectoryName(name);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        return dir;
                    }
                }
            }
            catch (Exception ex)
            {
                ex.LogAsync().FireAndForget();
            }

            return fallback;
        }
    }
}
