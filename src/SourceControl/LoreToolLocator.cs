using System;
using System.IO;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// Resolves the <c>lore</c> / <c>loreserver</c> executables to a runnable path.
    /// The Lore install script drops the binaries into <c>%USERPROFILE%\bin</c> and adds
    /// that directory to the user PATH, but a freshly launched process (such as
    /// <c>devenv.exe</c>) keeps a stale PATH until it is restarted. This locator bridges
    /// that gap by also probing the well-known install directory directly, so commands
    /// work immediately after an in-IDE install without requiring a VS restart.
    /// </summary>
    internal static class LoreToolLocator
    {
        /// <summary>Default install directory used by the Lore install script on Windows.</summary>
        public static string DefaultInstallDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "bin");

        /// <summary>
        /// Resolves <paramref name="exeOrPath"/> to a concrete path when possible. An absolute
        /// path is returned as-is. A bare name (e.g. "lore") is probed on PATH and then in the
        /// default install directory; if neither hit, the original value is returned so the OS
        /// can still attempt to resolve it.
        /// </summary>
        public static string Resolve(string exeOrPath)
        {
            if (string.IsNullOrWhiteSpace(exeOrPath))
            {
                return exeOrPath;
            }

            exeOrPath = exeOrPath.Trim();

            if (Path.IsPathRooted(exeOrPath))
            {
                return exeOrPath;
            }

            string name = Path.GetFileName(exeOrPath);
            string nameExe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";

            string onPath = ProbePath(nameExe);
            if (onPath != null)
            {
                return onPath;
            }

            string inInstallDir = Path.Combine(DefaultInstallDir, nameExe);
            if (File.Exists(inInstallDir))
            {
                return inInstallDir;
            }

            return exeOrPath;
        }

        /// <summary>Returns true when <paramref name="exeOrPath"/> can be located on disk.</summary>
        public static bool Exists(string exeOrPath)
        {
            string resolved = Resolve(exeOrPath);
            return Path.IsPathRooted(resolved) && File.Exists(resolved);
        }

        private static string ProbePath(string fileName)
        {
            string pathVar = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVar))
            {
                return null;
            }

            foreach (string dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                string candidate;
                try
                {
                    candidate = Path.Combine(dir.Trim(), fileName);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
