using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// <see cref="ILoreClient"/> implementation backed by the native <c>LoreVcs</c> .NET SDK.
    /// Because that SDK targets .NET 9 and loads a native library, it cannot run inside the
    /// .NET Framework Visual Studio process; instead this client launches the out-of-process
    /// <c>LoreVS.Worker</c> (.NET 9/10) and talks to it over a named pipe using JSON-RPC.
    /// </summary>
    /// <remarks>
    /// The synchronous <see cref="ILoreClient"/> surface is adapted to the asynchronous
    /// <see cref="ILoreWorkerContract"/> by blocking on a worker-thread task (callers already
    /// invoke the client off the UI thread). Repository discovery is pure filesystem work and is
    /// served locally without involving the worker. When the worker cannot be launched (for
    /// example the SDK payload was not deployed) read operations report empty status and write
    /// operations return a failed result so the IDE surfaces a clear error.
    /// </remarks>
    public sealed class LoreBrokeredClient : ILoreClient, IDisposable
    {
        /// <summary>How long a cached repository status snapshot stays fresh.</summary>
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(3);

        /// <summary>How long to wait for the worker's named pipe to come up.</summary>
        private const int ConnectTimeoutMs = 15000;

        /// <summary>
        /// Safety-net timeout for a single worker call so a wedged worker can never hang the caller
        /// forever. Generous so legitimately slow writes (push/sync) are bounded by the worker's own
        /// handling rather than tripping this.
        /// </summary>
        private const int DefaultCallTimeoutMs = 60000;

        /// <summary>
        /// Shorter timeout for read/status calls, which are expected to be quick, so the Changes
        /// window and Solution Explorer glyph scans stay responsive when the worker is slow.
        /// </summary>
        private const int StatusCallTimeoutMs = 15000;

        /// <summary>
        /// Timeout for the interactive sign-in call. Sign-in blocks on a human completing the
        /// browser-based login, so it gets a much longer budget than ordinary writes. Kept slightly
        /// above the worker's own login timeout so the worker times the operation out first and
        /// returns a clean failure rather than tripping this transport-level guard.
        /// </summary>
        private const int LoginCallTimeoutMs = 330000;

        /// <summary>How long to wait before retrying the worker launch after a transient failure.</summary>
        private static readonly TimeSpan LaunchRetryBackoff = TimeSpan.FromSeconds(5);

        private readonly object _gate = new object();
        private readonly string? _workerPath;
        private readonly ConcurrentDictionary<string, CacheEntry> _cache =
            new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private Process? _workerProcess;
        private NamedPipeClientStream? _pipe;
        private JsonRpc? _rpc;
        private ILoreWorkerContract? _proxy;
        private bool _workerUnavailable;
        private DateTime _nextLaunchAttemptUtc = DateTime.MinValue;
        private bool _disposed;

        /// <summary>
        /// Creates a brokered client. <paramref name="workerExecutablePath"/> overrides the default
        /// worker location (next to this assembly under <c>Worker\LoreVS.Worker.exe</c>); it is used
        /// by tests to point at a freshly built worker.
        /// </summary>
        public LoreBrokeredClient(string? workerExecutablePath = null)
        {
            _workerPath = string.IsNullOrWhiteSpace(workerExecutablePath)
                ? DefaultWorkerPath()
                : workerExecutablePath;
        }

        /// <inheritdoc/>
        public bool IsAvailable
        {
            get
            {
                ILoreWorkerContract? proxy = GetProxy();
                if (proxy == null)
                {
                    return false;
                }

                try
                {
                    return Run(ct => proxy.IsAvailableAsync(ct), StatusCallTimeoutMs);
                }
                catch (Exception ex)
                {
                    HandleCallFailure(ex);
                    return false;
                }
            }
        }

        /// <inheritdoc/>
        public string? FindRepositoryRoot(string path) => LoreRepositoryLocator.FindRoot(path);

        /// <inheritdoc/>
        public LoreFileStatus GetStatus(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return LoreFileStatus.NotControlled;
            }

            string? root = FindRepositoryRoot(filePath);
            if (root == null)
            {
                return LoreFileStatus.NotControlled;
            }

            IReadOnlyDictionary<string, LoreFileStatus> statuses = GetRepositoryStatus(root);
            string full = NormalizePath(filePath);
            return statuses.TryGetValue(full, out LoreFileStatus status)
                ? status
                : LoreFileStatus.Unchanged;
        }

        /// <summary>
        /// Returns the cached status for <paramref name="filePath"/> without ever contacting the
        /// worker. Repository discovery is local, so a path outside any Lore repository resolves to
        /// <see cref="LoreFileStatus.NotControlled"/>. Returns <see langword="false"/> on a cache
        /// miss so a UI-thread caller can warm the cache on a background thread instead of blocking
        /// on the worker.
        /// </summary>
        public bool TryGetCachedStatus(string filePath, out LoreFileStatus status)
        {
            status = LoreFileStatus.NotControlled;
            if (string.IsNullOrEmpty(filePath))
            {
                return true;
            }

            string? root = FindRepositoryRoot(filePath);
            if (root == null)
            {
                return true;
            }

            if (_cache.TryGetValue(NormalizePath(root), out CacheEntry entry) &&
                DateTime.UtcNow - entry.TimestampUtc < CacheLifetime)
            {
                status = entry.Statuses.TryGetValue(NormalizePath(filePath), out LoreFileStatus s)
                    ? s
                    : LoreFileStatus.Unchanged;
                return true;
            }

            return false;
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

        private IReadOnlyDictionary<string, LoreFileStatus> QueryStatus(string repositoryRoot)        {
            ILoreWorkerContract? proxy = GetProxy();
            if (proxy == null)
            {
                return EmptyStatus();
            }

            try
            {
                DiagLog.Write($"[rpc] QueryStatus invoking GetRepositoryStatusAsync('{repositoryRoot}')");
                LoreStatusEntry[] entries = Run(ct => proxy.GetRepositoryStatusAsync(repositoryRoot, ct), StatusCallTimeoutMs);
                DiagLog.Write($"[rpc] QueryStatus got {entries.Length} entr(y/ies)");
                var result = new Dictionary<string, LoreFileStatus>(StringComparer.OrdinalIgnoreCase);
                foreach (LoreStatusEntry e in entries)
                {
                    if (!string.IsNullOrEmpty(e.Path))
                    {
                        result[NormalizePath(e.Path)] = e.Status;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[rpc] QueryStatus FAILED: {ex.GetType().Name}: {ex.Message}");
                HandleCallFailure(ex);
                return EmptyStatus();
            }
        }

        /// <inheritdoc/>
        public LoreRepositoryInfo GetRepositoryInfo(string repositoryRoot)
        {
            if (string.IsNullOrEmpty(repositoryRoot))
            {
                return new LoreRepositoryInfo();
            }

            ILoreWorkerContract? proxy = GetProxy();
            if (proxy == null)
            {
                return new LoreRepositoryInfo();
            }

            try
            {
                return Run(ct => proxy.GetRepositoryInfoAsync(repositoryRoot, ct), StatusCallTimeoutMs);
            }
            catch (Exception ex)
            {
                HandleCallFailure(ex);
                return new LoreRepositoryInfo();
            }
        }

        /// <inheritdoc/>
        public LoreRepositorySnapshot GetRepositorySnapshot(string repositoryRoot)
        {
            if (string.IsNullOrEmpty(repositoryRoot))
            {
                return new LoreRepositorySnapshot();
            }

            ILoreWorkerContract? proxy = GetProxy();
            if (proxy == null)
            {
                return new LoreRepositorySnapshot();
            }

            try
            {
                LoreRepositorySnapshot snapshot = Run(ct => proxy.GetRepositorySnapshotAsync(repositoryRoot, ct), StatusCallTimeoutMs)
                    ?? new LoreRepositorySnapshot();

                // Refresh the status cache from the same pass so a later GetRepositoryStatus call
                // (e.g. glyph refresh) is served from cache instead of issuing another status scan.
                LoreStatusEntry[] entries = snapshot.Files ?? Array.Empty<LoreStatusEntry>();
                var statuses = new Dictionary<string, LoreFileStatus>(StringComparer.OrdinalIgnoreCase);
                foreach (LoreStatusEntry e in entries)
                {
                    if (!string.IsNullOrEmpty(e.Path))
                    {
                        statuses[NormalizePath(e.Path)] = e.Status;
                    }
                }

                _cache[NormalizePath(repositoryRoot)] = new CacheEntry(statuses, DateTime.UtcNow);
                return snapshot;
            }
            catch (Exception ex)
            {
                HandleCallFailure(ex);
                return new LoreRepositorySnapshot();
            }
        }

        /// <inheritdoc/>
        public LoreBranchEntry[] ListBranches(string repositoryRoot)
        {
            if (string.IsNullOrEmpty(repositoryRoot))
            {
                return Array.Empty<LoreBranchEntry>();
            }

            ILoreWorkerContract? proxy = GetProxy();
            if (proxy == null)
            {
                return Array.Empty<LoreBranchEntry>();
            }

            try
            {
                return Run(ct => proxy.ListBranchesAsync(repositoryRoot, ct), StatusCallTimeoutMs)
                    ?? Array.Empty<LoreBranchEntry>();
            }
            catch (Exception ex)
            {
                HandleCallFailure(ex);
                return Array.Empty<LoreBranchEntry>();
            }
        }

        /// <inheritdoc/>
        public LoreCommandResult CreateBranch(string workingDirectory, string branchName, string identity, bool checkout)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.CreateBranchAsync(workingDirectory, branchName, identity, checkout, ct));
        }

        /// <inheritdoc/>
        public LoreCommandResult SwitchBranch(string workingDirectory, string branchName)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.SwitchBranchAsync(workingDirectory, branchName, ct));
        }

        /// <inheritdoc/>
        public LoreMergeResult MergeBranch(string workingDirectory, string sourceBranch, string identity)
        {
            InvalidateCache();

            ILoreWorkerContract? proxy = GetProxy();
            if (proxy == null)
            {
                return new LoreMergeResult { Success = false, ErrorMessage = WorkerUnavailable().CombinedText };
            }

            try
            {
                return Run(ct => proxy.MergeBranchAsync(workingDirectory, sourceBranch, identity, ct))
                    ?? new LoreMergeResult { Success = false, ErrorMessage = "The merge produced no result." };
            }
            catch (Exception ex)
            {
                HandleCallFailure(ex);
                return new LoreMergeResult { Success = false, ErrorMessage = WorkerUnavailable().CombinedText };
            }
        }

        /// <inheritdoc/>
        public LoreCommandResult ResolveMerge(string workingDirectory, string[] paths, string message, string identity)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.ResolveMergeAsync(workingDirectory, paths, message, identity, ct));
        }

        /// <inheritdoc/>
        public LoreCommandResult AbortMerge(string workingDirectory)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.AbortMergeAsync(workingDirectory, ct));
        }

        /// <inheritdoc/>
        public LoreCommandResult CreateRepository(string workingDirectory, string repositoryUrl, string identity)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.CreateRepositoryAsync(workingDirectory, repositoryUrl, identity, ct));
        }

        /// <inheritdoc/>
        public LoreCommandResult CloneRepository(string repositoryUrl, string targetDirectory, string identity)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.CloneRepositoryAsync(repositoryUrl, targetDirectory, identity, ct));
        }

        /// <inheritdoc/>
        public LoreAuthResult Login(string workingDirectory, string remoteUrl)
        {
            ILoreWorkerContract? proxy = GetProxy();
            if (proxy == null)
            {
                return new LoreAuthResult
                {
                    Success = false,
                    ErrorMessage = "The Lore worker could not be started, so sign-in is unavailable.",
                };
            }

            try
            {
                return Run(
                    ct => proxy.LoginAsync(workingDirectory ?? string.Empty, remoteUrl ?? string.Empty, ct),
                    LoginCallTimeoutMs);
            }
            catch (Exception ex)
            {
                HandleCallFailure(ex);
                return new LoreAuthResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        /// <inheritdoc/>
        public LoreCommandResult StageAll(string workingDirectory)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.StageAllAsync(workingDirectory, ct));
        }

        /// <inheritdoc/>
        public LoreCommandResult Commit(string workingDirectory, string message, string identity)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.CommitAsync(workingDirectory, message, identity, ct));
        }

        /// <inheritdoc/>
        public LoreCommandResult Amend(string workingDirectory, string message, string identity)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.AmendAsync(workingDirectory, message, identity, ct));
        }

        /// <inheritdoc/>
        public LoreCommandResult CommitFiles(string workingDirectory, string[] paths, string message, string identity, bool amend)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.CommitFilesAsync(workingDirectory, paths, message, identity, amend, ct));
        }

        /// <inheritdoc/>
        public LoreCommandResult Push(string workingDirectory)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.PushAsync(workingDirectory, ct));
        }

        /// <inheritdoc/>
        public LoreCommandResult Sync(string workingDirectory)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.SyncAsync(workingDirectory, ct));
        }

        /// <inheritdoc/>
        public LoreCommandResult ResetFiles(string workingDirectory, string[] paths)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.ResetFilesAsync(workingDirectory, paths, ct));
        }

        /// <inheritdoc/>
        public LoreCommandResult WriteFileAtRevision(string workingDirectory, string relativePath, string revision, string outputPath)
        {
            return Invoke((proxy, ct) => proxy.WriteFileAtRevisionAsync(workingDirectory, relativePath, revision, outputPath, ct));
        }

        /// <summary>Clears any cached status so the next query re-runs against the worker.</summary>
        public void InvalidateCache() => _cache.Clear();

        /// <summary>
        /// Runs a write operation against the worker, returning a failed result when the worker is
        /// unavailable or the call fails.
        /// </summary>
        private LoreCommandResult Invoke(
            Func<ILoreWorkerContract, CancellationToken, Task<LoreCommandResult>> operation)
        {
            ILoreWorkerContract? proxy = GetProxy();
            if (proxy == null)
            {
                return WorkerUnavailable();
            }

            try
            {
                return Run(ct => operation(proxy, ct));
            }
            catch (Exception ex)
            {
                HandleCallFailure(ex);
                return WorkerUnavailable();
            }
        }

        private static LoreCommandResult WorkerUnavailable() =>
            LoreCommandResult.Failed(
                "The Lore worker could not be started. Reinstall the extension so the worker payload " +
                "is deployed, then try again.");

        /// <summary>Lazily launches the worker and returns its JSON-RPC proxy, or null on failure.</summary>
        private ILoreWorkerContract? GetProxy()
        {
            if (_workerUnavailable || _disposed)
            {
                return null;
            }

            lock (_gate)
            {
                if (_workerUnavailable || _disposed)
                {
                    return null;
                }

                if (_proxy != null && _workerProcess != null && !_workerProcess.HasExited)
                {
                    return _proxy;
                }

                // A previously connected worker died; tear the stale connection down and relaunch.
                if (_proxy != null)
                {
                    TeardownConnection();
                }

                // Back off briefly after a transient launch failure so rapid callers (glyph queries)
                // do not spin relaunching the worker every time.
                if (DateTime.UtcNow < _nextLaunchAttemptUtc)
                {
                    return null;
                }

                try
                {
                    StartWorker();
                    _nextLaunchAttemptUtc = DateTime.MinValue;
                    return _proxy;
                }
                catch (Exception ex)
                {
                    LogError(ex);
                    TeardownConnection();

                    // Only a genuinely missing worker payload is unrecoverable for the session. Transient
                    // failures (pipe connect timeout, a momentary Process.Start / AV file lock) must not
                    // permanently disable the client - allow a later call to retry after a short backoff.
                    if (ex is FileNotFoundException)
                    {
                        _workerUnavailable = true;
                    }
                    else
                    {
                        _nextLaunchAttemptUtc = DateTime.UtcNow + LaunchRetryBackoff;
                    }

                    return null;
                }
            }
        }

        private void StartWorker()
        {
            if (!File.Exists(_workerPath))
            {
                throw new FileNotFoundException(
                    $"The Lore worker was not found at '{_workerPath}'. Falling back to the Lore CLI.",
                    _workerPath);
            }

            string pipeName = "lorevs-" + Guid.NewGuid().ToString("N");

            var psi = new ProcessStartInfo
            {
                FileName = _workerPath!,
                Arguments = "\"" + pipeName + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_workerPath) ?? Environment.CurrentDirectory,
            };

            _workerProcess = Process.Start(psi);

            // Drain the worker's stderr. Redirecting it (RedirectStandardError = true) without
            // reading lets the OS pipe buffer fill, which blocks the worker mid-write and would
            // hang every synchronous call that is waiting on the worker to respond.
            if (_workerProcess != null)
            {
                _workerProcess.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        Debug.WriteLine("[LoreVS.Worker] " + e.Data);
                    }
                };
                _workerProcess.BeginErrorReadLine();
            }

            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            _pipe.Connect(ConnectTimeoutMs);
            DiagLog.Write($"[rpc] StartWorker connected pipe '{pipeName}'");

            var formatter = new SystemTextJsonFormatter();
            var handler = new HeaderDelimitedMessageHandler(_pipe, _pipe, formatter);
            _rpc = new JsonRpc(handler);
            _proxy = _rpc.Attach<ILoreWorkerContract>();
            _rpc.StartListening();
            DiagLog.Write($"[rpc] StartWorker attached proxy and started listening (StreamJsonRpc {typeof(JsonRpc).Assembly.GetName().Version})");
        }

        private void MarkWorkerFailed()
        {
            lock (_gate)
            {
                TeardownConnection();
                _workerUnavailable = true;
            }
        }

        /// <summary>
        /// Decides whether a worker-call exception means the worker transport itself is dead (so it
        /// must be torn down and permanently disabled) versus a single operation merely failing.
        /// A failed Lore operation - most importantly a non-zero result surfaced as a
        /// <see cref="RemoteInvocationException"/>, or a per-call <see cref="TimeoutException"/> -
        /// must NOT disable the worker: doing so would also kill the unrelated file-status scans
        /// that drive Solution Explorer glyphs, freezing the whole experience after one bad call.
        /// Only a genuinely broken pipe / exited process recycles the worker.
        /// </summary>
        private bool ShouldRecycleWorker(Exception ex)
        {
            if (ex is RemoteInvocationException)
            {
                // The remote method ran and threw (e.g. a LoreError). The worker is still healthy.
                return false;
            }

            if (ex is ConnectionLostException ||
                ex is ObjectDisposedException ||
                ex is IOException)
            {
                return true;
            }

            Process? worker = _workerProcess;
            return worker == null || worker.HasExited;
        }

        /// <summary>
        /// Logs <paramref name="ex"/> and recycles the worker only when the transport is dead, so a
        /// single failed/timed-out operation never disables glyphs and the rest of the session.
        /// </summary>
        private void HandleCallFailure(Exception ex)
        {
            LogError(ex);
            if (ShouldRecycleWorker(ex))
            {
                MarkWorkerFailed();
            }
        }

        private void TeardownConnection()
        {
            try { _rpc?.Dispose(); } catch { /* best effort */ }
            try { _pipe?.Dispose(); } catch { /* best effort */ }

            try
            {
                if (_workerProcess != null && !_workerProcess.HasExited)
                {
                    _workerProcess.Kill();
                }
            }
            catch { /* best effort */ }
            finally
            {
                try { _workerProcess?.Dispose(); } catch { /* best effort */ }
            }

            _rpc = null;
            _pipe = null;
            _proxy = null;
            _workerProcess = null;
        }

        /// <summary>
        /// Runs an async worker call to completion on a worker thread, bounded by a per-call timeout
        /// so a wedged worker can never hang the caller forever. <see cref="Task.Run{T}(Func{Task{T}}, CancellationToken)"/>
        /// detaches from any captured synchronization context so blocking here cannot deadlock the UI thread.
        /// </summary>
        private static T Run<T>(Func<CancellationToken, Task<T>> operation, int timeoutMs = DefaultCallTimeoutMs)
        {
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                try
                {
#pragma warning disable VSTHRD002 // callers always invoke off the UI thread, and Task.Run detaches from any captured context
                    return Task.Run(() => operation(cts.Token), cts.Token).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    // Surface a stuck worker as a per-call timeout. HandleCallFailure treats this as a
                    // non-fatal operation failure (it does not recycle the worker), so unrelated status
                    // scans keep working instead of the whole session freezing on one wedged call.
                    throw new TimeoutException($"The Lore worker did not respond within {timeoutMs} ms.");
                }
            }
        }

        private static string DefaultWorkerPath()
        {
            try
            {
                string? baseDir = Path.GetDirectoryName(typeof(LoreBrokeredClient).Assembly.Location);
                return Path.Combine(baseDir ?? string.Empty, "Worker", "LoreVS.Worker.exe");
            }
            catch
            {
                return "LoreVS.Worker.exe";
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

        private static void LogError(Exception ex)
        {
            try
            {
                // Prefer the toolkit's logger; fall back to Debug when running outside Visual Studio.
                ex.LogAsync().FireAndForget();
            }
            catch
            {
                Debug.WriteLine($"[LoreVS] brokered client error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (_gate)
            {
                TeardownConnection();
            }

            _cache.Clear();
        }

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
