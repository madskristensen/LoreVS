using System.IO;
using LoreVS.SourceControl;

namespace LoreVS.UI
{
    /// <summary>
    /// A single changed file shown in the Lore Changes window. Carries the absolute path, the
    /// path relative to the repository root (used for Lore operations and display), and the
    /// normalized Lore status that drives the status badge and the diff behavior.
    /// </summary>
    public sealed class LoreChangeItem
    {
        public LoreChangeItem(string fullPath, string repositoryRoot, LoreFileStatus status)
        {
            FullPath = fullPath;
            Status = status;
            RelativePath = MakeRelative(repositoryRoot, fullPath);
            FileName = Path.GetFileName(fullPath);

            string dir = Path.GetDirectoryName(RelativePath);
            Directory = string.IsNullOrEmpty(dir) ? string.Empty : dir;
        }

        /// <summary>Absolute path of the file on disk.</summary>
        public string FullPath { get; }

        /// <summary>Path relative to the repository root (forward/back slashes as on disk).</summary>
        public string RelativePath { get; }

        /// <summary>The file name without directory.</summary>
        public string FileName { get; }

        /// <summary>The directory portion of <see cref="RelativePath"/> (empty at the root).</summary>
        public string Directory { get; }

        /// <summary>The normalized Lore status.</summary>
        public LoreFileStatus Status { get; }

        /// <summary>Single-letter status badge (M, A, D, C, L) shown next to the file.</summary>
        public string StatusBadge
        {
            get
            {
                switch (Status)
                {
                    case LoreFileStatus.Modified: return "M";
                    case LoreFileStatus.Added: return "A";
                    case LoreFileStatus.Deleted: return "D";
                    case LoreFileStatus.Conflicted: return "C";
                    case LoreFileStatus.Locked: return "L";
                    default: return string.Empty;
                }
            }
        }

        /// <summary>Human-readable status used for tooltips and accessibility.</summary>
        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case LoreFileStatus.Modified: return "Modified";
                    case LoreFileStatus.Added: return "Added";
                    case LoreFileStatus.Deleted: return "Deleted";
                    case LoreFileStatus.Conflicted: return "Conflicted";
                    case LoreFileStatus.Locked: return "Locked";
                    default: return Status.ToString();
                }
            }
        }

        private static string MakeRelative(string root, string fullPath)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath))
            {
                return fullPath ?? string.Empty;
            }

            string normalizedRoot = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(normalizedRoot, System.StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(normalizedRoot.Length);
            }

            return fullPath;
        }
    }
}
