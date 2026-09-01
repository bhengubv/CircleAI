package com.bhengubv.circleai.presentations

import kotlinx.serialization.json.Json
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** The deck model and its JSON shape. */
class PresentationsTest {

    private val json = Json { ignoreUnknownKeys = true }

    @Test fun `a slide from a title and inline bullets`() {
        val s = Slide.of("Heading", "one", "two")
        assertEquals("Heading", s.title)
        assertEquals(listOf("one", "two"), s.bullets)
        assertNull(s.footer)
        assertNull(s.notes)
    }

    // A heading-only divider is a legitimate and common deck element.
    @Test fun `a slide with no bullets is valid`() {
        val s = Slide.of("Part Two")
        assertTrue(s.bullets.isEmpty())
    }

    // A model TOLD that a divider slide has no bullets writes exactly this.
    // Treating a missing array as an error made a whole deck fail to parse.
    @Test fun `a deck decodes from the json a model would plausibly emit`() {
        val text = """{"title":"D","slides":[{"title":"Part Two"},{"title":"B","bullets":["x"]}]}"""
        val deck = json.decodeFromString<Deck>(text)
        assertEquals(2, deck.slides.size)
        assertTrue(deck.slides[0].bullets.isEmpty())
        assertEquals(listOf("x"), deck.slides[1].bullets)
    }

    @Test fun `a deck with no slides array decodes as empty`() {
        val deck = json.decodeFromString<Deck>("""{"title":"Just a title"}""")
        assertEquals("Just a title", deck.title)
        assertTrue(deck.slides.isEmpty())
        assertNull(deck.subtitle)
    }

    @Test fun `a deck round trips through json`() {
        val original = SampleDeck.create()
        val back = json.decodeFromString<Deck>(json.encodeToString(Deck.serializer(), original))
        assertEquals(original, back)
    }

    // The override rule is a property of the MODEL, so every template does not
    // have to re-implement it - one of them wrongly.
    @Test fun `a slide footer beats the deck footer`() {
        val deck = Deck(
            title = "d",
            slides = listOf(Slide("a"), Slide("b", footer = "own")),
            footer = "deck-wide",
        )
        assertEquals("deck-wide", deck.footerFor(deck.slides[0]))
        assertEquals("own", deck.footerFor(deck.slides[1]))
    }

    @Test fun `a deck with no footer gives none to its slides`() {
        val deck = Deck(title = "d", slides = listOf(Slide("a")))
        assertNull(deck.footerFor(deck.slides[0]))
    }

    // The sample is living documentation: it must exercise every field.
    @Test fun `the sample deck exercises the whole model`() {
        val d = SampleDeck.create()
        assertTrue(d.title.isNotEmpty())
        assertTrue(d.subtitle!!.isNotEmpty())
        assertTrue(d.author!!.isNotEmpty())
        assertTrue(d.footer!!.isNotEmpty())
        assertTrue(d.slides.size >= 4)
        assertTrue(d.slides.any { it.bullets.isEmpty() }, "a heading-only divider")
        assertTrue(d.slides.any { it.footer != null }, "a per-slide footer override")
        assertTrue(d.slides.any { it.notes != null }, "speaker notes")
    }

    // Deterministic: the same input must give the same deck.
    @Test fun `the sample deck is deterministic`() {
        assertEquals(SampleDeck.create(), SampleDeck.create())
    }

    @Test fun `a deck from inline slides`() {
        val d = Deck.of("t", Slide.of("a"), Slide.of("b"))
        assertEquals(2, d.slides.size)
    }

    @Test fun `a document result compares by content not reference`() {
        val a = DocumentResult(byteArrayOf(1, 2, 3), "application/pdf", "d.pdf")
        val b = DocumentResult(byteArrayOf(1, 2, 3), "application/pdf", "d.pdf")
        assertEquals(a, b)
        assertEquals(a.hashCode(), b.hashCode())
    }
}
