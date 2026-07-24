#nullable enable

// SampleDeck.cs
//
// A static, deterministic sample Deck. Two jobs:
//   1. A smoke test — hand SampleDeck.Create() to PdfSharpDeckEngine and you get a
//      real multi-page landscape PDF, no model and no network involved.
//   2. Living documentation — it exercises every field of the model (title slide
//      via Title/Subtitle/Author, heading-only divider, bullets, a per-slide
//      footer, and speaker notes) so the shape is obvious at a glance.

using System.Collections.Generic;

namespace CircleAI.Presentations;

/// <summary>Builds a self-contained example <see cref="Deck"/> for demos and smoke tests.</summary>
public static class SampleDeck
{
    /// <summary>The canonical sample deck. Deterministic — same input, same PDF.</summary>
    public static Deck Create() => new(
        Title:    "CircleAI Offline Presentations",
        Subtitle: "Outline in, PDF out",
        Author:   "The Geek Network",
        Footer:   "CircleAI • Presentations",
        Slides: new[]
        {
            Slide.Of(
                "What This Is",
                "Turn an outline into a slide deck — fully offline",
                "One slide per page, exported as a single PDF",
                "No cloud, no Google, no network at generation time"),

            Slide.Of(
                "How It Works",
                "You supply a Deck: a title and an ordered list of slides",
                "Each slide is a heading plus a few bullet points",
                "PDFsharp-MigraDoc lays it out in landscape A4",
                "It renders with the same embedded font as the CV engine"),

            // A heading-only "section divider" slide — empty bullets is valid.
            Slide.Of("Part Two — Why It Fits The Phone"),

            new Slide(
                Title: "Runs On A Low-End Phone",
                Bullets: new[]
                {
                    "Pure-managed: no native library to bundle or load",
                    "Reuses CircleAI.Documents; adds no new dependency",
                    "Deterministic: the same Deck always makes the same PDF",
                },
                Footer: "CircleAI • Presentations • v1",
                Notes:  "Speaker note: stress that this works on a Huawei P30 Lite with no Play Services."),
        });
}
