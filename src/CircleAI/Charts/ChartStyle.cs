#nullable enable

// ChartStyle.cs
//
// The visual knobs, separated from the data (ChartSpec). Colours are held as
// #RRGGBB strings so the model layer stays free of any PDFsharp type; the
// renderer parses them once per draw.
//
// The DEFAULT palette is a blue -> slate ramp built only from the house colours
// (#2196F3 blue and #2C3E50 slate and steps between), per feedback_no_orange:
// the app's identity colours are blue / slate / white, so a sequential blue ramp
// keeps categories distinguishable AND on-brand. Callers who need a different
// palette (e.g. an external customer's deck) just supply their own PaletteHex.

using System.Collections.Generic;

namespace CircleAI.Charts;

/// <summary>
/// Rendering options for a chart. Immutable; use <c>with</c> to tweak a copy of
/// <see cref="Default"/>. All colours are <c>#RRGGBB</c> strings.
/// </summary>
public sealed record ChartStyle
{
    /// <summary>
    /// Categorical colours, applied to series (bar/line) or slices (pie) by index
    /// and cycled if there are more categories than colours. Default: an on-brand
    /// blue -> slate ramp.
    /// </summary>
    public IReadOnlyList<string> PaletteHex { get; init; } = DefaultPalette;

    /// <summary>Plot background fill. Default white.</summary>
    public string BackgroundHex { get; init; } = "#FFFFFF";

    /// <summary>Axis line + tick colour. Default a muted blue-grey.</summary>
    public string AxisHex { get; init; } = "#90A4AE";

    /// <summary>Grid line colour. Default a very light blue-grey.</summary>
    public string GridHex { get; init; } = "#ECEFF1";

    /// <summary>Text colour for title, labels and legend. Default house slate.</summary>
    public string TextHex { get; init; } = "#2C3E50";

    /// <summary>
    /// Font family the renderer asks PDFsharp for. The value barely matters in
    /// practice: the installed resolver (this library's, or CircleAI.Documents')
    /// maps every family to the same embedded DejaVu face. Kept configurable for
    /// hosts that install a resolver exposing several families.
    /// </summary>
    public string FontFamily { get; init; } = ChartFonts.FamilyName;

    /// <summary>Point size of the chart title.</summary>
    public double TitleFontSize { get; init; } = 14;

    /// <summary>Point size of axis labels, legend text and value labels.</summary>
    public double LabelFontSize { get; init; } = 9;

    /// <summary>Padding (points) between the chart's outer bounds and its content.</summary>
    public double Padding { get; init; } = 12;

    /// <summary>Whether to draw horizontal grid lines behind bar/line plots.</summary>
    public bool ShowGrid { get; init; } = true;

    /// <summary>Number of horizontal grid lines / value ticks on bar and line charts.</summary>
    public int ValueTickCount { get; init; } = 4;

    // Blue (#2196F3) -> slate (#2C3E50) ramp: eight steps that stay within the
    // house palette while remaining easy to tell apart. Cycled for >8 categories.
    //
    // MUST be declared BEFORE Default: static initialisers run in textual order,
    // and Default = new() reads this field through the PaletteHex initialiser, so
    // this has to be assigned first or Default would capture a null palette.
    private static readonly string[] DefaultPalette =
    {
        "#90CAF9", // blue 200
        "#42A5F5", // blue 400
        "#2196F3", // blue 500  (house blue)
        "#1E88E5", // blue 600
        "#1976D2", // blue 700
        "#1565C0", // blue 800
        "#0D47A1", // blue 900
        "#2C3E50", // house slate
    };

    /// <summary>The shared, on-brand default. Reuse rather than reallocating.</summary>
    public static ChartStyle Default { get; } = new();
}
