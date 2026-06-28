using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using LoreVS.SourceControl;

namespace LoreVS.Tests
{
    /// <summary>
    /// Unit tests for <see cref="LoreBrokeredClient.TryGetCachedStatus"/>, the non-blocking status
    /// lookup that keeps the UI-thread SCC glyph/tooltip callbacks from blocking on the out-of-process
    /// worker. These tests never launch the worker - they exercise only the local repository
    /// discovery and the in-memory cache, so a deliberately missing worker path proves no worker call
    /// is made.
    /// </summary>
    [TestClass]
    public class LoreBrokeredClientCacheTests
    {
        private string _tempRoot = string.Empty;

        [TestInitialize]
        public void Setup()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "lorevs_cache_" + Guid.NewGuid().ToString("N"));
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
        public void TryGetCachedStatus_EmptyPath_ResolvesToNotControlled()
        {
            using var client = new LoreBrokeredClient(MissingWorkerPath());

            bool resolved = client.TryGetCachedStatus(string.Empty, out LoreFileStatus status);

            Assert.IsTrue(resolved, "An empty path is resolved without contacting the worker.");
            Assert.AreEqual(LoreFileStatus.NotControlled, status);
        }

        [TestMethod]
        public void TryGetCachedStatus_PathOutsideRepository_ResolvesToNotControlled()
        {
            string file = Path.Combine(_tempRoot, "loose.txt");
            File.WriteAllText(file, "x");

            using var client = new LoreBrokeredClient(MissingWorkerPath());

            bool resolved = client.TryGetCachedStatus(file, out LoreFileStatus status);

            Assert.IsTrue(resolved, "A file outside any Lore repository resolves locally as not controlled.");
            Assert.AreEqual(LoreFileStatus.NotControlled, status);
        }

        [TestMethod]
        public void TryGetCachedStatus_InRepositoryButUncached_ReturnsFalseWithoutWorker()
        {
            // A '.lore' marker makes the file resolve to a repository root, but the status cache is
            // empty. The lookup must report a miss (false) immediately rather than launching or
            // blocking on the worker, so the UI-thread caller can warm the cache off the UI thread.
            string repo = Path.Combine(_tempRoot, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".lore"));
            string file = Path.Combine(repo, "file.txt");
            File.WriteAllText(file, "x");

            using var client = new LoreBrokeredClient(MissingWorkerPath());

            bool resolved = client.TryGetCachedStatus(file, out LoreFileStatus status);

            Assert.IsFalse(resolved, "A cache miss returns false so the caller warms asynchronously.");
            Assert.AreEqual(LoreFileStatus.NotControlled, status);
        }

        private string MissingWorkerPath() => Path.Combine(_tempRoot, "no-such-worker.exe");
    }
}
