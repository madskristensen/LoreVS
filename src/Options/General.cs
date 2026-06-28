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
    /// </summary>
    public class General : BaseOptionModel<General>
    {
        [Category("Lore")]
        [DisplayName("Identity")]
        [Description("Commit identity (e.g. you@example.com). Leave empty to use the repository default.")]
        [DefaultValue("")]
        public string Identity { get; set; } = string.Empty;

        [Category("Lore")]
        [DisplayName("Push after commit")]
        [Description("When enabled, a successful commit is automatically pushed to the remote.")]
        [DefaultValue(false)]
        public bool AutoPushOnCommit { get; set; } = false;

        [Category("Lore")]
        [DisplayName("Server port (gRPC/QUIC)")]
        [Description("gRPC/QUIC port used to build repository URLs. Default 41337 (the zero-config " +
                     "demo server). Change to point at a server on a different port.")]
        [DefaultValue(41337)]
        public int ServerPort { get; set; } = 41337;
    }
}
