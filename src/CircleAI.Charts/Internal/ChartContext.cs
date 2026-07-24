#nullable enable

// ChartContext.cs
//
// Per-render scratch state shared by every drawer: the target surface, the
// resolved style, the two fonts, the reusable brushes/pens, and the parsed
// colour palette. Building these once (rather than per bar/slice) keeps a dense
// chart cheap on a low-end phone. Also here: the tiny colour/format/number
// helpers the drawers lean on.

using System.Globalization;
using PdfSharp.Drawing;

namespace CircleAI.Charts.Internal;

/// <summary>Reusable state for one chart render pass.</summary>
internal sealed class ChartContext
{
    public XGraphics Gfx { get; }
    public ChartStyle Style { get; }
    public XFont TitleFont { get; }
    public XFont LabelFont { get; }
    public XBrush TextBrush { get; }
    public XPen AxisPen { get; }
    public XPen GridPen { get; }

    private readonly XColor[] _palette;

    public ChartContext(XGraphics gfx, ChartStyle style)
    {
        Gfx = gfx;
        Style = style;
        TitleFont = new XFont(style.FontFamily, style.TitleFontSize, XFontStyleEx.Bold);
        LabelFont = new XFont(style.FontFamily, style.LabelFontSize, XFontStyleEx.Regular);
        TextBrush = new XSolidBrush(ChartColor.Parse(style.TextHex));
        AxisPen = new XPen(ChartColor.Parse(style.AxisHex), 0.75);
        GridPen = new XPen(ChartColor.Parse(style.GridHex), 0.5);
        _palette = BuildPalette(style.PaletteHex);
    }

    /// <summary>The palette colour for category/series index <paramref name="index"/>, cycled.</summary>
    public XColor Color(int index)
    {
        var n = _palette.Length;
        return _palette[((index % n) + n) % n]; // safe for negative indices too
    }

    /// <summary>A fresh solid brush for palette index <paramref name="index"/>.</summary>
    public XBrush Brush(int index) => new XSolidBrush(Color(index));

    /// <summary>Line height (points) of <paramref name="font"/> on this surface.</summary>
    public double TextHeight(XFont font) => Gfx.MeasureString("Ag", font).Height;

    private static XColor[] BuildPalette(IReadOnlyList<string> hexes)
    {
        if (hexes is null || hexes.Count == 0)
            return new[] { ChartColor.Parse("#2C3E50") };

        var arr = new XColor[hexes.Count];
        for (var i = 0; i < hexes.Count; i++)
            arr[i] = ChartColor.Parse(hexes[i]);
        return arr;
    }
}

/// <summary>Parses <c>#RRGGBB</c> / <c>#RGB</c> strings to <see cref="XColor"/>, with a safe fallback.</summary>
internal static class ChartColor
{
    // House slate — used when a caller hands us something unparseable rather than
    // throwing mid-render.
    private static readonly XColor Fallback = XColor.FromArgb(255, 44, 62, 80);

    public static XColor Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Fallback;

        var s = hex.Trim();
        if (s.Length > 0 && s[0] == '#') s = s.Substring(1);

        // #RGB shorthand -> #RRGGBB
        if (s.Length == 3)
            s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });

        if (s.Length != 6) return Fallback;

        if (byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return XColor.FromArgb(255, r, g, b);
        }

        return Fallback;
    }
}

/// <summary>Number formatting and text-fitting helpers.</summary>
internal static class ChartText
{
    /// <summary>Formats an axis/value number: whole numbers without a decimal, else two places.</summary>
    public static string FormatValue(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "";
        if (System.Math.Abs(v - System.Math.Round(v)) < 1e-9 && System.Math.Abs(v) < 1e15)
            return ((long)System.Math.Round(v)).ToString(CultureInfo.InvariantCulture);
        return v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>Truncates <paramref name="text"/> with an ellipsis so it fits <paramref name="maxWidth"/>.</summary>
    public static string Fit(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0) return text ?? "";
        if (gfx.MeasureString(text, font).Width <= maxWidth) return text;

        const string ellipsis = "…";
        var t = text;
        while (t.Length > 1 && gfx.MeasureString(t + ellipsis, font).Width > maxWidth)
            t = t.Substring(0, t.Length - 1);
        return t + ellipsis;
    }
}

/// <summary>Pre-built <see cref="XStringFormat"/> presets (constructed, not the named statics, to avoid version drift).</summary>
internal static class Fmt
{
    public static readonly XStringFormat Center = new()
        { Alignment = XStringAlignment.Center, LineAlignment = XLineAlignment.Center };

    public static readonly XStringFormat TopCenter = new()
        { Alignment = XStringAlignment.Center, LineAlignment = XLineAlignment.Near };

    public static readonly XStringFormat RightMid = new()
        { Alignment = XStringAlignment.Far, LineAlignment = XLineAlignment.Center };

    public static readonly XStringFormat LeftMid = new()
        { Alignment = XStringAlignment.Near, LineAlignment = XLineAlignment.Center };
}
