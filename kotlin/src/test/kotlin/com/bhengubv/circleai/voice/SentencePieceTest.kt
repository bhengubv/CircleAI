package com.bhengubv.circleai.voice

import java.io.ByteArrayOutputStream
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Builds a real SentencePiece ModelProto so the reader is tested on real wire bytes. */
object SpmFixture {

    private fun varint(v: Long): ByteArray {
        val out = ByteArrayOutputStream()
        var x = v
        while (true) {
            val b = (x and 0x7F).toInt()
            x = x ushr 7
            if (x == 0L) { out.write(b); break }
            out.write(b or 0x80)
        }
        return out.toByteArray()
    }

    private fun piece(text: String, score: Float, kind: Int): ByteArray {
        val out = ByteArrayOutputStream()
        val utf8 = text.toByteArray(Charsets.UTF_8)
        out.write(0x0A) // field 1, length-delimited
        out.write(varint(utf8.size.toLong()))
        out.write(utf8)

        out.write(0x15) // field 2, fixed32
        val bits = score.toRawBits()
        out.write(bits and 0xFF)
        out.write((bits ushr 8) and 0xFF)
        out.write((bits ushr 16) and 0xFF)
        out.write((bits ushr 24) and 0xFF)

        out.write(0x18) // field 3, varint
        out.write(varint(kind.toLong()))
        return out.toByteArray()
    }

    /** A model containing exactly these pieces, all NORMAL unless a kind is given. */
    fun model(vararg entries: Triple<String, Float, Int>): ByteArray {
        val out = ByteArrayOutputStream()
        for ((text, score, kind) in entries) {
            val body = piece(text, score, kind)
            out.write(0x0A) // ModelProto field 1, length-delimited
            out.write(varint(body.size.toLong()))
            out.write(body)
        }
        return out.toByteArray()
    }

    fun normal(vararg entries: Pair<String, Float>): ByteArray =
        model(*entries.map { Triple(it.first, it.second, 1) }.toTypedArray())

    /** A ModelProto with a trainer-spec blob in front, which every real model has. */
    fun withUnknownFields(pieces: ByteArray): ByteArray {
        val out = ByteArrayOutputStream()
        // field 2, length-delimited: a normaliser spec this reader must skip.
        out.write(0x12)
        out.write(varint(5))
        out.write(byteArrayOf(1, 2, 3, 4, 5))
        // field 5, varint
        out.write(0x28)
        out.write(varint(99))
        // field 6, fixed64
        out.write(0x31)
        out.write(ByteArray(8))
        out.write(pieces)
        return out.toByteArray()
    }
}

class SentencePieceReaderTest {

    @Test
    fun itReadsThePieceTheScoreAndTheKind() {
        val t = SentencePieceTokenizer(
            SpmFixture.model(Triple("▁hey", -1.5f, 1), Triple("<unk>", 0f, 2)),
        )
        assertEquals(2, t.pieces.size)
        assertEquals("▁hey", t.pieces[0].piece)
        assertEquals(-1.5f, t.pieces[0].score)
        assertEquals(SentencePieceKind.NORMAL, t.pieces[0].kind)
        assertEquals(SentencePieceKind.UNKNOWN, t.pieces[1].kind)
        assertEquals(1, t.pieces[1].id)
    }

    @Test
    fun unknownFieldsAreSkippedByWIRETYPEsoARealModelStillReads() {
        // Every real model carries a trainer spec and a normaliser blob. A
        // reader that stops at the first field it does not know reads zero
        // pieces and the symptom is a tokenizer that segments into characters.
        val bytes = SpmFixture.withUnknownFields(SpmFixture.normal("▁hey" to -1f, "▁b" to -2f))
        val t = SentencePieceTokenizer(bytes)
        assertEquals(2, t.pieces.size)
    }

    @Test
    fun anUnknownPieceTYPEfromANewerTrainerReadsAsNormal() {
        // Dropping the piece would silently shrink the vocabulary.
        val t = SentencePieceTokenizer(SpmFixture.model(Triple("▁x", -1f, 42)))
        assertEquals(SentencePieceKind.NORMAL, t.pieces[0].kind)
    }

