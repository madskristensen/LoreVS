using LoreVS.SourceControl;
using LoreVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    /// <summary>
    /// Tests for <see cref="LoreChangeItem"/>, the view model row behind each file in the Lore
    /// Changes window. The path shaping and status mapping are pure logic and must stay stable so
    /// the window displays the correct relative path, directory, and status badge.
    /// </summary>
    [TestClass]
    public class LoreChangeItemTests
    {
        private const string Root = @"C:\repo";

        [TestMethod]
        public void Constructor_ComputesRelativePathFileNameAndDirectory()
        {
            var item = new LoreChangeItem(@"C:\repo\src\app\Program.cs", Root, LoreFileStatus.Modified);

            Assert.AreEqual(@"src\app\Program.cs", item.RelativePath);
            Assert.AreEqual("Program.cs", item.FileName);
            Assert.AreEqual(@"src\app", item.Directory);
        }

        [TestMethod]
        public void Constructor_RootLevelFileHasEmptyDirectory()
        {
            var item = new LoreChangeItem(@"C:\repo\readme.md", Root, LoreFileStatus.Added);

            Assert.AreEqual("readme.md", item.RelativePath);
            Assert.AreEqual("readme.md", item.FileName);
            Assert.AreEqual(string.Empty, item.Directory);
        }

        [TestMethod]
        public void Constructor_TrailingSeparatorOnRootIsHandled()
        {
            var item = new LoreChangeItem(@"C:\repo\src\File.cs", @"C:\repo\", LoreFileStatus.Modified);

            Assert.AreEqual(@"src\File.cs", item.RelativePath);
        }

        [TestMethod]
        public void Constructor_PathOutsideRootFallsBackToFullPath()
        {
            var item = new LoreChangeItem(@"C:\other\File.cs", Root, LoreFileStatus.Modified);

            Assert.AreEqual(@"C:\other\File.cs", item.RelativePath);
        }

        [TestMethod]
        public void Constructor_RootMatchIsCaseInsensitive()
        {
            var item = new LoreChangeItem(@"C:\REPO\src\File.cs", Root, LoreFileStatus.Modified);

            Assert.AreEqual(@"src\File.cs", item.RelativePath);
        }

        [DataTestMethod]
        [DataRow(LoreFileStatus.Modified, "M", "Modified")]
        [DataRow(LoreFileStatus.Added, "A", "Added")]
        [DataRow(LoreFileStatus.Deleted, "D", "Deleted")]
        [DataRow(LoreFileStatus.Conflicted, "C", "Conflicted")]
        [DataRow(LoreFileStatus.Locked, "L", "Locked")]
        public void StatusBadgeAndText_MapKnownStatuses(LoreFileStatus status, string badge, string text)
        {
            var item = new LoreChangeItem(@"C:\repo\File.cs", Root, status);

            Assert.AreEqual(badge, item.StatusBadge);
            Assert.AreEqual(text, item.StatusText);
        }
    }
}
