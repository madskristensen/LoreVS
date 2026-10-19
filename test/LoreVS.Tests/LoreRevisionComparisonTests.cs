using System;
using System.Collections.Generic;
using System.IO;
using LoreVS.SourceControl;
using LoreVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    [TestClass]
    public sealed class LoreRevisionComparisonTests
    {
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Selection_OrdersOlderBeforeNewer_AndDiscardsPreviousPair(bool reverseClicks)
        {
            var vm = new LoreHistoryViewModel(new FakeLoreClient(), @"C:\repo");
            vm.ApplyRevisionPage(new LoreRevisionPage { Entries = new[]
            {
                new LoreRevisionEntry { RevisionHash = "new" },
                new LoreRevisionEntry { RevisionHash = "middle" },
                new LoreRevisionEntry { RevisionHash = "old" },
            } }, true);
            vm.ApplySelection(reverseClicks ? new[] { vm.Revisions[2], vm.Revisions[0] }
                : new[] { vm.Revisions[0], vm.Revisions[2] });
            Assert.AreEqual("new", vm.SelectedRevision!.RevisionHash);
            Assert.AreEqual("old", vm.ComparisonBaseRevisionHash);
            string previous = vm.SelectionRequestKey;
            vm.ApplySelection(new[] { vm.Revisions[0], vm.Revisions[1] });
            Assert.AreEqual("middle", vm.ComparisonBaseRevisionHash);
            Assert.IsFalse(vm.ApplyRevisionChanges(previous, new[] { new LoreRevisionChange { Path = @"C:\repo\stale" } }));
            Assert.AreEqual(0, vm.SelectedRevisionChanges.Count);
            vm.ApplySelection(new[] { vm.Revisions[2], vm.Revisions[0] });
            Assert.AreNotEqual(previous, vm.SelectionRequestKey, "Returning to a pair must not accept an earlier request.");
        }

        [TestMethod]
        public void Selection_SingleEmptyAndTooMany_ClearComparison()
        {
            var vm = new LoreHistoryViewModel(new FakeLoreClient(), @"C:\repo");
            vm.ApplyRevisionPage(new LoreRevisionPage { Entries = new[]
            {
                new LoreRevisionEntry { RevisionHash = "a" }, new LoreRevisionEntry { RevisionHash = "b" },
                new LoreRevisionEntry { RevisionHash = "c" },
            } }, true);
            vm.ApplySelection(vm.Revisions);
            Assert.IsNull(vm.SelectedRevision);
            Assert.AreEqual(string.Empty, vm.ComparisonBaseRevisionHash);
            Assert.AreEqual("Select at most two revisions to compare", vm.ChangesTitle);
            vm.ApplySelection(new[] { vm.Revisions[1] });
            Assert.AreSame(vm.Revisions[1], vm.SelectedRevision);
            Assert.AreEqual(string.Empty, vm.ComparisonBaseRevisionHash);
            vm.ApplySelection(Array.Empty<LoreRevisionItem>());
            Assert.IsNull(vm.SelectedRevision);
        }

        [TestMethod]
        public void Changes_PreserveRenameSource()
        {
            var vm = new LoreHistoryViewModel(new FakeLoreClient(), @"C:\repo");
            vm.BeginChangesLoad("pair");
            Assert.IsTrue(vm.ApplyRevisionChanges("pair", new[] { new LoreRevisionChange
            {
                Path = @"C:\repo\new.txt", OriginalPath = @"C:\repo\old.txt", Status = LoreFileStatus.Renamed,
            } }));
            Assert.AreEqual(@"C:\repo\old.txt", vm.SelectedRevisionChanges[0].OriginalFullPath);
        }

        [TestMethod]
        [DataRow(LoreFileStatus.Modified)]
        [DataRow(LoreFileStatus.Added)]
        [DataRow(LoreFileStatus.Deleted)]
        [DataRow(LoreFileStatus.Renamed)]
        public void MaterializeComparison_UsesHistoricalContentAndCorrectPaths(LoreFileStatus status)
        {
            var calls = new List<string>();
            var paths = new List<string>();
            var client = new FakeLoreClient
            {
                WriteFileAtRevisionHandler = (root, path, revision, output) =>
                {
                    calls.Add(path + "@" + revision);
                    paths.Add(output);
                    File.WriteAllText(output, revision);
                    return new LoreCommandResult(true, 0, string.Empty, string.Empty);
                },
            };
            var item = new LoreChangeItem(@"C:\repo\new.txt", @"C:\repo", status,
                status == LoreFileStatus.Renamed ? @"C:\repo\old.txt" : null);
            try
            {
                var result = LoreHistoryDiffPresenter.MaterializeComparison(client, @"C:\repo", item, "source", "target");
                paths.Add(result.Left);
                paths.Add(result.Right);
                Assert.AreEqual(status == LoreFileStatus.Added ? string.Empty : "source", File.ReadAllText(result.Left));
                Assert.AreEqual(status == LoreFileStatus.Deleted ? string.Empty : "target", File.ReadAllText(result.Right));
                Assert.AreEqual(status == LoreFileStatus.Added || status == LoreFileStatus.Deleted ? 1 : 2, calls.Count);
                if (status != LoreFileStatus.Added)
                {
                    Assert.AreEqual((status == LoreFileStatus.Renamed ? @"C:\repo\old.txt" : item.FullPath) + "@source", calls[0]);
                }
                if (status != LoreFileStatus.Deleted)
                {
                    Assert.AreEqual(item.FullPath + "@target", calls[calls.Count - 1]);
                }
            }
            finally
            {
                foreach (string path in paths) { File.Delete(path); }
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Materialize_FailureOrMissingOutput_ThrowsInsteadOfOpeningWorkingFile(bool success)
        {
            var client = new FakeLoreClient
            {
                WriteFileAtRevisionHandler = (_, __, ___, ____) => success
                    ? new LoreCommandResult(true, 0, string.Empty, string.Empty) : LoreCommandResult.Failed("historical read failed"),
            };
            var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
                LoreHistoryDiffPresenter.Materialize(client, @"C:\repo", @"C:\repo\file.txt", "source"));
            StringAssert.Contains(error.Message, "Could not read");
        }
    }
}