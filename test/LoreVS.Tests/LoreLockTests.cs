using System;
using System.IO;
using System.Linq;
using LoreVS.Commands;
using LoreVS.SourceControl;
using LoreVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    [TestClass]
    public sealed class LoreLockTests
    {
        [TestMethod]
        public void BuildChangeItems_LockOwnerDoesNotReplaceContentStatusOrAddCleanFilesToCommit()
        {
            LoreChangeItem[] changes = LoreChangesViewModel.BuildChangeItems(@"C:\repo", new[]
            {
                new LoreStatusEntry { Path = @"C:\repo\asset.bin", Status = LoreFileStatus.Modified },
                new LoreStatusEntry { Path = @"C:\repo\new.bin", Status = LoreFileStatus.Renamed, OriginalPath = @"C:\repo\old.bin" },
                new LoreStatusEntry { Path = @"C:\repo\clean.bin", Status = LoreFileStatus.Unchanged },
                new LoreStatusEntry { Path = @"C:\repo\unlocked.txt", Status = LoreFileStatus.Added },
            }, new[]
            {
                new LoreFileLock { Path = @"C:\REPO\ASSET.BIN", Owner = "alice" },
                new LoreFileLock { Path = @"C:\repo\new.bin", Owner = "bob" },
                new LoreFileLock { Path = @"C:\repo\clean.bin", Owner = "charlie" },
            }).ToArray();
            Assert.AreEqual(3, changes.Length);
            Assert.AreEqual(LoreFileStatus.Modified, changes[0].Status);
            Assert.AreEqual("M", changes[0].StatusBadge);
            Assert.AreEqual("alice", changes[0].LockOwner);
            Assert.AreEqual("Locked: alice", changes[0].LockText);
            Assert.AreEqual(LoreFileStatus.Renamed, changes[1].Status);
            Assert.AreEqual(@"C:\repo\old.bin", changes[1].OriginalFullPath);
            Assert.AreEqual("bob", changes[1].LockOwner);
            Assert.IsFalse(changes[2].IsLocked);
            Assert.AreEqual(string.Empty, changes[2].LockText);
            Assert.IsFalse(changes.Any(item => item.FileName == "clean.bin"));
        }

        [TestMethod]
        public void ApplyLocks_FailedQueryClearsStaleOwnersAndReportsFailure()
        {
            var vm = new LoreChangesViewModel();
            vm.ApplyLocks(new LoreLockQueryResult
            {
                Result = new LoreCommandResult(true, 0, string.Empty, string.Empty),
                Locks = new[] { new LoreFileLock { Path = @"C:\repo\clean.bin", Owner = "alice" } },
            }, @"C:\repo", "main");
            Assert.AreEqual(1, vm.Locks.Count);
            Assert.AreEqual(0, vm.Changes.Count);
            Assert.AreEqual(0, vm.SelectedFileCount);
            Assert.IsFalse(vm.HasChanges);
            Assert.AreEqual("1 locked file(s) on the current branch.", vm.LockStatusText);
            vm.ApplyLocks(new LoreLockQueryResult
            {
                Result = LoreCommandResult.Failed("permission denied"),
                Locks = new[] { new LoreFileLock { Path = @"C:\repo\stale.bin", Owner = "bob" } },
            }, @"C:\repo", "main");
            Assert.AreEqual(0, vm.Locks.Count);
            Assert.AreEqual("Lock status unavailable: permission denied", vm.LockStatusText);
            vm.ApplyLocks(new LoreLockQueryResult { Result = new LoreCommandResult(true, 0, string.Empty, string.Empty) }, @"C:\repo", "main");
            Assert.AreEqual("0 locked file(s) on the current branch.", vm.LockStatusText);
            vm.ApplyLocks(null, @"C:\repo", "main");
            Assert.AreEqual("Locks require a remote repository.", vm.LockStatusText);
        }

        [TestMethod]
        public void EmptyOwner_IsStillDisplayedAsLocked()
        {
            var item = new LoreChangeItem(@"C:\repo\asset.bin", @"C:\repo", LoreFileStatus.Modified, lockOwner: string.Empty);
            Assert.IsTrue(item.IsLocked);
            Assert.AreEqual("Locked: (unknown owner)", item.LockText);
            Assert.AreEqual(@"C:\repo\asset.bin - locked by (unknown owner)", new LoreFileLock { Path = item.FullPath }.DisplayText);
            Assert.IsFalse(new LoreLockQueryResult().Result.Success);
        }

        [TestMethod]
        public void LockRefresh_ReusesResultsUntilInvalidatedOrWorkspaceBranchChanges()
        {
            var vm = new LoreChangesViewModel();
            Assert.IsTrue(vm.BeginLockRefresh(@"C:\repo", "main"));
            vm.ApplyLocks(new LoreLockQueryResult { Result = LoreCommandResult.Failed("server unreachable") }, @"C:\repo", "main");
            Assert.IsFalse(vm.BeginLockRefresh(@"C:\REPO", "main"), "Saves must not retry a failed remote query.");
            vm.InvalidateLocks();
            Assert.IsTrue(vm.BeginLockRefresh(@"C:\repo", "main"));
            vm.ApplyLocks(null, @"C:\repo", "main");
            Assert.IsFalse(vm.BeginLockRefresh(@"C:\repo", "main"));
            Assert.IsTrue(vm.BeginLockRefresh(@"C:\repo", "feature"));
            vm.ApplyLocks(null, @"C:\repo", "feature");
            Assert.IsFalse(vm.BeginLockRefresh(@"C:\repo", "feature"));
            Assert.IsTrue(vm.BeginLockRefresh(@"C:\other", "feature"));
        }

        [TestMethod]
        public void ValidateSelection_DeduplicatesFilesCaseInsensitively()
        {
            string[] result = LoreLockActions.ValidateSelection(@"C:\repo", new[] { @"C:\repo\asset.bin", @"C:\REPO\ASSET.BIN" });
            CollectionAssert.AreEqual(new[] { @"C:\repo\asset.bin" }, result);
        }

        [TestMethod]
        [DataRow(@"C:\repo-other\asset.bin")]
        [DataRow(@"C:\repo\..\outside.bin")]
        [DataRow(@"relative.bin")]
        [DataRow("")]
        public void ValidateSelection_RejectsOutsideOrRelativeFiles(string path)
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => LoreLockActions.ValidateSelection(@"C:\repo", new[] { path }));
        }

        [TestMethod]
        public void ValidateSelection_RejectsEmptySelectionAndFolders()
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => LoreLockActions.ValidateSelection(@"C:\repo", Array.Empty<string>()));
            string temp = Path.GetTempPath().TrimEnd('\\');
            Assert.ThrowsExactly<InvalidOperationException>(() => LoreLockActions.ValidateSelection(Path.GetDirectoryName(temp)!, new[] { temp }));
        }
    }
}