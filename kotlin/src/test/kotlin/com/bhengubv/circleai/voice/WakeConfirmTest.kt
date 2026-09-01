package com.bhengubv.circleai.voice

import kotlin.math.PI
import kotlin.math.sin
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlinx.coroutines.test.runTest

class KwsDetectionTest {

    @Test
    fun frameIndicesBecomeMillisecondsAtFortyPerFrame() {
        val d = KwsDetection("hey b", atFrame = 50, probability = 0.9, startFrame = 40)
        assertEquals(1600.0, d.startMs)
        assertEquals(2000.0, d.endMs)
    }

    @Test
    fun aMissingStartFrameFallsBackToTheEnd() {
        // -1 means the spotter did not report a start. Treating it as frame -1
        // would place the phrase 40 ms before the stream began.
        val d = KwsDetection("hey b", atFrame = 25, probability = 0.9)
        assertEquals(1000.0, d.startMs)
        assertEquals(1000.0, d.endMs)
    }
}

class UtteranceOnsetConfirmerTest {

    private val sampleRate = 16_000
    private val per = 160 // samples in a 10 ms bucket

    /** Speech for [speechMs], then the phrase; the confirmer judges the run-up. */
    private fun window(leadInMs: Int, phraseMs: Int, gapMs: Int = 0): FloatArray {
        val total = ((leadInMs + gapMs + phraseMs) / 10) * per
        val out = FloatArray(total)
        var i = 0
        fun fill(ms: Int, loud: Boolean) {
            repeat((ms / 10) * per) {
                if (i < total) out[i] = if (loud) (sin(i * 0.05) * 0.5).toFloat() else 0f
                i++
            }
        }
        fill(leadInMs, true)
        fill(gapMs, false)
        fill(phraseMs, true)
        return out
    }

    private fun candidate(w: FloatArray, keywordEnd: Int = w.size) =
        WakeCandidate(KwsDetection("hey b", 0, 1.0), w, 0, keywordEnd)

    @Test
    fun aPhraseAfterAPAUSEisConfirmed() = runTest {
        // Somebody addressing a device pauses first. That is the whole signal.
        val c = UtteranceOnsetConfirmer()
        val w = window(leadInMs = 800, phraseMs = 400, gapMs = 400)
        assertTrue(c.confirm(candidate(w)))
        assertNull(c.lastReason)
    }

    @Test
    fun aPhraseInTheMIDDLEofARunningSentenceIsRejected() = runTest {
        val c = UtteranceOnsetConfirmer()
        val w = window(leadInMs = 2000, phraseMs = 400)
        assertFalse(c.confirm(candidate(w)))
        assertNotNull(c.lastReason)
        assertContains(c.lastReason!!, "had been speaking")
    }

    @Test
    fun theRejectionReasonNamesBothTheMeasurementAndTheLimit() = runTest {
        // A rejection nobody can read is a wake word nobody can tune.
        val c = UtteranceOnsetConfirmer(maxLeadInMs = 300.0)
        val w = window(leadInMs = 2000, phraseMs = 400)
        c.confirm(candidate(w))
        assertContains(c.lastReason!!, "ms before the phrase ended")
        assertContains(c.lastReason!!, "max 300")
    }

    @Test
    fun anEmptyOrVeryShortWindowFAILSOPEN() = runTest {
        // A confirmer that rejects when it cannot see is a device that stops
        // answering. Both of these are "nothing to judge", not "no".
        val c = UtteranceOnsetConfirmer()
        assertTrue(c.confirm(candidate(FloatArray(0))))
        assertNull(c.lastReason)
        assertTrue(c.confirm(candidate(FloatArray(per * 3))))
    }

    @Test
    fun aSILENTwindowIsRejectedAndSaysSo() = runTest {
        // Different from empty: there IS audio and there is no speech in it,
        // which means the spotter fired on nothing.
        val c = UtteranceOnsetConfirmer()
        assertFalse(c.confirm(candidate(FloatArray(per * 100))))
        assertEquals("silence", c.lastReason)
    }

    @Test
    fun aShortGapDoesNotCountAsAPause() = runTest {
        // 50 ms of quiet inside a sentence is a consonant, not somebody
        // stopping to address the device. The gap tolerance is 150 ms.
        val c = UtteranceOnsetConfirmer()
        val w = window(leadInMs = 1500, phraseMs = 400, gapMs = 50)
        assertFalse(c.confirm(candidate(w)))
    }

