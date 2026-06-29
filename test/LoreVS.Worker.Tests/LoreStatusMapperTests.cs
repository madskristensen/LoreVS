using LoreVcs.Types.Enums;
using LoreVS.SourceControl;
using LoreVS.Worker;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Worker.Tests
{
    /// <summary>
    /// Tests for <see cref="LoreStatusMapper.Map(LoreFileAction, bool, bool, bool)"/>, which normalizes
    /// a Lore status file event into the <see cref="LoreFileStatus"/> the SCC glyphs consume. Conflict
    /// must win over the action, MOVE/COPY count as modified, and a dirty KEEP is modified.
    /// </summary>
    [TestClass]
    public class LoreStatusMapperTests
    {
        [DataTestMethod]
        [DataRow(LoreFileAction.ADD, LoreFileStatus.Added)]
        [DataRow(LoreFileAction.DELETE, LoreFileStatus.Deleted)]
        [DataRow(LoreFileAction.MOVE, LoreFileStatus.Modified)]
        [DataRow(LoreFileAction.COPY, LoreFileStatus.Modified)]
        public void Map_ActionDrivesStatus(LoreFileAction action, LoreFileStatus expected)
        {
            Assert.AreEqual(expected, LoreStatusMapper.Map(action, conflict: false, conflictUnresolved: false, dirty: false));
        }

        [TestMethod]
        public void Map_KeepDirty_IsModified()
        {
            Assert.AreEqual(LoreFileStatus.Modified, LoreStatusMapper.Map(LoreFileAction.KEEP, false, false, dirty: true));
        }

        [TestMethod]
        public void Map_KeepClean_IsUnchanged()
        {
            Assert.AreEqual(LoreFileStatus.Unchanged, LoreStatusMapper.Map(LoreFileAction.KEEP, false, false, dirty: false));
        }

        [DataTestMethod]
        [DataRow(true, false)]
        [DataRow(false, true)]
        public void Map_AnyConflictFlag_WinsOverAction(bool conflict, bool unresolved)
        {
            Assert.AreEqual(LoreFileStatus.Conflicted, LoreStatusMapper.Map(LoreFileAction.ADD, conflict, unresolved, dirty: true));
        }
    }
}