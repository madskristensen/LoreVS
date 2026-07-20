using LoreVS.SourceControl;
using LoreVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    /// <summary>
    /// Tests for the pure pagination and change-loading state of <see cref="LoreHistoryViewModel"/>.
    /// The paging append/dedup behavior, the load-more flag derived from the page, the next-start
    /// continuation hash, and the stale-guard that discards a superseded change load are exercised
    /// directly - the threaded async wrappers are intentionally not unit tested.
    /// </summary>
    [TestClass]
    public class LoreHistoryViewModelTests
    {
        private const string Root = @"C:\repo";

        private static LoreRevisionEntry Rev(string hash, long number = 0) => new LoreRevisionEntry
        {
            RevisionHash = hash,
            RevisionNumber = number,
        };

        private static LoreHistoryViewModel NewViewModel() =>
            new LoreHistoryViewModel(new FakeLoreClient(), Root);

        [TestMethod]
        public void ApplyRevisionPage_Initial_PopulatesRevisions()
        {
            var vm = NewViewModel();

            var page = new LoreRevisionPage
            {
                Entries = new[] { Rev("aaa"), Rev("bbb") },
                HasMore = true,
            };

            Assert.IsTrue(vm.ApplyRevisionPage(page, isInitial: true));
            Assert.AreEqual(2, vm.Revisions.Count);
            Assert.AreEqual("aaa", vm.Revisions[0].RevisionHash);
            Assert.IsTrue(vm.CanLoadMore);
        }

        [TestMethod]
        public void ApplyRevisionPage_LoadMore_AppendsInOrder()
        {
            var vm = NewViewModel();
            vm.ApplyRevisionPage(new LoreRevisionPage { Entries = new[] { Rev("aaa"), Rev("bbb") }, HasMore = true }, isInitial: true);

            vm.ApplyRevisionPage(new LoreRevisionPage { Entries = new[] { Rev("ccc") }, HasMore = false }, isInitial: false);

            Assert.AreEqual(3, vm.Revisions.Count);
            Assert.AreEqual("ccc", vm.Revisions[2].RevisionHash);
            Assert.IsFalse(vm.CanLoadMore);
        }

        [TestMethod]
        public void ApplyRevisionPage_SkipsDuplicateHashes()
        {
            var vm = NewViewModel();
            vm.ApplyRevisionPage(new LoreRevisionPage { Entries = new[] { Rev("aaa"), Rev("bbb") }, HasMore = true }, isInitial: true);

            // A continuation page that repeats an already-shown hash must not add it twice.
            vm.ApplyRevisionPage(new LoreRevisionPage { Entries = new[] { Rev("bbb"), Rev("ccc") }, HasMore = false }, isInitial: false);

            Assert.AreEqual(3, vm.Revisions.Count);
            CollectionAssertHashes(vm, "aaa", "bbb", "ccc");
        }

        [TestMethod]
        public void ApplyRevisionPage_SkipsEntriesWithEmptyHash()
        {
            var vm = NewViewModel();

            vm.ApplyRevisionPage(new LoreRevisionPage { Entries = new[] { Rev("aaa"), Rev(string.Empty) }, HasMore = false }, isInitial: true);

            Assert.AreEqual(1, vm.Revisions.Count);
        }

        [TestMethod]
        public void ApplyRevisionPage_InitialClearsPreviousRevisions()
        {
            var vm = NewViewModel();
            vm.ApplyRevisionPage(new LoreRevisionPage { Entries = new[] { Rev("aaa") }, HasMore = true }, isInitial: true);

            vm.ApplyRevisionPage(new LoreRevisionPage { Entries = new[] { Rev("zzz") }, HasMore = false }, isInitial: true);

            Assert.AreEqual(1, vm.Revisions.Count);
            Assert.AreEqual("zzz", vm.Revisions[0].RevisionHash);
        }

        [TestMethod]
        public void ApplyRevisionPage_NullPage_ClearsLoadMore()
        {
            var vm = NewViewModel();
            vm.ApplyRevisionPage(new LoreRevisionPage { Entries = new[] { Rev("aaa") }, HasMore = true }, isInitial: true);

            Assert.IsFalse(vm.ApplyRevisionPage(null, isInitial: false));
            Assert.IsFalse(vm.CanLoadMore);
        }

        [TestMethod]
        public void NextStartRevision_IsLastLoadedHash()
        {
            var vm = NewViewModel();
            Assert.AreEqual(string.Empty, vm.NextStartRevision());

            vm.ApplyRevisionPage(new LoreRevisionPage { Entries = new[] { Rev("aaa"), Rev("bbb") }, HasMore = true }, isInitial: true);
            Assert.AreEqual("bbb", vm.NextStartRevision());
        }

        [TestMethod]
        public void ApplyRevisionChanges_AppliesForPendingRevision()
        {
            var vm = NewViewModel();
            vm.BeginChangesLoad("rev1");

            var changes = new[]
            {
                new LoreRevisionChange { Path = @"C:\repo\a.cs", Status = LoreFileStatus.Modified },
                new LoreRevisionChange { Path = @"C:\repo\b.cs", Status = LoreFileStatus.Added },
            };

            Assert.IsTrue(vm.ApplyRevisionChanges("rev1", changes));
            Assert.AreEqual(2, vm.SelectedRevisionChanges.Count);
            Assert.AreEqual("a.cs", vm.SelectedRevisionChanges[0].FileName);
        }

        [TestMethod]
        public void ApplyRevisionChanges_StaleResultIsDiscarded()
        {
            var vm = NewViewModel();
            vm.BeginChangesLoad("rev1");
            vm.BeginChangesLoad("rev2"); // user selected a newer revision

            var changes = new[] { new LoreRevisionChange { Path = @"C:\repo\a.cs", Status = LoreFileStatus.Modified } };

            // The late result for the superseded rev1 must not overwrite the current selection.
            Assert.IsFalse(vm.ApplyRevisionChanges("rev1", changes));
            Assert.AreEqual(0, vm.SelectedRevisionChanges.Count);
        }

        [TestMethod]
        public void ApplyRevisionChanges_SkipsEntriesWithEmptyPath()
        {
            var vm = NewViewModel();
            vm.BeginChangesLoad("rev1");

            var changes = new[]
            {
                new LoreRevisionChange { Path = @"C:\repo\a.cs", Status = LoreFileStatus.Modified },
                new LoreRevisionChange { Path = string.Empty, Status = LoreFileStatus.Added },
            };

            Assert.IsTrue(vm.ApplyRevisionChanges("rev1", changes));
            Assert.AreEqual(1, vm.SelectedRevisionChanges.Count);
        }

        [TestMethod]
        public void BeginChangesLoad_ClearsPreviousChanges()
        {
            var vm = NewViewModel();
            vm.BeginChangesLoad("rev1");
            vm.ApplyRevisionChanges("rev1", new[] { new LoreRevisionChange { Path = @"C:\repo\a.cs", Status = LoreFileStatus.Modified } });
            Assert.AreEqual(1, vm.SelectedRevisionChanges.Count);

            vm.BeginChangesLoad("rev2");
            Assert.AreEqual(0, vm.SelectedRevisionChanges.Count);
        }

        [TestMethod]
        public void HasRepository_TrueWhenRootProvided()
        {
            Assert.IsTrue(NewViewModel().HasRepository);
        }

        [TestMethod]
        public void RepositoryChanged_SameRoot_IsFalse()
        {
            Assert.IsFalse(LoreHistoryViewModel.RepositoryChanged(@"C:\repo", @"C:\repo"));
        }

        [TestMethod]
        public void RepositoryChanged_IgnoresCase()
        {
            Assert.IsFalse(LoreHistoryViewModel.RepositoryChanged(@"C:\Repo", @"c:\repo"));
        }

        [TestMethod]
        public void RepositoryChanged_DifferentRoot_IsTrue()
        {
            Assert.IsTrue(LoreHistoryViewModel.RepositoryChanged(@"C:\repo", @"C:\other"));
        }

        [TestMethod]
        public void RepositoryChanged_TreatsNullAndEmptyAlike()
        {
            Assert.IsFalse(LoreHistoryViewModel.RepositoryChanged(null, string.Empty));
        }

        [TestMethod]
        public void RepositoryChanged_OpenedRepository_IsTrue()
        {
            // A window that was showing "no repository" (null) and now resolves to a real root
            // must reload so the newly opened repository's history appears.
            Assert.IsTrue(LoreHistoryViewModel.RepositoryChanged(null, @"C:\repo"));
        }

        [TestMethod]
        public void RepositoryChanged_ClosedRepository_IsTrue()
        {
            Assert.IsTrue(LoreHistoryViewModel.RepositoryChanged(@"C:\repo", null));
        }

        private static void CollectionAssertHashes(LoreHistoryViewModel vm, params string[] expected)
        {
            Assert.AreEqual(expected.Length, vm.Revisions.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], vm.Revisions[i].RevisionHash);
            }
        }
    }
}