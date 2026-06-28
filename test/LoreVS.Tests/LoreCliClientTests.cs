using System;
using System.IO;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    /// <summary>
    /// Contract tests for <see cref="ILoreClient"/> behavior that must hold regardless of the
    /// backing implementation (CLI today, Lore SDK later): repository discovery via the
    /// <c>.lore</c> marker, the "not in a repo" status, and executable-path normalization. These
    /// exercise only the deterministic, process-free parts of <see cref="LoreCliClient"/>.
    /// </summary>
    [TestClass]
    public class LoreCliClientTests
    {
        private string _tempRoot = string.Empty;

        [TestInitialize]
        public void Setup()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "LoreVS.Tests", Guid.NewGuid().ToString("N"));
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
                // Best effort; temp cleanup must never fail a test.
            }
        }

        [TestMethod]
        public void FindRepositoryRoot_ReturnsRootContainingLoreMarker()
        {
            string repo = Path.Combine(_tempRoot, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".lore"));
            string nested = Path.Combine(repo, "src", "deep");
            Directory.CreateDirectory(nested);

            var client = new LoreCliClient();

            Assert.AreEqual(repo, client.FindRepositoryRoot(nested));
        }

        [TestMethod]
        public void FindRepositoryRoot_WalksUpFromAFilePath()
        {
            string repo = Path.Combine(_tempRoot, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".lore"));
            string file = Path.Combine(repo, "src", "file.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "// test");

            var client = new LoreCliClient();

            Assert.AreEqual(repo, client.FindRepositoryRoot(file));
        }

        [TestMethod]
        public void FindRepositoryRoot_ReturnsNullWhenNoMarkerPresent()
        {
            string noRepo = Path.Combine(_tempRoot, "plain", "child");
            Directory.CreateDirectory(noRepo);

            var client = new LoreCliClient();

            Assert.IsNull(client.FindRepositoryRoot(noRepo));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        public void FindRepositoryRoot_NullOrEmptyReturnsNull(string path)
        {
            var client = new LoreCliClient();
            Assert.IsNull(client.FindRepositoryRoot(path));
        }

        [TestMethod]
        public void GetStatus_OutsideRepositoryIsNotControlled()
        {
            string file = Path.Combine(_tempRoot, "loose.txt");
            File.WriteAllText(file, "x");

            var client = new LoreCliClient();

            Assert.AreEqual(LoreFileStatus.NotControlled, client.GetStatus(file));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        public void GetStatus_NullOrEmptyIsNotControlled(string path)
        {
            var client = new LoreCliClient();
            Assert.AreEqual(LoreFileStatus.NotControlled, client.GetStatus(path));
        }

        [TestMethod]
        public void GetRepositoryStatus_EmptyRootReturnsEmpty()
        {
            var client = new LoreCliClient();
            Assert.AreEqual(0, client.GetRepositoryStatus(string.Empty).Count);
        }

        [TestMethod]
        public void ExecutablePath_DefaultsToBareLore()
        {
            var client = new LoreCliClient();
            Assert.AreEqual("lore", client.ExecutablePath);
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow(null)]
        public void ExecutablePath_BlankResetsToDefault(string value)
        {
            var client = new LoreCliClient { ExecutablePath = "C:\\tools\\lore.exe" };

            client.ExecutablePath = value;

            Assert.AreEqual("lore", client.ExecutablePath);
        }

        [TestMethod]
        public void ExecutablePath_TrimsWhitespace()
        {
            var client = new LoreCliClient { ExecutablePath = "  C:\\tools\\lore.exe  " };
            Assert.AreEqual("C:\\tools\\lore.exe", client.ExecutablePath);
        }
    }
}
