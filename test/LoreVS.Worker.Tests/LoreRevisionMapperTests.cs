using LoreVcs.Types.Enums;
using LoreVS.SourceControl;
using LoreVS.Worker;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Worker.Tests
{
    /// <summary>
    /// Tests for <see cref="LoreRevisionMapper"/>, which normalizes the native revision-history /
    /// revision-info FFI data into the shapes the Lore History window consumes. The file-action
    /// mapping, the direct-parent selection from the fixed-length parent slots, and the hash helpers
    /// must stay stable so revisions and their changed files render correctly.
    /// </summary>
    [TestClass]
    public class LoreRevisionMapperTests
    {
        [TestMethod]
        [DataRow(LoreFileAction.ADD, LoreFileStatus.Added)]
        [DataRow(LoreFileAction.DELETE, LoreFileStatus.Deleted)]
        [DataRow(LoreFileAction.MOVE, LoreFileStatus.Renamed)]
        [DataRow(LoreFileAction.COPY, LoreFileStatus.Modified)]
        public void MapDelta_ActionDrivesStatus(LoreFileAction action, LoreFileStatus expected)
        {
            Assert.AreEqual(expected, LoreRevisionMapper.MapDelta(action, flagModify: false));
        }

        [TestMethod]
        public void MapDelta_KeepModified_IsModified()
        {
            Assert.AreEqual(LoreFileStatus.Modified, LoreRevisionMapper.MapDelta(LoreFileAction.KEEP, flagModify: true));
        }

        [TestMethod]
        public void MapDelta_KeepClean_IsUnchanged()
        {
            Assert.AreEqual(LoreFileStatus.Unchanged, LoreRevisionMapper.MapDelta(LoreFileAction.KEEP, flagModify: false));
        }

        [TestMethod]
        public void ToHex_NullOrEmpty_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, LoreRevisionMapper.ToHex(null));
            Assert.AreEqual(string.Empty, LoreRevisionMapper.ToHex(new byte[0]));
        }

        [TestMethod]
        public void ToHex_ProducesLowercaseHex()
        {
            byte[] data = { 0x0a, 0xFF, 0x01, 0x00 };
            Assert.AreEqual("0aff0100", LoreRevisionMapper.ToHex(data));
        }

        [TestMethod]
        public void IsEmptyHash_TreatsNullEmptyAndAllZerosAsEmpty()
        {
            Assert.IsTrue(LoreRevisionMapper.IsEmptyHash(null!));
            Assert.IsTrue(LoreRevisionMapper.IsEmptyHash(string.Empty));
            Assert.IsTrue(LoreRevisionMapper.IsEmptyHash("00000000"));
        }

        [TestMethod]
        public void IsEmptyHash_NonZeroIsNotEmpty()
        {
            Assert.IsFalse(LoreRevisionMapper.IsEmptyHash("00000001"));
            Assert.IsFalse(LoreRevisionMapper.IsEmptyHash("abcdef"));
        }

        [TestMethod]
        public void ParentHash_Null_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, LoreRevisionMapper.ParentHash(null));
        }

        [TestMethod]
        public void ParentHash_AllZeroSlots_ReturnsEmptyForRootRevision()
        {
            var parents = new byte[][]
            {
                new byte[] { 0x00, 0x00 },
                new byte[] { 0x00, 0x00 },
            };

            Assert.AreEqual(string.Empty, LoreRevisionMapper.ParentHash(parents));
        }

        [TestMethod]
        public void ParentHash_ReturnsFirstNonZeroSlot()
        {
            var parents = new byte[][]
            {
                new byte[] { 0x00, 0x00 },
                new byte[] { 0x12, 0x34 },
            };

            Assert.AreEqual("1234", LoreRevisionMapper.ParentHash(parents));
        }

        [TestMethod]
        public void ParentHash_FirstSlotWins_WhenBothNonZero()
        {
            var parents = new byte[][]
            {
                new byte[] { 0xab, 0xcd },
                new byte[] { 0x12, 0x34 },
            };

            Assert.AreEqual("abcd", LoreRevisionMapper.ParentHash(parents));
        }
    }
}