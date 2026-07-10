// SpeechTest.kt
//
// Verifies the CircleAI.Speech Kotlin port against the C# reference: G.711
// mu-law/a-law codec round-trips + wire formats, linear resample, NLMS echo
// cancel convergence + reset, spectral-subtraction gate, energy VAD (RMS + ZCR +
// hangover), rule-based / smart-turn end-of-turn detection, and all fail-closed
// null / fallback backends.

package com.bhengubv.circleai.speech

import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.time.Duration
import kotlin.math.PI
import kotlin.math.sin
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertTrue

class SpeechTest {

    // ── helpers ──────────────────────────────────────────────────────────

    private fun pcm(vararg samples: Int): ByteArray {
        val b = ByteArray(samples.size * 2)
        for (i in samples.indices) writeInt16Le(b, i * 2, samples[i].toShort())
        return b
    }

    /** A loud sine tone (voiced-speech-like) of [n] samples at [freq] Hz / [rate] Hz. */
    private fun tone(n: Int, freq: Double = 200.0, rate: Int = 16_000, amp: Double = 0.6): ByteArray {
        val b = ByteArray(n * 2)
        for (i in 0 until n) {
            val v = (sin(2 * PI * freq * i / rate) * amp * Short.MAX_VALUE).toInt()
            writeInt16Le(b, i * 2, v.toShort())
        }
        return b
    }

    private fun silence(n: Int): ByteArray = ByteArray(n * 2)

    private fun readSample(b: ByteArray, i: Int): Int = readInt16Le(b, i * 2).toInt()

    // ── PCM-16 LE helper parity ──────────────────────────────────────────

    @Test
    fun `pcm16 little-endian read-write round trips`() {
        val values = intArrayOf(0, 1, -1, 32767, -32768, 12345, -12345, 256, -256)
        val b = ByteArray(values.size * 2)
        for (i in values.indices) writeInt16Le(b, i * 2, values[i].toShort())
        for (i in values.indices) assertEquals(values[i], readInt16Le(b, i * 2).toInt())
        // Byte order: 0x0100 -> [0x00, 0x01]
        val one = ByteArray(2).also { writeInt16Le(it, 0, 256) }
        assertEquals(0x00.toByte(), one[0])
        assertEquals(0x01.toByte(), one[1])
    }

    // ── G.711 codecs ─────────────────────────────────────────────────────

    @Test
    fun `mulaw decode produces two bytes per input byte`() {
        val mulaw = byteArrayOf(0xFF.toByte(), 0x00, 0x7F, 0x80.toByte())
        val pcm = AudioFormatConverter.decodeMuLawToPcm16(mulaw)
        assertEquals(mulaw.size * 2, pcm.size)
    }

    @Test
    fun `mulaw quantisation is second-pass idempotent`() {
        // G.711 mu-law is NOT a perfect bijection under encode(decode(code)) for
        // all 256 codes (e.g. +0 / -0 both decode to magnitude 0). The true
        // invariant — matching the ITU-T reference the C# port implements — is
        // that once a sample is mu-law-quantised, re-encoding is stable:
        //   decode(encode(decode(code))) == decode(code) for every code.
        val allCodes = ByteArray(256) { it.toByte() }
        val pcm = AudioFormatConverter.decodeMuLawToPcm16(allCodes)
        val reEncoded = AudioFormatConverter.encodePcm16ToMuLaw(pcm)
        val pcm2 = AudioFormatConverter.decodeMuLawToPcm16(reEncoded)
        assertTrue(pcm.contentEquals(pcm2), "mu-law decode of a re-encoded quantised signal must be stable")
    }

    @Test
    fun `mulaw decodes known ITU-T reference values`() {
        // Pin exact parity with the C# MuLawToLinear bit math on anchor codes.
        // 0xFF = mu-law "positive zero" -> 0 ; 0x7F = "negative zero" -> 0.
        assertEquals(0, AudioFormatConverter.decodeMuLawToPcm16(byteArrayOf(0xFF.toByte())).let { readInt16Le(it, 0).toInt() })
        assertEquals(0, AudioFormatConverter.decodeMuLawToPcm16(byteArrayOf(0x7F.toByte())).let { readInt16Le(it, 0).toInt() })
        // 0x00 is the largest-magnitude negative code; 0x80 the largest positive.
        assertEquals(-32124, AudioFormatConverter.decodeMuLawToPcm16(byteArrayOf(0x00)).let { readInt16Le(it, 0).toInt() })
        assertEquals(32124, AudioFormatConverter.decodeMuLawToPcm16(byteArrayOf(0x80.toByte())).let { readInt16Le(it, 0).toInt() })
    }

