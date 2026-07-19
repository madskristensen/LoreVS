using System;
using Microsoft.VisualStudio.Imaging.Interop;

namespace LoreVS
{
    /// <summary>
    /// Strongly-typed access to Lore's custom image monikers, backed by
    /// <c>Resources\Lore.imagemanifest</c> (registered with the Visual Studio image service via the
    /// <c>Microsoft.VisualStudio.ImageManifest</c> VSIX asset). Use these anywhere Visual Studio
    /// accepts an <see cref="ImageMoniker"/> (tool window icons, VSCT buttons, and so on).
    /// </summary>
    internal static class LoreMonikers
    {
        // Must match the Guid/ID symbols declared in Resources\Lore.imagemanifest.
        private static readonly Guid ManifestGuid = new Guid("06556302-fa5a-4449-9882-52130e13d283");

        private const int LoreLogoId = 1;

        /// <summary>The Lore product logo.</summary>
        public static ImageMoniker LoreLogo => new ImageMoniker { Guid = ManifestGuid, Id = LoreLogoId };
    }
}
