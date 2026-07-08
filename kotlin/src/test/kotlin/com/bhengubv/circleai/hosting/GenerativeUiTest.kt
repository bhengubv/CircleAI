// GenerativeUiTest.kt
//
// Verifies JsonRenderParser against the C# reference: valid parse with children +
// managed property coercion, strict rejection of unknown kinds / disallowed
// properties / disallowed children, lenient fallback to a debug textBlock, the
// catalog prompt description, and the recording renderer.

package com.bhengubv.circleai.hosting

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

class GenerativeUiTest {

    @Test
    fun `parses a card with children and coerces property types`() {
        val json = """
          { "kind": "card",
            "properties": { "title": "Hi", "caption": "sub" },
            "children": [
              { "kind": "textBlock", "properties": { "text": "body", "markdown": true } }
            ]
          }
        """.trimIndent()
        val c = JsonRenderParser.parse(json, UiCatalogs.Default)
        assertEquals("card", c.kind)
        assertEquals("Hi", c.properties["title"])
        assertNotNull(c.children)
        assertEquals("textBlock", c.children!![0].kind)
        assertEquals(true, c.children!![0].properties["markdown"])
    }

    @Test
    fun `strict rejects unknown kind`() {
        val json = """{ "kind": "spaceship", "properties": {} }"""
        assertFailsWith<IllegalStateException> { JsonRenderParser.parse(json, UiCatalogs.Default) }
    }

    @Test
    fun `lenient turns unknown kind into a debug textBlock`() {
        val json = """{ "kind": "spaceship", "properties": {} }"""
        val c = JsonRenderParser.parse(json, UiCatalogs.Default, strict = false)
        assertEquals("textBlock", c.kind)
        assertTrue((c.properties["text"] as String).contains("spaceship"))
    }

    @Test
    fun `strict rejects a disallowed property`() {
        val json = """{ "kind": "button", "properties": { "label": "Go", "danger": true } }"""
        assertFailsWith<IllegalStateException> { JsonRenderParser.parse(json, UiCatalogs.Default) }
    }

    @Test
    fun `strict rejects children on a non-container`() {
        val json = """
          { "kind": "button", "properties": { "label": "Go", "action": "go" },
            "children": [ { "kind": "textBlock", "properties": { "text": "x" } } ] }
        """.trimIndent()
        assertFailsWith<IllegalStateException> { JsonRenderParser.parse(json, UiCatalogs.Default) }
    }

    @Test
    fun `missing kind throws`() {
        assertFailsWith<IllegalStateException> { JsonRenderParser.parse("""{ "properties": {} }""", UiCatalogs.Default) }
    }

    @Test
    fun `describe catalog lists kinds and properties`() {
        val text = JsonRenderParser.describeCatalogForPrompt(UiCatalogs.Default)
        assertTrue(text.contains("card"))
        assertTrue(text.contains("children: array of components"))
        assertTrue(text.contains("- label: string"))
    }

    @Test
    fun `recording renderer captures last component`() = runTest {
        val renderer = RecordingGenerativeUIRenderer()
        val c = UiComponent("textBlock", mapOf("text" to "hi"))
        renderer.renderAsync(c)
        assertEquals(c, renderer.lastRendered)
        assertEquals(1, renderer.renderCount)
    }
}