    @Test
    fun aLongerLimitAcceptsWhatAShorterOneRejects() = runTest {
        val w = window(leadInMs = 900, phraseMs = 300)
        assertFalse(UtteranceOnsetConfirmer(maxLeadInMs = 400.0).confirm(candidate(w)))
        assertTrue(UtteranceOnsetConfirmer(maxLeadInMs = 5000.0).confirm(candidate(w)))
    }
}

class TranscriptConfirmerTest {

    // The default phrase deliberately AVOIDS a filler word - see the last test
    // in this class for why "hey b" cannot be used here.
    private fun candidate(phrase: String = "circle") =
        WakeCandidate(KwsDetection(phrase, 0, 1.0), FloatArray(1600), 0, 1600)

    private fun confirmer(heard: String) = TranscriptConfirmer({ heard })

    @Test
    fun aPhraseAtTheSTARTofTheUtteranceIsConfirmed() = runTest {
        val c = confirmer("circle what is the weather")
        assertTrue(c.confirm(candidate()))
        assertNull(c.lastReason)
    }

    @Test
    fun aFewFillerWordsInFrontAreAllowed() = runTest {
        // People really do say these before addressing a device.
        for (lead in listOf("um", "uh", "okay", "so", "please", "yeah")) {
            assertTrue(confirmer(lead + " circle play music").confirm(candidate()), "rejected " + lead)
        }
        assertTrue(confirmer("um uh okay circle").confirm(candidate()))
    }

    @Test
    fun aPhraseBURIEDinTheSentenceIsRejected() = runTest {
        val c = confirmer("i was telling her that circle never works")
        assertFalse(c.confirm(candidate()))
        assertContains(c.lastReason!!, "not how it starts")
    }

    @Test
    fun theRejectionQuotesWhatWasActuallyHeard() = runTest {
        val c = confirmer("i was telling her that circle never works")
        c.confirm(candidate())
        assertContains(c.lastReason!!, "i was telling her that circle")
    }

    @Test
    fun punctuationAndCasingCannotMakeAMatchFail() = runTest {
        assertTrue(confirmer("Circle! What is the weather?").confirm(candidate()))
        assertTrue(confirmer("CIRCLE   ").confirm(candidate()))
    }

    @Test
    fun aMultiWordPhraseMustMatchInOrder() = runTest {
        val c = confirmer("open circle please")
        assertTrue(c.confirm(candidate("open circle")))
        assertFalse(confirmer("circle open please").confirm(candidate("open circle")))
    }

    @Test
    fun aTranscriberThatTHROWSmustNotSilenceTheDevice() = runTest {
        // Fail open, and say why. A wake word that stops working because the
        // confirmer crashed is worse than one that occasionally over-triggers.
        val c = TranscriptConfirmer({ throw IllegalStateException("model not loaded") })
        assertTrue(c.confirm(candidate()))
        assertContains(c.lastReason!!, "confirmer unavailable")
    }

    @Test
    fun anEmptyTranscriptFailsOpen() = runTest {
        val c = confirmer("")
        assertTrue(c.confirm(candidate()))
        assertNull(c.lastReason)
    }

    @Test
    fun aGREEDYleadInSkipEatsAPhraseThatStartsWithAFiller() = runTest {
        // A REAL DEFECT IN THE REFERENCE, ported faithfully rather than fixed.
        //
        // "hey" is in the filler list. The skip loop walks PAST it before the
        // comparison starts, so a phrase that BEGINS with "hey" can never be
        // found: the match then starts at "b" and is compared against "hey".
        //
        // The wake phrase this product ships with is "Hey B". TranscriptConfirmer
        // therefore cannot confirm the product wake word - not a rare edge, the
        // main path. Pinned here so that the day somebody fixes the C#, this
        // test fails and says exactly what changed and why it was ever like this.
        val c = confirmer("hey b what is the weather")
        assertFalse(c.confirm(candidate("hey b")))
        assertContains(c.lastReason!!, "not how it starts")

        // The same phrase confirms fine once the filler is not part of it.
        assertTrue(confirmer("circle b what is the weather").confirm(candidate("circle b")))
    }

