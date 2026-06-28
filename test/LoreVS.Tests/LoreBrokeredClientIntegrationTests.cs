using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
}
