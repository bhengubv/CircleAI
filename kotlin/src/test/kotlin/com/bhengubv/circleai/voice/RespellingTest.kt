package com.bhengubv.circleai.voice

import java.time.Instant
import java.time.temporal.ChronoUnit
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class LoanwordRespellerTest {

    @Test
    fun anEnglishWordWithASettledZuluSpellingUsesIt() {
        assertEquals("inthanethi", LoanwordRespeller.respell("internet", "zu"))
        assertEquals("khompiyutha", LoanwordRespeller.respell("computer", "zu"))
        assertEquals("bhange", LoanwordRespeller.respell("bank", "zu"))
    }

    @Test
    fun lookupIsCaseInsensitive() {
        assertEquals("wotsapha", LoanwordRespeller.respell("WhatsApp", "zu"))
        assertEquals("wotsapha", LoanwordRespeller.respell("WHATSAPP", "zu"))
    }

    @Test
    fun aLanguageThatDoesNotNeedRespellingGetsNONE() {
        // An English voice saying an English word is already correct, and a
        // table lookup that fired anyway would make it worse.
        assertNull(LoanwordRespeller.respell("internet", "en"))
        assertNull(LoanwordRespeller.respell("internet", "fr"))
        assertNull(LoanwordRespeller.respell("internet", ""))
        assertTrue(LoanwordRespeller.table("en").isEmpty())
    }

    @Test
    fun theWholeNguniAndSothoTswanaFamilyShareTheOneTable() {
        // They share the sound system this respelling targets.
        for (tag in listOf("zu", "zul", "xh", "xho", "ss", "ssw", "nr", "nbl", "st", "sot", "nso", "tn", "tsn")) {
            assertTrue(LoanwordRespeller.isNguniOrSotho(tag), tag + " should be covered")
            assertNotNull(LoanwordRespeller.respell("phone", tag))
        }
        for (tag in listOf("en", "af", "ts", "ve", "sw", "am")) {
            assertFalse(LoanwordRespeller.isNguniOrSotho(tag), tag + " should not be covered")
        }
    }

    @Test
    fun languageTagsAreMatchedCaseInsensitively() {
        assertTrue(LoanwordRespeller.isNguniOrSotho("ZU"))
        assertEquals("foni", LoanwordRespeller.respell("phone", "ZU"))
    }

    @Test
    fun anAttestedSpellingIsDistinguishedFromOneWeAreProposing() {
        // An attested form ships silently; a proposed one is a suggestion
        // somebody may want to correct, and the caller has to be able to tell.
        assertEquals(RespellingSource.ATTESTED, LoanwordRespeller.source("internet"))
        assertEquals(RespellingSource.ATTESTED, LoanwordRespeller.source("doctor"))
        assertEquals(RespellingSource.PROPOSED, LoanwordRespeller.source("whatsapp"))
        assertEquals(RespellingSource.PROPOSED, LoanwordRespeller.source("circleai"))
        assertNull(LoanwordRespeller.source("aardvark"))
    }

    @Test
    fun aBlankWordIsNotLookedUp() {
        assertNull(LoanwordRespeller.respell("", "zu"))
        assertNull(LoanwordRespeller.respell("   ", "zu"))
    }

    @Test
    fun anUnknownWordIsNullSoTheCallerCanDeriveOrSpellItOut() {
        assertNull(LoanwordRespeller.respell("aardvark", "zu"))
        assertTrue(LoanwordRespeller.known.contains("internet"))
    }
}

class NguniRespellerTest {

    @Test
    fun aConsonantCLUSTERgetsAVowelPushedBetweenItsParts() {
        // Nguni syllables are open. This is the rule that does all the work.
        // s-t-a: the s and t cannot sit together.
        assertEquals("setha", NguniRespeller.fromIpa("sta"))
    }

    @Test
    fun aWordFinalConsonantGetsAVowelAfterIt() {
        assertEquals("bathe", NguniRespeller.fromIpa("bat"))
    }

    @Test
    fun anOpenSyllableIsLeftAlone() {
        assertEquals("ba", NguniRespeller.fromIpa("ba"))
        assertEquals("badi", NguniRespeller.fromIpa("badi"))
    }

    @Test
    fun aDIPHTHONGisOneUnitNotTwoVowels() {
        // Longest match first. Read as two, "aɪ" becomes "ai" and the word
        // gains a syllable that is not in it.
        assertEquals("ayi", NguniRespeller.fromIpa("aɪ"))
        assertEquals("awu", NguniRespeller.fromIpa("aʊ"))
        assertEquals("eyi", NguniRespeller.fromIpa("eɪ"))
    }

