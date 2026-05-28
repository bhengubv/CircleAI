package com.bhengubv.circleai.android

import com.bhengubv.circleai.android.languages.KnownLanguages
import com.bhengubv.circleai.android.languages.WritingSystem
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Cross-language fixture tests for KnownLanguages — fixtures/language_tags.json */
class LanguageRegistryTest {

    @Test fun totalCount() = assertEquals("All must be 20", 20, KnownLanguages.all.size)

    @Test fun declarationOrder() {
        val tags = KnownLanguages.all.map { it.bcp47Tag }
        assertEquals(listOf(
            "zu","st","af","sw","ha","am","yo","ig","xh","nso",
            "tn","so","om","ar","en","pt","fr","es","zh","hi"
        ), tags)
    }

    @Test fun onlyArabicIsRtl() {
        val rtlLangs = KnownLanguages.all.filter { it.isRtl }.map { it.bcp47Tag }
        assertEquals(listOf("ar"), rtlLangs)
    }

    @Test fun africaCount() {
        val african = listOf("zu","st","af","sw","ha","am","yo","ig","xh","nso","tn","so","om")
        assertEquals(13, KnownLanguages.all.count { it.bcp47Tag in african })
    }

    @Test fun writingSystems() {
        assertEquals(WritingSystem.Ethiopic,   KnownLanguages.Amharic.writingSystem)
        assertEquals(WritingSystem.Arabic,     KnownLanguages.Arabic.writingSystem)
        assertEquals(WritingSystem.Han,        KnownLanguages.Mandarin.writingSystem)
        assertEquals(WritingSystem.Devanagari, KnownLanguages.Hindi.writingSystem)
        assertEquals(WritingSystem.Latin,      KnownLanguages.English.writingSystem)
    }

    @Test fun nativeNames() {
        assertEquals("isiZulu",      KnownLanguages.IsiZulu.nativeName)
        assertEquals("العربية",      KnownLanguages.Arabic.nativeName)
        assertEquals("አማርኛ",         KnownLanguages.Amharic.nativeName)
        assertEquals("中文",          KnownLanguages.Mandarin.nativeName)
        assertEquals("हिन्दी",        KnownLanguages.Hindi.nativeName)
        assertEquals("Kiswahili",    KnownLanguages.Swahili.nativeName)
        assertEquals("Afaan Oromoo", KnownLanguages.Oromo.nativeName)
    }

    @Test fun primaryRegions() {
        assertEquals("ZA", KnownLanguages.IsiZulu.primaryRegion)
        assertEquals("KE", KnownLanguages.Swahili.primaryRegion)
        assertEquals("NG", KnownLanguages.Hausa.primaryRegion)
        assertEquals("SA", KnownLanguages.Arabic.primaryRegion)
        assertEquals("GB", KnownLanguages.English.primaryRegion)
        assertEquals("CN", KnownLanguages.Mandarin.primaryRegion)
        assertEquals("IN", KnownLanguages.Hindi.primaryRegion)
    }

    @Test fun englishNames() {
        assertEquals("isiZulu",    KnownLanguages.IsiZulu.englishName)
        assertEquals("Swahili",    KnownLanguages.Swahili.englishName)
        assertEquals("Amharic",    KnownLanguages.Amharic.englishName)
        assertEquals("Arabic",     KnownLanguages.Arabic.englishName)
        assertEquals("English",    KnownLanguages.English.englishName)
        assertEquals("Portuguese", KnownLanguages.Portuguese.englishName)
        assertEquals("French",     KnownLanguages.French.englishName)
        assertEquals("Spanish",    KnownLanguages.Spanish.englishName)
        assertEquals("Mandarin",   KnownLanguages.Mandarin.englishName)
        assertEquals("Hindi",      KnownLanguages.Hindi.englishName)
    }
}
