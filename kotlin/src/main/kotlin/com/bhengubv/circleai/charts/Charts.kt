// Charts.kt
//
// Kotlin port of CircleAI.Charts — the C# reference is the EXACT spec.
//
// A chart described as DATA: a spec plus a style, both free of any drawing
// type, so the description travels and the host draws it with whatever it has.
//
// Fidelity notes:
//   * C# `record` -> `data class`.
//   * The C# renderer takes PDFsharp `XGraphics` / `PdfPage`. Neither exists on
//     the JVM here, and inventing a stand-in would be a lie about what this
//     package can do - so `ChartRenderer` is generic over its surface, and
//     `renderToPdf` keeps its byte-array signature exactly.
//   * `ChartFonts` keeps the family NAME, which is the part a spec and a host
//     renderer must agree on; the C# also installs a PDFsharp font resolver,
//     which has no meaning here.

package com.bhengubv.circleai.charts

/** Which chart to draw. */
enum class ChartType {
    /** Vertical bars; clustered when there is more than one series. */
    BAR,
    /** Connected line(s) over a shared category axis. */
    LINE,
    /** A single ring split into slices (uses the first series only). */
    PIE,
}

/**
 * One data point: a category/slice [label] and its [value].
 *
 * Bar and line accept negatives (the baseline sits at zero); pie treats
 * negative or zero values as empty slices, since a slice cannot be a negative
 * fraction of a whole.
 */
data class ChartDataPoint(val label: String, val value: Double)

/**
 * A named series of points. [colorHex] optionally overrides the palette colour
 * for this series with a `#RRGGBB` value; when null the renderer takes one from
 * [ChartStyle.paletteHex] by index.
 */
data class ChartSeries(
    val name: String,
    val points: List<ChartDataPoint>,
    val colorHex: String? = null,
)

/** A complete, self-contained description of one chart. */
data class ChartSpec(
    val type: ChartType,
    val title: String,
    val series: List<ChartSeries>,
    val valueAxisLabel: String? = null,
    val showLegend: Boolean = true,
    /** Off by default, to keep dense charts clean. */
    val showValueLabels: Boolean = false,
)

/** The font family a chart asks for by name. */
object ChartFonts {
    const val FAMILY_NAME = "CircleChartSans"
}

/**
 * Rendering options. Every colour is a `#RRGGBB` string, which is what keeps
 * this layer free of any drawing type.
 */
data class ChartStyle(
    val paletteHex: List<String> = DEFAULT_PALETTE,
    val backgroundHex: String = "#FFFFFF",
    val axisHex: String = "#90A4AE",
    val gridHex: String = "#ECEFF1",
    val textHex: String = "#2C3E50",
    val fontFamily: String = ChartFonts.FAMILY_NAME,
    val titleFontSize: Double = 14.0,
    val labelFontSize: Double = 9.0,
    val padding: Double = 12.0,
    val showGrid: Boolean = true,
    val valueTickCount: Int = 4,
) {
    /**
     * The palette colour for category [index], CYCLED.
     *
     * Not in the C#, where the renderer does the modulo inline. It is here
     * because every host renderer would otherwise write the same line, and one
     * of them would write it wrong for an empty palette.
     */
    fun colorHex(index: Int): String {
        if (paletteHex.isEmpty()) return textHex
        var i = index % paletteHex.size
        if (i < 0) i += paletteHex.size
        return paletteHex[i]
    }

    companion object {
        /**
         * Blue to slate: eight steps that stay within the house palette while
         * remaining easy to tell apart, cycled beyond eight categories.
         *
         * The house colours are blue, slate and white, so a sequential blue
         * ramp keeps categories distinguishable AND on-brand rather than
         * reaching for a colour the product does not have.
         */
        val DEFAULT_PALETTE = listOf(
            "#90CAF9", // blue 200
            "#42A5F5", // blue 400
            "#2196F3", // blue 500 (house blue)
            "#1E88E5", // blue 600
            "#1976D2", // blue 700
            "#1565C0", // blue 800
            "#0D47A1", // blue 900
            "#2C3E50", // house slate
        )

        /** The shared, on-brand default. */
        val DEFAULT = ChartStyle()
    }
}

/**
 * A rectangle in points. Stands in for PDFsharp XRect so the renderer contract
 * can name bounds without dragging in a platform graphics framework.
 */
