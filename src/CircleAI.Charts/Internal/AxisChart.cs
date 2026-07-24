#nullable enable

// AxisChart.cs
//
// The shared "chrome" behind bar and line charts: the value (y) axis with its
// grid lines and tick labels, the category (x) axis with its labels, and the
// value<->pixel mapping the drawers use to place bars and points. Pie charts do
// NOT go through here — they have no axes.
//
// Coordinate note: PDF/XGraphics space has y growing DOWNWARD, so a larger value
// maps to a SMALLER y. AxisFrame.Y() hides that.

using System.Collections.Generic;
using PdfSharp.Drawing;

namespace CircleAI.Charts.Internal;

/// <summary>The computed plot rectangle plus the value/category mappings for one axis chart.</summary>
internal sealed class AxisFrame
{
    public XRect Plot { get; }
    public double Min { get; }
    public double Max { get; }
    public int CategoryCount { get; }

    public AxisFrame(XRect plot, double min, double max, int categoryCount)
    {
        Plot = plot;
        Min = min;
        Max = max;
        CategoryCount = categoryCount;
    }

    /// <summary>Maps a data value to a y coordinate inside the plot.</summary>
    public double Y(double value) => Plot.Bottom - (value - Min) / (Max - Min) * Plot.Height;

    /// <summary>Width of one category's horizontal slot.</summary>
    public double SlotWidth => CategoryCount > 0 ? Plot.Width / CategoryCount : Plot.Width;

    /// <summary>Centre x of category <paramref name="i"/>.</summary>
    public double CategoryCenter(int i) => Plot.Left + (i + 0.5) * SlotWidth;

    /// <summary>The y of the zero baseline (or the plot floor when zero is out of range).</summary>
    public double BaselineY => (Min <= 0 && Max >= 0) ? Y(0) : Plot.Bottom;
}

/// <summary>Draws the axis frame and returns the geometry drawers need.</summary>
internal static class AxisChart
{
    public static AxisFrame DrawFrame(ChartContext ctx, XRect area, ChartSpec spec)
    {
        var (min, max) = ValueRange(spec);
        var categories = Categories(spec);
        var n = categories.Count;
        var ticks = System.Math.Max(2, ctx.Style.ValueTickCount);

        // Left gutter must fit the widest y tick label.
        double gutter = 10;
        for (var t = 0; t <= ticks; t++)
        {
            var v = min + (max - min) * t / ticks;
            var w = ctx.Gfx.MeasureString(ChartText.FormatValue(v), ctx.LabelFont).Width;
            if (w + 6 > gutter) gutter = w + 6;
        }

        var xLabelH = ctx.TextHeight(ctx.LabelFont) + 4; // room under the plot for category labels
        var top = area.Top;
        if (!string.IsNullOrWhiteSpace(spec.ValueAxisLabel))
            top += ctx.TextHeight(ctx.LabelFont) + 2; // a caption line above the plot

        var left = area.Left + gutter;
        var right = area.Right - 6;
        var bottom = area.Bottom - xLabelH;

        var plot = new XRect(left, top, System.Math.Max(1, right - left), System.Math.Max(1, bottom - top));
        var frame = new AxisFrame(plot, min, max, n);

        // Optional value-axis caption (drawn horizontally above the plot — no text
        // rotation, which keeps the pure-managed path simple and robust).
        if (!string.IsNullOrWhiteSpace(spec.ValueAxisLabel))
        {
            var capRect = new XRect(area.Left, area.Top, plot.Width + gutter, ctx.TextHeight(ctx.LabelFont));
            ctx.Gfx.DrawString(spec.ValueAxisLabel!, ctx.LabelFont, ctx.TextBrush, capRect, Fmt.LeftMid);
        }

        // Grid lines + y tick labels.
        for (var t = 0; t <= ticks; t++)
        {
            var v = min + (max - min) * t / ticks;
            var y = frame.Y(v);

            if (ctx.Style.ShowGrid)
                ctx.Gfx.DrawLine(ctx.GridPen, plot.Left, y, plot.Right, y);

            var label = ChartText.FormatValue(v);
            var h = ctx.TextHeight(ctx.LabelFont);
            var lr = new XRect(area.Left, y - h / 2, gutter - 4, h);
            ctx.Gfx.DrawString(label, ctx.LabelFont, ctx.TextBrush, lr, Fmt.RightMid);
        }

        // Axis lines: left vertical + zero baseline.
        ctx.Gfx.DrawLine(ctx.AxisPen, plot.Left, plot.Top, plot.Left, plot.Bottom);
        ctx.Gfx.DrawLine(ctx.AxisPen, plot.Left, frame.BaselineY, plot.Right, frame.BaselineY);

        // Category labels centred under each slot.
        for (var i = 0; i < n; i++)
        {
            var cx = frame.CategoryCenter(i);
            var slot = frame.SlotWidth;
            var text = ChartText.Fit(ctx.Gfx, categories[i], ctx.LabelFont, slot);
            var lr = new XRect(cx - slot / 2, plot.Bottom + 2, slot, xLabelH - 2);
            ctx.Gfx.DrawString(text, ctx.LabelFont, ctx.TextBrush, lr, Fmt.TopCenter);
        }

        return frame;
    }

    /// <summary>Value span across all series, always including the zero baseline, with a little top headroom.</summary>
    private static (double Min, double Max) ValueRange(ChartSpec spec)
    {
        var any = false;
        double min = 0, max = 0;

        foreach (var s in spec.Series)
        {
            foreach (var p in s.Points)
            {
                if (double.IsNaN(p.Value) || double.IsInfinity(p.Value)) continue;
                if (!any) { min = max = p.Value; any = true; }
                else { if (p.Value < min) min = p.Value; if (p.Value > max) max = p.Value; }
            }
        }

        if (!any) { min = 0; max = 1; }

        // Bars/lines read against a zero baseline, so fold zero into the range.
        if (min > 0) min = 0;
        if (max < 0) max = 0;

        if (max - min < 1e-9) max = min + 1;   // guard a zero span (all-equal data)
        max += (max - min) * 0.05;             // headroom so the top bar/marker clears the frame
        return (min, max);
    }

    /// <summary>Category labels, taken from the series with the most points.</summary>
    private static IReadOnlyList<string> Categories(ChartSpec spec)
    {
        IReadOnlyList<ChartDataPoint> longest = System.Array.Empty<ChartDataPoint>();
        foreach (var s in spec.Series)
            if (s.Points.Count > longest.Count) longest = s.Points;

        var labels = new List<string>(longest.Count);
        foreach (var p in longest) labels.Add(p.Label);
        return labels;
    }
}
