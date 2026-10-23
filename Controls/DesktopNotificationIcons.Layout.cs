using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace alpha_island.Controls;

internal static partial class DesktopNotificationIcons
{
    private static readonly ConditionalWeakTable<BitmapSource, DecodedLogo> PresentationCache = new();

    private static DecodedLogo FitVisibleIcon(BitmapSource source)
    {
        try
        {
            int width = source.PixelWidth, height = source.PixelHeight;
            if (width < 2 || height < 2 || width > 512 || height > 512) return new(source);
            BitmapSource bitmap = source.Format == PixelFormats.Bgra32 ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
            int left = width, top = height, right = -1, bottom = -1;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                if (pixels[(y * width + x) * 4 + 3] < 24) continue;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
            if (right < left) return new(source);
            int inkWidth = right - left + 1, inkHeight = bottom - top + 1, extent = Math.Max(inkWidth, inkHeight);
            // Already well-filled logos and opaque content images keep their original framing.
            if (extent >= Math.Max(width, height) * .82) return new(source);
            int side = extent + 2 * Math.Max(2, (int)Math.Ceiling(extent * .08));
            var fitted = new byte[side * side * 4];
            int xOffset = (side - inkWidth) / 2, yOffset = (side - inkHeight) / 2;
            for (int row = 0; row < inkHeight; row++)
                Buffer.BlockCopy(pixels, ((top + row) * width + left) * 4,
                    fitted, ((yOffset + row) * side + xOffset) * 4, inkWidth * 4);
            // A square canvas preserves the original aspect ratio in every compact icon slot.
            var result = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, fitted, side * 4);
            result.Freeze(); return new(result);
        }
        catch { return new(source); }
    }
}