    @Test
    fun anEmptyModelIsEmptyRatherThanAnError() {
        val t = SentencePieceTokenizer(ByteArray(0))
        assertTrue(t.pieces.isEmpty())
        // Every character falls through at the unknown penalty, so a model with
        // no vocabulary still segments rather than throwing.
        assertEquals(listOf("▁", "h", "i"), t.encode("hi"))
    }

    @Test
    fun aTruncatedModelStopsRatherThanSpinningOrThrowing() {
        val full = SpmFixture.normal("▁hey" to -1f, "▁b" to -2f)
        for (cut in 1 until full.size) {
            SentencePieceTokenizer(full.copyOfRange(0, cut))
        }
    }

    @Test
    fun aVarintIsBoundedSoACorruptFileCannotSpin() {
        // Ten continuation bytes with no terminator.
        val i = intArrayOf(0)
        assertNull(SentencePieceTokenizer.readVarint(ByteArray(20) { 0xFF.toByte() }, i))
    }

    @Test
    fun aMultiByteVarintDecodesToTheRightNumber() {
        val i = intArrayOf(0)
        // 300 = 0b100101100 -> 0xAC 0x02
        assertEquals(300L, SentencePieceTokenizer.readVarint(byteArrayOf(0xAC.toByte(), 0x02), i))
        assertEquals(2, i[0])
    }
}

class SentencePieceTokenizerTest {

    private fun tok(vararg pieces: Pair<String, Float>) =
        SentencePieceTokenizer(SpmFixture.normal(*pieces))

    @Test
    fun aWordBoundaryIsTheBLOCKcharacterNotASpace() {
        // A space is a character the model has never seen; U+2581 is what it
        // was trained on. Feed it a space and every word is unknown.
        val t = tok("▁hey" to -1f, "▁b" to -2f)
        assertEquals(listOf("▁hey", "▁b"), t.encode("hey b"))
        assertEquals(SentencePieceTokenizer.WORD_START, '▁')
    }

    @Test
    fun theLeadingBoundaryIsAddedEvenWithoutASpaceInFront() {
        val t = tok("▁hey" to -1f)
        assertEquals(listOf("▁hey"), t.encode("hey"))
    }

    @Test
    fun runsOfWhitespaceCollapseToOneBoundary() {
        val t = tok("▁hey" to -1f, "▁b" to -2f)
        assertEquals(listOf("▁hey", "▁b"), t.encode("  hey    b  "))
        assertEquals(listOf("▁hey", "▁b"), t.encode("hey\t\nb"))
    }

    @Test
    fun viterbiPrefersTheHIGHERscoringSegmentation() {
        // "▁ab" scores better than "▁a" + "b", so it wins even though both cover
        // the text. A greedy longest-match would get this right by accident; a
        // greedy shortest-match would not.
        val t = tok("▁ab" to -1f, "▁a" to -5f, "b" to -5f)
        assertEquals(listOf("▁ab"), t.encode("ab"))
    }

    @Test
    fun viterbiSplitsWhenTwoPiecesBeatOne() {
        val t = tok("▁ab" to -20f, "▁a" to -1f, "b" to -1f)
        assertEquals(listOf("▁a", "b"), t.encode("ab"))
    }

    @Test
    fun aSingleCharacterALWAYShasAWayThroughAtAPenalty() {
        // No input can be unsegmentable. A tokenizer that returns nothing for
        // real text is a listener that silently ignores a wake word.
        val t = tok("▁hey" to -1f)
        val out = t.encode("zzz")
        assertEquals(4, out.size, "expected the boundary plus three characters")
        assertTrue(out.contains("z"))
    }

    @Test
    fun anUnknownPenaltyIsWorseThanANYrealPiece() {
        // Otherwise a segmentation that gives up beats one that covers the text.
        val t = tok("▁ab" to -100f, "▁a" to -100f, "b" to -100f)
        assertEquals(listOf("▁ab"), t.encode("ab"))
    }

