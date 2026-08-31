// Charts.swift
//
// Port of src/CircleAI.Charts/:
//   • ChartSpec.cs         → ChartType, ChartDataPoint, ChartSeries, ChartSpec
//   • ChartStyle.cs        → ChartStyle (+ .default, the on-brand palette)
//   • ChartSpecFactory.cs  → ChartSpecFactory (the four sample specs)
//   • ChartFonts.cs        → ChartFonts.familyName
//   • IChartRenderer.cs    → ChartRenderer
//
// Porting notes:
//   • The C# model is deliberately renderer-agnostic - its own header says
//     "WHAT to draw, never HOW" and nothing in it references PDFsharp. That is
//     what makes this port possible at all, and the whole model comes across
//     unchanged.
//
//   • IChartRenderer is the one place the seam leaks: its Render/RenderToPage
//     overloads take PDFsharp's XGraphics and PdfPage, which are .NET types with
//     no Swift equivalent. Rather than invent a fake XGraphics, the protocol here
//     is generic over the drawing Surface, so an Apple host can satisfy it with
//     CGContext and a Linux host with whatever it has. renderToPDF is the one
//     entry point whose signature survives intact, because bytes are bytes.
//
//   • NO CONCRETE RENDERER IS PORTED. PdfSharpChartRenderer is ~400 lines against
//     an API that does not exist here. Shipping a half-drawn chart engine would
//     be worse than shipping the vocabulary and letting the host draw.
//
//   • C# `record` → `struct: Sendable, Equatable`; `IReadOnlyList<T>` → `[T]`;
//     `string?` → `String?`. `ChartStyle.Default` → `ChartStyle.default`, and the
//     C# comment about static-initialiser ORDER does not apply: Swift statics are
//     lazy, so the palette cannot be captured null the way it could there.

import Foundation

// MARK: - Chart vocabulary

/// The kind of chart to draw.
public enum ChartType: Int, Sendable, Equatable, CaseIterable {
    /// Vertical bars; clustered when there is more than one series.
    case bar = 0
    /// Connected line(s) over a shared category axis.
    case line
    /// A single ring split into slices (uses the first series only).
    case pie
}

/// One data point: a category/slice `label` and its `value`.
///
/// Bar and line accept negatives (the baseline sits at zero); pie treats
/// negative or zero values as empty slices, since a slice cannot be a negative
/// fraction of a whole.
public struct ChartDataPoint: Sendable, Equatable {
    /// Category name (bar/line x-axis) or slice name (pie legend).
    public let label: String
    /// The magnitude.
    public let value: Double

    public init(label: String, value: Double) {
        self.label = label
        self.value = value
    }
}

/// A named series of points.
///
/// `colorHex` optionally overrides the palette colour for this series (bar/line)
/// with a `#RRGGBB` value; when nil the renderer assigns a colour from
/// `ChartStyle.paletteHex` by index.
public struct ChartSeries: Sendable, Equatable {
    /// Legend label for this series.
    public let name: String
    /// The (label, value) points, in category order.
    public let points: [ChartDataPoint]
    /// Optional `#RRGGBB` override; nil = use the palette.
    public let colorHex: String?

    public init(name: String, points: [ChartDataPoint], colorHex: String? = nil) {
        self.name = name
        self.points = points
        self.colorHex = colorHex
    }
}

/// A complete, self-contained description of one chart.
public struct ChartSpec: Sendable, Equatable {
    /// Which chart to draw.
    public let type: ChartType
    /// Heading shown above the plot; may be empty for no title.
    public let title: String
    /// One or more data series. For `.pie` only the first series is used.
    public let series: [ChartSeries]
    /// Optional caption for the value (y) axis on bar/line charts.
    public let valueAxisLabel: String?
    /// Whether to draw the legend strip (series for bar/line, slices for pie).
    public let showLegend: Bool
    /// Whether to print each value on the chart. Off by default to keep dense
    /// charts clean.
    public let showValueLabels: Bool

    public init(
        type: ChartType,
        title: String,
        series: [ChartSeries],
        valueAxisLabel: String? = nil,
        showLegend: Bool = true,
        showValueLabels: Bool = false
    ) {
        self.type = type
        self.title = title
        self.series = series
        self.valueAxisLabel = valueAxisLabel
        self.showLegend = showLegend
        self.showValueLabels = showValueLabels
    }
}

// MARK: - Style