    @Test
    fun anAFFRICATEisOneConsonantNotTwo() {
        assertEquals("tshe", NguniRespeller.fromIpa("tʃ"))
        assertEquals("je", NguniRespeller.fromIpa("dʒ"))
    }

    @Test
    fun aTIEBARdoesNotSwallowTheConsonantItJoins() {
        // The tie bar is skipped as a segment, but the letters on either side
        // still have to be matched. Treating the bar as fusing with the letter
        // before it loses that consonant entirely - the symptom is a word
        // silently missing a sound.
        assertEquals("theshe", NguniRespeller.fromIpa("t͡ʃ".let { "t" + Char(0x0361) + "ʃ" }))
    }

    @Test
    fun aLENGTHmarkSelectsTheLongVowelRatherThanBeingIgnored() {
        assertEquals("i", NguniRespeller.fromIpa("iː"))
        assertEquals("a", NguniRespeller.fromIpa("ɑː"))
        assertEquals("o", NguniRespeller.fromIpa("ɔː"))
    }

    @Test
    fun stressMarksAndSyllableDotsCarryNoSegment() {
        assertEquals(NguniRespeller.fromIpa("bata"), NguniRespeller.fromIpa("ˈba.ta"))
        assertEquals(NguniRespeller.fromIpa("bata"), NguniRespeller.fromIpa("ˌba ta"))
    }

    @Test
    fun aSymbolThisDoesNotModelContributesNOTHINGratherThanBreakingTheWord() {
        // A click or a tone letter must not take the rest of the word with it.
        assertEquals("bathe", NguniRespeller.fromIpa("b" + "ǃ" + "at"))
    }

    @Test
    fun nothingInMeansNothingOut() {
        assertEquals("", NguniRespeller.fromIpa(null))
        assertEquals("", NguniRespeller.fromIpa(""))
        assertEquals("", NguniRespeller.fromIpa("   "))
    }

    @Test
    fun theParserReportsBothTheOrthographyAndWhetherItIsAVowel() {
        val units = NguniRespeller.parse("bat")
        assertEquals(3, units.size)
        assertEquals(NguniRespeller.Unit("b", false), units[0])
        assertEquals(NguniRespeller.Unit("a", true), units[1])
        assertEquals(NguniRespeller.Unit("th", false), units[2])
    }

    @Test
    fun aRealEnglishWordComesOutSayable() {
        // "whatsapp" as an English IPA transcription, respelt:
        //   w o th [e] s a ph [e]
        // The two bracketed vowels are epenthetic - one splitting the ts
        // cluster, one after the final p. Every syllable that comes out is
        // consonant-vowel, which is the point.
        assertEquals("wothesaphe", NguniRespeller.fromIpa("wɒtsæp"))
    }
}

class PersonalRespellingsTest {

    private val at = Instant.ofEpochSecond(1_782_896_400L)

    @Test
    fun whatSomebodyTypesIsWhatComesBack() {
        val p = PersonalRespellings()
        assertTrue(p.learn("Mokoena", "mokwena", at))
        assertEquals("mokwena", p.respell("Mokoena"))
    }

    @Test
    fun lookupIsCaseInsensitiveButTheOriginalSpellingIsKept() {
        val p = PersonalRespellings()
        p.learn("Mokoena", "mokwena", at)
        assertEquals("mokwena", p.respell("mokoena"))
        assertEquals("mokwena", p.respell("MOKOENA"))
        assertEquals("Mokoena", p.all.first().word)
    }

    @Test
    fun surroundingSpaceIsTrimmedOffBothSides() {
        val p = PersonalRespellings()
        p.learn("  Mokoena  ", "  mokwena  ", at)
        assertEquals("mokwena", p.respell("Mokoena"))
    }

    @Test
    fun aBlankWordOrRespellingIsREFUSEDratherThanStored() {
        // An empty correction would shadow the shipped table with nothing.
        val p = PersonalRespellings()
        assertFalse(p.learn("", "x", at))
        assertFalse(p.learn("  ", "x", at))
        assertFalse(p.learn("word", "", at))
        assertFalse(p.learn("word", "   ", at))
        assertTrue(p.all.isEmpty())
    }

    @Test
    fun learningTheSameWordTwiceReplacesIt() {
        val p = PersonalRespellings()
        p.learn("Mokoena", "mokwena", at)
        p.learn("Mokoena", "mukhwena", at.plus(1, ChronoUnit.DAYS))
        assertEquals("mukhwena", p.respell("Mokoena"))
        assertEquals(1, p.all.size)
    }

    @Test
    fun forgettingReportsWhetherThereWasAnythingToForget() {
        val p = PersonalRespellings()
        p.learn("Mokoena", "mokwena", at)
        assertTrue(p.forget("MOKOENA"))
        assertFalse(p.forget("Mokoena"))
        assertNull(p.respell("Mokoena"))
    }