    @Test
    fun canRepresentNamesTheSoundsTheListenerDoesNotHave() {
        // So somebody can be told WHICH part of their wake word will not work,
        // rather than just being refused.
        val t = tok("▁hey" to -1f, "▁b" to -2f)
        val (ok, unknown) = t.canRepresent("hey b")
        assertTrue(ok)
        assertTrue(unknown.isEmpty())

        val (ok2, unknown2) = t.canRepresent("hey qx")
        assertFalse(ok2)
        assertTrue(unknown2.isNotEmpty())
    }

    @Test
    fun anUnknownPieceIsReportedOnceNotOncePerOccurrence() {
        // Three z characters, one complaint about z. A list that repeats itself
        // is unreadable in the sentence it ends up inside.
        val t = tok("▁hey" to -1f)
        val (ok, unknown) = t.canRepresent("zzz")
        assertFalse(ok)
        assertEquals(listOf("▁", "z"), unknown)
    }

    @Test
    fun anUpperCaseVocabularyIsDETECTEDandTheInputIsFolded() {
        // Getting this wrong makes every word unknown, and the symptom is a
        // tokenizer that segments everything into single characters.
        val upper = SentencePieceTokenizer(SpmFixture.normal("▁HEY" to -1f, "▁B" to -2f, "▁OKAY" to -3f))
        assertTrue(upper.vocabularyIsUpperCase)
        assertEquals(listOf("▁HEY", "▁B"), upper.encode("hey b"))
    }

    @Test
    fun aMixedCaseVocabularyIsNotFolded() {
        val lower = SentencePieceTokenizer(SpmFixture.normal("▁hey" to -1f, "▁b" to -2f, "▁okay" to -3f))
        assertFalse(lower.vocabularyIsUpperCase)
        assertEquals(listOf("▁hey", "▁b"), lower.encode("hey b"))
    }

    @Test
    fun aControlPieceIsNotUsedForSegmentation() {
        // <s> and </s> are in the vocabulary but must never appear in output.
        val t = SentencePieceTokenizer(
            SpmFixture.model(Triple("▁ab", -1f, 3), Triple("▁a", -2f, 1), Triple("b", -2f, 1)),
        )
        assertEquals(listOf("▁a", "b"), t.encode("ab"))
    }

    @Test
    fun aUserDefinedPieceISusedForSegmentation() {
        val t = SentencePieceTokenizer(
            SpmFixture.model(Triple("▁ab", -1f, 4), Triple("▁a", -9f, 1), Triple("b", -9f, 1)),
        )
        assertEquals(listOf("▁ab"), t.encode("ab"))
    }

    @Test
    fun emptyTextStillCarriesTheLeadingWordBOUNDARY() {
        // The boundary is added unconditionally, so empty text encodes to the
        // marker alone rather than to nothing. Worth pinning: a caller counting
        // tokens to judge a wake phrase sees 1 here, not 0.
        val t = tok("▁hey" to -1f)
        assertEquals(listOf("▁"), t.encode(""))
        assertEquals(listOf("▁"), t.encode("   "))
    }

    @Test
    fun aDuplicatePieceKeepsTheFIRSTidRatherThanTheLast() {
        val t = SentencePieceTokenizer(SpmFixture.normal("▁a" to -1f, "▁a" to -9f))
        assertEquals(2, t.pieces.size)
        // The lookup resolves to the first, so its score is the one used.
        assertEquals(listOf("▁a"), t.encode("a"))
    }
}

class WakePhraseBookTest {

