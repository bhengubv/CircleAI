#nullable enable

// Deck.cs
//
// The content model for an offline slide deck — the single most important type
// here, because (like CvDocument in CircleAI.Documents) it is meant to serve
// DOUBLE duty:
//
//   1. The typed input a template lays out into a PDF.
//   2. The JSON shape an on-device model can fill in: ask the Neuron to emit JSON
//      matching this schema (a title + an ordered list of slides, each with a
//      heading and a few bullets) and deserialize straight into a Deck. Model owns
//      the words; the template owns the layout. That is exactly the presenton
//      flow — an outline becomes a deck — done fully offline.
//
// The model is deliberately plain: a deck is a title (+ optional subtitle/author)
// and an ORDERED list of slides; a slide is a heading, some bullet points, and an
// optional per-slide footer and speaker notes. No animations, no themes, no media
// — those cannot survive a single-file offline PDF and are out of scope for v1.

using System.Collections.Generic;

namespace CircleAI.Presentations;

/// <summary>
/// A complete slide deck, ready to lay out. Also a clean JSON target for a model
/// that turns an outline into slides.
/// </summary>
/// <param name="Title">
/// The deck title. When non-empty it renders as an opening TITLE SLIDE (page 1);
/// content slides follow, one per page.
/// </param>
/// <param name="Slides">The content slides, in the order they appear.</param>
/// <param name="Subtitle">Optional subtitle for the title slide.</param>
/// <param name="Author">Optional author/presenter for the title slide.</param>
/// <param name="Footer">
/// Optional deck-wide footer text, shown on every content slide unless a slide
/// overrides it with its own <see cref="Slide.Footer"/>.
/// </param>
public sealed record Deck(
    string                Title,
    IReadOnlyList<Slide>  Slides,
    string?               Subtitle = null,
    string?               Author   = null,
    string?               Footer   = null)
{
    /// <summary>Convenience factory: a titled deck from a sequence of slides.</summary>
    public static Deck Of(string title, params Slide[] slides) => new(title, slides);
}

/// <summary>
/// One slide: a heading, a handful of bullet points, and optional footer/notes.
/// </summary>
/// <param name="Title">The slide heading. Rendered large at the top of the page.</param>
/// <param name="Bullets">
/// The bullet points. May be empty — an empty list gives a heading-only "section
/// divider" slide, which is a legitimate and common deck element.
/// </param>
/// <param name="Footer">
/// Optional footer text for this slide. Overrides the deck-wide
/// <see cref="Deck.Footer"/> when present.
/// </param>
/// <param name="Notes">
/// Optional speaker notes. A real .pptx keeps these hidden behind the slide; a
/// single-file offline PDF has nowhere to hide them, so the template surfaces them
/// SUBTLY at the bottom of the slide (small, grey, clearly labelled) rather than
/// dropping them — losing the presenter's notes would be worse than showing them.
/// </param>
public sealed record Slide(
    string                Title,
    IReadOnlyList<string> Bullets,
    string?               Footer = null,
    string?               Notes  = null)
{
    /// <summary>Convenience factory: a slide from a title and inline bullet points.</summary>
    public static Slide Of(string title, params string[] bullets) => new(title, bullets);
}
