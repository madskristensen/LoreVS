using LoreVS.Worker;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Worker.Tests
{
    [TestClass]
    public class LoreSyncWarningTests
    {
        [TestMethod]
        [DataRow(false, false, false)]
        [DataRow(false, false, true)]
        [DataRow(false, true, false)]
        [DataRow(false, true, true)]
        [DataRow(true, true, true)]
        public void GetSyncWarning_AuthoritativeOrRemoteTargetHasNoWarning(bool local, bool remoteAvailable, bool remoteAuthorized)
        {
            Assert.AreEqual(string.Empty, LoreServiceImpl.GetSyncWarning(local, remoteAvailable, remoteAuthorized));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void GetSyncWarning_NoRemoteExplainsLocalOnlySync(bool remoteAuthorized)
        {
            Assert.AreEqual(
                "No Lore remote is configured. Only local revisions were synchronized; no remote changes were fetched.",
                LoreServiceImpl.GetSyncWarning(local: true, remoteAvailable: false, remoteAuthorized));
        }

        [TestMethod]
        public void GetSyncWarning_RemoteQueryFailureExplainsMissingChanges()
        {
            Assert.AreEqual(
                "Lore could not query the remote branch with the current connection or permissions. " +
                "Only local revisions were synchronized; remote changes may be missing.",
                LoreServiceImpl.GetSyncWarning(local: true, remoteAvailable: true, remoteAuthorized: false));
        }
    }
}