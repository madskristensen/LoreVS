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
        public void BranchInfoAndDiff_CompareDivergedSnapshotsAndMergeConflictsWithoutMutatingFiles()
        {
            string worker = RequireWorker();
            string root = Path.Combine(_tempRoot, "branch-diff");
            SeedRepository(worker, root);
            using var client = new LoreBrokeredClient(worker);
            CommitBranchChanges(client, root, "Clean base");
            string main = client.ListBranches(root).Single(branch => branch.IsCurrent && !branch.IsRemote).Name;
            LoreBranchInfo mainInfo = client.GetBranchInfo(root, main);
            AssertServerSuccess(mainInfo.Result, "Read main branch info");
            Assert.AreEqual(main, mainInfo.Name);
            Assert.IsFalse(string.IsNullOrEmpty(mainInfo.Id));
            Assert.AreEqual(client.GetRevisionHistory(root, string.Empty, 1).Entries.Single().RevisionHash, mainInfo.LatestRevision);
            AssertServerSuccess(client.CreateBranch(root, "diff-feature", "test@lorevs", checkout: true), "Create diff branch");
            File.WriteAllText(Path.Combine(root, "file.txt"), "feature content");
            string oldPath = Path.Combine(root, "untracked.txt");
            string newPath = Path.Combine(root, "renamed.txt");
            File.Move(oldPath, newPath);
            AssertServerSuccess(client.RecordMove(root, oldPath, newPath), "Record feature rename");
            CommitBranchChanges(client, root, "Feature change");
            LoreBranchInfo featureInfo = client.GetBranchInfo(root, "diff-feature");
            AssertServerSuccess(featureInfo.Result, "Read feature info");
            Assert.AreEqual(mainInfo.Id, featureInfo.ParentBranchId);
            Assert.AreEqual(mainInfo.LatestRevision, featureInfo.BranchPoint);
            Assert.IsTrue(featureInfo.Stack.Any(point => point.BranchId == mainInfo.Id && point.Revision == mainInfo.LatestRevision));
            Assert.AreEqual("test@lorevs", featureInfo.Creator);
            Assert.IsTrue(featureInfo.Created > 0);
            Assert.IsFalse(featureInfo.Archived);
            AssertServerSuccess(client.SwitchBranch(root, main), "Return to main");
            File.WriteAllText(Path.Combine(root, "file.txt"), "main content");
            CommitBranchChanges(client, root, "Divergent main");
            string mainTip = client.GetBranchInfo(root, main).LatestRevision;

            LoreBranchComparison comparison = client.CompareBranches(root, main, "diff-feature");
            AssertServerSuccess(comparison.Result, "Compare branches");
            Assert.AreEqual(mainTip, comparison.SourceRevision);
            Assert.AreEqual(featureInfo.LatestRevision, comparison.TargetRevision);
            Assert.AreEqual(mainInfo.LatestRevision, comparison.BaseRevision);
            Assert.AreEqual(2, comparison.Changes.Length);
            Assert.AreEqual(LoreFileStatus.Modified, comparison.Changes.Single(change => Path.GetFileName(change.Path) == "file.txt").Status);
            LoreRevisionChange rename = comparison.Changes.Single(change => change.Status == LoreFileStatus.Renamed);
            Assert.AreEqual(newPath, rename.Path);
            Assert.AreEqual(oldPath, rename.OriginalPath);
            CollectionAssert.Contains(comparison.ConflictPaths, Path.Combine(root, "file.txt"));
            LoreBranchComparison reverse = client.CompareBranches(root, "diff-feature", main);
            AssertServerSuccess(reverse.Result, "Reverse branch comparison");
            Assert.AreEqual(comparison.TargetRevision, reverse.SourceRevision);
            Assert.AreEqual(comparison.SourceRevision, reverse.TargetRevision);
            LoreBranchComparison same = client.CompareBranches(root, main, main);
            AssertServerSuccess(same.Result, "Compare same branch");
            Assert.AreEqual(0, same.Changes.Length);
            Assert.AreEqual(0, same.ConflictPaths.Length);
            Assert.IsFalse(client.GetBranchInfo(root, "missing-branch").Result.Success);
            LoreBranchComparison invalid = client.CompareBranches(root, "missing-branch", main);
            Assert.IsFalse(invalid.Result.Success);
            Assert.AreEqual(0, invalid.Changes.Length);
            Assert.AreEqual("main content", File.ReadAllText(Path.Combine(root, "file.txt")));
            Assert.IsTrue(File.Exists(oldPath));
            Assert.IsFalse(File.Exists(newPath));
            Assert.AreEqual(main, client.ListBranches(root).Single(branch => branch.IsCurrent && !branch.IsRemote).Name);
            Assert.AreEqual(0, client.GetRepositorySnapshot(root).Files.Length);
        }

        [TestMethod]
        public void BranchReset_RequiresCleanTreeAndConfirmedTip_AndOnlyResetsRequestedBranch()
        {
            string worker = RequireWorker();
            string root = Path.Combine(_tempRoot, "branch-reset");
            SeedRepository(worker, root);
            using var client = new LoreBrokeredClient(worker);
            CommitBranchChanges(client, root, "Base");
            string main = client.ListBranches(root).Single(branch => branch.IsCurrent && !branch.IsRemote).Name;
            AssertServerSuccess(client.CreateBranch(root, "reset-feature", "test@lorevs", checkout: true), "Create reset branch");
            File.WriteAllText(Path.Combine(root, "file.txt"), "first");
            CommitBranchChanges(client, root, "First feature revision");
            string first = client.GetBranchInfo(root, "reset-feature").LatestRevision;
            File.WriteAllText(Path.Combine(root, "file.txt"), "second");
            CommitBranchChanges(client, root, "Second feature revision");
            string second = client.GetBranchInfo(root, "reset-feature").LatestRevision;
            Assert.IsFalse(client.ResetBranch(root, "reset-feature", first, first).Success, "A changed tip must be reconfirmed.");
            File.WriteAllText(Path.Combine(root, "file.txt"), "dirty");
            Assert.IsFalse(client.ResetBranch(root, "reset-feature", first, second).Success);
            Assert.AreEqual("dirty", File.ReadAllText(Path.Combine(root, "file.txt")));
            AssertServerSuccess(client.StageAll(root), "Stage dirty file");
            Assert.IsFalse(client.ResetBranch(root, "reset-feature", first, second).Success);
            Assert.AreEqual("dirty", File.ReadAllText(Path.Combine(root, "file.txt")));
            AssertServerSuccess(client.ResetFiles(root, new[] { Path.Combine(root, "file.txt") }), "Discard pending change");
            File.WriteAllText(Path.Combine(root, "new-untracked.txt"), "untracked");
            Assert.IsFalse(client.ResetBranch(root, "reset-feature", first, second).Success);
            Assert.AreEqual("untracked", File.ReadAllText(Path.Combine(root, "new-untracked.txt")));
            File.Delete(Path.Combine(root, "new-untracked.txt"));
            Assert.IsFalse(client.ResetBranch(root, "reset-feature", "not-a-revision", second).Success);
            Assert.IsFalse(client.ResetBranch(root, "reset-feature", string.Empty, second).Success);
            Assert.AreEqual(second, client.GetBranchInfo(root, "reset-feature").LatestRevision);
            Assert.AreEqual("second", File.ReadAllText(Path.Combine(root, "file.txt")));
            Assert.AreEqual(0, client.GetRepositorySnapshot(root).Files.Length, "An empty tracking revision is not an uncommitted change.");
            LoreCommandResult reset = client.ResetBranch(root, "reset-feature", first, second);
            Assert.IsTrue(reset.Success, "Reset current branch: " + reset.CombinedText);
            Assert.AreEqual(0, reset.ExitCode);
            StringAssert.Contains(reset.Warning, second);
            StringAssert.Contains(reset.Warning, "local only");
            Assert.AreEqual(first, client.GetBranchInfo(root, "reset-feature").LatestRevision);
            Assert.AreEqual("first", File.ReadAllText(Path.Combine(root, "file.txt")));
            AssertServerSuccess(client.SwitchBranch(root, main), "Switch to main");
            string mainContent = File.ReadAllText(Path.Combine(root, "file.txt"));
            LoreCommandResult noncurrentReset = client.ResetBranch(root, "reset-feature", second, first);
            Assert.IsTrue(noncurrentReset.Success, "Reset noncurrent branch: " + noncurrentReset.CombinedText);
            Assert.AreEqual(second, client.GetBranchInfo(root, "reset-feature").LatestRevision);
            Assert.AreEqual(mainContent, File.ReadAllText(Path.Combine(root, "file.txt")));
            Assert.AreEqual(main, client.ListBranches(root).Single(branch => branch.IsCurrent && !branch.IsRemote).Name);

            File.WriteAllText(Path.Combine(root, "file.txt"), "main-only revision");
            CommitBranchChanges(client, root, "Main-only revision");
            string wrongBranchRevision = client.GetBranchInfo(root, main).LatestRevision;
            Assert.IsFalse(client.ResetBranch(root, "reset-feature", wrongBranchRevision, second).Success,
                "A reset target must belong to the requested branch or its branch point.");
            Assert.AreEqual(second, client.GetBranchInfo(root, "reset-feature").LatestRevision);
            Assert.AreEqual("main-only revision", File.ReadAllText(Path.Combine(root, "file.txt")));
        }

        [TestMethod]
        public void BranchArchiveAndProtection_RespectCurrentDefaultAndProtectedBranchGuards()
        {
            string worker = RequireWorker();
            string root = Path.Combine(_tempRoot, "branch-archive");
            SeedRepository(worker, root);
            using var client = new LoreBrokeredClient(worker);
            CommitBranchChanges(client, root, "Base");
            string main = client.ListBranches(root).Single(branch => branch.IsCurrent && !branch.IsRemote).Name;
            AssertServerSuccess(client.CreateBranch(root, "archive-feature", "test@lorevs", checkout: true), "Create archive branch");
            Assert.IsFalse(client.ArchiveBranch(root, "archive-feature").Success, "Current branch must not be archived.");
            Assert.IsFalse(client.ArchiveBranch(root, main).Success, "Default branch must not be archived.");
            AssertServerSuccess(client.SwitchBranch(root, main), "Switch to main");
            AssertServerSuccess(client.SetBranchProtection(root, "archive-feature", true), "Protect local branch");
            Assert.IsFalse(client.ArchiveBranch(root, "archive-feature").Success, "Protected branch must not be archived.");
            Assert.IsTrue(client.ListBranches(root).Any(branch => branch.Name == "archive-feature"));
            AssertServerSuccess(client.SetBranchProtection(root, "archive-feature", false), "Unprotect local branch");
            AssertServerSuccess(client.ArchiveBranch(root, "archive-feature"), "Archive local branch");
            Assert.IsFalse(client.ListBranches(root).Any(branch => branch.Name == "archive-feature"));
            Assert.IsFalse(client.SetBranchProtection(root, "missing-branch", true).Success);
            Assert.IsFalse(client.ArchiveBranch(root, "missing-branch").Success);
            Assert.AreEqual("hello lore - changed", File.ReadAllText(Path.Combine(root, "file.txt")));
            Assert.AreEqual(0, client.GetRepositorySnapshot(root).Files.Length);
        }

        private static void CommitBranchChanges(LoreBrokeredClient client, string root, string message)
        {
            AssertServerSuccess(client.StageAll(root), "Stage " + message);
            AssertServerSuccess(client.Commit(root, message, "test@lorevs"), "Commit " + message);
        }
    }
}