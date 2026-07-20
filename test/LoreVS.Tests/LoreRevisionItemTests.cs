using System;
using System.Globalization;
using LoreVS.SourceControl;
using LoreVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    /// <summary>
    /// Tests for <see cref="LoreRevisionItem"/>, the view model row behind each revision in the Lore
    /// History window. The display shaping (short hash, first line of the message, local timestamp,
    /// author/time subtitle, parent detection) is pure formatting logic and must stay stable.
    /// </summary>
    [TestClass]
    public class LoreRevisionItemTests
    {
        private static LoreRevisionEntry Entry() => new LoreRevisionEntry
        {
            RevisionHash = "abcdef0123456789",
            ParentRevisionHash = "1111222233334444",
            RevisionNumber = 7,
            Message = "Fix the thing\nwith more detail",
            Author = "Ada",
            BranchName = "main",
            TimestampUnixMs = 1_700_000_000_000,
        };

        [TestMethod]
        public void ShortHash_TakesLeadingCharacters()
        {
            var item = new LoreRevisionItem(Entry());
            Assert.AreEqual("abcdef01", item.ShortHash);
        }

        [TestMethod]
        public void ShortHash_ShorterHashReturnedWhole()
        {
            var item = new LoreRevisionItem(new LoreRevisionEntry { RevisionHash = "abc" });
            Assert.AreEqual("abc", item.ShortHash);
        }

        [TestMethod]
        public void MessageFirstLine_ReturnsFirstNonEmptyLine()
        {
            var item = new LoreRevisionItem(Entry());
            Assert.AreEqual("Fix the thing", item.MessageFirstLine);
        }

        [TestMethod]
        public void MessageFirstLine_EmptyMessageFallsBack()
        {
            var item = new LoreRevisionItem(new LoreRevisionEntry { Message = string.Empty });
            Assert.AreEqual("(no message)", item.MessageFirstLine);
        }

        [TestMethod]
        public void MessageFirstLine_SkipsLeadingBlankLines()
        {
            var item = new LoreRevisionItem(new LoreRevisionEntry { Message = "\r\n\r\n  Real line" });
            Assert.AreEqual("Real line", item.MessageFirstLine);
        }

        [TestMethod]
        public void HasParent_ReflectsParentHash()
        {
            Assert.IsTrue(new LoreRevisionItem(Entry()).HasParent);
            Assert.IsFalse(new LoreRevisionItem(new LoreRevisionEntry { ParentRevisionHash = string.Empty }).HasParent);
        }

        [TestMethod]
        public void LocalTimestamp_ZeroIsEmpty()
        {
            var item = new LoreRevisionItem(new LoreRevisionEntry { TimestampUnixMs = 0 });
            Assert.AreEqual(string.Empty, item.LocalTimestamp);
        }

        [TestMethod]
        public void LocalTimestamp_FormatsInLocalZone()
        {
            var item = new LoreRevisionItem(Entry());
            string expected = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000).LocalDateTime
                .ToString("g", CultureInfo.CurrentCulture);
            Assert.AreEqual(expected, item.LocalTimestamp);
        }

        [TestMethod]
        public void AuthorAndTime_CombinesAuthorAndTime()
        {
            var item = new LoreRevisionItem(Entry());
            StringAssert.Contains(item.AuthorAndTime, "Ada");
            StringAssert.Contains(item.AuthorAndTime, item.LocalTimestamp);
        }

        [TestMethod]
        public void AuthorAndTime_AuthorOnlyWhenNoTimestamp()
        {
            var item = new LoreRevisionItem(new LoreRevisionEntry { Author = "Ada", TimestampUnixMs = 0 });
            Assert.AreEqual("Ada", item.AuthorAndTime);
        }

        [TestMethod]
        public void Constructor_NullEntryThrows()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => new LoreRevisionItem(null!));
        }

        [TestMethod]
        public void RowToolTip_IncludesRevisionNumberAuthorAndMessage()
        {
            var item = new LoreRevisionItem(Entry());
            StringAssert.Contains(item.RowToolTip, "Revision 7");
            StringAssert.Contains(item.RowToolTip, "Ada");
            StringAssert.Contains(item.RowToolTip, "main");
            StringAssert.Contains(item.RowToolTip, "Fix the thing");
        }
    }
}