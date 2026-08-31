// Presentations.swift
//
// Port of src/CircleAI.Presentations/:
//   • Deck.cs        → Deck, Slide
//   • IDeckEngine.cs → DeckEngine
//   • SampleDeck.cs  → SampleDeck
//
// Porting notes:
//   • Like Documents, the CONTENT MODEL ports and the PDFsharp template does not.
//     LandscapeSlideTemplate is landscape-A4 drawing code against an API with no
//     Swift counterpart; PdfSharpDeckEngine likewise.
//
//   • The C# header is explicit that Deck serves double duty: the typed input a
//     template lays out, AND the JSON shape an on-device model fills in. That
//     second job is why these are Codable here - a Deck is meant to be
//     deserialised straight out of a model's reply, and a port that could not do
//     that would have dropped half the point of the type.
//
//   • DeckEngine returns DocumentResult, reusing the Documents type exactly as the
//     C# does: "a rendered deck is just another bytes + MIME + filename artifact,
//     so the host's share/open plumbing is identical."
//
//   • `Deck.Of` / `Slide.Of` take `params`; Swift takes an array, so the
//     convenience is `Deck.of(title:slides:)` and `Slide.of(title:bullets:)`.

import Foundation

// MARK: - Model

/// One slide: a heading, a handful of bullet points, and optional footer/notes.
public struct Slide: Sendable, Equatable, Codable {
    /// The slide heading. Rendered large at the top of the page.
    public let title: String
    /// The bullet points. MAY BE EMPTY - an empty list gives a heading-only
    /// "section divider" slide, which is a legitimate and common deck element.
    public let bullets: [String]
    /// Optional footer for this slide. Overrides the deck-wide footer when present.
    public let footer: String?
    /// Optional speaker notes.
    ///
    /// A real .pptx keeps these hidden behind the slide; a single-file offline PDF
    /// has nowhere to hide them, so a template surfaces them SUBTLY at the bottom
    /// rather than dropping them - losing the presenter's notes would be worse
    /// than showing them.
    public let notes: String?

    public init(title: String, bullets: [String] = [], footer: String? = nil, notes: String? = nil) {
        self.title = title
        self.bullets = bullets
        self.footer = footer
        self.notes = notes
    }

    /// Convenience: a slide from a title and inline bullet points.
    public static func of(_ title: String, _ bullets: String...) -> Slide {
        Slide(title: title, bullets: bullets)
    }

    /// AN OMITTED `bullets` DECODES AS EMPTY, and this is why the initialiser is
    /// written out rather than synthesised.
    ///
    /// Swift's generated Codable treats a missing key as an ERROR, so a deck from
    /// a model that wrote a heading-only divider slide as `{"title":"Part Two"}`
    /// - which is exactly what the model is told an empty-bullets slide is -
    /// failed to decode at all. The C# has no such trap: a missing JSON array
    /// lands as null and the template moves on. Caught by
    /// PresentationsTests.test_a_deck_decodes_from_the_json_a_model_would_plausibly_emit.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        title = try c.decode(String.self, forKey: .title)
        bullets = try c.decodeIfPresent([String].self, forKey: .bullets) ?? []
        footer = try c.decodeIfPresent(String.self, forKey: .footer)
        notes = try c.decodeIfPresent(String.self, forKey: .notes)
    }
}

/// A complete slide deck, ready to lay out. Also a clean JSON target for a model
/// that turns an outline into slides.
public struct Deck: Sendable, Equatable, Codable {
    /// The deck title. When non-empty it renders as an opening TITLE SLIDE
    /// (page 1); content slides follow, one per page.
    public let title: String
    /// The content slides, in the order they appear.
    public let slides: [Slide]
    /// Optional subtitle for the title slide.
    public let subtitle: String?
    /// Optional author/presenter for the title slide.
    public let author: String?
    /// Optional deck-wide footer, shown on every content slide unless a slide
    /// overrides it with its own footer.
    public let footer: String?

    public init(title: String, slides: [Slide] = [], subtitle: String? = nil,
                author: String? = nil, footer: String? = nil) {
        self.title = title
        self.slides = slides
        self.subtitle = subtitle
        self.author = author
        self.footer = footer
    }

    /// Convenience: a titled deck from a sequence of slides.
    public static func of(_ title: String, _ slides: Slide...) -> Deck {
        Deck(title: title, slides: slides)
    }

    /// An omitted `slides` decodes as empty - same reason as `Slide`.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        title = try c.decode(String.self, forKey: .title)
        slides = try c.decodeIfPresent([Slide].self, forKey: .slides) ?? []
        subtitle = try c.decodeIfPresent(String.self, forKey: .subtitle)
        author = try c.decodeIfPresent(String.self, forKey: .author)
        footer = try c.decodeIfPresent(String.self, forKey: .footer)
    }

    /// The footer a given slide should actually print.
    ///
    /// Not in the C#, where each template re-derives it. It is here because the
    /// override rule - slide footer beats deck footer - is a property of the
    /// MODEL, and every host template would otherwise re-implement it, one of
    /// them wrongly.
    public func footer(for slide: Slide) -> String? {
        slide.footer ?? footer
    }
}

// MARK: - The engine seam

/// Renders a `Deck` to a PDF (one slide per page), fully offline and on-device.
public protocol DeckEngine: Sendable {
    /// Renders one deck to PDF bytes.
    func render(_ deck: Deck) async throws -> DocumentResult
    /// Template ids this engine can render, for a host to offer as a choice. The
    /// first entry is the default when none is requested.
    var availableTemplates: [String] { get }
}

// MARK: - Sample

/// Builds a self-contained example `Deck` for demos and smoke tests.
///
/// Two jobs, per the C#: a smoke test that needs no model and no network, and
/// living documentation - it exercises every field of the model (title slide,
/// heading-only divider, bullets, a per-slide footer, speaker notes) so the shape
/// is obvious at a glance.
public enum SampleDeck {

    /// The canonical sample deck. Deterministic - same input, same PDF.
    public static func create() -> Deck {
        Deck(
            title: "CircleAI Offline Presentations",
            slides: [
                Slide.of(
                    "What This Is",
                    "Turn an outline into a slide deck — fully offline",
                    "One slide per page, exported as a single PDF",
                    "No cloud, no Google, no network at generation time"),

                Slide.of(
                    "How It Works",
                    "You supply a Deck: a title and an ordered list of slides",
                    "Each slide is a heading plus a few bullet points",
                    "PDFsharp-MigraDoc lays it out in landscape A4",
                    "It renders with the same embedded font as the CV engine"),

                // A heading-only "section divider" slide — empty bullets is valid.
                Slide.of("Part Two — Why It Fits The Phone"),

                Slide(
                    title: "Runs On A Low-End Phone",
                    bullets: [
                        "Pure-managed: no native library to bundle or load",
                        "Reuses CircleAI.Documents; adds no new dependency",
                        "Deterministic: the same Deck always makes the same PDF",
                    ],
                    footer: "CircleAI • Presentations • v1",
                    notes: "Speaker note: stress that this works on a Huawei P30 Lite with no Play Services."),
            ],
            subtitle: "Outline in, PDF out",
            author: "The Geek Network",
            footer: "CircleAI • Presentations")
    }
}
