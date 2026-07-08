// VoiceListener.kt
//
// Kotlin port of CircleAI.Companion.IVoiceListener + VoiceCompanionListener —
// the C# reference (IVoiceListener.cs, VoiceCompanionListener.cs) is the EXACT
// spec. Bridges a voice pipeline with an ICompanionSession: transcribed
// utterances are forwarded to the session and the Companion's reply is surfaced
// back to subscribers.
//
// The C# type wires a CircleAI.Voice.VoicePipeline (via its .NET `Transcribed`
// event) to the session. That pipeline is not in the Kotlin tree, so — per the
// porting rules — the pipeline is an INJECTED dependency behind a minimal
// [IVoicePipeline] contract that exposes a cold Flow of transcriptions (the
// idiomatic Kotlin equivalent of the event). .NET events map to registerable
// listener callbacks. The forward-off-the-hot-path + swallow-failures semantics
// are preserved.

package com.bhengubv.circleai.companion

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.launch
import java.time.Instant
import java.util.concurrent.CopyOnWriteArrayList

// ---------------------------------------------------------------------------
// Event payloads
// ---------------------------------------------------------------------------

/**
 * Raised when a user utterance has been fully transcribed and forwarded to the
 * Companion session. Mirrors C# `UtteranceDetectedEventArgs`.
 */
data class UtteranceDetectedEvent(
    val text: String,
    val confidence: Float = 0f,
    val detectedAt: Instant = Instant.now(),
)

/**
 * Raised when the Companion has produced a reply to a voice utterance. Mirrors
 * C# `ResponseReadyEventArgs`.
 */
data class ResponseReadyEvent(
    val text: String,
    val originalUtterance: String,
    val completedAt: Instant = Instant.now(),
)

// ---------------------------------------------------------------------------
// Injected voice-pipeline port
// ---------------------------------------------------------------------------

/** One transcription result from the pipeline: [text] with a [confidence] and completion time. */
data class VoiceTranscription(val text: String, val confidence: Float, val completedAt: Instant = Instant.now())

/**
 * Minimal voice-pipeline port. The host's real pipeline (wake-word + ASR)
 * exposes its transcriptions as a cold [Flow]; [startAsync]/[stopAsync] control
 * capture. Injected so the neural ASR binding never leaks into this module.
 */
interface IVoicePipeline {
    fun transcriptions(): Flow<VoiceTranscription>
    suspend fun startAsync()
    suspend fun stopAsync()
    suspend fun closeAsync() {}
}

// ---------------------------------------------------------------------------
// IVoiceListener
// ---------------------------------------------------------------------------

/**
 * Bridges a voice pipeline with an [ICompanionSession]. Register listeners for
 * transcribed utterances and Companion replies; [startAsync]/[stopAsync] control
 * the underlying pipeline. Mirrors C# `IVoiceListener`.
 */
interface IVoiceListener {
    fun onUtteranceDetected(listener: (UtteranceDetectedEvent) -> Unit)
    fun onResponseReady(listener: (ResponseReadyEvent) -> Unit)
    suspend fun startAsync()
    suspend fun stopAsync()
    suspend fun closeAsync()
}

// ---------------------------------------------------------------------------
// VoiceCompanionListener
// ---------------------------------------------------------------------------

/**
 * Wires an [IVoicePipeline] to an [ICompanionSession]: each transcription is
 * forwarded to the session and the reply surfaced via [onResponseReady]. The
 * session call runs off the pipeline collection so it never blocks capture;
 * failures are logged, not thrown. Owns both the pipeline and the session —
 * [closeAsync] closes them.
 */
class VoiceCompanionListener(
    private val pipeline: IVoicePipeline,
    private val session: ICompanionSession,
) : IVoiceListener {

    private val utteranceListeners = CopyOnWriteArrayList<(UtteranceDetectedEvent) -> Unit>()
    private val responseListeners = CopyOnWriteArrayList<(ResponseReadyEvent) -> Unit>()

    @Volatile
    private var disposed = false

    private val scope = CoroutineScope(Dispatchers.Default + Job())
    private var collectJob: Job? = null

    override fun onUtteranceDetected(listener: (UtteranceDetectedEvent) -> Unit) {
        utteranceListeners.add(listener)
    }

    override fun onResponseReady(listener: (ResponseReadyEvent) -> Unit) {
        responseListeners.add(listener)
    }

    override suspend fun startAsync() {
        check(!disposed) { "VoiceCompanionListener is disposed" }
        if (collectJob == null) {
            collectJob = scope.launch {
                pipeline.transcriptions().collect { onTranscribed(it) }
            }
        }
        pipeline.startAsync()
    }

    override suspend fun stopAsync() {
        check(!disposed) { "VoiceCompanionListener is disposed" }
        pipeline.stopAsync()
    }

    /** Handle one transcription: notify subscribers, then forward to the session off the hot path. */
    private fun onTranscribed(t: VoiceTranscription) {
        if (disposed) return

        val detected = UtteranceDetectedEvent(
            text = t.text,
            confidence = t.confidence,
            detectedAt = t.completedAt,
        )
        for (l in utteranceListeners) l(detected)

        // Forward to the Companion asynchronously — never block the pipeline flow.
        scope.launch {
            try {
                val reply = session.sendAsync(t.text)
                if (!disposed) {
                    val ready = ResponseReadyEvent(
                        text = reply,
                        originalUtterance = t.text,
                        completedAt = Instant.now(),
                    )
                    for (l in responseListeners) l(ready)
                }
            } catch (ex: Exception) {
                System.err.println("VoiceCompanionListener: session failed for utterance '${t.text}': ${ex.message}")
            }
        }
    }

    override suspend fun closeAsync() {
        if (disposed) return
        disposed = true
        collectJob?.cancel()
        scope.cancel()
        pipeline.closeAsync()
        session.close()
    }
}