    @Test
    fun `alaw quantisation is second-pass idempotent`() {
        val allCodes = ByteArray(256) { it.toByte() }
        val pcm = AudioFormatConverter.decodeALawToPcm16(allCodes)
        val reEncoded = AudioFormatConverter.encodePcm16ToALaw(pcm)
        val pcm2 = AudioFormatConverter.decodeALawToPcm16(reEncoded)
        assertTrue(pcm.contentEquals(pcm2), "a-law decode of a re-encoded quantised signal must be stable")
    }

    @Test
    fun `convert mulaw 8k to pcm16 16k doubles sample count`() {
        val mulaw = ByteArray(160) { (it % 256).toByte() } // 20 ms @ 8 kHz
        val out = AudioFormatConverter.convert(
            mulaw, AudioCodec.MuLaw, 8_000, AudioCodec.Pcm16, 16_000,
        )
        // 160 mu-law samples -> 160 pcm samples -> resampled 2x -> 320 samples -> 640 bytes
        assertEquals(640, out.size)
    }

    @Test
    fun `pcm16 passthrough at same rate is a copy`() {
        val input = pcm(100, -200, 300)
        val out = AudioFormatConverter.convert(input, AudioCodec.Pcm16, 16_000, AudioCodec.Pcm16, 16_000)
        assertTrue(input.contentEquals(out))
        assertNotEquals(System.identityHashCode(input), System.identityHashCode(out))
    }

    // ── resampling ───────────────────────────────────────────────────────

    @Test
    fun `linear resample halves sample count going 16k to 8k`() {
        val src = tone(320) // 320 samples = 640 bytes
        val dst = AudioFormatConverter.resamplePcm16Linear(src, 16_000, 8_000)
        assertEquals(160 * 2, dst.size)
    }

    @Test
    fun `resample same rate returns input`() {
        val src = tone(64)
        assertTrue(src === AudioFormatConverter.resamplePcm16Linear(src, 16_000, 16_000))
    }

    // ── NLMS echo canceller ──────────────────────────────────────────────

    @Test
    fun `nlms cancels a scaled echo of the reference`() {
        val ec = NlmsEchoCanceller()
        assertEquals("nlms", ec.backendId)

        // far-end reference tone; near-end = 0.5 * reference (pure echo, no near speech).
        val n = 4000
        val far = tone(n, freq = 300.0)
        val near = ByteArray(far.size)
        for (i in 0 until n) writeInt16Le(near, i * 2, (readSample(far, i) / 2).toShort())

        val dst = ByteArray(near.size)
        ec.cancel(near, far, 16_000, dst)

        // Residual energy in the second half should be far below the near-end echo energy
        // once the adaptive filter has converged.
        val half = n / 2
        var echoEnergy = 0.0
        var residEnergy = 0.0
        for (i in half until n) {
            val e = readSample(near, i).toDouble()
            val r = readSample(dst, i).toDouble()
            echoEnergy += e * e
            residEnergy += r * r
        }
        assertTrue(residEnergy < echoEnergy * 0.25, "NLMS should suppress the echo (resid=$residEnergy echo=$echoEnergy)")
    }

    @Test
    fun `nlms reset clears filter state`() {
        val ec = NlmsEchoCanceller()
        val far = tone(2000, freq = 250.0)
        val near = far.copyOf()
        val dst = ByteArray(near.size)
        ec.cancel(near, far, 16_000, dst)
        ec.reset()
        // After reset the first output equals mic-minus-zero-estimate = mic sample.
        val near2 = pcm(1000, 2000)
        val far2 = pcm(500, 500)
        val dst2 = ByteArray(near2.size)
        ec.cancel(near2, far2, 16_000, dst2)
        // First sample: estimate is 0 (weights cleared) so error == mic (1000).
        assertEquals(1000, readSample(dst2, 0))
    }

    @Test
    fun `nlms rejects mismatched lengths`() {
        val ec = NlmsEchoCanceller()
        var threw = false
        try {
            ec.cancel(ByteArray(4), ByteArray(6), 16_000, ByteArray(6))
        } catch (e: IllegalArgumentException) {
            threw = true
        }
        assertTrue(threw)
    }

    @Test
    fun `null echo canceller passes through`() {
        val ec = NullEchoCanceller.Instance
        assertEquals("null", ec.backendId)
        val near = pcm(1, 2, 3)
        val far = pcm(9, 9, 9)
        val dst = ByteArray(near.size)
        val written = ec.cancel(near, far, 16_000, dst)
        assertEquals(near.size, written)
        assertTrue(near.contentEquals(dst))
    }

