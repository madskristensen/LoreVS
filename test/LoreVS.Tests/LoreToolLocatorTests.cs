using System;
using System.IO;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    /// <summary>
    /// Tests for <see cref="LoreToolLocator"/>. The locator resolves the <c>lore</c>/<c>loreserver</c>
    /// executables; the <c>loreserver</c> binary is still needed after the SDK swap, so its
    /// resolution rules (rooted-as-is, bare-name probing, graceful fallback) are pinned here.
    /// </summary>
    [TestClass]
    public class LoreToolLocatorTests
    {
        [TestMethod]
        public void Resolve_RootedPathReturnedAsIs()
        {
            string rooted = Path.Combine(Path.GetTempPath(), "tools", "lore.exe");
            Assert.AreEqual(rooted, LoreToolLocator.Resolve(rooted));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        public void Resolve_BlankReturnedUnchanged(string value)
        {
            Assert.AreEqual(value, LoreToolLocator.Resolve(value));
        }

        [TestMethod]
        public void Resolve_UnresolvableBareNameFallsBackToOriginal()
        {
            // A name that exists on neither PATH nor the install dir must come back unchanged so the
            // OS can still try to launch it.
            string name = "lore-nonexistent-" + Guid.NewGuid().ToString("N");
            Assert.AreEqual(name, LoreToolLocator.Resolve(name));
        }

        [TestMethod]
        public void Resolve_FindsExecutableOnPath()
        {
            string dir = Path.Combine(Path.GetTempPath(), "LoreVS.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, "lorefake.exe");
            File.WriteAllText(exe, string.Empty);

            string originalPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + originalPath);

                // Bare name without extension should be resolved to the rooted .exe on PATH.
                Assert.AreEqual(exe, LoreToolLocator.Resolve("lorefake"));
                Assert.IsTrue(LoreToolLocator.Exists("lorefake"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // Best effort.
                }
            }
        }

        [TestMethod]
        public void Exists_FalseForUnresolvableName()
        {
            string name = "lore-nonexistent-" + Guid.NewGuid().ToString("N");
            Assert.IsFalse(LoreToolLocator.Exists(name));
        }
    }
}