    @Test
    fun pcm16IsLittleEndianAndClampsRatherThanWrapping() {
        // A sample above 1.0 that wraps comes back as a loud negative, and a
        // clipped syllable turns into a click the transcriber cannot read.
        val bytes = TranscriptConfirmer.toPcm16(floatArrayOf(0f, 1.0f, -1.0f, 4.0f, -4.0f))
        assertEquals(10, bytes.size)
        assertEquals(0, bytes[0].toInt())
        assertEquals(0, bytes[1].toInt())
        // 32767 = 0x7FFF, little-endian
        assertEquals(0xFF, bytes[2].toInt() and 0xFF)
        assertEquals(0x7F, bytes[3].toInt() and 0xFF)
        // Clamped, not wrapped.
        assertEquals(0xFF, bytes[6].toInt() and 0xFF)
        assertEquals(0x7F, bytes[7].toInt() and 0xFF)
    }
}

class EitherConfirmerTest {

    private val candidate = WakeCandidate(KwsDetection("hey b", 0, 1.0), FloatArray(160), 0, 160)

    private class Fixed(private val answer: Boolean, private val why: String?) : IWakeConfirmer {
        var calls = 0
        override val lastReason: String? get() = why
        override suspend fun confirm(candidate: WakeCandidate): Boolean { calls++; return answer }
    }

    @Test
    fun bothMustAgreeDespiteTheName() = runTest {
        // The name says either; the C# requires BOTH, and this port matches the
        // code rather than the name.
        assertTrue(EitherConfirmer(Fixed(true, null), Fixed(true, null)).confirm(candidate))
        assertFalse(EitherConfirmer(Fixed(true, null), Fixed(false, "no")).confirm(candidate))
        assertFalse(EitherConfirmer(Fixed(false, "no"), Fixed(true, null)).confirm(candidate))
    }

    @Test
    fun theCheapOneRunsFirstAndSHORTCIRCUITStheExpensiveOne() = runTest {
        // The entire reason for the pairing: do not pay for a transcription on
        // a candidate the energy check can reject outright.
        val cheap = Fixed(false, "silence")
        val precise = Fixed(true, null)
        EitherConfirmer(cheap, precise).confirm(candidate)
        assertEquals(1, cheap.calls)
        assertEquals(0, precise.calls, "the expensive confirmer was paid for anyway")
    }

    @Test
    fun theReasonComesFromWhicheverStageSaidNo() = runTest {
        val a = EitherConfirmer(Fixed(false, "silence"), Fixed(true, null))
        a.confirm(candidate)
        assertEquals("silence", a.lastReason)

        val b = EitherConfirmer(Fixed(true, null), Fixed(false, "not how it starts"))
        b.confirm(candidate)
        assertEquals("not how it starts", b.lastReason)
    }

    @Test
    fun aConfirmedCandidateClearsTheReason() = runTest {
        val e = EitherConfirmer(Fixed(true, "stale"), Fixed(true, "stale"))
        e.confirm(candidate)
        assertNull(e.lastReason)
    }
}

class ConfirmedKeywordSpotterTest {

    private class FakeSpotter : IKeywordSpotter {
        override val keywords = listOf("hey b")
        override val shadowedKeywords = emptyList<Pair<String, String>>()
        override var onDetected: ((KwsDetection) -> Unit)? = null
        var accepted = 0
        var flushed = 0
        var wasReset = 0
        var closed = 0
        var fireOnNextAccept: KwsDetection? = null

        override fun acceptWaveform(samples: FloatArray) {
            accepted += samples.size
            fireOnNextAccept?.let { onDetected?.invoke(it); fireOnNextAccept = null }
        }
        override fun flush() { flushed++ }
        override fun reset() { wasReset++ }
        override fun close() { closed++ }
    }

    private fun audio(n: Int) = FloatArray(n) { (sin(it * 0.05) * 0.5).toFloat() }

    @Test
    fun aConfirmedDetectionWakesTheDevice() = runTest {
        val spotter = FakeSpotter()
        val s = ConfirmedKeywordSpotter(spotter, AlwaysConfirm())
        val woke = mutableListOf<KwsDetection>()
        s.onWoke = { woke.add(it) }

        spotter.fireOnNextAccept = KwsDetection("hey b", atFrame = 10, probability = 0.9, startFrame = 5)
        s.acceptWaveform(audio(16_000))
        assertEquals(1, woke.size)
        assertEquals("hey b", woke[0].phrase)
    }

