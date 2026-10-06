#nullable enable

// Legend.cs
//
// A compact single-row legend: a colour swatch + label per entry, centred in the
// strip the renderer reserves at the bottom. If the entries are wider than the
// strip, the overflow is clipped rather than wrapped — small category counts (the
// normal case for a report chart) fit comfortably, and clipping keeps the layout
// predictable instead of pushing the plot around.

using System.Collections.Generic;
using PdfSharp.Drawing;

namespace CircleAI.Charts.Internal;

internal static class Legend
{
    public static void Draw(ChartContext ctx, XRect area, IReadOnlyList<(string Label, XColor Color)> items)
    {
        if (items.Count == 0) return;

        const double swatch = 9;
        const double swatchGap = 5;   // swatch -> label
        const double itemGap = 14;    // item -> item

        // Measure each item so we can centre the row.
        var widths = new double[items.Count];
        double total = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var labelW = ctx.Gfx.MeasureString(items[i].Label, ctx.LabelFont).Width;
            widths[i] = swatch + swatchGap + labelW;
            total += widths[i] + (i > 0 ? itemGap : 0);
        }

        var x = area.Left + System.Math.Max(0, (area.Width - total) / 2);
        var midY = area.Top + area.Height / 2;
        var textH = ctx.TextHeight(ctx.LabelFont);

        foreach (var (item, w) in Pairs(items, widths))
        {
            if (x + w > area.Right) break; // clip rather than wrap
            ctx.Gfx.DrawRectangle(new XSolidBrush(item.Color), new XRect(x, midY - swatch / 2, swatch, swatch));
            var lr = new XRect(x + swatch + swatchGap, midY - textH / 2, w - swatch - swatchGap, textH);
            ctx.Gfx.DrawString(item.Label, ctx.LabelFont, ctx.TextBrush, lr, Fmt.LeftMid);
            x += w + itemGap;
        }
    }

    private static IEnumerable<((string Label, XColor Color) Item, double Width)> Pairs(
        IReadOnlyList<(string Label, XColor Color)> items, double[] widths)
    {
        for (var i = 0; i < items.Count; i++)
            yield return (items[i], widths[i]);
    }
}
