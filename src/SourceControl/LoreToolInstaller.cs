using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// Installs the <c>lore</c> CLI and <c>loreserver</c> binaries (for any that are missing)
    /// by running the official install script, so the user can onboard and run a local server
    /// without leaving Visual Studio. Shared by the "Install Lore Tools" command and the
    /// automatic first-run prompt.
    /// </summary>
    internal static class LoreToolInstaller
    {
        private const string InstallScriptUrl =
            "https://raw.githubusercontent.com/EpicGames/lore/main/scripts/install.ps1";

        /// <summary>
        /// Installs the missing tools. <paramref name="installCli"/> and
        /// <paramref name="installServer"/> indicate which binaries to install (typically the
        /// ones that are not already present). Returns true when every requested install
        /// succeeded. Output is written to the "Lore" Output pane.
        /// </summary>
        public static async Task<bool> InstallAsync(bool installCli, bool installServer)
        {
            bool ok = true;
            if (installCli)
            {
                ok &= await RunInstallAsync("Lore CLI", prefix: string.Empty);
            }

            if (ok && installServer)
            {
                ok &= await RunInstallAsync("Lore server", prefix: "$env:LORE_SERVER=1; ");
            }

            return ok;
        }

        private static async Task<bool> RunInstallAsync(string label, string prefix)
        {
            string script = prefix + "irm " + InstallScriptUrl + " | iex";
            (int exitCode, string output) = await Task.Run(() => RunPowerShell(script));
            await LoreLog.WriteCommandAsync($"install ({label})", output);
            return exitCode == 0;
        }

        private static (int, string) RunPowerShell(string script)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            var sb = new StringBuilder();
            try
            {
                using (var process = new Process { StartInfo = psi })
                {
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) { lock (sb) { sb.AppendLine(e.Data); } } };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) { lock (sb) { sb.AppendLine(e.Data); } } };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();
                    return (process.ExitCode, sb.ToString());
                }
            }
            catch (System.Exception ex)
            {
                return (-1, sb + ex.Message);
            }
        }
    }
}
