using System;
using System.Linq;
using LoreVS.SourceControl;
using LoreVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    [TestClass]
    public sealed class LoreBranchManagementTests
    {
        [TestMethod]
        public void QueryResults_DefaultToExplicitFailure()
        {
            Assert.IsFalse(new LoreBranchInfo().Result.Success);
            Assert.IsFalse(new LoreBranchComparison().Result.Success);
            Assert.AreEqual(0, new LoreBranchComparison().Changes.Length);
            Assert.AreEqual(0, new LoreBranchComparison().ConflictPaths.Length);
        }

        [TestMethod]
        public void BranchList_PrefersLocalNames_SelectsCurrent_AndExcludesArchived()
        {
            var vm = new LoreBranchesViewModel();
            var current = new LoreBranchEntry { Id = "local", Name = "main", IsCurrent = true };
            vm.ApplyBranches(@"C:\repo", new[]
            {
                new LoreBranchEntry { Name = "main", IsRemote = true },
                new LoreBranchEntry { Name = "archived", Archived = true },
                new LoreBranchEntry { Name = "feature" }, current,
            });
            CollectionAssert.AreEqual(new[] { "feature", "main" }, vm.Branches.Select(branch => branch.Name).ToArray());
            Assert.AreSame(current, vm.SelectedBranch);
            Assert.AreSame(current, vm.SourceBranch);
            Assert.AreEqual("feature", vm.TargetBranch!.Name);
            Assert.AreEqual("main (current)", current.DisplayName);
            Assert.IsTrue(vm.CanInteract);
            vm.IsBusy = true;
            Assert.IsFalse(vm.CanInteract);
            Assert.IsFalse(vm.CanRefresh);
        }

        [TestMethod]
        public void Info_LoadAndSelectionChanges_DiscardSupersededResults()
        {
            var vm = new LoreBranchesViewModel();
            vm.ApplyBranches(@"C:\repo", new[] { new LoreBranchEntry { Name = "main" }, new LoreBranchEntry { Name = "feature" } });
            Guid old = vm.BeginInfoLoad();
            vm.SelectedBranch = vm.Branches[1];
            Assert.IsFalse(vm.ApplyInfo(old, SuccessfulInfo("stale")));
            Assert.IsNull(vm.BranchInfo);
            Guid current = vm.BeginInfoLoad();
            Assert.IsTrue(vm.ApplyInfo(current, SuccessfulInfo("selected")));
            Assert.AreEqual("selected", vm.BranchInfo!.Name);
            StringAssert.Contains(vm.InfoText, "Local tip: local-tip");
            StringAssert.Contains(vm.InfoText, "Remote tip: remote-tip");
            Assert.IsTrue(vm.ApplyInfo(current, new LoreBranchInfo { Result = LoreCommandResult.Failed("permission denied") }));
            Assert.IsNull(vm.BranchInfo);
            Assert.AreEqual("permission denied", vm.InfoText);
        }

        [TestMethod]
        public void Comparison_PreservesSnapshotsRenamesAndSeparateMergeConflicts()
        {
            var vm = new LoreBranchesViewModel();
            vm.ApplyBranches(@"C:\repo", Array.Empty<LoreBranchEntry>());
            Guid key = vm.BeginComparison();
            var result = new LoreBranchComparison
            {
                Result = Success(), SourceRevision = "left", TargetRevision = "right", BaseRevision = "base",
                ConflictPaths = new[] { @"C:\repo\new.txt" },
                Changes = new[] { new LoreRevisionChange { Path = @"C:\repo\new.txt", OriginalPath = @"C:\repo\old.txt", Status = LoreFileStatus.Renamed } },
            };
            Assert.IsTrue(vm.ApplyComparison(key, result));
            Assert.AreSame(result, vm.Comparison);
            Assert.AreEqual(@"C:\repo\old.txt", vm.Changes.Single().OriginalFullPath);
            Assert.AreEqual(LoreFileStatus.Renamed, vm.Changes.Single().Status);
            StringAssert.Contains(vm.ComparisonText, "Left: left. Right: right.");
            StringAssert.Contains(vm.ComparisonText, "Merge base: base. Potential merge conflicts: 1.");
            StringAssert.Contains(vm.ComparisonText, @"C:\repo\new.txt");
            vm.TargetBranch = new LoreBranchEntry { Name = "another" };
            Assert.IsNull(vm.Comparison);
            Assert.AreEqual(0, vm.Changes.Count);
            Assert.IsFalse(vm.ApplyComparison(key, result));
        }

        [TestMethod]
        public void WorkspaceChange_InvalidatesInfoAndComparisons_EvenForReturningPair()
        {
            var vm = new LoreBranchesViewModel();
            var branches = new[] { new LoreBranchEntry { Name = "a" }, new LoreBranchEntry { Name = "b" } };
            vm.ApplyBranches(@"C:\one", branches);
            Guid info = vm.BeginInfoLoad();
            Guid pair = vm.BeginComparison();
            vm.ApplyBranches(@"C:\two", branches);
            Assert.IsFalse(vm.ApplyInfo(info, SuccessfulInfo("a")));
            Assert.IsFalse(vm.ApplyComparison(pair, new LoreBranchComparison { Result = Success() }));
            vm.ApplyBranches(null, Array.Empty<LoreBranchEntry>());
            Assert.IsNull(vm.RepositoryRoot);
            Assert.IsNull(vm.SelectedBranch);
            Assert.IsNull(vm.SourceBranch);
            Assert.IsNull(vm.TargetBranch);
            Assert.IsFalse(vm.CanInteract);
        }

        [TestMethod]
        public void ComparisonFailure_ClearsPreviousResultsAndShowsReason()
        {
            var vm = new LoreBranchesViewModel();
            Guid old = vm.BeginComparison();
            Assert.IsTrue(vm.ApplyComparison(old, new LoreBranchComparison { Result = Success(),
                Changes = new[] { new LoreRevisionChange { Path = @"C:\repo\old.txt" } } }));
            Guid current = vm.BeginComparison();
            Assert.IsTrue(vm.ApplyComparison(current, new LoreBranchComparison { Result = LoreCommandResult.Failed("Branch is unavailable") }));
            Assert.IsNull(vm.Comparison);
            Assert.AreEqual(0, vm.Changes.Count);
            Assert.AreEqual("Branch is unavailable", vm.ComparisonText);
        }

        [TestMethod]
        public void MissingWorker_AllBranchOperationsReturnFailure()
        {
            using var client = new LoreBrokeredClient(@"C:\nonexistent-lorevs-worker\worker.exe");
            Assert.IsFalse(client.GetBranchInfo(@"C:\repo", "main").Result.Success);
            Assert.IsFalse(client.CompareBranches(@"C:\repo", "main", "feature").Result.Success);
            Assert.IsFalse(client.ArchiveBranch(@"C:\repo", "feature").Success);
            Assert.IsFalse(client.ResetBranch(@"C:\repo", "main", "revision", "previous").Success);
            Assert.IsFalse(client.SetBranchProtection(@"C:\repo", "main", true).Success);
        }

        private static LoreCommandResult Success() => new(true, 0, string.Empty, string.Empty);
        private static LoreBranchInfo SuccessfulInfo(string name) => new()
        {
            Result = Success(), Name = name, LatestRevision = "local-tip", RemoteRevision = "remote-tip",
        };
    }
}