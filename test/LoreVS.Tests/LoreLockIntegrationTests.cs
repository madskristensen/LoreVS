using System;
using System.IO;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    public partial class LoreBrokeredClientIntegrationTests
    {
        [TestMethod]
        public void Locks_NoRemoteAndInvalidSelections_AreExplicitFailures()
        {
            string worker = RequireWorker();
            string root = Path.Combine(_tempRoot, "offline-locks");
            SeedRepository(worker, root);
            using var client = new LoreBrokeredClient(worker);
            Assert.IsFalse(client.AcquireLock(root, new[] { "file.txt" }).Success);
            Assert.IsFalse(client.ReleaseLock(root, new[] { "file.txt" }).Success);
            LoreLockQueryResult query = client.QueryLocks(root, string.Empty);
            Assert.IsFalse(query.Result.Success);
            Assert.AreEqual(0, query.Locks.Length);
            LoreLockQueryResult status = client.LockStatus(root, new[] { "file.txt" });
            Assert.IsFalse(status.Result.Success);
            Assert.AreEqual(0, status.Locks.Length);
            Assert.IsFalse(client.AcquireLock(root, Array.Empty<string>()).Success);
            Assert.IsFalse(client.ReleaseLock(root, new[] { "..\\outside.bin" }).Success);
            Assert.IsFalse(client.AcquireLock(root, new[] { "*.txt" }).Success);
            Assert.IsFalse(client.AcquireLock(root, new[] { root }).Success);
            Assert.AreEqual(2, client.GetRepositorySnapshot(root).Files.Length);
        }

        [TestMethod]
        public void Locks_UnavailableWorker_NeverReportsSuccessfulEmptyQuery()
        {
            using var client = new LoreBrokeredClient(Path.Combine(_tempRoot, "missing-worker.exe"));
            Assert.IsFalse(client.AcquireLock(_tempRoot, new[] { "asset.bin" }).Success);
            Assert.IsFalse(client.ReleaseLock(_tempRoot, new[] { "asset.bin" }).Success);
            Assert.IsFalse(client.QueryLocks(_tempRoot, string.Empty).Result.Success);
            Assert.IsFalse(client.LockStatus(_tempRoot, new[] { "asset.bin" }).Result.Success);
        }
    }
}