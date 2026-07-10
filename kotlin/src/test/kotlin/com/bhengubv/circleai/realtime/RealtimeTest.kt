// RealtimeTest.kt
//
// Verifies the CircleAI.Realtime port against the C# reference semantics:
//   - SampleRateOf mapping (16k/24k/8k)
//   - SilenceTextToAudio sizing: max(50, words*80) ms of 16-bit zero PCM, integer
//     sample count sr*ms/1000, *2 bytes
//   - Loopback session: echoes inbound audio; RMS silence detector drives
//     SpeechStarted/Ended; SendText emits delta+audio+final+turn-complete with
//     correct running offset; SendToolResult truncates to 60 chars; Cancel emits
//     TurnComplete; DisposeAsync completes the streams
//   - Service: providerId/isConfigured; StartSession yields a live loopback session
//   - Null implementations are inert; NullRealtimeService throws on StartSession

package com.bhengubv.circleai.realtime

import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertIs
import kotlin.test.assertTrue

class RealtimeTest {

    // ── Sample-rate mapping ──────────────────────────────────────────────────
    @Test
    fun `sample rate mapping matches the reference`() {
        assertEquals(16_000, LoopbackRealtimeSession.sampleRateOf(RealtimeAudioFormat.Pcm16k))
        assertEquals(24_000, LoopbackRealtimeSession.sampleRateOf(RealtimeAudioFormat.Pcm24k))
        assertEquals(8_000, LoopbackRealtimeSession.sampleRateOf(RealtimeAudioFormat.Mulaw8k))
    }

    // ── Silence TTS sizing ───────────────────────────────────────────────────
    @Test
    fun `silence tts is sized 80ms per word with a 50ms floor of 16-bit zero pcm`() = runTest {
        // 3 words @ 24kHz -> 240ms -> 24000*240/1000 = 5760 samples -> 11520 bytes.
        val threeWords = LoopbackRealtimeService.SilenceTextToAudio.invoke("one two three", RealtimeAudioFormat.Pcm24k)
        assertEquals(11_520, threeWords.size)
        assertTrue(threeWords.all { it.toInt() == 0 }) // real zero-amplitude PCM

        // Empty text -> 0 words -> floor 50ms -> 24000*50/1000 = 1200 samples -> 2400 bytes.
        val empty = LoopbackRealtimeService.SilenceTextToAudio.invoke("", RealtimeAudioFormat.Pcm24k)
        assertEquals(2_400, empty.size)

        // 1 word @ 16kHz -> max(50,80)=80ms -> 16000*80/1000 = 1280 samples -> 2560 bytes.
        val oneWord16k = LoopbackRealtimeService.SilenceTextToAudio.invoke("hi", RealtimeAudioFormat.Pcm16k)
        assertEquals(2_560, oneWord16k.size)
    }

    // ── Loopback echo + silence detection ────────────────────────────────────
    @Test
    fun `sendAudio echoes inbound and drives speech events by rms`() = runTest {
        val svc = LoopbackRealtimeService()
        val session = svc.startSessionAsync(RealtimeSessionConfig(model = "test"))

        // Loud frame: 64 samples of 10000 -> RMS 10000 -> SpeechStarted, then echoed.
        val loud = ByteArray(128)
        for (i in 0 until 64) {
            val v = 10_000
            loud[i * 2] = (v and 0xFF).toByte()
            loud[i * 2 + 1] = ((v shr 8) and 0xFF).toByte()
        }
        session.sendAudioAsync(RealtimeAudioFrame(loud, RealtimeAudioFormat.Pcm16k, Duration.ZERO))

        // Silent frame: 64 zero samples -> RMS 0 -> SpeechEnded, then echoed.
        val silent = ByteArray(128)
        session.sendAudioAsync(RealtimeAudioFrame(silent, RealtimeAudioFormat.Pcm16k, Duration.ZERO))

        session.disposeAsync()

        val frames = session.receiveAudioAsync().toList()
        assertEquals(2, frames.size) // both frames echoed back
        assertTrue(frames[0].pcm.contentEquals(loud))
        assertTrue(frames[1].pcm.contentEquals(silent))

        val events = session.receiveEventsAsync().toList()
        assertEquals(2, events.size)
        assertIs<SpeechStartedEvent>(events[0])
        assertIs<SpeechEndedEvent>(events[1])
    }

    @Test
    fun `a tiny frame under 64 bytes is treated as silent (no speech-started)`() = runTest {
        val session = LoopbackRealtimeSession(RealtimeSessionConfig(model = "test"))
        // 10 bytes < 64 -> IsSilent short-circuits true -> no state change from silent.
        session.sendAudioAsync(RealtimeAudioFrame(ByteArray(10) { 0x7F }, RealtimeAudioFormat.Pcm16k, Duration.ZERO))
        session.disposeAsync()
        assertTrue(session.receiveEventsAsync().toList().isEmpty())
        assertEquals(1, session.receiveAudioAsync().toList().size) // still echoed
    }

