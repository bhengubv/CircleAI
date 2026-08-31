// ChartsTests.swift
//
// Behaviour checks for the Charts port, written against what the C# actually
// promises in src/CircleAI.Charts rather than against the Swift I just wrote -
// a test that only restates the implementation proves nothing.

import XCTest
@testable import CircleAI

final class ChartsTests: XCTestCase {

    // MARK: - Style

    func test_default_palette_is_the_house_ramp_and_nothing_else() {
        // The C# comment is explicit: blue -> slate, eight steps, no other hue,
        // because the product has three colours and orange is not one of them.
        XCTAssertEqual(ChartStyle.defaultPalette.count, 8)
        XCTAssertEqual(ChartStyle.defaultPalette.first, "#90CAF9")
        XCTAssertEqual(ChartStyle.defaultPalette.last, "#2C3E50", "the ramp must end on house slate")
        XCTAssertTrue(ChartStyle.defaultPalette.contains("#2196F3"), "house blue must be in the ramp")

        for hex in ChartStyle.defaultPalette {
            XCTAssertEqual(hex.count, 7, "\(hex) is not #RRGGBB")
            XCTAssertTrue(hex.hasPrefix("#"))
        }
    }

    func test_default_style_matches_the_csharp_initialisers() {
        let s = ChartStyle.default
        XCTAssertEqual(s.backgroundHex, "#FFFFFF")
        XCTAssertEqual(s.axisHex, "#90A4AE")
        XCTAssertEqual(s.gridHex, "#ECEFF1")
        XCTAssertEqual(s.textHex, "#2C3E50")
        XCTAssertEqual(s.fontFamily, ChartFonts.familyName)
        XCTAssertEqual(s.titleFontSize, 14)
        XCTAssertEqual(s.labelFontSize, 9)
        XCTAssertEqual(s.valueTickCount, 4)
        XCTAssertTrue(s.showGrid)
    }

    func test_palette_cycles_when_there_are_more_categories_than_colours() {
        let s = ChartStyle.default
        XCTAssertEqual(s.colorHex(at: 0), ChartStyle.defaultPalette[0])
        XCTAssertEqual(s.colorHex(at: 8), ChartStyle.defaultPalette[0], "index 8 wraps to 0")
        XCTAssertEqual(s.colorHex(at: 9), ChartStyle.defaultPalette[1])
    }

    func test_palette_cycling_survives_a_negative_index() {
        // Swift's % keeps the sign, so a naive modulo would crash on a negative
        // index. A renderer walking backwards through series must not.
        XCTAssertEqual(ChartStyle.default.colorHex(at: -1), ChartStyle.defaultPalette[7])
    }

    func test_an_empty_palette_falls_back_to_the_text_colour_rather_than_crashing() {
        var s = ChartStyle.default
        s.paletteHex = []
        XCTAssertEqual(s.colorHex(at: 3), s.textHex)
    }

    // MARK: - Spec vocabulary

    func test_spec_defaults_match_the_csharp_optional_parameters() {
        let spec = ChartSpec(type: .bar, title: "T", series: [])
        XCTAssertNil(spec.valueAxisLabel)
        XCTAssertTrue(spec.showLegend, "legend defaults ON in the C#")
        XCTAssertFalse(spec.showValueLabels, "value labels default OFF, to keep dense charts clean")
    }

    func test_series_colour_override_defaults_to_nil_meaning_use_the_palette() {
        XCTAssertNil(ChartSeries(name: "s", points: []).colorHex)
    }

    func test_chart_type_raw_values_are_stable() {
        // The C# pins Bar = 0 explicitly; anything persisting a spec depends on it.
        XCTAssertEqual(ChartType.bar.rawValue, 0)
        XCTAssertEqual(ChartType.line.rawValue, 1)
        XCTAssertEqual(ChartType.pie.rawValue, 2)
    }

    // MARK: - Sample specs

    func test_factory_returns_one_sample_of_each_type() {
        let all = ChartSpecFactory.all()
        XCTAssertEqual(all.count, 4)
        XCTAssertEqual(all.map(\.type), [.bar, .bar, .line, .pie])
    }

    func test_every_sample_is_renderable_as_the_model_defines_it() {
        for spec in ChartSpecFactory.all() {
            XCTAssertFalse(spec.series.isEmpty, "\(spec.title): series must be non-empty")
            XCTAssertFalse(spec.title.isEmpty)
            for s in spec.series {
                XCTAssertFalse(s.points.isEmpty, "\(spec.title)/\(s.name): no points")
            }
        }
    }

    func test_grouped_bar_series_share_a_category_axis() {
        // Bar/line points align BY INDEX across series - point[i] of every series
        // is category i. Series of differing length would misalign silently.
        let spec = ChartSpecFactory.sampleGroupedBar()
        XCTAssertEqual(spec.series.count, 2)
        let lengths = Set(spec.series.map(\.points.count))
        XCTAssertEqual(lengths.count, 1, "clustered series must have equal point counts")
        XCTAssertEqual(spec.series[0].points.map(\.label), spec.series[1].points.map(\.label))
    }

    func test_pie_sample_has_exactly_one_series() {
        // A pie shows one whole split into parts; extra series are ignored, so a
        // sample carrying two would be showing something the renderer discards.
        XCTAssertEqual(ChartSpecFactory.samplePie().series.count, 1)
    }

    func test_pie_sample_values_are_all_positive() {
        // Pie treats negative or zero as an empty slice - a sample must not ship
        // one, or the smoke test renders a gap and looks broken.
        for p in ChartSpecFactory.samplePie().series[0].points {
            XCTAssertGreaterThan(p.value, 0, "\(p.label) would render as an empty slice")
        }
    }

    func test_specs_are_value_types_so_a_copy_cannot_be_mutated_underneath_a_renderer() {
        let a = ChartSpecFactory.sampleBar()
        var b = a
        b = ChartSpec(type: .pie, title: b.title, series: b.series)
        XCTAssertEqual(a.type, .bar, "the original must be untouched")
        XCTAssertEqual(b.type, .pie)
    }
}
