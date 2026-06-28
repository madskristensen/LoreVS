using System;
using System.IO;
using System.Text;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// Writes a default <c>.loreignore</c> at a repository root so transient Visual Studio
    /// artifacts (the <c>.vs</c> folder, build output, user files) are not staged or committed.
    /// The <c>.vs</c> folder in particular keeps files such as <c>*.vsidx</c> locked while the
    /// IDE is running, which would otherwise fail the commit ("the process cannot access the
    /// file because it is being used by another process"). Mirrors how Git tooling seeds a
    /// <c>.gitignore</c> on init.
    /// </summary>
    internal static class LoreIgnoreFile
    {
        public const string FileName = ".loreignore";

        /// <summary>Default ignore patterns for a Visual Studio solution/folder.</summary>
        private static readonly string[] DefaultPatterns =
        {
            "# Visual Studio temporary/user files",
            ".vs/",
            "*.user",
            "*.suo",
            "",
            "# Build output",
            "bin/",
            "obj/",
            "",
            "# OS / editor cruft",
            ".DS_Store",
            "Thumbs.db",
        };

        /// <summary>
        /// Creates <c>.loreignore</c> in <paramref name="repositoryRoot"/> when one does not
        /// already exist. Returns true if a file was written. Failures are swallowed (the file
        /// is a convenience, not a hard requirement).
        /// </summary>
        public static bool EnsureDefault(string repositoryRoot)
        {
            if (string.IsNullOrEmpty(repositoryRoot) || !Directory.Exists(repositoryRoot))
            {
                return false;
            }

            string path = Path.Combine(repositoryRoot, FileName);
            if (File.Exists(path))
            {
                return false;
            }

            try
            {
                File.WriteAllText(path, string.Join(Environment.NewLine, DefaultPatterns) + Environment.NewLine,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoreVS] Failed to write .loreignore: {ex.Message}");
                return false;
            }
        }
    }
}