    // ── SendText: transcript + audio + turn-complete, with offset accounting ──
    @Test
    fun `sendText emits delta, audio, final, turn-complete with running offset`() = runTest {
        val session = LoopbackRealtimeSession(RealtimeSessionConfig(model = "test", audioFormat = RealtimeAudioFormat.Pcm24k))
        session.sendTextAsync("one two three") // 240ms of audio -> offset advances to 240ms
        session.sendTextAsync("four")          // second frame carries offset 240ms
        session.disposeAsync()

        val events = session.receiveEventsAsync().toList()
        // Per SendText: Delta, Final, TurnComplete (audio goes to the audio channel).
        assertEquals(6, events.size)
        assertIs<TranscriptDeltaEvent>(events[0])
        assertEquals("one two three", (events[0] as TranscriptDeltaEvent).delta)
        assertEquals(RealtimeDirection.Outbound, (events[0] as TranscriptDeltaEvent).direction)
        assertIs<TranscriptFinalEvent>(events[1])
        assertEquals("one two three", (events[1] as TranscriptFinalEvent).text)
        assertIs<TurnCompleteEvent>(events[2])
        assertIs<TranscriptDeltaEvent>(events[3])
        assertIs<TranscriptFinalEvent>(events[4])
        assertIs<TurnCompleteEvent>(events[5])

        val frames = session.receiveAudioAsync().toList()
        assertEquals(2, frames.size)
        // First frame at offset 0; second at 240ms (11520 bytes / 2 / 24000 * 1000).
        assertEquals(Duration.ZERO, frames[0].offset)
        assertEquals(Duration.ofMillis(240), frames[1].offset)
        assertEquals(11_520, frames[0].pcm.size)
    }

    // ── Tool result + cancel ─────────────────────────────────────────────────
    @Test
    fun `sendToolResult emits a truncated delta and rejects blank callId`() = runTest {
        val session = LoopbackRealtimeSession(RealtimeSessionConfig(model = "test"))
        assertFailsWith<IllegalArgumentException> { session.sendToolResultAsync("", "{}") }

        val long = "x".repeat(100)
        session.sendToolResultAsync("call-1", long)
        session.disposeAsync()

        val events = session.receiveEventsAsync().toList()
        assertEquals(1, events.size)
        val d = assertIs<TranscriptDeltaEvent>(events[0])
        // "[tool call-1: " + 60 chars + "…]" — truncated to 60 with an ellipsis.
        assertEquals("[tool call-1: " + "x".repeat(60) + "…]", d.delta)
    }

    @Test
    fun `cancelResponse emits a turn-complete`() = runTest {
        val session = LoopbackRealtimeSession(RealtimeSessionConfig(model = "test"))
        session.cancelResponseAsync()
        session.disposeAsync()
        val events = session.receiveEventsAsync().toList()
        assertEquals(1, events.size)
        assertIs<TurnCompleteEvent>(events[0])
    }

    // ── Service surface ──────────────────────────────────────────────────────
    @Test
    fun `service reports loopback identity and opens a session`() = runTest {
        val svc = LoopbackRealtimeService()
        assertEquals("loopback", svc.providerId)
        assertTrue(svc.isConfigured)
        val s = svc.startSessionAsync(RealtimeSessionConfig(model = "m"))
        assertTrue(s.sessionId.startsWith("loop-"))
        s.disposeAsync()
    }

    @Test
    fun `custom text-to-audio delegate is honoured`() = runTest {
        val fixed = ByteArray(8) { 1 }
        val svc = LoopbackRealtimeService { _, _ -> fixed }
        val s = svc.startSessionAsync(RealtimeSessionConfig(model = "m"))
        s.sendTextAsync("anything")
        s.disposeAsync()
        val frames = s.receiveAudioAsync().toList()
        assertEquals(1, frames.size)
        assertTrue(frames[0].pcm.contentEquals(fixed))
    }

    // ── Null implementations ─────────────────────────────────────────────────
    @Test
    fun `null service throws on start and reports unconfigured`() = runTest {
        val svc = NullRealtimeService.Instance
        assertEquals("null", svc.providerId)
        assertTrue(!svc.isConfigured)
        assertFailsWith<IllegalStateException> { svc.startSessionAsync(RealtimeSessionConfig(model = "m")) }
    }

    @Test
    fun `null session is muted`() = runTest {
        val s = NullRealtimeSession()
        assertEquals("null", s.sessionId)
        s.sendAudioAsync(RealtimeAudioFrame(ByteArray(0), RealtimeAudioFormat.Pcm16k, Duration.ZERO))
        s.sendTextAsync("hi")
        s.sendToolResultAsync("c", "{}")
        s.cancelResponseAsync()
        assertTrue(s.receiveAudioAsync().toList().isEmpty())
        assertTrue(s.receiveEventsAsync().toList().isEmpty())
        s.disposeAsync()
    }
}
