using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using LoreVS.SourceControl;

namespace LoreVS.Tests
{
    /// <summary>
    /// End-to-end tests for <see cref="LoreBrokeredClient"/>. These launch the real .NET worker
    /// process and exercise the full JSON-RPC-over-named-pipe transport (including the
    /// <c>SystemTextJsonFormatter</c> and the native <c>LoreVcs</c> SDK), then assert the worker's
    /// status events map to the same normalized <see cref="LoreFileStatus"/> values the CLI client
    /// produced. The tests are skipped (Inconclusive) when the worker has not been built so the
    /// unit-test suite still runs on machines without the .NET 9 payload.
    /// </summary>
    [TestClass]
    public class LoreBrokeredClientIntegrationTests
    {
        private string _tempRoot = string.Empty;

        [TestInitialize]
        public void Setup()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "lorevs_it_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best effort; the OS reclaims the temp directory eventually.
            }
        }

        [TestMethod]
        public void IsAvailable_LaunchesWorkerAndReturnsTrue()
        {
            string worker = RequireWorker();

            using var client = new LoreBrokeredClient(worker);
            Assert.IsTrue(client.IsAvailable, "The worker should report the native Lore SDK as available.");
        }

        [TestMethod]
        public void GetRepositoryStatus_MapsSeededRepoOverRpc()
        {
            string worker = RequireWorker();
            string repoPath = Path.Combine(_tempRoot, "repo");
            SeedRepository(worker, repoPath);

            using var client = new LoreBrokeredClient(worker);
            IReadOnlyDictionary<string, LoreFileStatus> status = client.GetRepositoryStatus(repoPath);

            string modified = Path.GetFullPath(Path.Combine(repoPath, "file.txt"));
            string added = Path.GetFullPath(Path.Combine(repoPath, "untracked.txt"));

            Assert.IsTrue(status.ContainsKey(modified), "The committed-then-edited file should be reported.");
            Assert.AreEqual(LoreFileStatus.Modified, status[modified]);
            Assert.IsTrue(status.ContainsKey(added), "The untracked file should be reported.");
            Assert.AreEqual(LoreFileStatus.Added, status[added]);
        }

        [TestMethod]
        public void GetRepositorySnapshot_ReturnsFilesAndBranchInOnePass()
        {
            string worker = RequireWorker();
            string repoPath = Path.Combine(_tempRoot, "repo");
            SeedRepository(worker, repoPath);

            using var client = new LoreBrokeredClient(worker);
            LoreRepositorySnapshot snapshot = client.GetRepositorySnapshot(repoPath);

            Assert.IsNotNull(snapshot, "The snapshot should never be null.");
            Assert.IsNotNull(snapshot.Files, "The snapshot file list should never be null.");

            string modified = Path.GetFullPath(Path.Combine(repoPath, "file.txt"));
            string added = Path.GetFullPath(Path.Combine(repoPath, "untracked.txt"));

            var byPath = new Dictionary<string, LoreFileStatus>(StringComparer.OrdinalIgnoreCase);
            foreach (LoreStatusEntry entry in snapshot.Files)
            {
                byPath[Path.GetFullPath(entry.Path)] = entry.Status;
            }

            Assert.IsTrue(byPath.ContainsKey(modified), "The committed-then-edited file should be reported.");
            Assert.AreEqual(LoreFileStatus.Modified, byPath[modified]);
            Assert.IsTrue(byPath.ContainsKey(added), "The untracked file should be reported.");
            Assert.AreEqual(LoreFileStatus.Added, byPath[added]);
        }

        [TestMethod]
        public void ConcurrentStatusCalls_DoNotCrashWorker()
        {
            string worker = RequireWorker();
            string repoPath = Path.Combine(_tempRoot, "repo");
            SeedRepository(worker, repoPath);

            using var client = new LoreBrokeredClient(worker);

            // Reproduce the IDE's call pattern: the glyph-warming path and the Lore Changes window
            // fan out several status/snapshot queries against the same repository at once. The native
            // SDK drives its own runtime per call; if those calls are allowed to run concurrently the
            // worker can crash, which surfaces in the package as a ConnectionLostException and an
            // empty/never-updating window. Each call must therefore complete and agree on the result.
            const int parallelism = 16;
            var tasks = new List<Task<int>>(parallelism);
            for (int i = 0; i < parallelism; i++)
            {
                bool snapshot = (i % 2) == 0;
                tasks.Add(Task.Run(() =>
                    snapshot
                        ? (client.GetRepositorySnapshot(repoPath)?.Files?.Length ?? -1)
                        : client.GetRepositoryStatus(repoPath).Count));
            }

#pragma warning disable VSTHRD002 // Test deliberately blocks to join the parallel worker calls.
            Task.WaitAll(tasks.ToArray());

            int[] counts = tasks.Select(t => t.Result).ToArray();
#pragma warning restore VSTHRD002
            Assert.IsFalse(counts.Any(c => c < 0), "A snapshot call returned null Files (worker dropped the call).");

            // The seeded repo has exactly two changed files; every concurrent call must see them.
            // A crashed worker yields 0 (empty status) instead, so requiring the real count both
            // proves liveness and guards against the worker silently returning nothing.
            Assert.IsTrue(counts.All(c => c == 2),
                "Concurrent status calls disagreed or returned empty (worker likely crashed): [" +
                string.Join(", ", counts) + "]");
        }

        [TestMethod]
        public void GetStatus_ResolvesSingleFileThroughRepository()
        {
            string worker = RequireWorker();
            string repoPath = Path.Combine(_tempRoot, "repo");
            SeedRepository(worker, repoPath);

            using var client = new LoreBrokeredClient(worker);
            LoreFileStatus status = client.GetStatus(Path.Combine(repoPath, "file.txt"));

            Assert.AreEqual(LoreFileStatus.Modified, status);
        }

        [TestMethod]
        public void GetStatus_OutsideRepositoryIsNotControlled()
        {
            string worker = RequireWorker();

            using var client = new LoreBrokeredClient(worker);
            LoreFileStatus status = client.GetStatus(Path.Combine(_tempRoot, "loose.txt"));

            Assert.AreEqual(LoreFileStatus.NotControlled, status);
        }

        [TestMethod]
        public void BlockingCallsUnderSingleThreadedSyncContext_DoNotDeadlock()
        {
            string worker = RequireWorker();
            string repoPath = Path.Combine(_tempRoot, "repo");
            SeedRepository(worker, repoPath);

            // Visual Studio's main thread runs under a single-threaded, pumped SynchronizationContext.
            // The glyph-warming and command paths call the synchronous brokered-client APIs (which
            // bridge async-over-sync) from that thread. If the worker proxy ever captures or posts a
            // continuation back to that single thread while the thread is blocked waiting for the call
            // to finish, the result is a deadlock: the worker never even receives the request (the
            // exact symptom seen in the IDE - "client connected" then silence). This test reproduces
            // that environment with a real pumped context and fails if the call cannot complete.
            using var context = new SingleThreadedSynchronizationContext();
            Exception? failure = null;
            bool completed = false;

            context.Run(() =>
            {
                try
                {
                    using var client = new LoreBrokeredClient(worker);
                    Assert.IsTrue(client.IsAvailable, "IsAvailable returned false under a UI-style sync context.");
                    int count = client.GetRepositoryStatus(repoPath).Count;
                    Assert.AreEqual(2, count, "Status under a UI-style sync context returned the wrong count.");
                    completed = true;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    context.Complete();
                }
            }, TimeSpan.FromSeconds(45));

            if (failure != null)
            {
                throw new Exception("Brokered call failed under a single-threaded sync context.", failure);
            }

            Assert.IsTrue(completed,
                "Brokered calls deadlocked under a single-threaded sync context (the worker never " +
                "completed the request - reproduces the IDE hang).");
        }

        /// <summary>Runs the worker in <c>--seed</c> mode to create a real offline repository.</summary>
        private static void SeedRepository(string workerExe, string repoPath)
        {
            using var process = StartWorker(workerExe, "--seed \"" + repoPath + "\"");
            if (!process.WaitForExit(120000))
            {
                try { process.Kill(); } catch { /* best effort */ }
                Assert.Fail("Seeding the Lore repository timed out.");
            }

            Assert.AreEqual(0, process.ExitCode,
                "Seeding failed: " + process.StandardError.ReadToEnd());
            Assert.IsTrue(Directory.Exists(Path.Combine(repoPath, ".lore")),
                "Seeding did not create a .lore marker directory.");
        }

        private static Process StartWorker(string workerExe, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = workerExe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(workerExe),
            };

            return Process.Start(psi)!;
        }

        /// <summary>
        /// Returns the path to the built worker executable, or marks the test Inconclusive when it
        /// has not been built (so the rest of the suite stays green without the .NET 9 payload).
        /// </summary>
        private static string RequireWorker()
        {
            string? worker = FindWorkerExecutable();
            if (worker == null)
            {
                Assert.Inconclusive(
                    "LoreVS.Worker.exe was not found. Build worker\\LoreVS.Worker (win-x64) to run the brokered-client integration tests.");
            }

            return worker!;
        }

        private static string? FindWorkerExecutable()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string workerProject = Path.Combine(dir.FullName, "worker", "LoreVS.Worker", "bin");
                if (Directory.Exists(workerProject))
                {
                    foreach (string config in new[] { "Release", "Debug" })
                    {
                        string candidate = Path.Combine(workerProject, config, "net9.0", "win-x64", "LoreVS.Worker.exe");
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }

                dir = dir.Parent;
            }

            return null;
        }
    }

    /// <summary>
    /// A minimal single-threaded, message-pumping <see cref="SynchronizationContext"/> that mimics
    /// Visual Studio's main (UI) thread: all posted work runs on one dedicated thread that processes
    /// a queue. Used to reproduce sync-context-sensitive deadlocks that never appear on the free
    /// thread pool used by the rest of the integration tests.
    /// </summary>
    internal sealed class SingleThreadedSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _queue =
            new System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)>();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException("Synchronous Send is not supported on the test sync context.");

        /// <summary>Pumps the queue on the current thread until <see cref="Complete"/> is called or the timeout elapses.</summary>
        public void Run(Action initialWork, TimeSpan timeout)
        {
            SynchronizationContext? previous = Current;
            SetSynchronizationContext(this);
            try
            {
                Post(_ => initialWork(), null);
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < timeout)
                {
                    if (_queue.TryTake(out (SendOrPostCallback callback, object? state) item, 250))
                    {
                        item.callback(item.state);
                    }

                    if (_queue.IsAddingCompleted && _queue.Count == 0)
                    {
                        return;
                    }
                }
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        public void Complete() => _queue.CompleteAdding();

        public void Dispose() => _queue.Dispose();
    }
}
