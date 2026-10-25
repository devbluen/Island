using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Island;

static class ColorUtil
{
    // Cor de destaque tirada da capa do álbum (pesando pixels mais saturados).
    public static Color Accent(BitmapSource src)
    {
        try
        {
            var small = new TransformedBitmap(src, new ScaleTransform(24.0 / src.PixelWidth, 24.0 / src.PixelHeight));
            var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                byte B = px[i], G = px[i + 1], R = px[i + 2];
                int max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                double sat = max == 0 ? 0 : (max - min) / (double)max;
                double weight = sat * sat * (max > 50 ? 1 : 0.05) + 0.002;
                r += R * weight; g += G * weight; b += B * weight; total += weight;
            }
            r /= total; g /= total; b /= total;

            double top = Math.Max(r, Math.Max(g, b)), low = Math.Min(r, Math.Min(g, b));
            if (top < 1 || (top - low) / top < 0.18) return Color.FromRgb(235, 235, 235);
            double k = 235 / top;
            return Color.FromRgb((byte)Math.Min(255, r * k), (byte)Math.Min(255, g * k), (byte)Math.Min(255, b * k));
        }
        catch
        {
            return Colors.White;
        }
    }
}
