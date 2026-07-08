// VoiceListenerTest.kt
//
// Verifies VoiceCompanionListener against the C# reference: a pipeline
// transcription raises UtteranceDetected, is forwarded to the session, and the
// reply surfaces via ResponseReady; a session failure is swallowed (no crash);
// start/stop drive the pipeline; and close disposes both.

package com.bhengubv.circleai.companion

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.consumeAsFlow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.time.Instant
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class VoiceListenerTest {

    /** A pipeline that emits a fixed list of transcriptions, then completes. */
    private class ScriptedPipeline(private val items: List<VoiceTranscription>) : IVoicePipeline {
        val started = AtomicBoolean(false)
        val stopped = AtomicBoolean(false)
        val closed = AtomicBoolean(false)
        override fun transcriptions(): Flow<VoiceTranscription> = flow {
            for (i in items) emit(i)
        }
        override suspend fun startAsync() { started.set(true) }
        override suspend fun stopAsync() { stopped.set(true) }
        override suspend fun closeAsync() { closed.set(true) }
    }

    /** Minimal session: returns a canned reply (or throws). */
    private class StubSession(
        private val reply: String,
        private val fail: Boolean = false,
    ) : ICompanionSession {
        val closed = AtomicBoolean(false)
        override val sessionId = "s"
        override val identityId = "id"
        override val interfaceKind = InterfaceKind.Headless
        override val history = emptyList<CompanionTurn>()
        override val proactiveEvents: Flow<CompanionProactiveEvent> = emptyFlow()
        override suspend fun sendAsync(message: String): String {
            if (fail) throw RuntimeException("session boom")
            return reply
        }
        override fun streamAsync(message: String): Flow<String> = emptyFlow()
        override suspend fun agentAsync(instruction: String): String = reply
        override fun getContext(): CompanionContext = CompanionContext(
            identityId, "", null, interfaceKind, "", "", emptyList(), emptyList(), Instant.now(),
        )
        override suspend fun refreshContextAsync() {}
        override suspend fun signalFeedbackAsync(positive: Boolean, note: String?) {}
        override fun close() { closed.set(true) }
    }

    @Test
    fun `transcription flows through to a companion reply`() = runBlocking {
        val pipeline = ScriptedPipeline(listOf(VoiceTranscription("what time is it", 0.95f)))
        val session = StubSession("It is noon.")
        val listener = VoiceCompanionListener(pipeline, session)

        val utterance = AtomicReference<UtteranceDetectedEvent>()
        val response = AtomicReference<ResponseReadyEvent>()
        val latch = CountDownLatch(1)
        listener.onUtteranceDetected { utterance.set(it) }
        listener.onResponseReady { response.set(it); latch.countDown() }

        listener.startAsync()
        assertTrue(pipeline.started.get())
        assertTrue(latch.await(3, TimeUnit.SECONDS), "response not produced in time")

        assertEquals("what time is it", utterance.get().text)
        assertEquals(0.95f, utterance.get().confidence)
        assertEquals("It is noon.", response.get().text)
        assertEquals("what time is it", response.get().originalUtterance)

        listener.closeAsync()
        assertTrue(pipeline.closed.get())
        assertTrue(session.closed.get())
    }

    @Test
    fun `a failing session does not crash the listener`() = runBlocking {
        val pipeline = ScriptedPipeline(listOf(VoiceTranscription("hello", 1.0f)))
        val session = StubSession("unused", fail = true)
        val listener = VoiceCompanionListener(pipeline, session)

        val utteranceLatch = CountDownLatch(1)
        val gotResponse = AtomicBoolean(false)
        listener.onUtteranceDetected { utteranceLatch.countDown() }
        listener.onResponseReady { gotResponse.set(true) }

        listener.startAsync()
        // The utterance is still detected even though the session throws.
        assertTrue(utteranceLatch.await(3, TimeUnit.SECONDS))
        // Give the (swallowed) failure a moment; no ResponseReady should fire.
        Thread.sleep(200)
        assertTrue(!gotResponse.get())
        listener.closeAsync()
    }

    @Test
    fun `stop drives the pipeline stop`() = runBlocking {
        val pipeline = ScriptedPipeline(emptyList())
        val listener = VoiceCompanionListener(pipeline, StubSession("x"))
        listener.startAsync()
        listener.stopAsync()
        assertTrue(pipeline.stopped.get())
        listener.closeAsync()
    }
}