/// Fonts a chart asks for.
///
/// In C# this also installs a PDFsharp `IFontResolver` that maps every family to
/// one embedded DejaVu face. There is no PDFsharp here, so what survives is the
/// family NAME - the thing a spec and a host renderer have to agree on.
public enum ChartFonts {
    /// The family a chart asks for by default.
    public static let familyName = "CircleChartSans"
}

/// Rendering options for a chart. All colours are `#RRGGBB` strings, which is
/// what keeps this layer free of any drawing type.
public struct ChartStyle: Sendable, Equatable {
    /// Categorical colours, applied to series (bar/line) or slices (pie) by index
    /// and cycled if there are more categories than colours.
    public var paletteHex: [String]
    /// Plot background fill.
    public var backgroundHex: String
    /// Axis line + tick colour.
    public var axisHex: String
    /// Grid line colour.
    public var gridHex: String
    /// Text colour for title, labels and legend.
    public var textHex: String
    /// Font family the renderer is asked for.
    public var fontFamily: String
    /// Point size of the chart title.
    public var titleFontSize: Double
    /// Point size of axis labels, legend text and value labels.
    public var labelFontSize: Double
    /// Padding (points) between the chart's outer bounds and its content.
    public var padding: Double
    /// Whether to draw horizontal grid lines behind bar/line plots.
    public var showGrid: Bool
    /// Number of horizontal grid lines / value ticks on bar and line charts.
    public var valueTickCount: Int

    /// Blue -> slate ramp: eight steps that stay within the house palette while
    /// remaining easy to tell apart. Cycled for more than eight categories.
    ///
    /// The house colours are blue, slate and white, so a sequential blue ramp
    /// keeps categories distinguishable AND on-brand rather than reaching for a
    /// colour the product does not have.
    public static let defaultPalette: [String] = [
        "#90CAF9", // blue 200
        "#42A5F5", // blue 400
        "#2196F3", // blue 500  (house blue)
        "#1E88E5", // blue 600
        "#1976D2", // blue 700
        "#1565C0", // blue 800
        "#0D47A1", // blue 900
        "#2C3E50", // house slate
    ]

    public init(
        paletteHex: [String] = ChartStyle.defaultPalette,
        backgroundHex: String = "#FFFFFF",
        axisHex: String = "#90A4AE",
        gridHex: String = "#ECEFF1",
        textHex: String = "#2C3E50",
        fontFamily: String = ChartFonts.familyName,
        titleFontSize: Double = 14,
        labelFontSize: Double = 9,
        padding: Double = 12,
        showGrid: Bool = true,
        valueTickCount: Int = 4
    ) {
        self.paletteHex = paletteHex
        self.backgroundHex = backgroundHex
        self.axisHex = axisHex
        self.gridHex = gridHex
        self.textHex = textHex
        self.fontFamily = fontFamily
        self.titleFontSize = titleFontSize
        self.labelFontSize = labelFontSize
        self.padding = padding
        self.showGrid = showGrid
        self.valueTickCount = valueTickCount
    }

    /// The shared, on-brand default.
    public static let `default` = ChartStyle()

    /// The palette colour for category `index`, cycled.
    ///
    /// Not in the C#, where the renderer does the modulo inline. It is here
    /// because every host renderer would otherwise write the same line, and one
    /// of them would write it wrong for an empty palette.
    public func colorHex(at index: Int) -> String {
        guard !paletteHex.isEmpty else { return textHex }
        let i = index % paletteHex.count
        return paletteHex[i < 0 ? i + paletteHex.count : i]
    }
}

// MARK: - The renderer seam

/// Draws a `ChartSpec` onto a surface, fully offline and on-device.
///
/// GENERIC OVER THE SURFACE, and that is the one real departure from the C#.
/// There, `Render` and `RenderToPage` take PDFsharp's `XGraphics` and `PdfPage`;
/// neither type exists in Swift and inventing a stand-in would be a lie about
/// what this package can do. `Surface` lets an Apple host bind `CGContext` and
/// anything else bind what it has, while `renderToPDF` keeps its C# signature
/// exactly - bytes are bytes on both sides.
public protocol ChartRenderer {
    /// Whatever this renderer draws onto.
    associatedtype Surface

    /// Draws the chart onto an existing surface, confined to `bounds` (in points,
    /// in the surface's coordinate space). The primitive the other entry point
    /// builds on, and the way to embed a chart into a page the host already owns.
    func render(_ spec: ChartSpec, on surface: Surface, bounds: ChartRect, style: ChartStyle?) throws

