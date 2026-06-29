using System;
using System.Collections.Concurrent;
using System.IO;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// Locates the root of a Lore repository by walking up the directory tree looking for the
    /// <c>.lore</c> marker directory. This is pure filesystem logic with no dependency on the
    /// Lore SDK, so <see cref="LoreBrokeredClient"/> serves repository discovery locally without
    /// involving the worker. Discovered roots are cached and matched by prefix so the common
    /// glyph hot path (Visual Studio querying status for many files at once) does not re-walk the
    /// directory tree for every file.
    /// </summary>
    internal static class LoreRepositoryLocator
    {
        /// <summary>Marker directory that identifies the root of a Lore repository.</summary>
        public const string RepositoryMarker = ".lore";

        // Repository roots discovered this session. A path under a known root resolves without
        // touching the filesystem, which keeps the per-file glyph queries on the UI thread cheap.
        private static readonly ConcurrentDictionary<string, byte> _knownRoots =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the root of the Lore repository that contains <paramref name="path"/>,
        /// walking up the directory tree, or <see langword="null"/> if none is found.
        /// </summary>
        public static string? FindRoot(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            try
            {
                string full = Path.GetFullPath(path);
                if (TryMatchKnownRoot(full, out string? known))
                {
                    return known;
                }

                DirectoryInfo? dir = Directory.Exists(full)
                    ? new DirectoryInfo(full)
                    : new DirectoryInfo(Path.GetDirectoryName(full) ?? full);

                while (dir != null)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, RepositoryMarker)))
                    {
                        _knownRoots[dir.FullName.TrimEnd('\\', '/')] = 0;
                        return dir.FullName;
                    }

                    dir = dir.Parent;
                }
            }
            catch (Exception ex)
            {
                ex.LogAsync().FireAndForget();
            }

            return null;
        }

        private static bool TryMatchKnownRoot(string fullPath, out string? root)
        {
            foreach (string candidate in _knownRoots.Keys)
            {
                if (fullPath.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                    fullPath.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    root = candidate;
                    return true;
                }
            }

            root = null;
            return false;
        }
    }
}