using System;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// GUIDs used by the Lore source control integration.
    /// A source control VSPackage requires three distinct GUIDs:
    /// the package, the SCC provider (also used as the activation UI context),
    /// and the private SCC provider service.
    /// </summary>
    internal static class LoreGuids
    {
        /// <summary>
        /// The registered name of the Lore source control provider. Must match the first
        /// argument of <c>[ProvideSourceControlProvider]</c> and is persisted into project
        /// files (via <c>SetSccLocation</c>) so Visual Studio re-binds Lore on reopen.
        /// </summary>
        public const string ProviderName = "Lore Source Control Provider";

        /// <summary>
        /// The SCC provider GUID. Registered with the SCC manager and asserted as a
        /// command UI context when Lore is the active provider (drives package auto-load
        /// and command visibility).
        /// </summary>
        public const string SccProviderString = "9a4f8d21-3c6e-4b7a-9f12-7e3d5a1b0c44";

        /// <summary>The private SCC provider service GUID (see <see cref="LoreSccService"/>).</summary>
        public const string SccServiceString = "2b8e6c10-5d3f-4a91-8e72-1c4f9a6d2b33";

        public static readonly Guid SccProvider = new Guid(SccProviderString);

        public static readonly Guid SccService = new Guid(SccServiceString);
    }
}