    /// Renders the chart to a self-contained one-page PDF and returns the bytes.
    /// The page is sized in points (72 points = 1 inch).
    func renderToPDF(_ spec: ChartSpec, widthPoints: Double, heightPoints: Double, style: ChartStyle?) throws -> Data
}

/// A rectangle in points. Stands in for PDFsharp's `XRect` so the protocol above
/// can name bounds without dragging in a platform graphics framework - CoreGraphics
/// is not available on every platform this package builds for.
public struct ChartRect: Sendable, Equatable {
    public let x: Double
    public let y: Double
    public let width: Double
    public let height: Double

    public init(x: Double, y: Double, width: Double, height: Double) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }
}

// MARK: - Sample specs

/// Ready-made sample specs - for a "does it render?" smoke test, a template
/// preview thumbnail, or copy-paste starting points. The data is generic and
/// self-contained, so it is safe to show to any user.
public enum ChartSpecFactory {

    /// A single-series bar chart with value labels.
    public static func sampleBar() -> ChartSpec {
        ChartSpec(
            type: .bar,
            title: "Monthly Active Users",
            series: [
                ChartSeries(name: "Users", points: [
                    ChartDataPoint(label: "Jan", value: 1200),
                    ChartDataPoint(label: "Feb", value: 1580),
                    ChartDataPoint(label: "Mar", value: 1490),
                    ChartDataPoint(label: "Apr", value: 2100),
                    ChartDataPoint(label: "May", value: 2460),
                    ChartDataPoint(label: "Jun", value: 2890),
                ]),
            ],
            valueAxisLabel: "users",
            showValueLabels: true)
    }

    /// A clustered bar chart comparing two series over the same categories.
    public static func sampleGroupedBar() -> ChartSpec {
        ChartSpec(
            type: .bar,
            title: "Revenue vs Cost by Quarter",
            series: [
                ChartSeries(name: "Revenue", points: [
                    ChartDataPoint(label: "Q1", value: 42000),
                    ChartDataPoint(label: "Q2", value: 51000),
                    ChartDataPoint(label: "Q3", value: 47500),
                    ChartDataPoint(label: "Q4", value: 63000),
                ]),
                ChartSeries(name: "Cost", points: [
                    ChartDataPoint(label: "Q1", value: 31000),
                    ChartDataPoint(label: "Q2", value: 34000),
                    ChartDataPoint(label: "Q3", value: 33500),
                    ChartDataPoint(label: "Q4", value: 39000),
                ]),
            ],
            valueAxisLabel: "ZAR")
    }

    /// A two-line trend chart over a shared category axis.
    public static func sampleLine() -> ChartSpec {
        ChartSpec(
            type: .line,
            title: "Weekly Sign-ups",
            series: [
                ChartSeries(name: "This year", points: [
                    ChartDataPoint(label: "W1", value: 120),
                    ChartDataPoint(label: "W2", value: 168),
                    ChartDataPoint(label: "W3", value: 154),
                    ChartDataPoint(label: "W4", value: 205),
                    ChartDataPoint(label: "W5", value: 246),
                    ChartDataPoint(label: "W6", value: 233),
                ]),
                ChartSeries(name: "Last year", points: [
                    ChartDataPoint(label: "W1", value: 90),
                    ChartDataPoint(label: "W2", value: 110),
                    ChartDataPoint(label: "W3", value: 132),
                    ChartDataPoint(label: "W4", value: 150),
                    ChartDataPoint(label: "W5", value: 149),
                    ChartDataPoint(label: "W6", value: 178),
                ]),
            ])
    }

    /// A pie chart with percentage labels.
    public static func samplePie() -> ChartSpec {
        ChartSpec(
            type: .pie,
            title: "Traffic by Channel",
            series: [
                ChartSeries(name: "Channels", points: [
                    ChartDataPoint(label: "Direct", value: 38),
                    ChartDataPoint(label: "Search", value: 27),
                    ChartDataPoint(label: "Social", value: 19),
                    ChartDataPoint(label: "Referral", value: 11),
                    ChartDataPoint(label: "Email", value: 5),
                ]),
            ],
            showValueLabels: true)
    }

    /// All four samples, e.g. for a one-pass render test.
    public static func all() -> [ChartSpec] {
        [sampleBar(), sampleGroupedBar(), sampleLine(), samplePie()]
    }
}
