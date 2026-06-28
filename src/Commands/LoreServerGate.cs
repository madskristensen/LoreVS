using System.Threading.Tasks;
using LoreVS.Server;

namespace LoreVS.Commands
{
    /// <summary>
    /// Shared helper that ensures a local Lore server is running before an operation that needs
    /// it, surfacing a clear message (and a pointer to the install command) when it cannot.
    /// </summary>
    internal static class LoreServerGate
    {
        /// <summary>
        /// Ensures the configured local server is reachable. Returns true if the caller may
        /// proceed (server running, started, or not managed), false if it should abort.
        /// </summary>
        public static async Task<bool> EnsureAsync(LoreVSPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            EnsureServerResult result = await package.EnsureServerRunningAsync();
            switch (result)
            {
                case EnsureServerResult.Started:
                    await LoreLog.WriteLineAsync("Started local Lore server.");
                    await VS.StatusBar.ShowMessageAsync("Started local Lore server.");
                    return true;

                case EnsureServerResult.AlreadyRunning:
                case EnsureServerResult.NotManaged:
                    return true;

                case EnsureServerResult.MissingBinary:
                    await VS.MessageBox.ShowErrorAsync("Lore",
                        "A local Lore server is required but 'loreserver' could not be found.\n\n" +
                        "Run Tools > Install Lore Tools to install it, or set its path in " +
                        "Tools > Options > Lore.");
                    return false;

                case EnsureServerResult.ExternalUnreachable:
                    await VS.MessageBox.ShowErrorAsync("Lore",
                        "The configured Lore server uses non-default ports and is not reachable. " +
                        "Start that server yourself, or reset the ports in Tools > Options > Lore to " +
                        "use the managed demo server.");
                    return false;

                case EnsureServerResult.FailedToStart:
                default:
                    await VS.MessageBox.ShowErrorAsync("Lore",
                        "The local Lore server failed to start. See the 'Lore' Output pane and the " +
                        "server log under %TEMP%\\LoreVS for details.");
                    return false;
            }
        }
    }
}
