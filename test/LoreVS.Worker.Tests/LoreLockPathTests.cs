using System;
using LoreVS.Worker;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Worker.Tests
{
    [TestClass]
    public sealed class LoreLockPathTests
    {
        [TestMethod]
        public void ValidateLockPaths_NormalizesRelativeAndDeduplicatesAbsoluteFiles()
        {
            string[] result = LoreServiceImpl.ValidateLockPaths(@"C:\repo", new[] { "asset.bin", @"C:\REPO\ASSET.BIN" });
            CollectionAssert.AreEqual(new[] { @"C:\repo\asset.bin" }, result);
        }

        [TestMethod]
        [DataRow("")]
        [DataRow(" ")]
        [DataRow("*.bin")]
        [DataRow("asset?.bin")]
        [DataRow(@"..\outside.bin")]
        [DataRow(@"C:\repo-other\asset.bin")]
        public void ValidateLockPaths_RejectsAmbiguousOrOutsidePaths(string path)
        {
            Assert.ThrowsExactly<ArgumentException>(() => LoreServiceImpl.ValidateLockPaths(@"C:\repo", new[] { path }));
        }

        [TestMethod]
        public void ValidateLockPaths_RejectsEmptySelectionAndRepositoryRoot()
        {
            Assert.ThrowsExactly<ArgumentException>(() => LoreServiceImpl.ValidateLockPaths(@"C:\repo", Array.Empty<string>()));
            Assert.ThrowsExactly<ArgumentException>(() => LoreServiceImpl.ValidateLockPaths(@"C:\repo", new[] { @"C:\repo" }));
            Assert.ThrowsExactly<ArgumentException>(() => LoreServiceImpl.ValidateLockPaths(string.Empty, new[] { "file.bin" }));
        }
    }
}