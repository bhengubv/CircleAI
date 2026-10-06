#nullable enable

// LineChartDrawer.cs
//
// One polyline per series over the shared AxisFrame, with a small filled marker
// at every data point. A single-point series draws just its marker (a polyline
// needs two points).

using System.Collections.Generic;
using PdfSharp.Drawing;

namespace CircleAI.Charts.Internal;

internal static class LineChartDrawer
{
    public static void Draw(ChartContext ctx, AxisFrame frame, ChartSpec spec)
    {
        var n = frame.CategoryCount;
        if (n == 0) return;

        var labelH = ctx.TextHeight(ctx.LabelFont);

        for (var si = 0; si < spec.Series.Count; si++)
        {
            var series = spec.Series[si];
            var color = series.ColorHex is not null ? ChartColor.Parse(series.ColorHex) : ctx.Color(si);
            var pen = new XPen(color, 1.5);
            var brush = new XSolidBrush(color);

            var points = new List<XPoint>(series.Points.Count);
            for (var i = 0; i < n && i < series.Points.Count; i++)
            {
                var v = series.Points[i].Value;
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                points.Add(new XPoint(frame.CategoryCenter(i), frame.Y(v)));
            }

            if (points.Count >= 2)
                ctx.Gfx.DrawLines(pen, points.ToArray());

            foreach (var p in points)
                ctx.Gfx.DrawEllipse(brush, new XRect(p.X - 2, p.Y - 2, 4, 4));

            if (spec.ShowValueLabels)
            {
                for (var i = 0; i < n && i < series.Points.Count; i++)
                {
                    var v = series.Points[i].Value;
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    var cx = frame.CategoryCenter(i);
                    var y = frame.Y(v);
                    var lr = new XRect(cx - 20, y - labelH - 3, 40, labelH);
                    ctx.Gfx.DrawString(ChartText.FormatValue(v), ctx.LabelFont, ctx.TextBrush, lr, Fmt.Center);
                }
            }
        }
    }
}
