// VoiceTest.kt
//
// Verifies the CircleAI.Voice Kotlin port against the C# reference: energy VAD
// segment framing, null pass-throughs, the energy wake-word ASR loop firing on a
// matching transcript, the VoicePipeline wake->capture->transcribe->event flow
// (with and without VAD), speaker-identity enroll+identify (cosine nearest
// centroid over threshold), and speech-emotion argmax + Russell circumplex.

package com.bhengubv.circleai.voice

import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import kotlin.math.PI
import kotlin.math.sin
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class VoiceTest {

    // ── helpers ──────────────────────────────────────────────────────────

    private fun writeLe(b: ByteArray, i: Int, v: Short) {
        b[i] = (v.toInt() and 0xFF).toByte()
        b[i + 1] = ((v.toInt() shr 8) and 0xFF).toByte()
    }

    private fun tone(nSamples: Int, freq: Double = 220.0, amp: Double = 0.6, rate: Int = 16_000): ByteArray {
        val b = ByteArray(nSamples * 2)
        for (i in 0 until nSamples) {
            val v = (sin(2 * PI * freq * i / rate) * amp * Short.MAX_VALUE).toInt()
            writeLe(b, i * 2, v.toShort())
        }
        return b
    }

    private fun silence(nSamples: Int): ByteArray = ByteArray(nSamples * 2)

    private fun captureOf(vararg chunks: ByteArray): IAudioCapture = object : IAudioCapture {
        override val format = AudioFormat.Pcm16Mono16k
        override fun captureAsync(): Flow<ByteArray> = flow { for (c in chunks) emit(c) }
        override fun close() {}
    }

    // ── AudioFormat ──────────────────────────────────────────────────────

    @Test
    fun `canonical audio format is 16k mono 16-bit`() {
        val f = AudioFormat.Pcm16Mono16k
        assertEquals(16_000, f.sampleRate)
        assertEquals(1, f.channels)
        assertEquals(16, f.bitsPerSample)
    }

    // ── EnergyVadDetector ────────────────────────────────────────────────

    @Test
    fun `energy vad emits a speech segment after trailing silence`() = runBlocking {
        // 320 samples speech (640 bytes = one frame) + 10 silence frames -> emit.
        val speech = tone(320)
        val trailingSilence = silence(320 * 10)
        val capture = captureOf(speech + trailingSilence)

        val vad = EnergyVadDetector(silenceFrames = 10, frameSizeBytes = 640)
        val segments = mutableListOf<VadSegment>()
        vad.detectAsync(capture.captureAsync()).collect { segments.add(it) }

        assertTrue(segments.isNotEmpty(), "expected at least one speech segment")
        assertTrue(segments.all { it.isSpeech })
        assertTrue(segments.first().audio.isNotEmpty())
    }

    @Test
    fun `energy vad emits mid-speech tail when stream ends without silence`() = runBlocking {
        val capture = captureOf(tone(640)) // pure speech, no trailing silence
        val vad = EnergyVadDetector(silenceFrames = 10, frameSizeBytes = 640)
        val segments = mutableListOf<VadSegment>()
        vad.detectAsync(capture.captureAsync()).collect { segments.add(it) }
        assertEquals(1, segments.size)
        assertTrue(segments[0].isSpeech)
    }

    @Test
    fun `null vad passes every chunk through as speech`() = runBlocking {
        val a = byteArrayOf(1, 2)
        val b = byteArrayOf(3, 4)
        val vad = NullVoiceActivityDetector()
        val out = mutableListOf<VadSegment>()
        vad.detectAsync(flow { emit(a); emit(b) }).collect { out.add(it) }
        assertEquals(2, out.size)
        assertTrue(out.all { it.isSpeech })
        assertTrue(out[0].audio.contentEquals(a))
        assertTrue(out[1].audio.contentEquals(b))
    }

    // ── Null transcriber / TTS ───────────────────────────────────────────

    @Test
    fun `null transcriber returns empty und result and drains stream`() = runBlocking {
        val t = NullVoiceTranscriber()
        val r = t.transcribeAsync(byteArrayOf(1, 2))
        assertEquals("", r.text)
        assertEquals(0f, r.confidence)
        assertEquals("und", r.languageCode)

        val parts = mutableListOf<PartialTranscription>()
        t.streamTranscribeAsync(flow { emit(byteArrayOf(1)); emit(byteArrayOf(2)) })
            .collect { parts.add(it) }
        assertTrue(parts.isEmpty())
        t.closeAsync()
    }

    @Test
    fun `null tts yields empty result and no chunks`() = runBlocking {
        val e = NullTtsEngine()
        val r = e.synthesiseAsync("hi")
        assertEquals(0, r.audioData.size)
        assertEquals(24_000, r.sampleRate)
        assertEquals(1, r.channels)
        assertEquals(16, r.bitsPerSample)

        val chunks = mutableListOf<ByteArray>()
        e.streamSynthesiseAsync("hi").collect { chunks.add(it) }
        assertTrue(chunks.isEmpty())
    }

    // ── Null wake-word ───────────────────────────────────────────────────

    @Test
    fun `null wake-word tracks listening but never fires`() = runBlocking {
        val ww = NullWakeWordDetector()
        assertEquals("Hey B", ww.wakeWord)
        assertFalse(ww.isListening)
        ww.startAsync()
        assertTrue(ww.isListening)
        ww.stopAsync()
        assertFalse(ww.isListening)
        ww.closeAsync()
    }

    // ── EnergyWakeWordDetector ───────────────────────────────────────────

    /** Transcriber that returns a fixed text for any speech segment. */
    private class FixedTranscriber(private val text: String) : IVoiceTranscriber {
        override suspend fun transcribeAsync(pcmAudio: ByteArray) = TranscriptionResult(text, 0.9f, "en")
        override fun streamTranscribeAsync(audioChunks: Flow<ByteArray>): Flow<PartialTranscription> = flow {
            audioChunks.collect { }
            emit(PartialTranscription(text, true, 0.9f))
        }
        override fun close() {}
    }

    @Test
    fun `energy wake-word fires when a transcribed segment contains the phrase`() = runBlocking {
        // A speech burst followed by enough silence to close the VAD segment.
        val audio = tone(640) + silence(640 * 10)
        val capture = captureOf(audio)
        val transcriber = FixedTranscriber("okay hey b what's up")

        val detector = EnergyWakeWordDetector(capture, transcriber, wakeWord = "hey b")
        assertEquals("hey b", detector.wakeWord)

        val latch = CountDownLatch(1)
        val got = AtomicReference<WakeWordDetectedEvent>()
        detector.onWakeWordDetected { got.set(it); latch.countDown() }

        detector.startAsync()
        assertTrue(latch.await(5, TimeUnit.SECONDS), "wake word did not fire in time")
        assertEquals("hey b", got.get().wakeWord)
        assertEquals(0.9f, got.get().confidence)
        detector.stopAsync()
        detector.closeAsync()
    }

    @Test
    fun `energy wake-word does not fire when phrase is absent`() = runBlocking {
        val audio = tone(640) + silence(640 * 10)
        val capture = captureOf(audio)
        val transcriber = FixedTranscriber("just some other words")
        val detector = EnergyWakeWordDetector(capture, transcriber, wakeWord = "hey b")

        val fired = AtomicBoolean(false)
        detector.onWakeWordDetected { fired.set(true) }
        detector.startAsync()
        // capture flow is finite; give the loop time to drain then assert no fire.
        Thread.sleep(400)
        detector.stopAsync()
        assertFalse(fired.get())
        detector.closeAsync()
    }

    // ── VoicePipeline ────────────────────────────────────────────────────

    /** A wake-word detector whose fire can be triggered manually. */
    private class ManualWakeWord(override val wakeWord: String = "hey b") : IWakeWordDetector {
        private val listeners = mutableListOf<(WakeWordDetectedEvent) -> Unit>()
        val started = AtomicBoolean(false)
        val stopped = AtomicBoolean(false)
        val closed = AtomicBoolean(false)
        override var isListening = false
            private set
        override fun onWakeWordDetected(listener: (WakeWordDetectedEvent) -> Unit) { listeners.add(listener) }
        override fun removeWakeWordDetected(listener: (WakeWordDetectedEvent) -> Unit) { listeners.remove(listener) }
        override suspend fun startAsync() { isListening = true; started.set(true) }
        override suspend fun stopAsync() { isListening = false; stopped.set(true) }
        override fun close() { closed.set(true) }
        fun fire() {
            val evt = WakeWordDetectedEvent(wakeWord, confidence = 1f)
            for (l in listeners.toList()) l(evt)
        }
    }

    @Test
    fun `pipeline transcribes captured audio on wake and raises transcribed`() = runBlocking {
        val wake = ManualWakeWord()
        val transcriber = FixedTranscriber("turn on the lights")
        val capture = captureOf(tone(320))
        val pipeline = VoicePipeline(wake, transcriber, capture)

        val latch = CountDownLatch(1)
        val got = AtomicReference<TranscribedEvent>()
        pipeline.onTranscribed { got.set(it); latch.countDown() }

        pipeline.startAsync()
        assertTrue(wake.started.get())
        wake.fire()

        assertTrue(latch.await(5, TimeUnit.SECONDS), "no transcription event")
        assertEquals("turn on the lights", got.get().result.text)
        assertEquals("und", got.get().result.languageCode) // stream path sets und

        pipeline.closeAsync()
        assertTrue(wake.closed.get())
    }

    @Test
    fun `pipeline routes audio through vad when supplied`() = runBlocking {
        val wake = ManualWakeWord()
        val transcriber = FixedTranscriber("hello world")
        val capture = captureOf(tone(640) + silence(640 * 10))
        val pipeline = VoicePipeline(wake, transcriber, capture, vad = EnergyVadDetector(silenceFrames = 10))

        val latch = CountDownLatch(1)
        val got = AtomicReference<TranscribedEvent>()
        pipeline.onTranscribed { got.set(it); latch.countDown() }

        pipeline.startAsync()
        wake.fire()
        assertTrue(latch.await(5, TimeUnit.SECONDS))
        assertEquals("hello world", got.get().result.text)
        pipeline.closeAsync()
    }

    @Test
    fun `pipeline exposes injected tts and collaborators`() {
        val wake = ManualWakeWord()
        val transcriber = NullVoiceTranscriber()
        val tts = NullTtsEngine()
        val pipeline = VoicePipeline(wake, transcriber, ttsEngine = tts)
        assertTrue(pipeline.ttsEngine === tts)
        assertTrue(pipeline.wakeDetector === wake)
        assertTrue(pipeline.theTranscriber === transcriber)
        assertTrue(pipeline.audioCapture is NullAudioCapture)
        assertNull(pipeline.voiceActivityDetector)
    }

    // ── SpeakerIdentity ──────────────────────────────────────────────────

    /**
     * Deterministic embedder: maps a window to a 2-D vector whose direction
     * depends only on the sign of the first sample — so two enrolled "speakers"
     * land on opposite unit vectors and identification is exact.
     */
    private class SignEmbedder : ISpeakerEmbedder {
        override fun embed(window: FloatArray, sampleRateHz: Int): FloatArray {
            val positive = window.firstOrNull { it != 0f }?.let { it > 0f } ?: true
            return if (positive) floatArrayOf(1f, 0f) else floatArrayOf(0f, 1f)
        }
    }

    private fun constPcm(nSamples: Int, value: Short): ByteArray {
        val b = ByteArray(nSamples * 2)
        for (i in 0 until nSamples) writeLe(b, i * 2, value)
        return b
    }

    @Test
    fun `speaker identity enrolls and identifies by nearest centroid`() = runBlocking {
        val id = SpeakerIdentity(SignEmbedder())
        // 16000 samples = 1000 ms meets MinUtteranceMs.
        val alice = constPcm(16_000, 10_000)   // positive -> (1,0)
        val bob = constPcm(16_000, -10_000)    // negative -> (0,1)

        id.enrollAsync("alice", alice, 16_000)
        id.enrollAsync("bob", bob, 16_000)

        assertEquals("alice", id.identifyAsync(constPcm(16_000, 5_000), 16_000))
        assertEquals("bob", id.identifyAsync(constPcm(16_000, -5_000), 16_000))
        id.closeAsync()
    }

    @Test
    fun `speaker identity returns null with no enrollments or too-short audio`() = runBlocking {
        val id = SpeakerIdentity(SignEmbedder())
        assertNull(id.identifyAsync(constPcm(16_000, 5_000), 16_000)) // nothing enrolled
        // Enroll, then query with under-minimum audio (500 ms < 1000 ms) -> null embedding.
        id.enrollAsync("alice", constPcm(16_000, 10_000), 16_000)
        assertNull(id.identifyAsync(constPcm(8_000, 5_000), 16_000))
        id.closeAsync()
    }

    @Test
    fun `speaker identity averages repeated enrollments into the centroid`() = runBlocking {
        val id = SpeakerIdentity(SignEmbedder())
        id.enrollAsync("alice", constPcm(16_000, 10_000), 16_000)
        id.enrollAsync("alice", constPcm(16_000, 10_000), 16_000)
        val speakers = id.enrolledSpeakers
        assertEquals(1, speakers.size)
        assertEquals(2, speakers[0].sampleCount)
        assertEquals("alice", speakers[0].userId)
        id.closeAsync()
    }

    @Test
    fun `speaker identity rejects mismatched sample rate`() = runBlocking {
        val id = SpeakerIdentity(SignEmbedder(), SpeakerIdentityConfig(sampleRateHz = 16_000))
        var threw = false
        try {
            id.enrollAsync("x", constPcm(16_000, 10_000), 8_000) // wrong rate -> embedding null -> throws
        } catch (e: IllegalStateException) {
            threw = true
        }
        assertTrue(threw)
        id.closeAsync()
    }

    // ── SpeechEmotionDetector ────────────────────────────────────────────

    @Test
    fun `speech emotion picks the argmax label and maps circumplex coords`() = runBlocking {
        // Logits favour class index 1 = "happy" in the default label set.
        val runner = object : IEmotionModelRunner {
            override fun scoreLogits(window: FloatArray, sampleRateHz: Int): FloatArray =
                floatArrayOf(0.1f, 3.0f, 0.2f, 0.1f) // neutral, happy, angry, sad
        }
        val det = SpeechEmotionDetector(runner)
        val frame = det.senseAsync(constPcm(4_000, 8_000), 16_000)!!
        assertEquals("happy", frame.label)
        assertEquals(0.55, frame.arousal, 1e-9)
        assertEquals(0.81, frame.valence, 1e-9)
        assertTrue(frame.probability > 0.5)
        det.closeAsync()
    }

    @Test
    fun `speech emotion returns null on empty audio or wrong sample rate`() = runBlocking {
        val runner = object : IEmotionModelRunner {
            override fun scoreLogits(window: FloatArray, sampleRateHz: Int): FloatArray = floatArrayOf(1f, 0f, 0f, 0f)
        }
        val det = SpeechEmotionDetector(runner)
        assertNull(det.senseAsync(ByteArray(0), 16_000))
        assertNull(det.senseAsync(constPcm(4_000, 8_000), 8_000)) // wrong rate
        det.closeAsync()
    }

    @Test
    fun `speech emotion honours a custom label set`() = runBlocking {
        val runner = object : IEmotionModelRunner {
            override fun scoreLogits(window: FloatArray, sampleRateHz: Int): FloatArray = floatArrayOf(0.1f, 0.1f, 5.0f)
        }
        val det = SpeechEmotionDetector(runner, SpeechEmotionConfig(labels = listOf("calm", "bored", "excited")))
        val frame = det.senseAsync(constPcm(4_000, 8_000), 16_000)!!
        assertEquals("excited", frame.label)
        assertEquals(0.82, frame.arousal, 1e-9)
        det.closeAsync()
    }
}