    @Test
    fun `webrtc echo canceller falls back to nlms without a runner and reports fallback id`() {
        val ec = WebRtcEchoCanceller()
        assertEquals("webrtc-aec3 (fallback)", ec.backendId)
        val near = tone(1000)
        val far = tone(1000)
        val dst = ByteArray(near.size)
        assertEquals(near.size, ec.cancel(near, far, 16_000, dst))

        // With a runner wired, backendId flips and the runner is used.
        val runner = object : IEchoCancellerModelRunner {
            var processed = false
            override fun process(nearEnd: ByteArray, farEnd: ByteArray, sampleRateHz: Int, destination: ByteArray): Int {
                processed = true
                nearEnd.copyInto(destination)
                return nearEnd.size
            }
            override fun reset() {}
        }
        val ec2 = WebRtcEchoCanceller(runner)
        assertEquals("webrtc-aec3", ec2.backendId)
        ec2.cancel(near, far, 16_000, dst)
        assertTrue(runner.processed)
    }

    // ── noise reducers ───────────────────────────────────────────────────

    @Test
    fun `null noise reducer passes through`() {
        val nr = NullNoiseReducer.Instance
        assertEquals("null", nr.backendId)
        assertTrue(nr.isAvailable)
        val input = pcm(100, 200, 300)
        val dst = ByteArray(input.size)
        assertEquals(input.size, nr.reduce(input, 16_000, dst))
        assertTrue(input.contentEquals(dst))
    }

    @Test
    fun `spectral subtraction attenuates low-level samples and keeps loud ones`() {
        val nr = SpectralSubtractionNoiseReducer() // floor 0.008 * 32767 ~= 262
        assertEquals("passthrough", nr.backendId)
        val input = pcm(100, 20000)  // 100 is below floor, 20000 above
        val dst = ByteArray(input.size)
        nr.reduce(input, 16_000, dst)
        assertEquals((100 * 0.25f).toInt(), readSample(dst, 0)) // attenuated
        assertEquals(20000, readSample(dst, 1))               // preserved
    }

    @Test
    fun `krisp and deepfilternet report fallback ids and pass through without a runner`() {
        val krisp = KrispNoiseReducer()
        val dfn = DeepFilterNetNoiseReducer()
        assertEquals("krisp (fallback)", krisp.backendId)
        assertEquals("deepfilternet (fallback)", dfn.backendId)

        val runner = object : INoiseReducerModelRunner {
            override fun process(audioPcm16Mono: ByteArray, sampleRateHz: Int, destination: ByteArray): Int {
                audioPcm16Mono.copyInto(destination)
                return audioPcm16Mono.size
            }
        }
        assertEquals("krisp", KrispNoiseReducer(runner).backendId)
        assertEquals("deepfilternet", DeepFilterNetNoiseReducer(runner).backendId)
    }

    // ── voice-activity detector ──────────────────────────────────────────

    @Test
    fun `energy vad flags a loud tone as speech and silence as not`() {
        val vad = EnergyVoiceActivityDetector()
        assertEquals("energy", vad.backendId)

        val speech = vad.classify(tone(480), 16_000, Duration.ZERO)
        assertTrue(speech.isSpeech, "loud voiced tone should be speech")
        assertTrue(speech.speechProbability >= vad.speechThreshold)

        vad.reset()
        val quiet = vad.classify(silence(480), 16_000, Duration.ZERO)
        assertFalse(quiet.isSpeech, "silence should not be speech")
    }

    @Test
    fun `energy vad holds speech through hangover frames`() {
        val vad = EnergyVoiceActivityDetector(hangoverFrames = 3)
        vad.classify(tone(480), 16_000, Duration.ZERO) // triggers, sets hangover = 3
        // Following silence frames remain "speech" until hangover drains.
        assertTrue(vad.classify(silence(480), 16_000, Duration.ZERO).isSpeech)
        assertTrue(vad.classify(silence(480), 16_000, Duration.ZERO).isSpeech)
        assertTrue(vad.classify(silence(480), 16_000, Duration.ZERO).isSpeech)
        assertFalse(vad.classify(silence(480), 16_000, Duration.ZERO).isSpeech) // hangover exhausted
    }

    @Test
    fun `null vad always reports speech`() {
        val vad = NullVoiceActivityDetector.Instance
        assertEquals("null", vad.backendId)
        val r = vad.classify(silence(10), 16_000, Duration.ofMillis(5))
        assertTrue(r.isSpeech)
        assertEquals(1f, r.speechProbability)
        assertEquals(Duration.ofMillis(5), r.offset)
    }

