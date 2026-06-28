using System;
using System.Collections.Generic;
using System.IO;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// Parses <c>lore status</c> output and maps Lore's single-letter status codes onto the
    /// normalized <see cref="LoreFileStatus"/> enum. This logic is intentionally separate from
    /// <see cref="LoreCliClient"/> (and from any process plumbing) so it can be unit tested
    /// directly and reused: a future SDK-based <see cref="ILoreClient"/> still needs to translate
    /// raw Lore states into the same normalized status the rest of the extension consumes.
    /// </summary>
    internal static class LoreStatusParser
    {
        /// <summary>
        /// Prose header lines emitted before the file list. These are skipped explicitly because a
        /// short leading word (notably <c>On</c> in <c>On branch ...</c>) would otherwise be mistaken
        /// for a 1-2 letter status code and produce a phantom entry.
        /// </summary>
        private static readonly string[] HeaderPrefixes =
        {
            "Repository ",
            "On branch ",
            "Remote ",
            "Local branch ",
        };

        /// <summary>
        /// Parses <c>lore status --scan</c> output. The report is human-readable: a few
        /// header lines (<c>Repository ...</c>, <c>On branch ...</c>, <c>Remote ...</c>,
        /// <c>Local branch ...</c>), optional section headers ending in <c>:</c>, and one
        /// line per changed file shaped as <c>&lt;CODE&gt; &lt;relative-path&gt;</c> — e.g.
        /// <c>A hello.txt</c> or <c>M src/foo.cpp</c>. Renames appear as
        /// <c>R old -&gt; new</c>. The format is still stabilizing pre-1.0, so anything that
        /// does not look like a status line is skipped and unknown codes degrade to
        /// <see cref="LoreFileStatus.Modified"/>. Returned keys are absolute, normalized paths.
        /// </summary>
        public static IReadOnlyDictionary<string, LoreFileStatus> Parse(string output, string repositoryRoot)
        {
            var result = new Dictionary<string, LoreFileStatus>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(output))
            {
                return result;
            }

            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string trimmed = line.Trim();

                // Skip blank lines and section headers (e.g. "Changes staged for commit:").
                if (trimmed.Length == 0 || trimmed.EndsWith(":", StringComparison.Ordinal))
                {
                    continue;
                }

                // Skip the prose header lines that precede the file list.
                if (IsHeaderLine(trimmed))
                {
                    continue;
                }

                int space = trimmed.IndexOf(' ');
                if (space <= 0)
                {
                    continue;
                }

                string code = trimmed.Substring(0, space);
                string rest = trimmed.Substring(space + 1).Trim();

                // A status code is 1-2 letters; anything longer is a prose/header line.
                if (code.Length > 2 || !IsAlpha(code))
                {
                    continue;
                }

                // Renames/moves are reported as "old -> new"; track the destination path.
                int arrow = rest.IndexOf("->", StringComparison.Ordinal);
                if (arrow >= 0)
                {
                    rest = rest.Substring(arrow + 2).Trim();
                }

                string relative = rest.Trim().Trim('"');
                if (relative.Length == 0)
                {
                    continue;
                }

                LoreFileStatus status = MapCode(code);
                string absolute = NormalizePath(Path.Combine(repositoryRoot, relative));
                result[absolute] = status;
            }

            return result;
        }

        /// <summary>Maps a Lore status code (case-insensitive) to a normalized status.</summary>
        public static LoreFileStatus MapCode(string code)
        {
            switch ((code ?? string.Empty).ToUpperInvariant())
            {
                case "A":
                    return LoreFileStatus.Added;
                case "D":
                    return LoreFileStatus.Deleted;
                case "C":
                case "U":
                    return LoreFileStatus.Conflicted;
                case "L":
                    return LoreFileStatus.Locked;
                case "I":
                    return LoreFileStatus.Ignored;
                case "R": // rename/move
                case "M":
                default:
                    return LoreFileStatus.Modified;
            }
        }

        private static bool IsHeaderLine(string trimmed)
        {
            foreach (string prefix in HeaderPrefixes)
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAlpha(string value)
        {
            foreach (char c in value)
            {
                if (!char.IsLetter(c))
                {
                    return false;
                }
            }

            return value.Length > 0;
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd('\\', '/');
            }
            catch
            {
                return path;
            }
        }
    }
}
