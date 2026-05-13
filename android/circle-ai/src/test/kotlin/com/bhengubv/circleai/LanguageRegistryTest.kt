package com.bhengubv.circleai

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.*
import org.junit.Test
import java.io.File

class LanguageRegistryTest {
    private val json = Json { ignoreUnknownKeys = true }

    private fun locateFixture(name: String): File {
        val absolute = File("C:\\Dev\\Solutions\\com.bhengubv\\CircleAI\\fixtures\\$name")
        if (absolute.exists()) return absolute
        val relative = File("../../fixtures/$name")
        if (relative.exists()) return relative
        error("Cannot locate fixture $name")
    }

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
        val root = json.parseToJsonElement(locateFixture("language_tags.json").readText()).jsonObject
        val fixtureLangs = root["languages"]!!.jsonArray
        assertEquals(20, fixtureLangs.size)
        for (lang in fixtureLangs) {
            val bcpTag = lang.jsonObject["bcpTag"]!!.jsonPrimitive.content
            val found = KnownLanguages.findByBcpTag(bcpTag)
            assertNotNull("Missing: $bcpTag", found)
        }
    }
}
