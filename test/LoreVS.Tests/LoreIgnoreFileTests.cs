using System;
using System.IO;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    /// <summary>
    /// Tests for <see cref="LoreIgnoreFile.EnsureDefault"/>, which seeds a <c>.loreignore</c> so
    /// Visual Studio artifacts are not staged. This onboarding step is independent of how Lore
    /// itself is invoked, so it must keep working after the CLI -> SDK swap.
    /// </summary>
    [TestClass]
    public class LoreIgnoreFileTests
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
                // Best effort.
            }
        }

        [TestMethod]
        public void EnsureDefault_WritesFileWhenMissing()
        {
            bool written = LoreIgnoreFile.EnsureDefault(_tempRoot);

            string path = Path.Combine(_tempRoot, LoreIgnoreFile.FileName);
            Assert.IsTrue(written);
            Assert.IsTrue(File.Exists(path));

            string content = File.ReadAllText(path);
            StringAssert.Contains(content, ".vs/");
            StringAssert.Contains(content, "bin/");
            StringAssert.Contains(content, "obj/");
        }

        [TestMethod]
        public void EnsureDefault_DoesNotOverwriteExistingFile()
        {
            string path = Path.Combine(_tempRoot, LoreIgnoreFile.FileName);
            File.WriteAllText(path, "custom");

            bool written = LoreIgnoreFile.EnsureDefault(_tempRoot);

            Assert.IsFalse(written);
            Assert.AreEqual("custom", File.ReadAllText(path));
        }

        [TestMethod]
        public void EnsureDefault_ReturnsFalseForNonexistentDirectory()
        {
            string missing = Path.Combine(_tempRoot, "does-not-exist");
            Assert.IsFalse(LoreIgnoreFile.EnsureDefault(missing));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        public void EnsureDefault_ReturnsFalseForBlankRoot(string root)
        {
            Assert.IsFalse(LoreIgnoreFile.EnsureDefault(root));
        }
    }
}