    @Test
    fun `silero vad falls back to energy without a runner`() {
        val vad = SileroVoiceActivityDetector()
        assertEquals("silero (fallback)", vad.backendId)
        assertTrue(vad.classify(tone(480), 16_000, Duration.ZERO).isSpeech)

        val runner = object : IVadModelRunner {
            override fun scoreFrame(audioPcm16Mono: ByteArray, sampleRateHz: Int): Float = 0.9f
        }
        val vad2 = SileroVoiceActivityDetector(runner, speechThreshold = 0.5f)
        assertEquals("silero", vad2.backendId)
        val r = vad2.classify(silence(480), 16_000, Duration.ZERO)
        assertTrue(r.isSpeech)
        assertEquals(0.9f, r.speechProbability)
    }

    // ── end-of-turn detectors ────────────────────────────────────────────

    @Test
    fun `null end-of-turn always complete`() {
        val d = NullEndOfTurnDetector.Instance
        assertEquals("null", d.backendId)
        val r = d.predict("hello", Duration.ZERO)
        assertTrue(r.isComplete)
        assertEquals(1f, r.confidence)
        assertEquals(0, r.waitMoreMs)
    }

    @Test
    fun `rule-based end-of-turn completes on terminal punctuation after min silence`() {
        val d = RuleBasedEndOfTurnDetector()
        assertEquals("rules", d.backendId)
        val r = d.predict("what time is it?", Duration.ofMillis(500))
        assertTrue(r.isComplete)
        assertEquals(0.9f, r.confidence)
    }

    @Test
    fun `rule-based end-of-turn waits on a hanging connector`() {
        val d = RuleBasedEndOfTurnDetector()
        val r = d.predict("I want to go and", Duration.ofMillis(200))
        assertFalse(r.isComplete)
        assertTrue(r.waitMoreMs > 0)
        // Once hanging-silence elapses it completes.
        val r2 = d.predict("I want to go and", Duration.ofMillis(1000))
        assertTrue(r2.isComplete)
        assertEquals(0.6f, r2.confidence)
    }

    @Test
    fun `rule-based end-of-turn force-completes past max silence`() {
        val d = RuleBasedEndOfTurnDetector()
        val r = d.predict("um", Duration.ofMillis(3000))
        assertTrue(r.isComplete)
        assertEquals(0.7f, r.confidence)
    }

    @Test
    fun `rule-based end-of-turn on empty transcript waits`() {
        val d = RuleBasedEndOfTurnDetector()
        val r = d.predict("", Duration.ZERO)
        assertFalse(r.isComplete)
        assertEquals(0.2f, r.confidence)
        assertTrue(r.waitMoreMs >= 150)
    }

    @Test
    fun `smart-turn falls back to rules and honours a wired model`() {
        val fallback = SmartTurnDetector()
        assertEquals("smart-turn (fallback)", fallback.backendId)
        assertTrue(fallback.predict("done.", Duration.ofMillis(500)).isComplete)

        val runner = object : ITurnModelRunner {
            override fun scoreCompletion(partialTranscript: String, trailingSilence: Duration): Float = 0.9f
        }
        val d = SmartTurnDetector(runner, threshold = 0.5f)
        assertEquals("smart-turn-v2", d.backendId)
        val r = d.predict("anything", Duration.ZERO)
        assertTrue(r.isComplete)
        assertEquals(0.9f, r.confidence)

        val low = object : ITurnModelRunner {
            override fun scoreCompletion(partialTranscript: String, trailingSilence: Duration): Float = 0.25f
        }
        val d2 = SmartTurnDetector(low, threshold = 0.5f)
        val r2 = d2.predict("anything", Duration.ZERO)
        assertFalse(r2.isComplete)
        assertEquals(Math.round((1f - 0.25f) * 1000f), r2.waitMoreMs)
    }

    // ── null recogniser / synthesizer / wake-word / ocr ──────────────────

    @Test
    fun `null recognizer synthesizer ocr wake-word are fail-closed`() = runBlocking {
        val rec = NullSpeechRecognizer.Instance
        assertEquals("null", rec.backendId)
        val tr = rec.transcribeAsync(pcm(1, 2), 16_000, "en")
        assertEquals("", tr.text)
        assertEquals("en", tr.language)
        assertTrue(tr.segments.isEmpty())

        val syn = NullSpeechSynthesizer.Instance
        val sr = syn.synthesizeAsync("hi")
        assertEquals(0, sr.audioPcm16Mono.size)
        assertEquals(16_000, sr.sampleRateHz)

        val ocr = NullOpticalCharacterRecognizer.Instance
        val or = ocr.recognizeAsync(ByteArray(4))
        assertEquals("", or.text)
        assertTrue(or.blocks.isEmpty())

        val ww = NullWakeWordDetector()
        assertEquals("null", ww.backendId)
        ww.startAsync()
        ww.stopAsync()
        var fired = false
        val handle = ww.subscribe { fired = true }
        handle.close()
        ww.closeAsync()
        assertFalse(fired)
    }
}
