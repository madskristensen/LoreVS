using System;
using LoreVcs;
using LoreVS.SourceControl;
using LoreVS.Worker;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Worker.Tests
{
    /// <summary>
    /// Tests that SDK status codes, not message text, drive the reactive sign-in prompt.
    /// </summary>
    [TestClass]
    public class LoreAuthErrorClassifierTests
    {
        [TestMethod]
        [DataRow(12, true)]
        [DataRow(7, false)]
        [DataRow(18, false)]
        [DataRow(6, false)]
        [DataRow(17, false)]
        [DataRow(-1, false)]
        public void MapFailure_PreservesStatusAndClassifiesAuthentication(int code, bool requiresAuthentication)
        {
            var error = new LoreError(code, new LoreErrorDetailData(
                code, "Authentication or permission denied: 401 403", Array.Empty<LoreErrorTraceLocation>()));

            LoreCommandResult result = LoreServiceImpl.MapFailure(error);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(code, result.ExitCode);
            Assert.AreEqual(requiresAuthentication, result.RequiresAuthentication);
            Assert.AreEqual("Authentication or permission denied: 401 403", result.Error);
        }

        [TestMethod]
        public void MapFailure_UnwrapsAggregateAndOmitsTraceFromUserMessage()
        {
            var detail = new LoreErrorDetailData(12, "Sign in again", new[]
            {
                new LoreErrorTraceLocation("internal.rs", 42, 3, "private diagnostic context"),
            });
            var error = new AggregateException(new AggregateException(new LoreError(12, detail)));

            LoreCommandResult result = LoreServiceImpl.MapFailure(error);

            Assert.AreEqual(12, result.ExitCode);
            Assert.IsTrue(result.RequiresAuthentication);
            Assert.AreEqual("Sign in again", result.Error);
        }

        [TestMethod]
        public void MapFailure_WithoutDetailStillUsesStatus()
        {
            var error = new LoreError(12);

            LoreCommandResult result = LoreServiceImpl.MapFailure(error);

            Assert.AreEqual(12, result.ExitCode);
            Assert.IsTrue(result.RequiresAuthentication);
            Assert.AreEqual(error.Message, result.Error);
        }

        [TestMethod]
        public void MapFailure_NonSdkExceptionDoesNotOfferSignIn()
        {
            LoreCommandResult result = LoreServiceImpl.MapFailure(new TimeoutException("authentication timed out"));

            Assert.IsFalse(result.Success);
            Assert.AreEqual(-1, result.ExitCode);
            Assert.IsFalse(result.RequiresAuthentication);
            Assert.AreEqual("authentication timed out", result.Error);
        }
    }
}
