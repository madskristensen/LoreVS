using System;
using System.Diagnostics;
using System.IO;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// Locates the root of a Lore repository by walking up the directory tree looking for the
    /// <c>.lore</c> marker directory. This is pure filesystem logic with no dependency on the
    /// Lore SDK, so <see cref="LoreBrokeredClient"/> serves repository discovery locally without
    /// involving the worker.
    /// </summary>
    internal static class LoreRepositoryLocator
    {
        /// <summary>Marker directory that identifies the root of a Lore repository.</summary>
        public const string RepositoryMarker = ".lore";

        /// <summary>
        /// Returns the root of the Lore repository that contains <paramref name="path"/>,
        /// walking up the directory tree, or <see langword="null"/> if none is found.
        /// </summary>
        public static string FindRoot(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            try
            {
                DirectoryInfo dir = Directory.Exists(path)
                    ? new DirectoryInfo(path)
                    : new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path)));

                while (dir != null)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, RepositoryMarker)))
                    {
                        return dir.FullName;
                    }

                    dir = dir.Parent;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoreVS] FindRepositoryRoot failed for '{path}': {ex.Message}");
            }

            return null;
        }
    }
}
