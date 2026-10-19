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
        public void ServerBranches_CompareProtectArchiveAndLocalReset_PreserveRemoteHistory()
        {
            string? executable = Environment.GetEnvironmentVariable("LOREVS_TEST_SERVER_PATH");
            if (string.IsNullOrWhiteSpace(executable))
            {
                Assert.Inconclusive("Set LOREVS_TEST_SERVER_PATH to run local-server branch management tests.");
            }

            Assert.IsTrue(File.Exists(executable));
            string worker = RequireWorker();
            using var server = new LocalLoreServer(executable!, _tempRoot, httpEnabled: false);
            try
            {
                server.WaitUntilReady();
                string root = Path.Combine(_tempRoot, "branch-writer");
                string clone = Path.Combine(_tempRoot, "branch-reader");
                Directory.CreateDirectory(root);
                using var writer = new LoreBrokeredClient(worker);
                using var reader = new LoreBrokeredClient(worker);
                string url = "lore://127.0.0.1:" + server.Port + "/branches";
                AssertServerSuccess(writer.CreateRepository(root, url, "test@lorevs"), "Create branch repository");
                File.WriteAllText(Path.Combine(root, "file.txt"), "base");
                CommitBranchChanges(writer, root, "Server base");
                AssertServerSuccess(writer.Push(root), "Push base");
                string main = writer.ListBranches(root).Single(branch => branch.IsCurrent && !branch.IsRemote).Name;
                LoreBranchInfo baseInfo = writer.GetBranchInfo(root, main);
                AssertServerSuccess(baseInfo.Result, "Read remote-backed base info");
                Assert.AreEqual(baseInfo.LatestRevision, baseInfo.RemoteRevision);

                AssertServerSuccess(writer.CreateBranch(root, "server-feature", "test@lorevs", checkout: true), "Create server branch");
                File.WriteAllText(Path.Combine(root, "file.txt"), "first");
                CommitBranchChanges(writer, root, "First feature");
                AssertServerSuccess(writer.Push(root), "Push first feature");
                string first = writer.GetBranchInfo(root, "server-feature").LatestRevision;
                File.WriteAllText(Path.Combine(root, "file.txt"), "second");
                File.WriteAllText(Path.Combine(root, "feature.txt"), "feature");
                CommitBranchChanges(writer, root, "Second feature");
                AssertServerSuccess(writer.Push(root), "Push second feature");
                string second = writer.GetBranchInfo(root, "server-feature").LatestRevision;
                AssertServerSuccess(reader.CloneRepository(url, clone, "reader@lorevs"), "Clone branch repository");

                LoreBranchComparison comparison = reader.CompareBranches(clone, main, "server-feature");
                AssertServerSuccess(comparison.Result, "Compare uncached remote branch snapshots");
                Assert.AreEqual(baseInfo.LatestRevision, comparison.SourceRevision);
                Assert.AreEqual(second, comparison.TargetRevision);
                Assert.AreEqual(2, comparison.Changes.Length);
                Assert.AreEqual(0, comparison.ConflictPaths.Length);
                Assert.AreEqual(LoreFileStatus.Added, comparison.Changes.Single(change => Path.GetFileName(change.Path) == "feature.txt").Status);
                string historical = Path.Combine(_tempRoot, "feature-snapshot.txt");
                AssertServerSuccess(reader.WriteFileAtRevision(clone, "file.txt", comparison.TargetRevision, historical), "Read branch comparison content");
                Assert.AreEqual("second", File.ReadAllText(historical));
                Assert.AreEqual("base", File.ReadAllText(Path.Combine(clone, "file.txt")));

                string mergeRoot = Path.Combine(_tempRoot, "branch-merge-reader");
                using var merger = new LoreBrokeredClient(worker);
                AssertServerSuccess(merger.CloneRepository(url, mergeRoot, "merger@lorevs"), "Clone for uncached branch merge");
                LoreMergeResult merge = merger.MergeBranch(mergeRoot, "server-feature", "test@lorevs");
                Assert.IsTrue(merge.Success, "Merge remote-only branch: " + merge.ErrorMessage);
                Assert.AreEqual("second", File.ReadAllText(Path.Combine(mergeRoot, "file.txt")));

                LoreCommandResult reset = writer.ResetBranch(root, "server-feature", first, second);
                Assert.IsTrue(reset.Success, reset.CombinedText);
                StringAssert.Contains(reset.Warning, second);
                LoreBranchInfo resetInfo = writer.GetBranchInfo(root, "server-feature");
                AssertServerSuccess(resetInfo.Result, "Read reset branch local and remote tips");
                Assert.AreEqual(first, resetInfo.LatestRevision);
                Assert.AreEqual(second, resetInfo.RemoteRevision, "A local reset must not rewrite the server branch.");
                Assert.AreEqual("first", File.ReadAllText(Path.Combine(root, "file.txt")));
                Assert.IsFalse(File.Exists(Path.Combine(root, "feature.txt")));
                AssertServerSuccess(reader.SwitchBranch(clone, "server-feature"), "Switch clone to unchanged remote tip");
                Assert.AreEqual(second, reader.GetBranchInfo(clone, "server-feature").LatestRevision);
                Assert.AreEqual("second", File.ReadAllText(Path.Combine(clone, "file.txt")));
                AssertServerSuccess(writer.SwitchBranch(root, main), "Leave feature before archive");
                AssertServerSuccess(reader.SwitchBranch(clone, main), "Leave clone feature before archive");

                AssertServerSuccess(writer.SetBranchProtection(root, "server-feature", true), "Protect server branch");
                Assert.IsFalse(reader.ArchiveBranch(clone, "server-feature").Success, "Server protection must prevent archive from another working copy.");
                LoreBranchInfo protectedInfo = writer.GetBranchInfo(root, "server-feature");
                AssertServerSuccess(protectedInfo.Result, "Protected remote branch must survive rejected archive");
                Assert.AreEqual(second, protectedInfo.RemoteRevision);
                AssertServerSuccess(writer.SetBranchProtection(root, "server-feature", false), "Unprotect server branch");
                AssertServerSuccess(writer.ArchiveBranch(root, "server-feature"), "Archive server branch");
                string verification = Path.Combine(_tempRoot, "branch-verifier");
                using var verifier = new LoreBrokeredClient(worker);
                AssertServerSuccess(verifier.CloneRepository(url, verification, "verifier@lorevs"), "Clone after archive");
                Assert.IsFalse(verifier.GetBranchInfo(verification, "server-feature").Result.Success);
                Assert.IsFalse(verifier.ListBranches(verification).Any(branch => branch.Name == "server-feature"));
                Assert.AreEqual("base", File.ReadAllText(Path.Combine(root, "file.txt")));
                Assert.AreEqual("base", File.ReadAllText(Path.Combine(clone, "file.txt")));

                server.Stop();
                LoreCommandResult stopped = writer.SetBranchProtection(root, main, true);
                Assert.IsFalse(stopped.Success, "An unreachable server must not report successful protection.");
                Assert.IsFalse(stopped.RequiresAuthentication);
                Assert.AreEqual("base", File.ReadAllText(Path.Combine(root, "file.txt")));
            }
            finally
            {
                server.Stop();
                TestContext.WriteLine(server.Output);
            }
        }
    }
}