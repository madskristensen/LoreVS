using System;
using System.IO;
using System.Linq;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    public partial class LoreBrokeredClientIntegrationTests
    {
        [TestMethod]
        [TestCategory("ServerIntegration")]
        [DoNotParallelize]
        public void ServerLocks_AcquireQueryStatusRelease_AreSharedAcrossWorkingCopies()
        {
            string? executable = Environment.GetEnvironmentVariable("LOREVS_TEST_SERVER_PATH");
            if (string.IsNullOrWhiteSpace(executable))
            {
                Assert.Inconclusive("Set LOREVS_TEST_SERVER_PATH to run local-server locking tests.");
            }

            Assert.IsTrue(File.Exists(executable));
            string worker = RequireWorker();
            using var server = new LocalLoreServer(executable!, _tempRoot, httpEnabled: false);
            try
            {
                server.WaitUntilReady();
                string url = "lore://127.0.0.1:" + server.Port + "/locks";
                string root = Path.Combine(_tempRoot, "writer");
                string clone = Path.Combine(_tempRoot, "reader");
                Directory.CreateDirectory(root);
                using var writer = new LoreBrokeredClient(worker);
                using var reader = new LoreBrokeredClient(worker);
                AssertServerSuccess(writer.CreateRepository(root, url, "writer@lorevs"), "Create lock repository");
                File.WriteAllText(Path.Combine(root, "asset.bin"), "asset content");
                File.WriteAllText(Path.Combine(root, "other.bin"), "other content");
                AssertServerSuccess(writer.StageAll(root), "Stage assets");
                AssertServerSuccess(writer.Commit(root, "Assets", "writer@lorevs"), "Commit assets");
                AssertServerSuccess(writer.Push(root), "Push assets");
                AssertServerSuccess(reader.CloneRepository(url, clone, "reader@lorevs"), "Clone lock repository");
                LoreRepositoryInfo? info = writer.GetRepositorySnapshot(root).Info;
                Assert.IsNotNull(info);
                string branch = info.BranchName;
                LoreLockQueryResult empty = writer.QueryLocks(root, branch);
                AssertServerSuccess(empty.Result, "Query empty locks");
                Assert.AreEqual(0, empty.Locks.Length);

                AssertServerSuccess(writer.AcquireLock(root, new[] { "asset.bin" }), "Acquire asset lock");
                LoreLockQueryResult query = reader.QueryLocks(clone, branch);
                AssertServerSuccess(query.Result, "Query from second working copy");
                Assert.AreEqual(1, query.Locks.Length);
                Assert.AreEqual(Path.Combine(clone, "asset.bin"), query.Locks[0].Path);
                Assert.IsFalse(string.IsNullOrEmpty(query.Locks[0].BranchId));
                Assert.IsTrue(query.Locks[0].LockedAt > 0);
                TestContext.WriteLine("Unauthenticated server lock owner: " + query.Locks[0].Owner);
                LoreLockQueryResult status = reader.LockStatus(clone, new[] { "asset.bin", "other.bin" });
                AssertServerSuccess(status.Result, "Selected file lock status");
                Assert.AreEqual(1, status.Locks.Length);
                Assert.AreEqual(query.Locks[0].Path, status.Locks[0].Path);
                Assert.AreEqual(query.Locks[0].Owner, status.Locks[0].Owner);
                Assert.AreEqual(query.Locks[0].LockedAt, status.Locks[0].LockedAt);
                AssertServerSuccess(writer.AcquireLock(root, new[] { "asset.bin" }), "Reacquire owned lock");
                AssertLockCount(writer.QueryLocks(root, branch), 1, "Query reacquired lock");

                AssertServerSuccess(writer.CreateBranch(root, "other-lock-branch", "writer@lorevs", checkout: true), "Create separate lock branch");
                LoreLockQueryResult otherBranch = writer.QueryLocks(root, "other-lock-branch");
                AssertServerSuccess(otherBranch.Result, "Query separate branch locks");
                Assert.AreEqual(0, otherBranch.Locks.Length);
                AssertLockCount(writer.LockStatus(root, new[] { "asset.bin" }), 0, "Status on separate branch");
                AssertServerSuccess(writer.AcquireLock(root, new[] { "asset.bin" }), "Lock same path on separate branch");
                AssertLockCount(writer.QueryLocks(root, string.Empty), 2, "Query across branches");
                AssertLockCount(reader.QueryLocks(clone, branch), 1, "Query original branch from clone");
                AssertServerSuccess(writer.ReleaseLock(root, new[] { "asset.bin" }), "Release separate branch lock");
                AssertServerSuccess(writer.SwitchBranch(root, branch), "Return to original branch");

                Assert.IsFalse(writer.AcquireLock(root, new[] { "missing.bin" }).Success);
                Assert.IsFalse(writer.ReleaseLock(root, Array.Empty<string>()).Success);
                Assert.IsFalse(writer.AcquireLock(root, new[] { Path.Combine(_tempRoot, "outside.bin") }).Success);
                Assert.IsFalse(writer.LockStatus(root, Array.Empty<string>()).Result.Success);
                LoreLockQueryResult invalidBranch = reader.QueryLocks(clone, "no-such-branch");
                Assert.IsFalse(invalidBranch.Result.Success);
                Assert.AreEqual(0, invalidBranch.Locks.Length);
                AssertLockCount(writer.QueryLocks(root, branch), 1, "Rejected operations must preserve existing locks");

                AssertServerSuccess(writer.ReleaseLock(root, new[] { "asset.bin" }), "Release owned lock");
                LoreLockQueryResult released = reader.LockStatus(clone, new[] { "asset.bin" });
                AssertServerSuccess(released.Result, "Status after release");
                Assert.AreEqual(0, released.Locks.Length);
                AssertServerSuccess(reader.AcquireLock(clone, new[] { "asset.bin", "other.bin" }), "Acquire multiple locks from clone");
                AssertLockCount(writer.QueryLocks(root, branch), 2, "Query multiple locks from original");
                AssertServerSuccess(reader.ReleaseLock(clone, new[] { "asset.bin", "other.bin" }), "Release multiple locks");
                AssertLockCount(writer.QueryLocks(root, branch), 0, "Query after releasing all locks");
                Assert.AreEqual("asset content", File.ReadAllText(Path.Combine(root, "asset.bin")));
                Assert.AreEqual("asset content", File.ReadAllText(Path.Combine(clone, "asset.bin")));
                Assert.AreEqual(0, writer.GetRepositorySnapshot(root).Files.Length);
                server.Stop();
                LoreLockQueryResult unavailable = reader.QueryLocks(clone, branch);
                Assert.IsFalse(unavailable.Result.Success, "An unreachable server is not an empty successful lock list.");
                Assert.AreEqual(0, unavailable.Locks.Length);
            }
            finally
            {
                server.Stop();
                TestContext.WriteLine(server.Output);
            }
        }

        private static void AssertLockCount(LoreLockQueryResult query, int count, string operation)
        {
            AssertServerSuccess(query.Result, operation);
            Assert.AreEqual(count, query.Locks.Length, operation);
        }
    }
}