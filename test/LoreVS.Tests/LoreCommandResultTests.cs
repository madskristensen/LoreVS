using System;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    /// <summary>
    /// Tests for <see cref="LoreCommandResult"/>, the implementation-independent value type every
    /// <see cref="ILoreClient"/> write operation returns. Its display/text shaping must stay stable
    /// across the CLI -> SDK swap so command UX is unaffected.
    /// </summary>
    [TestClass]
    public class LoreCommandResultTests
    {
        [TestMethod]
        public void Failed_ProducesFailureWithMinusOneExitCode()
        {
            LoreCommandResult result = LoreCommandResult.Failed("boom");

            Assert.IsFalse(result.Success);
            Assert.AreEqual(-1, result.ExitCode);
            Assert.AreEqual(string.Empty, result.Output);
            Assert.AreEqual("boom", result.Error);
        }

        [TestMethod]
        public void Constructor_NullOutputAndErrorBecomeEmpty()
        {
            var result = new LoreCommandResult(true, 0, null!, null!);

            Assert.AreEqual(string.Empty, result.Output);
            Assert.AreEqual(string.Empty, result.Error);
        }

        [TestMethod]
        public void CombinedText_OutputOnly()
        {
            var result = new LoreCommandResult(true, 0, "all good", string.Empty);
            Assert.AreEqual("all good", result.CombinedText);
        }

        [TestMethod]
        public void CombinedText_ErrorOnly()
        {
            var result = new LoreCommandResult(false, 1, string.Empty, "bad thing");
            Assert.AreEqual("bad thing", result.CombinedText);
        }

        [TestMethod]
        public void CombinedText_JoinsOutputAndErrorWithNewLine()
        {
            var result = new LoreCommandResult(false, 1, "stdout", "stderr");
            Assert.AreEqual("stdout" + Environment.NewLine + "stderr", result.CombinedText);
        }

        [TestMethod]
        public void CombinedText_IsTrimmed()
        {
            var result = new LoreCommandResult(true, 0, "  padded  ", string.Empty);
            Assert.AreEqual("padded", result.CombinedText);
        }

        [TestMethod]
        public void CombinedText_BothEmptyIsEmpty()
        {
            var result = new LoreCommandResult(true, 0, string.Empty, string.Empty);
            Assert.AreEqual(string.Empty, result.CombinedText);
        }
    }
}
