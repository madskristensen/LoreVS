using System;
using System.IO;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    /// <summary>
    /// Tests for <see cref="LoreRepositoryLocator.FindRoot"/>, the pure filesystem walk-up that finds
    /// the nearest <c>.lore</c> marker. This drives repository discovery on the SCC glyph hot path, so
    /// the parent search, the prefix-cache boundary, and case-insensitive matching must stay stable.
    /// </summary>
    [TestClass]
    public class LoreRepositoryLocatorTests
    {
        private string _tempRoot = string.Empty;

        [TestInitialize]
        public void Setup()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "lorevs_locator_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best effort; the OS reclaims the temp directory eventually.
            }
        }

        [TestMethod]
        public void FindRoot_EmptyPath_ReturnsNull()
        {
            Assert.IsNull(LoreRepositoryLocator.FindRoot(string.Empty));
        }

        [TestMethod]
        public void FindRoot_NoMarkerAnywhere_ReturnsNull()
        {
            string file = Path.Combine(_tempRoot, "loose.txt");
            File.WriteAllText(file, "x");

            Assert.IsNull(LoreRepositoryLocator.FindRoot(file));
        }

        [TestMethod]
        public void FindRoot_FileInRepository_ReturnsRoot()
        {
            string repo = MakeRepo("repo");
            string file = Path.Combine(repo, "src", "Program.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "x");

            Assert.AreEqual(repo, LoreRepositoryLocator.FindRoot(file));
        }

        [TestMethod]
        public void FindRoot_RepositoryRootItself_ReturnsRoot()
        {
            string repo = MakeRepo("repo");

            Assert.AreEqual(repo, LoreRepositoryLocator.FindRoot(repo));
        }

        [TestMethod]
        public void FindRoot_SiblingWithSharedPrefix_DoesNotMatchOtherRepo()
        {
            string repo = MakeRepo("repo");
            LoreRepositoryLocator.FindRoot(repo);

            string sibling = MakeRepo("repo-extra");
            string file = Path.Combine(sibling, "file.txt");
            File.WriteAllText(file, "x");

            Assert.AreEqual(sibling, LoreRepositoryLocator.FindRoot(file));
        }

        [TestMethod]
        public void FindRoot_IsCaseInsensitive()
        {
            string repo = MakeRepo("repo");
            string file = Path.Combine(repo.ToUpperInvariant(), "file.txt");

            string? root = LoreRepositoryLocator.FindRoot(file);

            Assert.IsNotNull(root);
            Assert.AreEqual(repo, root, ignoreCase: true);
        }

        private string MakeRepo(string name)
        {
            string repo = Path.Combine(_tempRoot, name);
            Directory.CreateDirectory(Path.Combine(repo, LoreRepositoryLocator.RepositoryMarker));
            return repo;
        }
    }
}