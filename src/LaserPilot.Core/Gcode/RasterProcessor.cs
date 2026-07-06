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
}

/// <summary>
/// Prepares a grayscale image for engraving: optional invert, then either
/// pass-through grayscale or resampling to the physical output resolution
/// (one pixel per scan-line interval) followed by dithering to pure
/// black/white. Dithering at output resolution is what makes photos look
/// right — dot density, not dot darkness, carries the tones.
/// </summary>
public static class RasterProcessor
{
    public static (byte[] gray, int w, int h) Prepare(
        byte[] gray, int pxW, int pxH,
        double widthMm, double heightMm, RasterSettings s)
    {
        if (pxW <= 0 || pxH <= 0) return (gray, pxW, pxH);

        if (s.Dither == DitherMode.Grayscale)
        {
            if (!s.Invert) return (gray, pxW, pxH);
            var inv = new byte[gray.Length];
            for (int i = 0; i < gray.Length; i++) inv[i] = (byte)(255 - gray[i]);
            return (inv, pxW, pxH);
        }

        // Output grid: square pixels the size of the scan-line interval.
        double interval = Math.Max(0.02, s.LineIntervalMm);
        int outW = Math.Max(1, (int)Math.Round(widthMm / interval));
        int outH = Math.Max(1, (int)Math.Round(heightMm / interval));

        var f = BoxResample(gray, pxW, pxH, outW, outH);
        if (s.Invert)
            for (int i = 0; i < f.Length; i++) f[i] = 255.0 - f[i];

        var result = new byte[outW * outH];
        switch (s.Dither)
        {
            case DitherMode.Threshold:
                for (int i = 0; i < f.Length; i++) result[i] = f[i] < 128 ? (byte)0 : (byte)255;
                break;

            case DitherMode.FloydSteinberg:
                ErrorDiffuse(f, outW, outH, result, new[]
                {
                    (dx: 1, dy: 0, w: 7.0 / 16),
                    (dx: -1, dy: 1, w: 3.0 / 16),
                    (dx: 0, dy: 1, w: 5.0 / 16),
                    (dx: 1, dy: 1, w: 1.0 / 16),
                });
                break;

            case DitherMode.Jarvis:
                ErrorDiffuse(f, outW, outH, result, new[]
                {
                    (dx: 1, dy: 0, w: 7.0 / 48), (dx: 2, dy: 0, w: 5.0 / 48),
                    (dx: -2, dy: 1, w: 3.0 / 48), (dx: -1, dy: 1, w: 5.0 / 48), (dx: 0, dy: 1, w: 7.0 / 48), (dx: 1, dy: 1, w: 5.0 / 48), (dx: 2, dy: 1, w: 3.0 / 48),
                    (dx: -2, dy: 2, w: 1.0 / 48), (dx: -1, dy: 2, w: 3.0 / 48), (dx: 0, dy: 2, w: 5.0 / 48), (dx: 1, dy: 2, w: 3.0 / 48), (dx: 2, dy: 2, w: 1.0 / 48),
                });
                break;
        }
        return (result, outW, outH);
    }

    private static void ErrorDiffuse(double[] f, int w, int h, byte[] outBytes,
        (int dx, int dy, double w)[] kernel)
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                double old = f[i];
                double val = old < 128 ? 0 : 255;
                outBytes[i] = (byte)val;
                double err = old - val;
                foreach (var (dx, dy, kw) in kernel)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && nx < w && ny < h)
                        f[ny * w + nx] += err * kw;
                }
            }
        }
    }

    private static double[] BoxResample(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new double[dw * dh];
        double kx = (double)sw / dw, ky = (double)sh / dh;
        for (int y = 0; y < dh; y++)
        {
            int sy0 = (int)(y * ky);
            int sy1 = Math.Max(sy0 + 1, (int)((y + 1) * ky));
            sy1 = Math.Min(sy1, sh);
            for (int x = 0; x < dw; x++)
            {
                int sx0 = (int)(x * kx);
                int sx1 = Math.Max(sx0 + 1, (int)((x + 1) * kx));
                sx1 = Math.Min(sx1, sw);
                double sum = 0;
                int n = 0;
                for (int yy = sy0; yy < sy1; yy++)
                    for (int xx = sx0; xx < sx1; xx++)
                    { sum += src[yy * sw + xx]; n++; }
                dst[y * dw + x] = n > 0 ? sum / n : 255;
            }
        }
        return dst;
    }
}
