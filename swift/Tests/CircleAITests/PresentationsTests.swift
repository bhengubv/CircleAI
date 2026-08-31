// PresentationsTests.swift
//
// The model's two jobs are what get tested: laying out (footer override,
// divider slides) and round-tripping JSON, since the C# header says a Deck is
// meant to be deserialised straight out of a model's reply.

import XCTest
@testable import CircleAI

final class PresentationsTests: XCTestCase {

    func test_a_slide_with_no_bullets_is_valid_as_a_section_divider() {
        let s = Slide.of("Part Two — Why It Fits The Phone")
        XCTAssertTrue(s.bullets.isEmpty, "empty bullets is a legitimate divider, not a broken slide")
        XCTAssertFalse(s.title.isEmpty)
    }

    func test_slide_footer_overrides_the_deck_footer() {
        let deck = Deck(title: "T",
                        slides: [Slide(title: "A", footer: "slide footer")],
                        footer: "deck footer")
        XCTAssertEqual(deck.footer(for: deck.slides[0]), "slide footer")
    }

    func test_deck_footer_is_used_when_the_slide_has_none() {
        let deck = Deck(title: "T", slides: [Slide(title: "A")], footer: "deck footer")
        XCTAssertEqual(deck.footer(for: deck.slides[0]), "deck footer")
    }

    func test_no_footer_anywhere_prints_nothing() {
        let deck = Deck(title: "T", slides: [Slide(title: "A")])
        XCTAssertNil(deck.footer(for: deck.slides[0]))
    }

    func test_sample_deck_exercises_every_field_of_the_model() {
        // The C# calls this "living documentation": if a field stops being
        // covered the sample has quietly stopped documenting the shape.
        let d = SampleDeck.create()
        XCTAssertFalse(d.title.isEmpty)
        XCTAssertNotNil(d.subtitle)
        XCTAssertNotNil(d.author)
        XCTAssertNotNil(d.footer)
        XCTAssertTrue(d.slides.contains { $0.bullets.isEmpty }, "needs a divider slide")
        XCTAssertTrue(d.slides.contains { $0.footer != nil }, "needs a per-slide footer")
        XCTAssertTrue(d.slides.contains { $0.notes != nil }, "needs speaker notes")
        XCTAssertTrue(d.slides.contains { !$0.bullets.isEmpty }, "needs a bulleted slide")
    }

    func test_sample_deck_is_deterministic() {
        XCTAssertEqual(SampleDeck.create(), SampleDeck.create())
    }

    func test_deck_round_trips_through_json() throws {
        // The whole second job of this type: a model emits JSON, it deserialises
        // straight into a Deck. If that breaks, the presenton flow breaks.
        let original = SampleDeck.create()
        let data = try JSONEncoder().encode(original)
        let back = try JSONDecoder().decode(Deck.self, from: data)
        XCTAssertEqual(original, back)
    }

    func test_a_deck_decodes_from_the_json_a_model_would_plausibly_emit() throws {
        let json = """
        {"title":"Q3 Review","slides":[
            {"title":"Highlights","bullets":["Revenue up","Costs flat"]},
            {"title":"Next"}
        ]}
        """.data(using: .utf8)!
        let deck = try JSONDecoder().decode(Deck.self, from: json)
        XCTAssertEqual(deck.title, "Q3 Review")
        XCTAssertEqual(deck.slides.count, 2)
        XCTAssertEqual(deck.slides[0].bullets.count, 2)
        XCTAssertTrue(deck.slides[1].bullets.isEmpty, "an omitted bullets array must default to empty")
        XCTAssertNil(deck.subtitle)
    }

    func test_of_factories_keep_slide_order() {
        let d = Deck.of("T", Slide.of("one"), Slide.of("two"), Slide.of("three"))
        XCTAssertEqual(d.slides.map(\.title), ["one", "two", "three"])
    }
}