    @Test
    fun aRejectedDetectionSURFACEStheReasonRatherThanVanishing() = runTest {
        // "it does not wake" and "it woke and we vetoed it" have to look
        // different from outside, or the wake word cannot be tuned at all.
        val spotter = FakeSpotter()
        val nope = object : IWakeConfirmer {
            override val lastReason = "had been speaking 900 ms"
            override suspend fun confirm(candidate: WakeCandidate) = false
        }
        val s = ConfirmedKeywordSpotter(spotter, nope)
        var wokeCount = 0
        val rejected = mutableListOf<Pair<KwsDetection, String?>>()
        s.onWoke = { wokeCount++ }
        s.onRejected = { d, r -> rejected.add(d to r) }

        spotter.fireOnNextAccept = KwsDetection("hey b", 10, 0.9, 5)
        s.acceptWaveform(audio(16_000))
        assertEquals(0, wokeCount)
        assertEquals(1, rejected.size)
        assertEquals("had been speaking 900 ms", rejected[0].second)
    }

    @Test
    fun detectionsAreCollectedInsideTheCallbackAndJUDGEDafterwards() = runTest {
        // The detection arrives mid-decode; stage two wants the audio around it,
        // including some not yet decoded. Judging inside the callback would look
        // only backwards.
        val spotter = FakeSpotter()
        var judgedWindowLength = -1
        val watcher = object : IWakeConfirmer {
            override val lastReason: String? = null
            override suspend fun confirm(candidate: WakeCandidate): Boolean {
                judgedWindowLength = candidate.window.size
                return true
            }
        }
        val s = ConfirmedKeywordSpotter(spotter, watcher)
        s.onWoke = {}

        spotter.fireOnNextAccept = KwsDetection("hey b", 10, 0.9, 5)
        s.acceptWaveform(audio(8000))
        // The window is everything appended BEFORE the spotter ran, which
        // includes the whole chunk the detection was found in.
        assertEquals(8000, judgedWindowLength)
    }

    @Test
    fun aDetectionThatHasScrolledOutOfTheRingIsLETTHROUGH() = runTest {
        // Only possible when a caller pushes seconds at a time. There is nothing
        // left to judge, and dropping it silently would be a wake word that
        // works everywhere except under load.
        val spotter = FakeSpotter()
        var confirmCalls = 0
        val counting = object : IWakeConfirmer {
            override val lastReason: String? = null
            override suspend fun confirm(candidate: WakeCandidate): Boolean { confirmCalls++; return false }
        }
        val s = ConfirmedKeywordSpotter(spotter, counting, historySeconds = 0.5)
        var woke = 0
        s.onWoke = { woke++ }

        // Detection at frame 0 (0 ms), then push far more than the ring holds.
        spotter.fireOnNextAccept = KwsDetection("hey b", 1, 0.9, 0)
        s.acceptWaveform(audio(32_000))
        assertEquals(1, woke)
        assertEquals(0, confirmCalls, "a scrolled-out detection should not be judged")
    }

    @Test
    fun flushJudgesWhateverIsStillOutstanding() = runTest {
        val spotter = FakeSpotter()
        val s = ConfirmedKeywordSpotter(spotter, AlwaysConfirm())
        var woke = 0
        s.onWoke = { woke++ }

        s.acceptWaveform(audio(1600))
        // A detection that arrives with no further audio still has to be judged.
        spotter.onDetected!!.invoke(KwsDetection("hey b", 2, 0.9, 0))
        assertEquals(0, woke)
        s.flush()
        assertEquals(1, woke)
        assertEquals(1, spotter.flushed)
    }

    @Test
    fun resetClearsTheRingAndAnythingPending() = runTest {
        val spotter = FakeSpotter()
        val s = ConfirmedKeywordSpotter(spotter, AlwaysConfirm())
        var woke = 0
        s.onWoke = { woke++ }

        s.acceptWaveform(audio(1600))
        spotter.onDetected!!.invoke(KwsDetection("hey b", 2, 0.9, 0))
        s.reset()
        s.flush()
        assertEquals(0, woke, "a pending detection survived a reset")
        assertEquals(1, spotter.wasReset)
    }

    @Test
    fun itPassesThroughTheKeywordListAndTheShadowWarning() {
        val spotter = FakeSpotter()
        val s = ConfirmedKeywordSpotter(spotter, AlwaysConfirm())
        assertEquals(listOf("hey b"), s.keywords)
        assertTrue(s.shadowedKeywords.isEmpty())
    }

    @Test
    fun closingItClosesTheSpotterItOwns() {
        val spotter = FakeSpotter()
        ConfirmedKeywordSpotter(spotter, AlwaysConfirm()).close()
        assertEquals(1, spotter.closed)
    }
}
