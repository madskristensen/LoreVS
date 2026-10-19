using LoreVS.Worker;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Worker.Tests
{
    [TestClass]
    public sealed class BranchSwitchConnectivityTests
    {
        [TestMethod]
        [DataRow("Address not found", true)]
        [DataRow("Unable to synchronize state", true)]
        [DataRow("branch not found: feature (remote unavailable for branch lookup)", true)]
        [DataRow("Uncommitted changes would be overwritten", false)]
        [DataRow("Permission denied", false)]
        [DataRow("branch not found: feature", false)]
        [DataRow("", false)]
        public void OnlineRetry_OnlyForUncachedBranchData(string error, bool retry)
        {
            Assert.AreEqual(retry, LoreServiceImpl.RequiresRemoteBranchData(error));
        }
    }
}