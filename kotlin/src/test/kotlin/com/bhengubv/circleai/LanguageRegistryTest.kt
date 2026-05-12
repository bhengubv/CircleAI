// LanguageRegistryTest.kt
//
// Verifies KnownLanguages against the canonical fixture (language_tags.json).
// Assertions:
//   - KnownLanguages.All.size == 20
//   - Every tag in the fixture matches the declared Kotlin object property
//   - WritingSystem enum, isRtl flag, and isoRegion are correct for all entries
//   - Arabic is the only RTL language in the list

package com.bhengubv.circleai

import com.bhengubv.circleai.languages.KnownLanguages
import com.bhengubv.circleai.languages.WritingSystem
import com.fasterxml.jackson.databind.ObjectMapper
import org.junit.jupiter.api.Test
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

class LanguageRegistryTest {

    private val mapper = ObjectMapper()

    private fun locateFixture(name: String): File {
        val absolute = File("C:\\Dev\\Solutions\\com.bhengubv\\CircleAI\\fixtures\\$name")
        if (absolute.exists()) return absolute
        val relative = File("../../fixtures/$name")
        if (relative.exists()) return relative
        error("Cannot locate fixture $name")
    }

    @Test
    fun `KnownLanguages All has exactly 20 entries`() {
        assertEquals(20, KnownLanguages.All.size,
            "KnownLanguages.All must have exactly 20 entries, got ${KnownLanguages.All.size}")
    }

    @Test
    fun `All entries are unique by bcpTag`() {
        val tags = KnownLanguages.All.map { it.bcpTag }
        assertEquals(tags.size, tags.toSet().size, "Duplicate BCP tags found in KnownLanguages.All")
    }

    @Test
    fun `fixture matches KnownLanguages All in order`() {
        val root = mapper.readTree(locateFixture("language_tags.json"))
        val fixtureLanguages = root["languages"].toList()

        assertEquals(20, fixtureLanguages.size, "Fixture must have 20 languages")

        fixtureLanguages.forEachIndexed { index, node ->
            val tag = KnownLanguages.All[index]
            val bcpTag = node["bcpTag"].asText()
            val englishName = node["englishName"].asText()
            val nativeName = node["nativeName"].asText()
            val writingSystem = node["writingSystem"].asText()
            val isRtl = node["isRtl"].asBoolean()
            val primaryRegion = node["primaryRegion"].asText()

            assertEquals(bcpTag, tag.bcpTag,
                "[$index] bcpTag mismatch: fixture=$bcpTag, actual=${tag.bcpTag}")
            assertEquals(englishName, tag.displayName,
                "[$index] displayName mismatch for $bcpTag")
            assertEquals(nativeName, tag.nativeName,
                "[$index] nativeName mismatch for $bcpTag")
            assertEquals(writingSystem, tag.script.name,
                "[$index] writingSystem mismatch for $bcpTag")
            assertEquals(isRtl, tag.isRtl,
                "[$index] isRtl mismatch for $bcpTag")
            assertEquals(primaryRegion, tag.isoRegion,
                "[$index] isoRegion mismatch for $bcpTag")
        }
    }

    @Test
    fun `Arabic is the only RTL language`() {
        val rtlLanguages = KnownLanguages.All.filter { it.isRtl }
        assertEquals(1, rtlLanguages.size, "Expected exactly 1 RTL language, got ${rtlLanguages.size}")
        assertEquals("ar", rtlLanguages[0].bcpTag, "Expected Arabic (ar) to be the RTL language")
    }

    @Test
    fun `African languages count is 13`() {
        // First 13 entries in KnownLanguages.All are African
        val africanRegions = setOf("ZA", "KE", "NG", "ET", "SO")
        val african = KnownLanguages.All.filter { it.isoRegion in africanRegions }
        assertEquals(13, african.size, "Expected 13 African languages, got ${african.size}")
    }

    @Test
    fun `IsiZulu has correct properties`() {
        val zu = KnownLanguages.IsiZulu
        assertEquals("zu", zu.bcpTag)
        assertEquals("isiZulu", zu.displayName)
        assertEquals("isiZulu", zu.nativeName)
        assertEquals(WritingSystem.Latin, zu.script)
        assertFalse(zu.isRtl)
        assertEquals("ZA", zu.isoRegion)
    }

    @Test
    fun `Amharic uses Ethiopic script`() {
        val am = KnownLanguages.Amharic
        assertEquals("am", am.bcpTag)
        assertEquals(WritingSystem.Ethiopic, am.script)
        assertFalse(am.isRtl)
        assertEquals("ET", am.isoRegion)
    }

    @Test
    fun `Arabic uses Arabic script and is RTL`() {
        val ar = KnownLanguages.Arabic
        assertEquals("ar", ar.bcpTag)
        assertEquals(WritingSystem.Arabic, ar.script)
        assertTrue(ar.isRtl)
        assertEquals("SA", ar.isoRegion)
    }

    @Test
    fun `Mandarin uses Han script`() {
        val zh = KnownLanguages.Mandarin
        assertEquals("zh", zh.bcpTag)
        assertEquals(WritingSystem.Han, zh.script)
        assertFalse(zh.isRtl)
        assertEquals("CN", zh.isoRegion)
    }

    @Test
    fun `Hindi uses Devanagari script`() {
        val hi = KnownLanguages.Hindi
        assertEquals("hi", hi.bcpTag)
        assertEquals(WritingSystem.Devanagari, hi.script)
        assertFalse(hi.isRtl)
        assertEquals("IN", hi.isoRegion)
    }

    @Test
    fun `Sepedi has bcp tag nso`() {
        val nso = KnownLanguages.Sepedi
        assertEquals("nso", nso.bcpTag)
        assertEquals("Sepedi", nso.displayName)
        assertEquals("ZA", nso.isoRegion)
    }

    @Test
    fun `LanguageTag Unknown sentinel has bcp tag und`() {
        val unknown = com.bhengubv.circleai.languages.LanguageTag.Unknown
        assertEquals("und", unknown.bcpTag)
        assertEquals("Unknown", unknown.displayName)
        assertFalse(unknown.isRtl)
    }

    @Test
    fun `fixture assertions block validates counts`() {
        val root = mapper.readTree(locateFixture("language_tags.json"))
        val assertions = root["assertions"]
        assertEquals(20, assertions["totalCount"].intValue())
        val rtlTags = assertions["rtlLanguages"].map { it.asText() }
        assertEquals(listOf("ar"), rtlTags)
        assertEquals(13, assertions["africanLanguageCount"].intValue())
    }
}
