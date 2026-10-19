using System.IO;
using System.Linq;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    public partial class LoreBrokeredClientIntegrationTests
    {
        [TestMethod]
        public void CompareRevisions_NonAdjacentReverseSameAndInvalid_DoNotChangeWorkingTree()
        {
            string worker = RequireWorker();
            string root = Path.Combine(_tempRoot, "comparison");
            SeedRepository(worker, root);
            using var client = new LoreBrokeredClient(worker);
            AssertServerSuccess(client.StageAll(root), "Stage seeded changes");
            AssertServerSuccess(client.Commit(root, "Comparison base", "test@lorevs"), "Commit base");
            string source = client.GetRevisionHistory(root, string.Empty, 1).Entries.Single().RevisionHash;
            File.WriteAllText(Path.Combine(root, "file.txt"), "middle");
            AssertServerSuccess(client.StageAll(root), "Stage middle");
            AssertServerSuccess(client.Commit(root, "Middle", "test@lorevs"), "Commit middle");
            File.WriteAllText(Path.Combine(root, "file.txt"), "target");
            File.Delete(Path.Combine(root, "untracked.txt"));
            File.WriteAllText(Path.Combine(root, "added.txt"), "added");
            AssertServerSuccess(client.StageAll(root), "Stage target");
            AssertServerSuccess(client.Commit(root, "Target", "test@lorevs"), "Commit target");
            string target = client.GetRevisionHistory(root, string.Empty, 1).Entries.Single().RevisionHash;
            File.WriteAllText(Path.Combine(root, "file.txt"), "uncommitted");

            LoreRevisionComparison forward = client.CompareRevisions(root, source, target);
            AssertServerSuccess(forward.Result, "Compare nonadjacent revisions");
            Assert.AreEqual(3, forward.Changes.Length);
            Assert.AreEqual(LoreFileStatus.Modified, forward.Changes.Single(c => Path.GetFileName(c.Path) == "file.txt").Status);
            Assert.AreEqual(LoreFileStatus.Added, forward.Changes.Single(c => Path.GetFileName(c.Path) == "added.txt").Status);
            Assert.AreEqual(LoreFileStatus.Deleted, forward.Changes.Single(c => Path.GetFileName(c.Path) == "untracked.txt").Status);
            LoreRevisionComparison reverse = client.CompareRevisions(root, target, source);
            AssertServerSuccess(reverse.Result, "Reverse comparison");
            Assert.AreEqual(3, reverse.Changes.Length);
            Assert.AreEqual(LoreFileStatus.Deleted, reverse.Changes.Single(c => Path.GetFileName(c.Path) == "added.txt").Status);
            Assert.AreEqual(LoreFileStatus.Added, reverse.Changes.Single(c => Path.GetFileName(c.Path) == "untracked.txt").Status);
            LoreRevisionComparison same = client.CompareRevisions(root, source, source);
            AssertServerSuccess(same.Result, "Same revision comparison");
            Assert.AreEqual(0, same.Changes.Length);
            LoreRevisionComparison invalid = client.CompareRevisions(root, "not-a-revision", target);
            Assert.IsFalse(invalid.Result.Success);
            Assert.AreEqual(0, invalid.Changes.Length);
            Assert.IsFalse(client.CompareRevisions(root, string.Empty, target).Result.Success);
            Assert.AreEqual("uncommitted", File.ReadAllText(Path.Combine(root, "file.txt")));
            Assert.AreEqual("added", File.ReadAllText(Path.Combine(root, "added.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "untracked.txt")));

            string oldPath = Path.Combine(root, "added.txt");
            string newPath = Path.Combine(root, "renamed.txt");
            File.Move(oldPath, newPath);
            AssertServerSuccess(client.RecordMove(root, oldPath, newPath), "Record rename");
            AssertServerSuccess(client.Commit(root, "Rename", "test@lorevs"), "Commit rename");
            string renamed = client.GetRevisionHistory(root, string.Empty, 1).Entries.Single().RevisionHash;
            LoreRevisionComparison rename = client.CompareRevisions(root, target, renamed);
            AssertServerSuccess(rename.Result, "Compare renamed file");
            LoreRevisionChange moved = rename.Changes.Single();
            Assert.AreEqual(LoreFileStatus.Renamed, moved.Status);
            Assert.AreEqual(newPath, moved.Path);
            Assert.AreEqual(oldPath, moved.OriginalPath);
            string before = Path.Combine(_tempRoot, "before-rename.txt");
            string after = Path.Combine(_tempRoot, "after-rename.txt");
            AssertServerSuccess(client.WriteFileAtRevision(root, moved.OriginalPath, target, before), "Read rename source");
            AssertServerSuccess(client.WriteFileAtRevision(root, moved.Path, renamed, after), "Read rename target");
            Assert.AreEqual("added", File.ReadAllText(before));
            Assert.AreEqual("added", File.ReadAllText(after));
            Assert.AreEqual("uncommitted", File.ReadAllText(Path.Combine(root, "file.txt")));
        }
    }
}