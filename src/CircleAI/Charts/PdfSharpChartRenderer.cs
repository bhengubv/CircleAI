#nullable enable

// PdfSharpChartRenderer.cs
//
// The concrete IChartRenderer, on PDFsharp's XGraphics primitives. This is the
// ONLY file that orchestrates the pieces (font bootstrap, background, title,
// plot, legend); the per-type geometry lives in Internal/*. Everything is pure
// vector drawing onto the caller's surface, so a chart embeds into a
// CircleAI.Documents PDF with no rasterisation and no native code.
//
// Layout, top to bottom, inside the padded bounds:
//   [ title ]
//   [ plot  ]   <- bar/line get an axis frame; pie fills the area
//   [ legend]

using System.IO;
using CircleAI.Charts.Internal;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace CircleAI.Charts;

/// <summary>Draws charts with PDFsharp XGraphics primitives, fully offline and on-device.</summary>
public sealed class PdfSharpChartRenderer : IChartRenderer
{
    /// <inheritdoc />
    public void Render(ChartSpec spec, XGraphics gfx, XRect bounds, ChartStyle? style = null)
    {
        System.ArgumentNullException.ThrowIfNull(spec);
        System.ArgumentNullException.ThrowIfNull(gfx);
        if (spec.Series is null || spec.Series.Count == 0)
            throw new System.ArgumentException("A chart needs at least one series.", nameof(spec));

        // Make sure PDFsharp can find a font. No-op when CircleAI.Documents (or any
        // host) already installed a resolver — see ChartFonts for the ordering rule.
        ChartFonts.EnsureDefaultFontResolver();

        style ??= ChartStyle.Default;
        var ctx = new ChartContext(gfx, style);

        // Background.
        gfx.DrawRectangle(new XSolidBrush(ChartColor.Parse(style.BackgroundHex)), bounds);

        var pad = style.Padding;
        var inner = new XRect(
            bounds.Left + pad, bounds.Top + pad,
            System.Math.Max(1, bounds.Width - 2 * pad),
            System.Math.Max(1, bounds.Height - 2 * pad));

        // Title band.
        double titleH = 0;
        if (!string.IsNullOrWhiteSpace(spec.Title))
        {
            titleH = ctx.TextHeight(ctx.TitleFont) + 6;
            var titleRect = new XRect(inner.Left, inner.Top, inner.Width, titleH);
            gfx.DrawString(spec.Title, ctx.TitleFont, ctx.TextBrush, titleRect, Fmt.Center);
        }

        // Legend band (reserved now so the plot knows its height; drawn last).
        var legendItems = BuildLegend(ctx, spec);
        var legendH = (spec.ShowLegend && legendItems.Count > 0) ? ctx.TextHeight(ctx.LabelFont) + 8 : 0;

        var plotArea = new XRect(
            inner.Left, inner.Top + titleH,
            inner.Width,
            System.Math.Max(1, inner.Height - titleH - legendH));

        switch (spec.Type)
        {
            case ChartType.Pie:
                PieChartDrawer.Draw(ctx, plotArea, spec);
                break;

            case ChartType.Line:
                LineChartDrawer.Draw(ctx, AxisChart.DrawFrame(ctx, plotArea, spec), spec);
                break;

            case ChartType.Bar:
            default:
                BarChartDrawer.Draw(ctx, AxisChart.DrawFrame(ctx, plotArea, spec), spec);
                break;
        }

        if (legendH > 0)
        {
            var legendArea = new XRect(inner.Left, inner.Bottom - legendH, inner.Width, legendH);
            Legend.Draw(ctx, legendArea, legendItems);
        }
    }

    /// <inheritdoc />
    public void RenderToPage(ChartSpec spec, PdfPage page, XRect? bounds = null, ChartStyle? style = null)
    {
        System.ArgumentNullException.ThrowIfNull(page);
        using var gfx = XGraphics.FromPdfPage(page);
        var b = bounds ?? new XRect(0, 0, page.Width.Point, page.Height.Point);
        Render(spec, gfx, b, style);
    }

    /// <inheritdoc />
    public byte[] RenderToPdf(ChartSpec spec, double widthPoints = 480, double heightPoints = 320, ChartStyle? style = null)
    {
        if (widthPoints <= 0 || heightPoints <= 0)
            throw new System.ArgumentOutOfRangeException(nameof(widthPoints), "Page dimensions must be positive.");

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(widthPoints);
        page.Height = XUnit.FromPoint(heightPoints);

        RenderToPage(spec, page, new XRect(0, 0, widthPoints, heightPoints), style);

        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    // Legend entries: one per series for bar/line; one per (positive) slice for
    // pie. Pie colours are taken by point index so they match PieChartDrawer.
    private static IReadOnlyList<(string Label, XColor Color)> BuildLegend(ChartContext ctx, ChartSpec spec)
    {
        var items = new System.Collections.Generic.List<(string, XColor)>();

        if (spec.Type == ChartType.Pie)
        {
            var series = spec.Series[0];
            for (var i = 0; i < series.Points.Count; i++)
                if (series.Points[i].Value > 0)
                    items.Add((series.Points[i].Label, ctx.Color(i)));
        }
        else
        {
            for (var si = 0; si < spec.Series.Count; si++)
            {
                var s = spec.Series[si];
                var color = s.ColorHex is not null ? ChartColor.Parse(s.ColorHex) : ctx.Color(si);
                items.Add((s.Name, color));
            }
        }

        return items;
    }
}
