using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// <see cref="ILoreClient"/> implementation that shells out to the <c>lore</c>
    /// command line interface. This is the MVP/fallback bridge; it has no managed or
    /// native dependency on the Lore SDK and therefore works from the .NET Framework
    /// in-process VSPackage without a TFM conflict.
    /// </summary>
    public sealed class LoreCliClient : ILoreClient
    {
        /// <summary>Marker directory that identifies the root of a Lore repository.</summary>
        private const string RepositoryMarker = ".lore";

        /// <summary>How long a cached repository status snapshot stays fresh.</summary>
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(3);

        private readonly ConcurrentDictionary<string, CacheEntry> _cache =
            new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private bool _cliMissing;
        private bool? _available;
        private string _executablePath = "lore";

        /// <inheritdoc/>
        public string ExecutablePath
        {
            get => _executablePath;
            set
            {
                string newValue = string.IsNullOrWhiteSpace(value) ? "lore" : value.Trim();
                if (!string.Equals(newValue, _executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    _executablePath = newValue;
                    _available = null;
                    _cliMissing = false;
                    _cache.Clear();
                }
            }
        }

        /// <inheritdoc/>
        public bool IsAvailable
        {
            get
            {
                if (_available.HasValue)
                {
                    return _available.Value;
                }

                try
                {
                    LoreCommandResult result = Run("--version", null, 5000);
                    _available = result.ExitCode != -1;
                }
                catch
                {
                    _available = false;
                }

                _cliMissing = !_available.Value;
                return _available.Value;
            }
        }

        /// <inheritdoc/>
        public string FindRepositoryRoot(string path)
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

        /// <inheritdoc/>
        public LoreFileStatus GetStatus(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return LoreFileStatus.NotControlled;
            }

            string root = FindRepositoryRoot(filePath);
            if (root == null)
            {
                return LoreFileStatus.NotControlled;
            }

            IReadOnlyDictionary<string, LoreFileStatus> statuses = GetRepositoryStatus(root);

            string full = NormalizePath(filePath);
            if (statuses.TryGetValue(full, out LoreFileStatus status))
            {
                return status;
            }

            // Inside a repo but not reported as changed => tracked & unchanged.
            return LoreFileStatus.Unchanged;
        }

        /// <inheritdoc/>
        public IReadOnlyDictionary<string, LoreFileStatus> GetRepositoryStatus(string repositoryRoot)
        {
            if (string.IsNullOrEmpty(repositoryRoot))
            {
                return EmptyStatus();
            }

            string key = NormalizePath(repositoryRoot);

            if (_cache.TryGetValue(key, out CacheEntry entry) &&
                DateTime.UtcNow - entry.TimestampUtc < CacheLifetime)
            {
                return entry.Statuses;
            }

            IReadOnlyDictionary<string, LoreFileStatus> fresh = QueryStatus(repositoryRoot);
            _cache[key] = new CacheEntry(fresh, DateTime.UtcNow);
            return fresh;
        }

        /// <summary>
        /// Invalidates any cached status so the next query re-runs the CLI. Called by the
        /// "Refresh Lore Status" command and after the solution changes.
        /// </summary>
        public void InvalidateCache() => _cache.Clear();

        private IReadOnlyDictionary<string, LoreFileStatus> QueryStatus(string repositoryRoot)
        {
            if (_cliMissing)
            {
                return EmptyStatus();
            }

            try
            {
                LoreCommandResult result = Run("status --scan --no-pager .", repositoryRoot, 15000);
                if (result.ExitCode == -1)
                {
                    // Executable not found on PATH; stop trying for this session.
                    _cliMissing = true;
                    _available = false;
                    Debug.WriteLine("[LoreVS] 'lore' CLI not found; status disabled.");
                    return EmptyStatus();
                }

                if (!result.Success)
                {
                    Debug.WriteLine($"[LoreVS] 'lore status' exited with {result.ExitCode} in '{repositoryRoot}'.");
                    return EmptyStatus();
                }

                return ParseStatus(result.Output, repositoryRoot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoreVS] 'lore status' failed: {ex.Message}");
                return EmptyStatus();
            }
        }

        /// <summary>
        /// Parses <c>lore status --scan</c> output. The report is human-readable: a few
        /// header lines (<c>Repository ...</c>, <c>On branch ...</c>, <c>Remote ...</c>,
        /// <c>Local branch ...</c>), optional section headers ending in <c>:</c>, and one
        /// line per changed file shaped as <c>&lt;CODE&gt; &lt;relative-path&gt;</c> — e.g.
        /// <c>A hello.txt</c> or <c>M src/foo.cpp</c>. Renames appear as
        /// <c>R old -&gt; new</c>. The format is still stabilizing pre-1.0, so anything that
        /// does not look like a status line is skipped and unknown codes degrade to
        /// <see cref="LoreFileStatus.Modified"/>.
        /// </summary>
        private static IReadOnlyDictionary<string, LoreFileStatus> ParseStatus(string output, string repositoryRoot)
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

        private static LoreFileStatus MapCode(string code)
        {
            switch (code.ToUpperInvariant())
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

        /// <inheritdoc/>
        public LoreCommandResult CreateRepository(string workingDirectory, string repositoryUrl, string identity)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(repositoryUrl))
            {
                return LoreCommandResult.Failed("A working directory and repository URL are required.");
            }

            string args = BuildArgs(identity, "repository create " + Quote(repositoryUrl));
            LoreCommandResult result = Run(args, workingDirectory, 120000);
            InvalidateCache();
            return result;
        }

        /// <inheritdoc/>
        public LoreCommandResult StageAll(string workingDirectory)
        {
            // 'lore stage --scan' requires a path argument; '.' walks the whole repository
            // (the command runs with the repository root as its working directory).
            LoreCommandResult result = Run("stage --scan --no-pager .", workingDirectory, 120000);
            InvalidateCache();
            return result;
        }

        /// <inheritdoc/>
        public LoreCommandResult Commit(string workingDirectory, string message, string identity)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return LoreCommandResult.Failed("A commit message is required.");
            }

            string args = BuildArgs(identity, "commit " + Quote(message));
            LoreCommandResult result = Run(args, workingDirectory, 120000);
            InvalidateCache();
            return result;
        }

        /// <inheritdoc/>
        public LoreCommandResult Push(string workingDirectory)
        {
            LoreCommandResult result = Run("push --no-pager", workingDirectory, 300000);
            InvalidateCache();
            return result;
        }

        /// <inheritdoc/>
        public LoreCommandResult Sync(string workingDirectory)
        {
            LoreCommandResult result = Run("sync --no-pager", workingDirectory, 300000);
            InvalidateCache();
            return result;
        }

        /// <summary>Prepends global options (currently <c>--identity</c>) before a verb.</summary>
        private static string BuildArgs(string identity, string verbAndArgs)
        {
            return string.IsNullOrWhiteSpace(identity)
                ? verbAndArgs
                : "--identity " + Quote(identity) + " " + verbAndArgs;
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        /// <summary>
        /// Runs the Lore CLI and captures its output. Returns a result whose
        /// <see cref="LoreCommandResult.ExitCode"/> is -1 when the executable could not be
        /// launched (not found) or the operation timed out.
        /// </summary>
        private LoreCommandResult Run(string arguments, string workingDirectory, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _executablePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (!string.IsNullOrEmpty(workingDirectory))
            {
                psi.WorkingDirectory = workingDirectory;
            }

            try
            {
                using (var process = new Process { StartInfo = psi })
                {
                    var stdout = new StringBuilder();
                    var stderr = new StringBuilder();
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { /* best effort */ }
                        return new LoreCommandResult(false, -1, stdout.ToString(), "Lore command timed out.");
                    }

                    // Allow async readers to flush.
                    process.WaitForExit();

                    int exit = process.ExitCode;
                    return new LoreCommandResult(exit == 0, exit, stdout.ToString(), stderr.ToString());
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                _cliMissing = true;
                _available = false;
                return new LoreCommandResult(false, -1, string.Empty,
                    $"The Lore CLI ('{_executablePath}') was not found. Install it and ensure it is on PATH, or set its path in Tools > Options > Lore.");
            }
            catch (Exception ex)
            {
                return new LoreCommandResult(false, -1, string.Empty, ex.Message);
            }
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

        private static IReadOnlyDictionary<string, LoreFileStatus> EmptyStatus() =>
            new Dictionary<string, LoreFileStatus>(0);

        private readonly struct CacheEntry
        {
            public CacheEntry(IReadOnlyDictionary<string, LoreFileStatus> statuses, DateTime timestampUtc)
            {
                Statuses = statuses;
                TimestampUtc = timestampUtc;
            }

            public IReadOnlyDictionary<string, LoreFileStatus> Statuses { get; }

            public DateTime TimestampUtc { get; }
        }
    }
}
