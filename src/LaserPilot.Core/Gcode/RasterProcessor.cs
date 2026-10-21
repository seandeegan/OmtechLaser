namespace LaserPilot.Core.Gcode;

public enum DitherMode
{
    /// <summary>Direct grayscale → power mapping (good for depth/relief engraving).</summary>
    Grayscale,
    /// <summary>Floyd–Steinberg error diffusion (best all-round for photos).</summary>
    FloydSteinberg,
    /// <summary>Jarvis–Judice–Ninke error diffusion (smoother, larger kernel).</summary>
    Jarvis,
    /// <summary>Hard black/white threshold (logos, line art).</summary>
    Threshold,
    /// <summary>Stucki error diffusion (sharp and smooth; good for portraits).</summary>
    Stucki,
    /// <summary>Atkinson error diffusion (high contrast, crisp, lighter shadows).</summary>
    Atkinson,
    /// <summary>Ordered (Bayer) pattern: regular dot texture, very even on wood grain.</summary>
    Ordered,
}

/// <summary>
/// Prepares a grayscale image for engraving: resampling to the physical output
/// resolution (one pixel per scan-line interval), brightness/contrast/gamma,
/// optional invert, then either grayscale power levels or dithering to pure
/// black/white. Dithering at output resolution is what makes photos look
/// right — dot density, not dot darkness, carries the tones.
/// </summary>
public static class RasterProcessor
{
    /// <summary>Guard against images that would need gigabytes at the chosen resolution.</summary>
    public const long MaxOutputPixels = 25_000_000;

    public static (int W, int H) OutputSize(double widthMm, double heightMm, double intervalMm)
    {
        double interval = Math.Max(0.02, intervalMm);
        return (Math.Max(1, (int)Math.Round(widthMm / interval)), Math.Max(1, (int)Math.Round(heightMm / interval)));
    }

    public static (byte[] gray, int w, int h) Prepare(
        byte[] gray, int pxW, int pxH,
        double widthMm, double heightMm, RasterSettings s)
    {
        if (pxW <= 0 || pxH <= 0) return (gray, pxW, pxH);

        var (outW, outH) = OutputSize(widthMm, heightMm, s.LineIntervalMm);
        if ((long)outW * outH > MaxOutputPixels)
            throw new InvalidOperationException(
                $"This image would be {outW}×{outH} dots at a {s.LineIntervalMm:0.###} mm line interval — too large. " +
                "Make it smaller or use a bigger line interval.");

        var f = BoxResample(gray, pxW, pxH, outW, outH);
        Adjust(f, s);

        var result = new byte[outW * outH];
        switch (s.Dither)
        {
            case DitherMode.Grayscale:
                for (int i = 0; i < f.Length; i++) result[i] = (byte)Math.Clamp(Math.Round(f[i]), 0, 255);
                break;

            case DitherMode.Threshold:
                for (int i = 0; i < f.Length; i++) result[i] = f[i] < 128 ? (byte)0 : (byte)255;
                break;

            case DitherMode.Ordered:
                for (int y = 0; y < outH; y++)
                    for (int x = 0; x < outW; x++)
                    {
                        int i = y * outW + x;
                        double threshold = (Bayer8[(y & 7) * 8 + (x & 7)] + 0.5) * (255.0 / 64);
                        result[i] = f[i] < threshold ? (byte)0 : (byte)255;
                    }
                break;

            case DitherMode.Jarvis:
                ErrorDiffuse(f, outW, outH, result, Kernels.Jarvis);
                break;
            case DitherMode.Stucki:
                ErrorDiffuse(f, outW, outH, result, Kernels.Stucki);
                break;
            case DitherMode.Atkinson:
                ErrorDiffuse(f, outW, outH, result, Kernels.Atkinson);
                break;
            default:
                ErrorDiffuse(f, outW, outH, result, Kernels.FloydSteinberg);
                break;
        }
        return (result, outW, outH);
    }

    /// <summary>Brightness, contrast, gamma, then invert — in place, 0 = black.</summary>
    private static void Adjust(float[] f, RasterSettings s)
    {
        double brightness = Math.Clamp(s.Brightness, -100, 100) * 2.55;
        double contrast = (100 + Math.Clamp(s.Contrast, -100, 100)) / 100.0;
        double gamma = Math.Clamp(s.Gamma <= 0 ? 1 : s.Gamma, 0.1, 10);
        bool identity = brightness == 0 && contrast == 1 && gamma == 1;
        if (identity && !s.Invert) return;

        var lut = new float[256];
        for (int v = 0; v < 256; v++)
        {
            double x = (v - 127.5) * contrast + 127.5 + brightness;
            x = Math.Clamp(x, 0, 255);
            if (gamma != 1) x = 255 * Math.Pow(x / 255, 1 / gamma);
            if (s.Invert) x = 255 - x;
            lut[v] = (float)x;
        }
        for (int i = 0; i < f.Length; i++)
        {
            // Resampled values are fractional: interpolate the table.
            double v = Math.Clamp(f[i], 0, 255);
            int lo = (int)v;
            int hi = Math.Min(255, lo + 1);
            double t = v - lo;
            f[i] = (float)(lut[lo] + (lut[hi] - lut[lo]) * t);
        }
    }

