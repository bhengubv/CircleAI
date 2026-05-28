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
        val tags = KnownLanguages.all.map { it.bcpTag }
        assertEquals(listOf(
            "zu","st","af","sw","ha","am","yo","ig","xh","nso",
            "tn","so","om","ar","en","pt","fr","es","zh","hi"
        ), tags)
    }

    @Test fun onlyArabicIsRtl() {
        val rtlLangs = KnownLanguages.all.filter { it.isRtl }.map { it.bcpTag }
        assertEquals(listOf("ar"), rtlLangs)
    }

    @Test fun africaCount() {
        val african = listOf("zu","st","af","sw","ha","am","yo","ig","xh","nso","tn","so","om")
        assertEquals(13, KnownLanguages.all.count { it.bcpTag in african })
    }

    @Test fun scripts() {
        assertEquals(WritingSystem.Ethiopic,   KnownLanguages.Amharic.script)
        assertEquals(WritingSystem.Arabic,     KnownLanguages.Arabic.script)
        assertEquals(WritingSystem.Han,        KnownLanguages.Mandarin.script)
        assertEquals(WritingSystem.Devanagari, KnownLanguages.Hindi.script)
        assertEquals(WritingSystem.Latin,      KnownLanguages.English.script)
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

    @Test fun isoRegions() {
        assertEquals("ZA", KnownLanguages.IsiZulu.isoRegion)
        assertEquals("KE", KnownLanguages.Swahili.isoRegion)
        assertEquals("NG", KnownLanguages.Hausa.isoRegion)
        assertEquals("SA", KnownLanguages.Arabic.isoRegion)
        assertEquals("GB", KnownLanguages.English.isoRegion)
        assertEquals("CN", KnownLanguages.Mandarin.isoRegion)
        assertEquals("IN", KnownLanguages.Hindi.isoRegion)
    }

    @Test fun displayNames() {
        assertEquals("isiZulu",    KnownLanguages.IsiZulu.displayName)
        assertEquals("Swahili",    KnownLanguages.Swahili.displayName)
        assertEquals("Amharic",    KnownLanguages.Amharic.displayName)
        assertEquals("Arabic",     KnownLanguages.Arabic.displayName)
        assertEquals("English",    KnownLanguages.English.displayName)
        assertEquals("Portuguese", KnownLanguages.Portuguese.displayName)
        assertEquals("French",     KnownLanguages.French.displayName)
        assertEquals("Spanish",    KnownLanguages.Spanish.displayName)
        assertEquals("Mandarin",   KnownLanguages.Mandarin.displayName)
        assertEquals("Hindi",      KnownLanguages.Hindi.displayName)
    }
}