data class ChartRect(val x: Double, val y: Double, val width: Double, val height: Double)

/**
 * Draws a [ChartSpec] onto a surface, fully offline and on-device.
 *
 * GENERIC OVER THE SURFACE, which is the one real departure from the C#: there
 * the methods take PDFsharp types. This lets a JVM host bind Graphics2D and an
 * Android host bind Canvas, while [renderToPdf] keeps its C# shape exactly -
 * bytes are bytes on both sides.
 */
interface ChartRenderer<Surface> {
    /**
     * Draws the chart onto an existing surface, confined to [bounds] in points.
     * The primitive the other entry point builds on, and the way to embed a
     * chart into a page the host already owns.
     */
    fun render(spec: ChartSpec, surface: Surface, bounds: ChartRect, style: ChartStyle? = null)

    /**
     * Renders to a self-contained one-page PDF and returns the bytes. The page
     * is sized in points (72 points = 1 inch).
     */
    fun renderToPdf(
        spec: ChartSpec,
        widthPoints: Double,
        heightPoints: Double,
        style: ChartStyle? = null,
    ): ByteArray
}

/**
 * Ready-made sample specs - for a does-it-render smoke test, a template preview
 * thumbnail, or a copy-paste starting point. The data is generic and
 * self-contained, so it is safe to show to any user.
 */
object ChartSpecFactory {

    /** A single-series bar chart with value labels. */
    fun sampleBar() = ChartSpec(
        type = ChartType.BAR,
        title = "Monthly Active Users",
        series = listOf(
            ChartSeries("Users", listOf(
                ChartDataPoint("Jan", 1200.0),
                ChartDataPoint("Feb", 1580.0),
                ChartDataPoint("Mar", 1490.0),
                ChartDataPoint("Apr", 2100.0),
                ChartDataPoint("May", 2460.0),
                ChartDataPoint("Jun", 2890.0),
            )),
        ),
        valueAxisLabel = "users",
        showValueLabels = true,
    )

    /** A clustered bar chart comparing two series over the same categories. */
    fun sampleGroupedBar() = ChartSpec(
        type = ChartType.BAR,
        title = "Revenue vs Cost by Quarter",
        series = listOf(
            ChartSeries("Revenue", listOf(
                ChartDataPoint("Q1", 42000.0),
                ChartDataPoint("Q2", 51000.0),
                ChartDataPoint("Q3", 47500.0),
                ChartDataPoint("Q4", 63000.0),
            )),
            ChartSeries("Cost", listOf(
                ChartDataPoint("Q1", 31000.0),
                ChartDataPoint("Q2", 34000.0),
                ChartDataPoint("Q3", 33500.0),
                ChartDataPoint("Q4", 39000.0),
            )),
        ),
        valueAxisLabel = "ZAR",
    )

    /** A two-line trend chart over a shared category axis. */
    fun sampleLine() = ChartSpec(
        type = ChartType.LINE,
        title = "Weekly Sign-ups",
        series = listOf(
            ChartSeries("This year", listOf(
                ChartDataPoint("W1", 120.0),
                ChartDataPoint("W2", 168.0),
                ChartDataPoint("W3", 154.0),
                ChartDataPoint("W4", 205.0),
                ChartDataPoint("W5", 246.0),
                ChartDataPoint("W6", 233.0),
            )),
            ChartSeries("Last year", listOf(
                ChartDataPoint("W1", 90.0),
                ChartDataPoint("W2", 110.0),
                ChartDataPoint("W3", 132.0),
                ChartDataPoint("W4", 150.0),
                ChartDataPoint("W5", 149.0),
                ChartDataPoint("W6", 178.0),
            )),
        ),
    )

    /** A pie chart with percentage labels. */
    fun samplePie() = ChartSpec(
        type = ChartType.PIE,
        title = "Traffic by Channel",
        series = listOf(
            ChartSeries("Channels", listOf(
                ChartDataPoint("Direct", 38.0),
                ChartDataPoint("Search", 27.0),
                ChartDataPoint("Social", 19.0),
                ChartDataPoint("Referral", 11.0),
                ChartDataPoint("Email", 5.0),
            )),
        ),
        showValueLabels = true,
    )

    /** All four samples, e.g. for a one-pass render test. */
    fun all() = listOf(sampleBar(), sampleGroupedBar(), sampleLine(), samplePie())
}
