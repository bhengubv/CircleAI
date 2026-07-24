#nullable enable

// ChartSpec.cs
//
// The renderer-agnostic vocabulary of a chart: WHAT to draw, never HOW. Nothing
// here references PDFsharp — the same spec could feed a future SVG or raster
// renderer without change, which is the whole point of keeping the model clean.
//
// A chart is a title + a chart type + one or more data series. A series is a
// named list of (label, value) points:
//   * Bar  — each series is a group of bars; points share a category axis by
//            index (point[i] of every series belongs to category i). One series
//            is the common case; multiple series render as clustered bars.
//   * Line — each series is a polyline over the same category axis.
//   * Pie  — a single series; each point is one slice. Extra series are ignored
//            (a pie shows one whole, split into parts).

using System.Collections.Generic;

namespace CircleAI.Charts;

/// <summary>The kind of chart to draw.</summary>
public enum ChartType
{
    /// <summary>Vertical bars; clustered when there is more than one series.</summary>
    Bar = 0,

    /// <summary>Connected line(s) over a shared category axis.</summary>
    Line,

    /// <summary>A single ring split into slices (uses the first series only).</summary>
    Pie,
}

/// <summary>One data point: a category/slice <paramref name="Label"/> and its <paramref name="Value"/>.</summary>
/// <param name="Label">Category name (bar/line x-axis) or slice name (pie legend).</param>
/// <param name="Value">
/// The magnitude. Bar/line accept negatives (the baseline sits at zero); pie
/// treats negative or zero values as empty slices, since a slice cannot be a
/// negative fraction of a whole.
/// </param>
public sealed record ChartDataPoint(string Label, double Value);

/// <summary>
/// A named series of points. <paramref name="ColorHex"/> optionally overrides the
/// palette colour for this series (bar/line) with a <c>#RRGGBB</c> value; when
/// null the renderer assigns a colour from <see cref="ChartStyle.PaletteHex"/> by
/// index.
/// </summary>
/// <param name="Name">Legend label for this series.</param>
/// <param name="Points">The (label, value) points, in category order.</param>
/// <param name="ColorHex">Optional <c>#RRGGBB</c> override; null = use the palette.</param>
public sealed record ChartSeries(
    string Name,
    IReadOnlyList<ChartDataPoint> Points,
    string? ColorHex = null);

/// <summary>
/// A complete, self-contained description of one chart.
/// </summary>
/// <param name="Type">Which chart to draw.</param>
/// <param name="Title">Heading shown above the plot; may be empty for no title.</param>
/// <param name="Series">
/// One or more data series. Must be non-empty. For <see cref="ChartType.Pie"/>
/// only the first series is used.
/// </param>
/// <param name="ValueAxisLabel">Optional caption for the value (y) axis on bar/line charts.</param>
/// <param name="ShowLegend">Whether to draw the legend strip (series for bar/line, slices for pie).</param>
/// <param name="ShowValueLabels">
/// Whether to print each value on the chart (above bars, beside line markers,
/// as a percentage inside pie slices). Off by default to keep dense charts clean.
/// </param>
public sealed record ChartSpec(
    ChartType Type,
    string Title,
    IReadOnlyList<ChartSeries> Series,
    string? ValueAxisLabel = null,
    bool ShowLegend = true,
    bool ShowValueLabels = false);