    private static class Kernels
    {
        public static readonly (int dx, int dy, double w)[] FloydSteinberg =
        {
            (1, 0, 7.0 / 16),
            (-1, 1, 3.0 / 16), (0, 1, 5.0 / 16), (1, 1, 1.0 / 16),
        };
        public static readonly (int dx, int dy, double w)[] Jarvis =
        {
            (1, 0, 7.0 / 48), (2, 0, 5.0 / 48),
            (-2, 1, 3.0 / 48), (-1, 1, 5.0 / 48), (0, 1, 7.0 / 48), (1, 1, 5.0 / 48), (2, 1, 3.0 / 48),
            (-2, 2, 1.0 / 48), (-1, 2, 3.0 / 48), (0, 2, 5.0 / 48), (1, 2, 3.0 / 48), (2, 2, 1.0 / 48),
        };
        public static readonly (int dx, int dy, double w)[] Stucki =
        {
            (1, 0, 8.0 / 42), (2, 0, 4.0 / 42),
            (-2, 1, 2.0 / 42), (-1, 1, 4.0 / 42), (0, 1, 8.0 / 42), (1, 1, 4.0 / 42), (2, 1, 2.0 / 42),
            (-2, 2, 1.0 / 42), (-1, 2, 2.0 / 42), (0, 2, 4.0 / 42), (1, 2, 2.0 / 42), (2, 2, 1.0 / 42),
        };
        // Atkinson deliberately diffuses only 6/8 of the error.
        public static readonly (int dx, int dy, double w)[] Atkinson =
        {
            (1, 0, 1.0 / 8), (2, 0, 1.0 / 8),
            (-1, 1, 1.0 / 8), (0, 1, 1.0 / 8), (1, 1, 1.0 / 8),
            (0, 2, 1.0 / 8),
        };
    }

    private static readonly int[] Bayer8 =
    {
         0, 32,  8, 40,  2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44,  4, 36, 14, 46,  6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
         3, 35, 11, 43,  1, 33,  9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47,  7, 39, 13, 45,  5, 37,
        63, 31, 55, 23, 61, 29, 53, 21,
    };

    // Serpentine scan (alternate row direction, mirrored kernel) avoids the
    // diagonal "worm" artifacts of always scanning left-to-right.
    private static void ErrorDiffuse(float[] f, int w, int h, byte[] outBytes, (int dx, int dy, double w)[] kernel)
    {
        for (int y = 0; y < h; y++)
        {
            bool reverse = (y & 1) == 1;
            for (int k = 0; k < w; k++)
            {
                int x = reverse ? w - 1 - k : k;
                int i = y * w + x;
                double old = f[i];
                double val = old < 128 ? 0 : 255;
                outBytes[i] = (byte)val;
                double err = old - val;
                foreach (var (dx0, dy, kw) in kernel)
                {
                    int nx = x + (reverse ? -dx0 : dx0), ny = y + dy;
                    if (nx >= 0 && nx < w && ny < h)
                        f[ny * w + nx] += (float)(err * kw);
                }
            }
        }
    }

    private static float[] BoxResample(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new float[dw * dh];
        double kx = (double)sw / dw, ky = (double)sh / dh;
        for (int y = 0; y < dh; y++)
        {
            int sy0 = (int)(y * ky);
            int sy1 = Math.Max(sy0 + 1, (int)((y + 1) * ky));
            sy1 = Math.Min(sy1, sh);
            sy0 = Math.Min(sy0, sh - 1);
            for (int x = 0; x < dw; x++)
            {
                int sx0 = (int)(x * kx);
                int sx1 = Math.Max(sx0 + 1, (int)((x + 1) * kx));
                sx1 = Math.Min(sx1, sw);
                sx0 = Math.Min(sx0, sw - 1);
                double sum = 0;
                int n = 0;
                for (int yy = sy0; yy < sy1; yy++)
                    for (int xx = sx0; xx < sx1; xx++)
                    { sum += src[yy * sw + xx]; n++; }
                dst[y * dw + x] = n > 0 ? (float)(sum / n) : 255f;
            }
        }
        return dst;
    }
}
