#nullable enable

// PieChartDrawer.cs
//
// A single ring split into slices, using the first series only. Each slice is a
// filled pie sector drawn with XGraphics.DrawPie; a thin white stroke separates
// neighbours. Slice colours come from the palette by point index, so a slice and
// its legend entry always match (see PdfSharpChartRenderer.BuildLegend).
//
// Angles follow the GDI+/PDFsharp convention: degrees, 0 at 3 o'clock, positive
// sweeping clockwise. We start at -90 (12 o'clock).

using PdfSharp.Drawing;

namespace CircleAI.Charts.Internal;

internal static class PieChartDrawer
{
    public static void Draw(ChartContext ctx, XRect area, ChartSpec spec)
    {
        var series = spec.Series[0];

        double sum = 0;
        foreach (var p in series.Points)
            if (p.Value > 0 && !double.IsNaN(p.Value) && !double.IsInfinity(p.Value))
                sum += p.Value;

        if (sum <= 0)
        {
            ctx.Gfx.DrawString("No data", ctx.LabelFont, ctx.TextBrush, area, Fmt.Center);
            return;
        }

        var side = System.Math.Min(area.Width, area.Height) * 0.92;
        var cx = area.Left + area.Width / 2;
        var cy = area.Top + area.Height / 2;
        var boxX = cx - side / 2;
        var boxY = cy - side / 2;

        var separator = new XPen(XColor.FromArgb(255, 255, 255, 255), 1.0);
        var pctBrush = new XSolidBrush(XColor.FromArgb(255, 255, 255, 255));

        var start = -90.0;
        for (var i = 0; i < series.Points.Count; i++)
        {
            var v = series.Points[i].Value;
            if (v <= 0 || double.IsNaN(v) || double.IsInfinity(v)) continue;

            var sweep = v / sum * 360.0;
            var brush = ctx.Brush(i);

            ctx.Gfx.DrawPie(brush, boxX, boxY, side, side, start, sweep);
            ctx.Gfx.DrawPie(separator, boxX, boxY, side, side, start, sweep);

            if (spec.ShowValueLabels)
            {
                var midRad = (start + sweep / 2) * System.Math.PI / 180.0;
                var r = side * 0.28;
                var lx = cx + r * System.Math.Cos(midRad);
                var ly = cy + r * System.Math.Sin(midRad);
                var pct = (v / sum * 100.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
                var sz = ctx.Gfx.MeasureString(pct, ctx.LabelFont);
                var lr = new XRect(lx - sz.Width / 2, ly - sz.Height / 2, sz.Width, sz.Height);
                ctx.Gfx.DrawString(pct, ctx.LabelFont, pctBrush, lr, Fmt.Center);
            }

            start += sweep;
        }
    }
}
