using System.ComponentModel;
using System.Runtime.InteropServices;
using Community.VisualStudio.Toolkit;

namespace LoreVS.Options
{
    /// <summary>
    /// Community Toolkit options provider. Registered on the package via
    /// <c>[ProvideOptionPage(typeof(OptionsProvider.GeneralOptions), ...)]</c>.
    /// </summary>
    internal partial class OptionsProvider
    {
        [ComVisible(true)]
        public class GeneralOptions : BaseOptionPage<General> { }
    }

    /// <summary>
    /// Tools &gt; Options &gt; Lore settings (Community Toolkit <see cref="BaseOptionModel{T}"/>).
    /// The local server's URL/ports are owned by the extension (it starts the server), so they
    /// are not configurable here — only the tool paths, identity, and server behavior are.
    /// </summary>
    public class General : BaseOptionModel<General>
    {
        [Category("Lore")]
        [DisplayName("Identity")]
        [Description("Commit identity (e.g. you@example.com). Passed to 'lore' via --identity. " +
                     "Leave empty to use the CLI/repository default.")]
        [DefaultValue("")]
        public string Identity { get; set; } = string.Empty;

        [Category("Lore")]
        [DisplayName("Lore CLI path")]
        [Description("Path to the 'lore' executable. Leave as 'lore' to resolve it from PATH " +
                     "(or %USERPROFILE%\\bin), or set an absolute path.")]
        [DefaultValue("lore")]
        public string LoreExecutablePath { get; set; } = "lore";

        [Category("Lore")]
        [DisplayName("Prompt to install tools")]
        [Description("When the 'lore'/'loreserver' tools are missing, offer to install them on " +
                     "startup. Turn this off (or decline the prompt) to stop the automatic offer; " +
                     "you can still install later via the 'Install Lore Tools' command.")]
        [DefaultValue(true)]
        public bool PromptToInstallTools { get; set; } = true;

        [Category("Lore")]
        [DisplayName("Push after commit")]
        [Description("When enabled, a successful commit is automatically pushed to the remote.")]
        [DefaultValue(false)]
        public bool AutoPushOnCommit { get; set; } = false;

        [Category("Lore Server")]
        [DisplayName("Manage a local server")]
        [Description("Automatically start a local 'loreserver' before operations that need it. " +
                     "If a server is already running it is reused (multiple Visual Studio instances " +
                     "share one local server).")]
        [DefaultValue(true)]
        public bool ManageLocalServer { get; set; } = true;

        [Category("Lore Server")]
        [DisplayName("Stop server on exit")]
        [Description("Stop the local server when Visual Studio closes. Off by default so the shared " +
                     "server keeps running for other Visual Studio instances; use 'Stop Local Lore " +
                     "Server' to stop it manually.")]
        [DefaultValue(false)]
        public bool StopServerOnExit { get; set; } = false;

        [Category("Lore Server")]
        [DisplayName("Lore server path")]
        [Description("Path to the 'loreserver' executable. Leave as 'loreserver' to resolve it from " +
                     "PATH (or %USERPROFILE%\\bin), or set an absolute path.")]
        [DefaultValue("loreserver")]
        public string LoreServerExecutablePath { get; set; } = "loreserver";

        [Category("Lore Server")]
        [DisplayName("Server port (gRPC/QUIC)")]
        [Description("gRPC/QUIC port used to build repository URLs. Default 41337 (the zero-config " +
                     "demo server). Change only to reuse an external server on a different port.")]
        [DefaultValue(41337)]
        public int ServerPort { get; set; } = 41337;

        [Category("Lore Server")]
        [DisplayName("Server HTTP port")]
        [Description("HTTP port polled for the health check. Default 41339 (the zero-config demo " +
                     "server).")]
        [DefaultValue(41339)]
        public int ServerHttpPort { get; set; } = 41339;

        [Category("Lore Server")]
        [DisplayName("Persistent store path")]
        [Description("Optional directory for a persistent local server store so repositories survive " +
                     "restarts. Leave empty to run the server in ephemeral demo mode (data is not kept).")]
        [DefaultValue("")]
        public string LocalServerStorePath { get; set; } = string.Empty;
    }
}
