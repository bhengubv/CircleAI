package com.bhengubv.circleai.charts

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** The chart spec and its palette. */
class ChartsTest {

    @Test fun `the three chart types exist`() {
        assertEquals(3, ChartType.entries.size)
    }

    @Test fun `a spec carries its series in order`() {
        val s = ChartSpecFactory.sampleGroupedBar()
        assertEquals(2, s.series.size)
        assertEquals("Revenue", s.series[0].name)
        assertEquals("Cost", s.series[1].name)
        assertEquals(4, s.series[0].points.size)
    }

    @Test fun `value labels are off by default so dense charts stay clean`() {
        assertFalse(ChartSpec(ChartType.BAR, "t", emptyList()).showValueLabels)
        assertTrue(ChartSpec(ChartType.BAR, "t", emptyList()).showLegend)
    }

    // The house colours are blue, slate and white; the ramp stays on-brand.
    @Test fun `the default palette is the house blue ramp ending in slate`() {
        val p = ChartStyle.DEFAULT_PALETTE
        assertEquals(8, p.size)
        assertTrue(p.contains("#2196F3"), "the house blue must be in the ramp")
        assertEquals("#2C3E50", p.last(), "the ramp ends on the house slate")
    }

    // Cycled, so a ninth category does not fall off the end.
    @Test fun `palette colours cycle past the end`() {
        val s = ChartStyle.DEFAULT
        assertEquals(s.colorHex(0), s.colorHex(8))
        assertEquals(s.colorHex(1), s.colorHex(9))
    }

    // A negative index must not throw; it wraps like a positive one.
    @Test fun `a negative index wraps rather than throwing`() {
        val s = ChartStyle.DEFAULT
        assertEquals(s.colorHex(7), s.colorHex(-1))
    }

    // An empty palette falls back to the text colour rather than crashing,
    // because a chart with no colour is still better than no chart.
    @Test fun `an empty palette falls back to the text colour`() {
        val s = ChartStyle(paletteHex = emptyList(), textHex = "#123456")
        assertEquals("#123456", s.colorHex(0))
        assertEquals("#123456", s.colorHex(99))
    }

    @Test fun `a series can override its palette colour`() {
        val s = ChartSeries("x", emptyList(), colorHex = "#FF0000")
        assertEquals("#FF0000", s.colorHex)
        assertNull(ChartSeries("y", emptyList()).colorHex)
    }

    @Test fun `the default style is on-brand and grid-on`() {
        val s = ChartStyle.DEFAULT
        assertEquals("#FFFFFF", s.backgroundHex)
        assertEquals("#2C3E50", s.textHex)
        assertEquals(ChartFonts.FAMILY_NAME, s.fontFamily)
        assertTrue(s.showGrid)
        assertEquals(4, s.valueTickCount)
    }

    @Test fun `every sample spec is populated`() {
        val all = ChartSpecFactory.all()
        assertEquals(4, all.size)
        for (spec in all) {
            assertTrue(spec.title.isNotEmpty(), "a sample needs a title")
            assertTrue(spec.series.isNotEmpty(), "a sample needs data")
            assertTrue(spec.series.all { it.points.isNotEmpty() })
        }
    }

    // Pie uses the FIRST series only, so a pie sample must have exactly one.
    @Test fun `the pie sample has a single series`() {
        assertEquals(1, ChartSpecFactory.samplePie().series.size)
        assertEquals(ChartType.PIE, ChartSpecFactory.samplePie().type)
    }

    @Test fun `the line sample compares two periods over one axis`() {
        val s = ChartSpecFactory.sampleLine()
        assertEquals(ChartType.LINE, s.type)
        assertEquals(2, s.series.size)
        assertEquals(s.series[0].points.map { it.label }, s.series[1].points.map { it.label })
    }

    @Test fun `a rect carries its bounds`() {
        val r = ChartRect(1.0, 2.0, 300.0, 400.0)
        assertEquals(300.0, r.width)
        assertEquals(400.0, r.height)
    }
}
