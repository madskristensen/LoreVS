using System.Collections.Generic;
using System.Linq;
using LoreVS.SourceControl;
using LoreVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    /// <summary>
    /// Tests for <see cref="LoreTreeNode"/>, which arranges the flat list of changed files into the
    /// folder tree shown in the Lore Changes window. The grouping, ordering, depth, and
    /// expand/collapse flattening are pure logic and must stay stable so the tree renders correctly.
    /// </summary>
    [TestClass]
    public class LoreTreeNodeTests
    {
        private const string Root = @"C:\repo";

        private static LoreChangeItem File(string relative, LoreFileStatus status = LoreFileStatus.Modified) =>
            new LoreChangeItem(Root + "\\" + relative, Root, status);

        [TestMethod]
        public void BuildTree_GroupsFilesUnderFolderNodes()
        {
            List<LoreTreeNode> roots = LoreTreeNode.BuildTree(new[]
            {
                File(@"src\app\Program.cs"),
                File(@"src\app\Helper.cs"),
                File(@"readme.md"),
            });

            // Root level: the "src" folder then the root-level file.
            Assert.AreEqual(2, roots.Count);
            Assert.IsTrue(roots[0].IsFolder);
            Assert.AreEqual("src", roots[0].Name);
            Assert.AreEqual(0, roots[0].Depth);

            Assert.IsFalse(roots[1].IsFolder);
            Assert.AreEqual("readme.md", roots[1].Name);

            LoreTreeNode app = roots[0].Children.Single();
            Assert.IsTrue(app.IsFolder);
            Assert.AreEqual("app", app.Name);
            Assert.AreEqual(1, app.Depth);

            // The two files live under src\app, sorted alphabetically, at depth 2.
            Assert.AreEqual(2, app.Children.Count);
            CollectionAssert.AreEqual(
                new[] { "Helper.cs", "Program.cs" },
                app.Children.Select(c => c.Name).ToArray());
            Assert.IsTrue(app.Children.All(c => !c.IsFolder && c.Depth == 2));
        }

        [TestMethod]
        public void BuildTree_OrdersFoldersBeforeFilesThenAlphabetically()
        {
            List<LoreTreeNode> roots = LoreTreeNode.BuildTree(new[]
            {
                File(@"zeta.txt"),
                File(@"alpha.txt"),
                File(@"beta\inner.txt"),
            });

            // Folder "beta" first, then the two root files alphabetically.
            CollectionAssert.AreEqual(
                new[] { "beta", "alpha.txt", "zeta.txt" },
                roots.Select(r => r.Name).ToArray());
        }

        [TestMethod]
        public void Flatten_ExpandedFolderIncludesChildren()
        {
            List<LoreTreeNode> roots = LoreTreeNode.BuildTree(new[]
            {
                File(@"src\Program.cs"),
            });

            var visible = new List<LoreTreeNode>();
            LoreTreeNode.Flatten(roots, visible);

            CollectionAssert.AreEqual(
                new[] { "src", "Program.cs" },
                visible.Select(n => n.Name).ToArray());
        }

        [TestMethod]
        public void Flatten_CollapsedFolderHidesChildren()
        {
            List<LoreTreeNode> roots = LoreTreeNode.BuildTree(new[]
            {
                File(@"src\Program.cs"),
            });
            roots[0].IsExpanded = false;

            var visible = new List<LoreTreeNode>();
            LoreTreeNode.Flatten(roots, visible);

            CollectionAssert.AreEqual(
                new[] { "src" },
                visible.Select(n => n.Name).ToArray());
        }

        [TestMethod]
        public void FileLeaf_CarriesStatusBadgeAndPath()
        {
            List<LoreTreeNode> roots = LoreTreeNode.BuildTree(new[]
            {
                File(@"src\New.cs", LoreFileStatus.Added),
            });

            LoreTreeNode leaf = roots[0].Children.Single();
            Assert.AreEqual("A", leaf.StatusBadge);
            Assert.AreEqual("Added", leaf.StatusText);
            Assert.AreEqual(Root + @"\src\New.cs", leaf.FullPath);
            Assert.IsTrue(leaf.ExpanderGlyph.Length == 0);
        }

        [TestMethod]
        public void FolderNode_HasNoBadgeAndShowsChevron()
        {
            List<LoreTreeNode> roots = LoreTreeNode.BuildTree(new[]
            {
                File(@"src\New.cs"),
            });

            LoreTreeNode folder = roots[0];
            Assert.AreEqual(string.Empty, folder.StatusBadge);
            Assert.IsTrue(folder.ExpanderGlyph.Length > 0);
        }

        [TestMethod]
        public void FileLeaf_DefaultsToChecked()
        {
            List<LoreTreeNode> roots = LoreTreeNode.BuildTree(new[]
            {
                File(@"src\New.cs"),
            });

            Assert.AreEqual(true, roots[0].Children.Single().IsChecked);
        }

        [TestMethod]
        public void FolderNode_AggregatesDescendantCheckState()
        {
            List<LoreTreeNode> roots = LoreTreeNode.BuildTree(new[]
            {
                File(@"src\A.cs"),
                File(@"src\B.cs"),
            });
            LoreTreeNode folder = roots[0];

            // All children checked by default -> folder reads true.
            Assert.AreEqual(true, folder.IsChecked);

            // One child off -> mixed -> indeterminate (null).
            folder.Children[0].IsChecked = false;
            Assert.IsNull(folder.IsChecked);

            // Both off -> false.
            folder.Children[1].IsChecked = false;
            Assert.AreEqual(false, folder.IsChecked);
        }

        [TestMethod]
        public void FolderNode_SettingCheckPropagatesToDescendants()
        {
            List<LoreTreeNode> roots = LoreTreeNode.BuildTree(new[]
            {
                File(@"src\app\A.cs"),
                File(@"src\app\B.cs"),
            });
            LoreTreeNode src = roots[0];

            src.IsChecked = false;
            Assert.IsTrue(src.EnumerateFileLeaves().All(f => f.IsChecked == false));

            src.IsChecked = true;
            Assert.IsTrue(src.EnumerateFileLeaves().All(f => f.IsChecked == true));
        }

        [TestMethod]
        public void FileLeaf_CheckChange_RaisesEventAndUpdatesParentDisplay()
        {
            List<LoreTreeNode> roots = LoreTreeNode.BuildTree(new[]
            {
                File(@"src\A.cs"),
                File(@"src\B.cs"),
            });
            LoreTreeNode folder = roots[0];
            LoreTreeNode leaf = folder.Children[0];

            int leafEvents = 0;
            leaf.CheckedChanged += (s, e) => leafEvents++;

            var parentChanged = new List<string?>();
            folder.PropertyChanged += (s, e) => parentChanged.Add(e.PropertyName);

            leaf.IsChecked = false;

            Assert.AreEqual(1, leafEvents);
            Assert.IsTrue(parentChanged.Contains(nameof(LoreTreeNode.IsChecked)));

            // Toggling to the same value is a no-op (no extra event).
            leaf.IsChecked = false;
            Assert.AreEqual(1, leafEvents);
        }

        [TestMethod]
        public void BuildTree_WiresParentPointers()
        {
            List<LoreTreeNode> roots = LoreTreeNode.BuildTree(new[]
            {
                File(@"src\app\A.cs"),
            });

            LoreTreeNode src = roots[0];
            LoreTreeNode app = src.Children.Single();
            LoreTreeNode leaf = app.Children.Single();

            Assert.IsNull(src.Parent);
            Assert.AreSame(src, app.Parent);
            Assert.AreSame(app, leaf.Parent);
        }

        [TestMethod]
        public void EnumerateFileLeaves_ReturnsOnlyFiles()
        {
            List<LoreTreeNode> roots = LoreTreeNode.BuildTree(new[]
            {
                File(@"src\app\A.cs"),
                File(@"src\B.cs"),
                File(@"readme.md"),
            });

            string[] names = roots
                .SelectMany(r => r.EnumerateFileLeaves())
                .Select(n => n.Name)
                .OrderBy(n => n)
                .ToArray();

            CollectionAssert.AreEqual(new[] { "A.cs", "B.cs", "readme.md" }, names);
        }
    }
}
