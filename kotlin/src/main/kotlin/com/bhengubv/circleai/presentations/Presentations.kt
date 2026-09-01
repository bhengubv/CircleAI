// Presentations.kt
//
// Kotlin port of CircleAI.Presentations — the C# reference is the EXACT spec.
//
// Turn an outline into a slide deck, fully offline: one slide per page, out as
// a single PDF. No cloud and no network at generation time.
//
// Fidelity notes:
//   * C# `record` -> `data class`.
//   * An OMITTED `bullets` or `slides` array decodes as EMPTY, not as an error.
//     A model told that a heading-only divider slide has no bullets writes
//     exactly {"title":"Part Two"}, and that has to parse. kotlinx.serialization
//     needs a default for this; the C# lands a missing array as null and moves
//     on. This was a real bug in the Swift port, caught by its tests.
//   * The PDF engine itself is PDFsharp-MigraDoc in the C#; here it stays a
//     seam, because inventing a layout engine would be a lie about what this
//     package does.

package com.bhengubv.circleai.presentations

import kotlinx.serialization.Serializable

/** One slide: a heading and some bullets. */
@Serializable
data class Slide(
    /** Rendered large at the top of the page. */
    val title: String,
    /**
     * MAY BE EMPTY - an empty list gives a heading-only "section divider"
     * slide, which is a legitimate and common deck element.
     */
    val bullets: List<String> = emptyList(),
    /** Overrides the deck-wide footer when present. */
    val footer: String? = null,
    /**
     * Speaker notes.
     *
     * A real .pptx keeps these hidden behind the slide; a single-file offline
     * PDF has nowhere to hide them, so a template surfaces them SUBTLY at the
     * bottom rather than dropping them - losing the presenter notes would be
     * worse than showing them.
     */
    val notes: String? = null,
) {
    companion object {
        /** A slide from a title and inline bullet points. */
        fun of(title: String, vararg bullets: String) = Slide(title, bullets.toList())
    }
}

/**
 * A complete slide deck, ready to lay out. Also a clean JSON target for a model
 * that turns an outline into slides.
 */
@Serializable
data class Deck(
    /**
     * When non-empty this renders as an opening TITLE SLIDE (page 1); content
     * slides follow, one per page.
     */
    val title: String,
    val slides: List<Slide> = emptyList(),
    val subtitle: String? = null,
    val author: String? = null,
    /**
     * Shown on every content slide unless a slide overrides it with its own.
     */
    val footer: String? = null,
) {
    /**
     * The footer a given slide should actually print.
     *
     * Not in the C#, where each template re-derives it. It is here because the
     * override rule - slide footer beats deck footer - is a property of the
     * MODEL, and every host template would otherwise re-implement it, one of
     * them wrongly.
     */
    fun footerFor(slide: Slide): String? = slide.footer ?: footer

    companion object {
        fun of(title: String, vararg slides: Slide) = Deck(title, slides.toList())
    }
}

/** What an engine produced. */
data class DocumentResult(
    val bytes: ByteArray,
    /** e.g. application/pdf - for the share/open intent. */
    val mimeType: String,
    /** A suggestion; the host may override. */
    val suggestedFileName: String,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is DocumentResult) return false
        return bytes.contentEquals(other.bytes) &&
            mimeType == other.mimeType &&
            suggestedFileName == other.suggestedFileName
    }

    override fun hashCode(): Int =
        (bytes.contentHashCode() * 31 + mimeType.hashCode()) * 31 + suggestedFileName.hashCode()
}

/** Renders a [Deck] to a PDF (one slide per page), fully offline and on-device. */
interface DeckEngine {
    suspend fun render(deck: Deck): DocumentResult

    /**
     * Template ids this engine can render, for a host to offer as a choice.
     * The FIRST entry is the default when none is requested.
     */
    val availableTemplates: List<String>
}

/**
 * A self-contained example [Deck] for demos and smoke tests.
 *
 * Two jobs, per the C#: a smoke test that needs no model and no network, and
 * living documentation - it exercises every field of the model (title slide,
 * heading-only divider, bullets, a per-slide footer, speaker notes) so the
 * shape is obvious at a glance.
 */
object SampleDeck {

    /** The canonical sample deck. Deterministic - same input, same PDF. */
    fun create() = Deck(
        title = "CircleAI Offline Presentations",
        slides = listOf(
            Slide.of(
                "What This Is",
                "Turn an outline into a slide deck - fully offline",
                "One slide per page, exported as a single PDF",
                "No cloud, no Google, no network at generation time",
            ),
            Slide.of(
                "How It Works",
                "You supply a Deck: a title and an ordered list of slides",
                "Each slide is a heading plus a few bullet points",
                "PDFsharp-MigraDoc lays it out in landscape A4",
                "It renders with the same embedded font as the CV engine",
            ),
            // A heading-only section divider - empty bullets is VALID.
            Slide.of("Part Two - Why It Fits The Phone"),
            Slide(
                title = "Runs On A Low-End Phone",
                bullets = listOf(
                    "Pure-managed: no native library to bundle or load",
                    "Reuses CircleAI.Documents; adds no new dependency",
                    "Deterministic: the same Deck always makes the same PDF",
                ),
                footer = "CircleAI - Presentations - v1",
                notes = "Speaker note: stress that this works on a Huawei P30 Lite with no Play Services.",
            ),
        ),
        subtitle = "Outline in, PDF out",
        author = "The Geek Network",
        footer = "CircleAI - Presentations",
    )
}
