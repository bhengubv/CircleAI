package com.bhengubv.circleai

import com.fasterxml.jackson.databind.ObjectMapper
import org.junit.Assert.*
import org.junit.Test
import java.io.File

class LanguageRegistryTest {
    @Test fun countIs20() = assertEquals(20, KnownLanguages.count())

    @Test fun firstIsZulu() {
        val first = KnownLanguages.getAll().first()
        assertEquals("zu", first.bcpTag)
        assertEquals("Zulu", first.englishName)
        assertEquals(WritingSystem.LATIN, first.writingSystem)
        assertFalse(first.isRtl)
    }

    @Test fun arabicIsRtl() {
        val ar = KnownLanguages.findByBcpTag("ar")
        assertNotNull(ar)
        assertTrue(ar!!.isRtl)
        assertEquals(WritingSystem.ARABIC, ar.writingSystem)
    }

    @Test fun onlyArabicIsRtl() {
        val rtl = KnownLanguages.getAll().filter { it.isRtl }
        assertEquals(1, rtl.size)
        assertEquals("ar", rtl[0].bcpTag)
    }

    @Test fun hindiIsDevanagari() {
        val hi = KnownLanguages.findByBcpTag("hi")
        assertEquals(WritingSystem.DEVANAGARI, hi!!.writingSystem)
    }

    @Test fun lastIsHindi() {
        val last = KnownLanguages.getAll().last()
        assertEquals("hi", last.bcpTag)
    }

    @Test fun fixtureMatchesRegistry() {
        val mapper = ObjectMapper()
        val fixture = mapper.readTree(File("C:\\Dev\\Solutions\\com.bhengubv\\CircleAI\\fixtures\\language_tags.json"))
        val fixtureLangs = fixture["languages"]
        assertEquals(20, fixtureLangs.size())
        for (lang in fixtureLangs) {
            val found = KnownLanguages.findByBcpTag(lang["bcpTag"].asText())
            assertNotNull("Missing: ${lang["bcpTag"].asText()}", found)
        }
    }
}
