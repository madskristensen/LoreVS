using System;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Shell.Interop;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// A fixed list of <see cref="ImageMoniker"/> glyphs handed to Visual Studio via
    /// <see cref="IVsSccGlyphs2.GetCustomGlyphMonikerList"/>. Using monikers (rather than a Win32
    /// HIMAGELIST) lets the custom source control glyphs scale crisply on high-DPI displays.
    /// </summary>
    internal sealed class LoreSccGlyphList : IVsImageMonikerImageList
    {
        private readonly ImageMoniker[] _monikers;

        public LoreSccGlyphList(ImageMoniker[] monikers)
        {
            _monikers = monikers ?? throw new ArgumentNullException(nameof(monikers));
        }

        public int ImageCount => _monikers.Length;

        public void GetImageMonikers(int firstImageIndex, int imageMonikerCount, ImageMoniker[] imageMonikers)
        {
            if (imageMonikers == null || firstImageIndex < 0 || imageMonikerCount <= 0)
            {
                return;
            }

            for (int i = 0; i < imageMonikerCount; i++)
            {
                int source = firstImageIndex + i;
                if (source < 0 || source >= _monikers.Length || i >= imageMonikers.Length)
                {
                    break;
                }

                imageMonikers[i] = _monikers[source];
            }
        }
    }
}