    @Test
    fun theListingIsAlphabeticalSoItIsReadable() {
        val p = PersonalRespellings()
        p.learn("Zulu", "zulu", at)
        p.learn("Amara", "amara", at)
        p.learn("Mokoena", "mokwena", at)
        assertEquals(listOf("Amara", "Mokoena", "Zulu"), p.all.map { it.word })
    }

    @Test
    fun stateCanBeCapturedAndRestoredWordForWord() {
        val p = PersonalRespellings()
        p.learn("Mokoena", "mokwena", at)
        p.learn("Dlamini", "dlamini", at)
        val restored = LearningState.restore(LearningState.capture(p))
        assertEquals("mokwena", restored.respell("mokoena"))
        assertEquals(2, restored.all.size)
        assertEquals(at, restored.all.first().learnedAt)
    }
}

class RespellerTest {

    private class FixedIpa(private val ipa: String) : IPhonemizer {
        override fun phonemize(text: String): List<String> = PiperPhonemes.split(ipa)
    }

    @Test
    fun whatThePersonCorrectedOUTRANKSeverythingElse() = run {
        // They know how their own words are said and this code does not. This
        // is the entire reason the order exists.
        val personal = PersonalRespellings().apply { learn("internet", "MY WAY") }
        val r = Respeller("zu", personal, FixedIpa("bat"))
        assertEquals("MY WAY", r.respelling("internet"))
    }

    @Test
    fun theAttestedTableBeatsAnythingDerived() {
        val r = Respeller("zu", null, FixedIpa("bat"))
        assertEquals("inthanethi", r.respelling("internet"))
    }

    @Test
    fun anUnknownWordIsDerivedFromItsEnglishIpa() {
        val r = Respeller("zu", null, FixedIpa("sta"))
        assertEquals("setha", r.respelling("aardvark"))
    }

    @Test
    fun withNoPhonemizerAnUnknownWordIsNULLsoTheCallerCanSpellItOut() {
        // Better than mispronouncing it confidently.
        val r = Respeller("zu", null, null)
        assertNull(r.respelling("aardvark"))
    }

    @Test
    fun nothingIsDerivedForALanguageThisDoesNotModel() {
        val r = Respeller("en", null, FixedIpa("sta"))
        assertNull(r.respelling("aardvark"))
        assertNull(r.respelling("internet"))
    }

    @Test
    fun aWordThatDerivesToNothingIsNullRatherThanEmpty() {
        // An IPA string of symbols this does not model produces no units at all.
        val r = Respeller("zu", null, FixedIpa("ǃǁǂ"))
        assertNull(r.respelling("aardvark"))
    }

    @Test
    fun aBlankWordIsNull() {
        val r = Respeller("zu", null, FixedIpa("sta"))
        assertNull(r.respelling(""))
        assertNull(r.respelling("   "))
    }

    @Test
    fun aDerivationIsTRACEDwithBothTheInputAndTheResult() {
        // The one place a wrong pronunciation can be diagnosed after the fact.
        val lines = mutableListOf<String>()
        VoiceTrace.setSink { lines.add(it) }
        try {
            Respeller("zu", null, FixedIpa("sta")).respelling("aardvark")
        } finally {
            VoiceTrace.setSink(null)
        }
        assertEquals(1, lines.size)
        assertContains(lines[0], "aardvark")
        assertContains(lines[0], "setha")
        assertContains(lines[0], "sta")
    }

    @Test
    fun tracingIsOffUntilASinkIsAttachedAndCannotThrowIntoTheCaller() {
        assertFalse(VoiceTrace.enabled)
        VoiceTrace.write("nobody is listening")
        VoiceTrace.setSink { }
        assertTrue(VoiceTrace.enabled)
        VoiceTrace.setSink(null)
        assertFalse(VoiceTrace.enabled)
    }
}

class PassthroughPhonemizerTest {

    @Test
    fun theTextIsAlreadyPhonemesAndIsSplitPerCodePoint() {
        assertEquals(listOf("b", "a", "t"), PassthroughPhonemizer().phonemize("bat"))
    }

    @Test
    fun whitespaceIsNotAPhoneme() {
        assertEquals(listOf("b", "a"), PassthroughPhonemizer().phonemize("b a"))
        assertEquals(listOf("b", "a"), PassthroughPhonemizer().phonemize("b\ta\n"))
    }

    @Test
    fun emptyTextIsAnEmptyList() {
        assertTrue(PassthroughPhonemizer().phonemize("").isEmpty())
    }
}
