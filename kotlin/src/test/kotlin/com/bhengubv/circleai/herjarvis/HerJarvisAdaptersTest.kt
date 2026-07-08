// HerJarvisAdaptersTest.kt
//
// Verifies the ONNX-style adapters delegate to the injected neural port and
// apply the C# fallback behaviour: OnnxSpeakerIdentityAdapter forwards
// enroll/identify verbatim; OnnxSpeechEmotionSensor decodes base64 PCM from the
// fused JSON, hands it to the detector, and returns neutral on any missing /
// unparseable audio.

package com.bhengubv.circleai.herjarvis

import com.bhengubv.circleai.companion.herjarvis.EmotionFrame
import com.bhengubv.circleai.companion.herjarvis.ISpeakerIdentity
import com.bhengubv.circleai.companion.herjarvis.ISpeechEmotionDetector
import com.bhengubv.circleai.companion.herjarvis.OnnxSpeakerIdentityAdapter
import com.bhengubv.circleai.companion.herjarvis.OnnxSpeechEmotionSensor
import kotlinx.coroutines.test.runTest
import java.util.Base64
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class HerJarvisAdaptersTest {

    private class FakeSpeaker : ISpeakerIdentity {
        val enrolled = ArrayList<String>()
        override suspend fun identifyAsync(audioPcm16: ByteArray, sampleRateHz: Int): String? =
            if (audioPcm16.isNotEmpty()) "spk-$sampleRateHz" else null

        override suspend fun enrollAsync(userId: String, audioPcm16: ByteArray, sampleRateHz: Int) {
            enrolled.add(userId)
        }
    }

    private class FakeEmotion(private val frame: EmotionFrame?) : ISpeechEmotionDetector {
        var lastBytes: ByteArray? = null
        var lastSr: Int = -1
        override suspend fun senseAsync(audioPcm16: ByteArray, sampleRateHz: Int): EmotionFrame? {
            lastBytes = audioPcm16
            lastSr = sampleRateHz
            return frame
        }
    }

    @Test
    fun `speaker adapter forwards identify and enroll`() = runTest {
        val inner = FakeSpeaker()
        val a = OnnxSpeakerIdentityAdapter(inner)
        a.enrollAsync("alice", byteArrayOf(1, 2), 16000)
        assertEquals(listOf("alice"), inner.enrolled)
        assertEquals("spk-16000", a.identifyAsync(byteArrayOf(1, 2), 16000))
    }

    @Test
    fun `emotion sensor decodes base64 audio and forwards to the detector`() = runTest {
        val detector = FakeEmotion(EmotionFrame("joy", 0.8, 0.9))
        val sensor = OnnxSpeechEmotionSensor(detector)
        val pcm = byteArrayOf(4, 5, 6, 7)
        val b64 = Base64.getEncoder().encodeToString(pcm)
        val frame = sensor.senseAsync("""{"audio_pcm16_b64":"$b64","sample_rate_hz":22050}""")
        assertEquals("joy", frame.label)
        assertEquals(22050, detector.lastSr)
        assertTrue(detector.lastBytes!!.contentEquals(pcm))
    }

    @Test
    fun `emotion sensor defaults sample rate to 16k`() = runTest {
        val detector = FakeEmotion(EmotionFrame("calm", 0.1, 0.5))
        val sensor = OnnxSpeechEmotionSensor(detector)
        val b64 = Base64.getEncoder().encodeToString(byteArrayOf(1))
        sensor.senseAsync("""{"audio_pcm16_b64":"$b64"}""")
        assertEquals(16000, detector.lastSr)
    }

    @Test
    fun `emotion sensor returns neutral on missing or bad audio`() = runTest {
        val detector = FakeEmotion(EmotionFrame("joy", 1.0, 1.0))
        val sensor = OnnxSpeechEmotionSensor(detector)
        assertEquals("neutral", sensor.senseAsync("").label)
        assertEquals("neutral", sensor.senseAsync("not json").label)
        assertEquals("neutral", sensor.senseAsync("""{"other":1}""").label)
        assertEquals("neutral", sensor.senseAsync("""{"audio_pcm16_b64":""}""").label)
        assertEquals("neutral", sensor.senseAsync("""[1,2,3]""").label)
    }

    @Test
    fun `emotion sensor returns neutral when the detector cannot classify`() = runTest {
        val sensor = OnnxSpeechEmotionSensor(FakeEmotion(null))
        val b64 = Base64.getEncoder().encodeToString(byteArrayOf(1, 2))
        assertEquals("neutral", sensor.senseAsync("""{"audio_pcm16_b64":"$b64"}""").label)
    }
}