    // A vocabulary that can spell the phrases used here, plus the letters.
    private fun tokenizer(): SentencePieceTokenizer {
        val pieces = mutableListOf<Pair<String, Float>>()
        pieces.add("▁hey" to -1f)
        pieces.add("▁b" to -1f)
        pieces.add("▁there" to -1f)
        pieces.add("▁circle" to -1f)
        pieces.add("▁listen" to -1f)
        pieces.add("▁to" to -1f)
        pieces.add("▁me" to -1f)
        pieces.add("▁okay" to -1f)
        pieces.add("▁please" to -1f)
        pieces.add("▁stop" to -1f)
        pieces.add("▁now" to -1f)
        pieces.add("▁mokoena" to -1f)
        pieces.add("▁" to -3f)
        for (c in 'a'..'z') pieces.add(c.toString() to -5f)
        // Real vocabularies carry punctuation; without it every phrase with a
        // comma in it would come back unusable for the wrong reason.
        for (c in listOf(",", ".", "!", "?")) pieces.add(c to -5f)
        return SentencePieceTokenizer(SpmFixture.normal(*pieces.toTypedArray()))
    }

    private fun book() = WakePhraseBook(tokenizer())

    @Test
    fun aBlankPhraseIsUnusableAndSaysWhatToDo() {
        val p = book().evaluate("   ")
        assertEquals(WakePhraseVerdict.UNUSABLE, p.verdict)
        assertEquals("Type something to say.", p.advice)
    }

    @Test
    fun aPhraseTheListenerCannotSPELLisUnusableAndNamesTheSounds() {
        // Refusing without saying which part is wrong leaves somebody guessing.
        val t = SentencePieceTokenizer(SpmFixture.normal("▁hey" to -1f, "▁b" to -1f))
        val p = WakePhraseBook(t).evaluate("hey qx")
        assertEquals(WakePhraseVerdict.UNUSABLE, p.verdict)
        assertContains(p.advice, "sounds the listener does not know")
    }

    @Test
    fun aShortPhraseIsACAUTIONnotARefusal() {
        // The user may still want it, and this is their device.
        val p = book().evaluate("hey b")
        assertEquals(WakePhraseVerdict.CAUTION, p.verdict)
        assertContains(p.advice, "across a room")
    }

    @Test
    fun aPhraseOfONLYeverydayWordsIsACaution() {
        val p = book().evaluate("okay please stop now")
        assertEquals(WakePhraseVerdict.CAUTION, p.verdict)
        assertContains(p.advice, "talking to someone else")
    }

    @Test
    fun oneUnusualWordIsEnoughToLiftTheEverydayCaution() {
        val p = book().evaluate("okay please mokoena now")
        assertEquals(WakePhraseVerdict.GOOD, p.verdict)
        assertEquals("", p.advice)
    }

    @Test
    fun punctuationDoesNotHideAnEverydayWord() {
        val p = book().evaluate("okay, please stop now.")
        assertEquals(WakePhraseVerdict.CAUTION, p.verdict)
        assertContains(p.advice, "talking to someone else")
    }

    @Test
    fun aPhraseThatWouldBeSHADOWEDisUnusableAndSaysWhichOneWins() {
        // Said in terms of what will happen to the person, not in terms of a trie.
        val b = book()
        b.tryAdd("hey b")
        val p = b.evaluate("hey b there")
        assertEquals(WakePhraseVerdict.UNUSABLE, p.verdict)
        assertContains(p.advice, "would always trigger first")
        assertContains(p.advice, "hey b")
    }

    @Test
    fun aPhraseThatWouldSHADOWanExistingOneIsAlsoRefused() {
        // The collision has to be caught from BOTH directions, or adding a
        // shorter phrase silently kills one that already works.
        val b = book()
        b.tryAdd("hey b there")
        val p = b.evaluate("hey b")
        assertEquals(WakePhraseVerdict.UNUSABLE, p.verdict)
        assertContains(p.advice, "would stop working")
    }

    @Test
    fun aPhraseDoesNotCollideWithItself() {
        val b = book()
        b.tryAdd("hey b")
        // Re-evaluating the same text is a caution about length, not a collision.
        assertEquals(WakePhraseVerdict.CAUTION, b.evaluate("hey b").verdict)
    }

    @Test
    fun anUnusablePhraseIsNEVERstored() {
        // So the book can never hold a wake word that cannot fire.
        val b = book()
        val (added, phrase) = b.tryAdd("")
        assertFalse(added)
        assertEquals(WakePhraseVerdict.UNUSABLE, phrase.verdict)
        assertTrue(b.phrases.isEmpty())
    }

