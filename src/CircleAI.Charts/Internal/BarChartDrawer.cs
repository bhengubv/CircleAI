#nullable enable

// BarChartDrawer.cs
//
// Vertical bars against the shared AxisFrame. One series = a simple bar chart;
// several series = clustered bars sharing each category slot. Bars grow up from
// the zero baseline (and down from it for negative values).

using PdfSharp.Drawing;

namespace CircleAI.Charts.Internal;

internal static class BarChartDrawer
{
    public static void Draw(ChartContext ctx, AxisFrame frame, ChartSpec spec)
    {
        var n = frame.CategoryCount;
        var seriesCount = spec.Series.Count;
        if (n == 0 || seriesCount == 0) return;

        var slot = frame.SlotWidth;
        var groupWidth = slot * 0.7;          // leave a gap between category groups
        var barWidth = groupWidth / seriesCount;
        var baseY = frame.BaselineY;
        var labelH = ctx.TextHeight(ctx.LabelFont);

        for (var si = 0; si < seriesCount; si++)
        {
            var series = spec.Series[si];
            var color = series.ColorHex is not null ? ChartColor.Parse(series.ColorHex) : ctx.Color(si);
            var brush = new XSolidBrush(color);

            for (var i = 0; i < n && i < series.Points.Count; i++)
            {
                var v = series.Points[i].Value;
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;

                var groupLeft = frame.CategoryCenter(i) - groupWidth / 2;
                var laneLeft = groupLeft + si * barWidth;
                var yv = frame.Y(v);
                var top = System.Math.Min(yv, baseY);
                var height = System.Math.Abs(yv - baseY);

                // 10% inset on each side of the lane so neighbouring bars breathe.
                var rect = new XRect(laneLeft + barWidth * 0.1, top, barWidth * 0.8, System.Math.Max(0.5, height));
                ctx.Gfx.DrawRectangle(brush, rect);

                if (spec.ShowValueLabels && height > 1)
                {
                    var text = ChartText.FormatValue(v);
                    var above = v >= 0;
                    var ly = above ? top - labelH - 1 : top + height + 1;
                    var lr = new XRect(laneLeft - barWidth * 0.5, ly, barWidth * 2, labelH);
                    ctx.Gfx.DrawString(text, ctx.LabelFont, ctx.TextBrush, lr, Fmt.Center);
                }
            }
        }
    }
}
