using System.Collections.Generic;
using System.IO;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    /// <summary>
    /// Tests for <see cref="LoreStatusParser"/>. This is the most swap-sensitive logic: when the
    /// CLI back end is replaced by the .NET Lore SDK, raw Lore states must still translate into the
    /// same normalized <see cref="LoreFileStatus"/> values, so these assertions act as the contract.
    /// </summary>
    [TestClass]
    public class LoreStatusParserTests
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "LoreVS.Tests", "repo");

        private static LoreFileStatus StatusFor(IReadOnlyDictionary<string, LoreFileStatus> map, string relative)
        {
            string key = Path.GetFullPath(Path.Combine(Root, relative)).TrimEnd('\\', '/');
            Assert.IsTrue(map.ContainsKey(key), $"Expected an entry for '{relative}'.");
            return map[key];
        }

        [TestMethod]
        [DataRow("A", LoreFileStatus.Added)]
        [DataRow("a", LoreFileStatus.Added)]
        [DataRow("D", LoreFileStatus.Deleted)]
        [DataRow("C", LoreFileStatus.Conflicted)]
        [DataRow("U", LoreFileStatus.Conflicted)]
        [DataRow("L", LoreFileStatus.Locked)]
        [DataRow("I", LoreFileStatus.Ignored)]
        [DataRow("M", LoreFileStatus.Modified)]
        [DataRow("R", LoreFileStatus.Modified)]
        public void MapCode_MapsKnownCodes(string code, LoreFileStatus expected)
        {
            Assert.AreEqual(expected, LoreStatusParser.MapCode(code));
        }

        [TestMethod]
        [DataRow("X")]
        [DataRow("Z")]
        [DataRow("")]
        public void MapCode_UnknownCodesDegradeToModified(string code)
        {
            Assert.AreEqual(LoreFileStatus.Modified, LoreStatusParser.MapCode(code));
        }

        [TestMethod]
        public void MapCode_NullIsModified()
        {
            Assert.AreEqual(LoreFileStatus.Modified, LoreStatusParser.MapCode(null!));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        public void Parse_EmptyOutputIsEmpty(string output)
        {
            IReadOnlyDictionary<string, LoreFileStatus> map = LoreStatusParser.Parse(output, Root);
            Assert.AreEqual(0, map.Count);
        }

        [TestMethod]
        public void Parse_ReadsCodeAndRelativePath()
        {
            string output = "A hello.txt\nM src/foo.cpp\nD old.bin";

            IReadOnlyDictionary<string, LoreFileStatus> map = LoreStatusParser.Parse(output, Root);

            Assert.AreEqual(3, map.Count);
            Assert.AreEqual(LoreFileStatus.Added, StatusFor(map, "hello.txt"));
            Assert.AreEqual(LoreFileStatus.Modified, StatusFor(map, "src/foo.cpp"));
            Assert.AreEqual(LoreFileStatus.Deleted, StatusFor(map, "old.bin"));
        }

        [TestMethod]
        public void Parse_SkipsHeadersAndSectionTitles()
        {
            string output = string.Join("\n", new[]
            {
                "Repository my-repo",
                "On branch main",
                "Remote lore://127.0.0.1:41337/my-repo",
                "Local branch main",
                "Changes staged for commit:",
                "A added.txt",
                "",
                "M changed.txt",
            });

            IReadOnlyDictionary<string, LoreFileStatus> map = LoreStatusParser.Parse(output, Root);

            Assert.AreEqual(2, map.Count);
            Assert.AreEqual(LoreFileStatus.Added, StatusFor(map, "added.txt"));
            Assert.AreEqual(LoreFileStatus.Modified, StatusFor(map, "changed.txt"));
        }

        [TestMethod]
        public void Parse_RenameTracksDestinationPath()
        {
            string output = "R old/name.txt -> new/name.txt";

            IReadOnlyDictionary<string, LoreFileStatus> map = LoreStatusParser.Parse(output, Root);

            Assert.AreEqual(1, map.Count);
            Assert.AreEqual(LoreFileStatus.Modified, StatusFor(map, "new/name.txt"));
        }

        [TestMethod]
        public void Parse_StripsSurroundingQuotesFromPaths()
        {
            string output = "M \"path with spaces.txt\"";

            IReadOnlyDictionary<string, LoreFileStatus> map = LoreStatusParser.Parse(output, Root);

            Assert.AreEqual(1, map.Count);
            Assert.AreEqual(LoreFileStatus.Modified, StatusFor(map, "path with spaces.txt"));
        }

        [TestMethod]
        public void Parse_UnknownCodeStillReportedAsModified()
        {
            string output = "Q strange.txt";

            IReadOnlyDictionary<string, LoreFileStatus> map = LoreStatusParser.Parse(output, Root);

            Assert.AreEqual(1, map.Count);
            Assert.AreEqual(LoreFileStatus.Modified, StatusFor(map, "strange.txt"));
        }

        [TestMethod]
        public void Parse_IgnoresProseLinesWithoutAStatusCode()
        {
            // "nothing to commit" is prose: first token is longer than two letters.
            string output = "nothing to commit, working tree clean";

            IReadOnlyDictionary<string, LoreFileStatus> map = LoreStatusParser.Parse(output, Root);

            Assert.AreEqual(0, map.Count);
        }

        [TestMethod]
        public void Parse_HandlesCrLfLineEndings()
        {
            string output = "A one.txt\r\nM two.txt\r\n";

            IReadOnlyDictionary<string, LoreFileStatus> map = LoreStatusParser.Parse(output, Root);

            Assert.AreEqual(2, map.Count);
            Assert.AreEqual(LoreFileStatus.Added, StatusFor(map, "one.txt"));
            Assert.AreEqual(LoreFileStatus.Modified, StatusFor(map, "two.txt"));
        }

        [TestMethod]
        public void Parse_LastEntryWinsForDuplicatePaths()
        {
            string output = "A dup.txt\nM dup.txt";

            IReadOnlyDictionary<string, LoreFileStatus> map = LoreStatusParser.Parse(output, Root);

            Assert.AreEqual(1, map.Count);
            Assert.AreEqual(LoreFileStatus.Modified, StatusFor(map, "dup.txt"));
        }
    }
}
