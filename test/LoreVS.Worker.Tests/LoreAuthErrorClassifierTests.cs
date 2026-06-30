using LoreVS.Worker;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Worker.Tests
{
    /// <summary>
    /// Tests for <see cref="LoreServiceImpl.IsAuthError(string)"/>, the heuristic that classifies a
    /// failed server operation as authentication-related so the package can offer the reactive
    /// sign-in prompt. Must match common credential/permission vocabulary and ignore unrelated errors.
    /// </summary>
    [TestClass]
    public class LoreAuthErrorClassifierTests
    {
        [DataTestMethod]
        [DataRow("Request failed: 401 Unauthorized")]
        [DataRow("permission denied for repository")]
        [DataRow("error: forbidden (403)")]
        [DataRow("authentication required")]
        [DataRow("Your token expired, please log in again")]
        [DataRow("no credentials found for server")]
        public void IsAuthError_TrueForAuthFailures(string message)
        {
            Assert.IsTrue(LoreServiceImpl.IsAuthError(message));
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow(null)]
        [DataRow("connection refused")]
        [DataRow("merge conflict in file.txt")]
        [DataRow("no server is reachable")]
        public void IsAuthError_FalseForUnrelatedFailures(string message)
        {
            Assert.IsFalse(LoreServiceImpl.IsAuthError(message));
        }
    }
}