    @Test
    fun aCautionPhraseISstoredBecauseItStillWorks() {
        val b = book()
        val (added, _) = b.tryAdd("hey b")
        assertTrue(added)
        assertEquals(1, b.phrases.size)
    }

    @Test
    fun removeIsCaseInsensitiveAndReportsWhetherItFoundAnything() {
        val b = book()
        b.tryAdd("hey b")
        assertTrue(b.remove("HEY B"))
        assertFalse(b.remove("hey b"))
        assertTrue(b.phrases.isEmpty())
    }

    @Test
    fun theThresholdAndBoostRideAlongUntouched() {
        val p = book().evaluate("hey b", threshold = 0.42, boost = 3.0)
        assertEquals(0.42, p.threshold)
        assertEquals(3.0, p.boost)
    }
}

class WakePhraseCandidatesTest {

    @Test
    fun theRegionIsDroppedSoEnZaFindsTheEnglishList() {
        assertEquals(listOf("Hey B"), WakePhraseBook.candidates("en-ZA"))
        assertEquals(listOf("Hey B"), WakePhraseBook.candidates("en"))
        assertEquals(listOf("Hey B"), WakePhraseBook.candidates("EN-za"))
    }

    @Test
    fun anUnknownLanguageFallsBackToEnglishRatherThanToNOTHING() {
        // A device with no candidates has no wake word at all.
        assertEquals(listOf("Hey B"), WakePhraseBook.candidates("xh"))
        assertEquals(listOf("Hey B"), WakePhraseBook.candidates(null))
        assertEquals(listOf("Hey B"), WakePhraseBook.candidates(""))
    }

    @Test
    fun theCjkLanguagesCarryBothANativeFormAndARomanisation() {
        // A listener whose vocabulary has no CJK still has something to match.
        for (tag in listOf("ja", "ko", "zh", "yue")) {
            val c = WakePhraseBook.candidates(tag)
            assertTrue(c.size >= 2, tag + " has no romanised fallback")
        }
    }

    /** Latin in both cases plus the kana the Japanese candidates need. */
    private fun broadTokenizer(): SentencePieceTokenizer {
        val pieces = mutableListOf("▁" to -3f)
        for (c in 'a'..'z') pieces.add(c.toString() to -5f)
        for (c in 'A'..'Z') pieces.add(c.toString() to -5f)
        for (c in listOf("ビ", "ー", "さ", "ん", "ま", "비", "님", "小", "B")) pieces.add(c to -5f)
        return SentencePieceTokenizer(SpmFixture.normal(*pieces.toTypedArray()))
    }

    @Test
    fun theBestCandidateIsTheLONGESTusableOne() {
        // More tokens means fewer false wakes, so among the candidates that a
        // listener CAN represent, the longest one wins.
        val book = WakePhraseBook(broadTokenizer())
        val best = book.best("ja")
        assertNotNull(best)
        val usable = WakePhraseBook.candidates("ja")
            .map { book.evaluate(it) }
            .filter { it.verdict != WakePhraseVerdict.UNUSABLE }
        assertTrue(usable.size > 1, "the fixture did not offer a choice to make")
        assertEquals(usable.maxOf { it.tokens.size }, best.tokens.size)
        assertEquals("Bee san", best.text)
    }

    @Test
    fun aCandidateTheListenerCannotSPELLisSkippedRatherThanChosen() {
        // A vocabulary with no kana still has to end up with a wake word.
        val latinOnly = mutableListOf("▁" to -3f)
        for (c in 'a'..'z') latinOnly.add(c.toString() to -5f)
        for (c in 'A'..'Z') latinOnly.add(c.toString() to -5f)
        val book = WakePhraseBook(SentencePieceTokenizer(SpmFixture.normal(*latinOnly.toTypedArray())))
        assertEquals("Bee san", book.best("ja")!!.text)
    }
}
