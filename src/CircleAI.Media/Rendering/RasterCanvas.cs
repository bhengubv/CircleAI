// RasterCanvas.cs
//
// (Rendering 1.0) A pure-managed straight-alpha RGBA32 compositor. This is
// the genuine engine: it clears a background, composites the user's images
// (bilinear-sampled, aspect-fitted), and rasterises text overlays with the
// built-in bitmap font. No System.Drawing (Windows-only, deprecated), no
// SkiaSharp, no native code — runs identically on a low-end Android device.

using System;
using System.Collections.Generic;
using System.Text;

namespace CircleAI.Media.Rendering;

/// <summary>Drawing operations over a <see cref="PixelBuffer"/>.</summary>
public sealed class RasterCanvas
{
    public PixelBuffer Buffer { get; }
    public int Width => Buffer.Width;
    public int Height => Buffer.Height;

    public RasterCanvas(int width, int height) => Buffer = new PixelBuffer(width, height);

    public RasterCanvas(PixelBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Buffer = buffer;
    }

    /// <summary>Overwrite every pixel with a solid colour.</summary>
    public void Clear(Rgba32 c)
    {
        byte[] p = Buffer.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            p[i] = c.R;
            p[i + 1] = c.G;
            p[i + 2] = c.B;
            p[i + 3] = c.A;
        }
    }

    /// <summary>Alpha-blend an axis-aligned rectangle.</summary>
    public void FillRect(int x0, int y0, int w, int h, Rgba32 c, double opacity = 1.0)
    {
        double a = (c.A / 255.0) * opacity;
        if (a <= 0) return;
        int xs = Math.Max(0, x0), ys = Math.Max(0, y0);
        int xe = Math.Min(Width, x0 + w), ye = Math.Min(Height, y0 + h);
        for (int y = ys; y < ye; y++)
            for (int x = xs; x < xe; x++)
                Blend(x, y, c.R, c.G, c.B, a);
    }

    /// <summary>
    /// Composite a source image into a destination rectangle (canvas pixels)
    /// honouring the <see cref="ContentFit"/>. Cover centre-crops, Contain
    /// letterboxes (leaving the background showing), Fill stretches.
    /// </summary>
    public void DrawImage(PixelBuffer src, double destX, double destY, double destW, double destH, ContentFit fit, double opacity = 1.0)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (src.Width <= 0 || src.Height <= 0 || destW <= 0 || destH <= 0 || opacity <= 0) return;

        // Placement of the *whole* source image in canvas pixels.
        double pw, ph, ox, oy;
        switch (fit)
        {
            case ContentFit.Fill:
                pw = destW; ph = destH; ox = destX; oy = destY;
                break;
            case ContentFit.Contain:
            {
                double s = Math.Min(destW / src.Width, destH / src.Height);
                pw = src.Width * s; ph = src.Height * s;
                ox = destX + (destW - pw) / 2.0; oy = destY + (destH - ph) / 2.0;
                break;
            }
            default: // Cover
            {
                double s = Math.Max(destW / src.Width, destH / src.Height);
                pw = src.Width * s; ph = src.Height * s;
                ox = destX + (destW - pw) / 2.0; oy = destY + (destH - ph) / 2.0;
                break;
            }
        }

        // Clip to the destination rect intersected with the canvas.
        int cx0 = Math.Max(0, (int)Math.Floor(destX));
        int cy0 = Math.Max(0, (int)Math.Floor(destY));
        int cx1 = Math.Min(Width, (int)Math.Ceiling(destX + destW));
        int cy1 = Math.Min(Height, (int)Math.Ceiling(destY + destH));
        if (cx1 <= cx0 || cy1 <= cy0) return;

        for (int y = cy0; y < cy1; y++)
        {
            double v = ((y + 0.5) - oy) / ph * src.Height;
            if (v < 0 || v > src.Height) continue;
            for (int x = cx0; x < cx1; x++)
            {
                double u = ((x + 0.5) - ox) / pw * src.Width;
                if (u < 0 || u > src.Width) continue;
                Sample(src, u - 0.5, v - 0.5, out int r, out int g, out int b, out int a);
                if (a <= 0) continue;
                Blend(x, y, r, g, b, (a / 255.0) * opacity);
            }
        }
    }

    /// <summary>
    /// Rasterise text inside a rectangle (canvas pixels) with word-wrap,
    /// alignment, an optional background box, and a per-call opacity.
    /// </summary>
    public void DrawText(
        BitmapFont font, string text,
        int rx, int ry, int rw, int rh,
        int pixelHeight, Rgba32 color, TextAlign align,
        Rgba32 box, double letterSpacingFrac, double lineSpacingFrac,
        double opacity = 1.0)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (string.IsNullOrEmpty(text) || rw <= 0 || rh <= 0 || opacity <= 0) return;

        int scale = Math.Max(1, (int)Math.Round((double)pixelHeight / BitmapFont.Rows, MidpointRounding.AwayFromZero));
        int glyphW = BitmapFont.Cols * scale;
        int glyphH = BitmapFont.Rows * scale;
        int letter = Math.Max(scale, (int)Math.Round(glyphW * letterSpacingFrac));
        int advance = glyphW + letter;
        int lineH = glyphH + Math.Max(scale, (int)Math.Round(glyphH * lineSpacingFrac));

        var lines = Wrap(text, rw, advance, glyphW);
        if (lines.Count == 0) return;

        int totalH = lines.Count * lineH - (lineH - glyphH);
        int startY = ry + Math.Max(0, (rh - totalH) / 2);

        if (box.A > 0)
        {
            int maxW = 0;
            foreach (var ln in lines) maxW = Math.Max(maxW, LineWidth(ln.Length, advance, glyphW));
            if (maxW > 0)
            {
                int pad = Math.Max(scale * 2, glyphW / 2);
                int bx = align switch
                {
                    TextAlign.Left => rx,
                    TextAlign.Right => rx + rw - maxW,
                    _ => rx + (rw - maxW) / 2
                };
                FillRect(bx - pad, startY - pad, maxW + pad * 2, totalH + pad * 2, box, opacity);
            }
        }

        double inkA = (color.A / 255.0) * opacity;
        int y0 = startY;
        foreach (var line in lines)
        {
            int lineW = LineWidth(line.Length, advance, glyphW);
            int x0 = align switch
            {
                TextAlign.Left => rx,
                TextAlign.Right => rx + rw - lineW,
                _ => rx + (rw - lineW) / 2
            };
            int cx = x0;
            foreach (char ch in line)
            {
                if (ch != ' ')
                {
                    for (int gy = 0; gy < BitmapFont.Rows; gy++)
                        for (int gx = 0; gx < BitmapFont.Cols; gx++)
                            if (font.IsPixelOn(ch, gx, gy))
                                FillBlock(cx + gx * scale, y0 + gy * scale, scale, color, inkA);
                }
                cx += advance;
            }
            y0 += lineH;
        }
    }

    // ---- internals -------------------------------------------------------

    private static int LineWidth(int charCount, int advance, int glyphW)
        => charCount <= 0 ? 0 : charCount * advance - (advance - glyphW);

    private static List<string> Wrap(string text, int maxWidth, int advance, int glyphW)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Replace("\r", string.Empty).Split('\n'))
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) { result.Add(string.Empty); continue; }

            var cur = new StringBuilder();
            foreach (var word in words)
            {
                int candidate = cur.Length == 0 ? word.Length : cur.Length + 1 + word.Length;
                if (cur.Length > 0 && LineWidth(candidate, advance, glyphW) > maxWidth)
                {
                    result.Add(cur.ToString());
                    cur.Clear();
                    cur.Append(word);
                }
                else
                {
                    if (cur.Length > 0) cur.Append(' ');
                    cur.Append(word);
                }
            }
            result.Add(cur.ToString());
        }
        return result;
    }

    private void FillBlock(int x0, int y0, int size, Rgba32 c, double alpha)
    {
        int xe = x0 + size, ye = y0 + size;
        for (int y = y0; y < ye; y++)
            for (int x = x0; x < xe; x++)
                Blend(x, y, c.R, c.G, c.B, alpha);
    }

    private void Blend(int x, int y, int r, int g, int b, double alpha)
    {
        if (alpha <= 0.0) return;
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        if (alpha > 1.0) alpha = 1.0;

        byte[] p = Buffer.Pixels;
        int idx = (y * Width + x) * 4;
        double da = p[idx + 3] / 255.0;
        double outA = alpha + da * (1.0 - alpha);
        if (outA <= 0.0)
        {
            p[idx] = p[idx + 1] = p[idx + 2] = p[idx + 3] = 0;
            return;
        }
        double inv = da * (1.0 - alpha);
        p[idx] = Clamp255((r * alpha + p[idx] * inv) / outA);
        p[idx + 1] = Clamp255((g * alpha + p[idx + 1] * inv) / outA);
        p[idx + 2] = Clamp255((b * alpha + p[idx + 2] * inv) / outA);
        p[idx + 3] = Clamp255(outA * 255.0);
    }

    private static void Sample(PixelBuffer src, double fx, double fy, out int r, out int g, out int b, out int a)
    {
        double maxX = src.Width - 1, maxY = src.Height - 1;
        if (fx < 0) fx = 0; else if (fx > maxX) fx = maxX;
        if (fy < 0) fy = 0; else if (fy > maxY) fy = maxY;

        int x0 = (int)fx, y0 = (int)fy;
        int x1 = x0 < maxX ? x0 + 1 : x0;
        int y1 = y0 < maxY ? y0 + 1 : y0;
        double tx = fx - x0, ty = fy - y0;

        byte[] p = src.Pixels;
        int w = src.Width;
        int i00 = (y0 * w + x0) * 4, i10 = (y0 * w + x1) * 4, i01 = (y1 * w + x0) * 4, i11 = (y1 * w + x1) * 4;
        r = Bilinear(p[i00], p[i10], p[i01], p[i11], tx, ty);
        g = Bilinear(p[i00 + 1], p[i10 + 1], p[i01 + 1], p[i11 + 1], tx, ty);
        b = Bilinear(p[i00 + 2], p[i10 + 2], p[i01 + 2], p[i11 + 2], tx, ty);
        a = Bilinear(p[i00 + 3], p[i10 + 3], p[i01 + 3], p[i11 + 3], tx, ty);
    }

    private static int Bilinear(int c00, int c10, int c01, int c11, double tx, double ty)
    {
        double top = c00 + (c10 - c00) * tx;
        double bot = c01 + (c11 - c01) * tx;
        return Clamp255(top + (bot - top) * ty);
    }

    private static byte Clamp255(double v)
        => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);
}
