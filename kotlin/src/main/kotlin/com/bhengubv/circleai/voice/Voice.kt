// Voice.kt
//
// Kotlin port of CircleAI.Voice — the C# reference (AudioFormat.cs,
// IVoiceTranscriber.cs, IWakeWordDetector.cs, ITtsEngine.cs,
// IVoiceActivityDetector.cs, VoicePipeline.cs, EnergyVadDetector.cs,
// EnergyWakeWordDetector.cs, Null*.cs, OnnxSpeakerIdentity.cs,
// OnnxSpeechEmotionDetector.cs) is the EXACT spec.
//
// This is the higher-level streaming voice surface: audio capture, VAD,
// transcription, wake-word, TTS, speaker identity, and speech-emotion — plus
// the VoicePipeline that composes them.
//
// Design fidelity notes:
//   * C# `record`                          -> Kotlin `data class`.
//   * C# `Task<T>`/`ValueTask<T>`          -> `suspend fun`.
//   * C# `IAsyncEnumerable<T>`             -> `kotlinx.coroutines.flow.Flow<T>`.
//   * C# `IAsyncDisposable`                -> `suspend fun closeAsync()` (+ AutoCloseable).
//   * C# `event EventHandler<T>`           -> registerable listener callbacks
//                                            (CopyOnWriteArrayList of lambdas).
//   * C# `ReadOnlyMemory<byte>`            -> `ByteArray`.
//   * ONNX / Whisper / native mic backends are INJECTED behind minimal runner
//     interfaces; the deterministic managed logic (energy VAD framing, RMS,
//     wake-word ASR loop, speaker centroid enrollment + cosine ID, emotion
//     circumplex + softmax) is ported algorithm-for-algorithm.
//
// CONCURRENCY: the wake-word background loop re-collects the COLD capture Flow
// per activation (as the C# re-enumerates CaptureAsync); wake-word fan-out uses a
// CopyOnWriteArrayList so no lock is held while a subscriber callback runs; the
// pipeline forwards the session call off the collection path. No stream
// continuation is completed under a lock its cleanup handler also takes.

package com.bhengubv.circleai.voice

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.launch
import java.io.ByteArrayOutputStream
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.math.exp
import kotlin.math.min
import kotlin.math.sqrt

// =====================================================================
// AudioFormat
// =====================================================================

/**
 * Describes a PCM audio format expected or produced by voice components.
 * Mirrors C# `AudioFormat`.
 *
 * @param sampleRate Samples per second (e.g. 16000 for 16 kHz).
 * @param channels Number of interleaved channels (1 = mono, 2 = stereo).
 * @param bitsPerSample Bit depth of each sample (e.g. 16 for signed 16-bit PCM).
 */
data class AudioFormat(val sampleRate: Int, val channels: Int, val bitsPerSample: Int) {
    companion object {
        /**
         * Canonical input format expected by Butler / B! voice components:
         * PCM signed 16-bit, mono, 16 kHz.
         */
        val Pcm16Mono16k = AudioFormat(16_000, 1, 16)
    }
}

// =====================================================================
// Transcription records
// =====================================================================

/**
 * Final transcription result produced by [IVoiceTranscriber.transcribeAsync].
 * Mirrors C# `TranscriptionResult` (Voice).
 *
 * @param text The recognised text. Empty string if nothing was recognised.
 * @param confidence Engine-reported confidence in [0, 1].
 * @param languageCode Detected language as BCP-47 / ISO 639 (e.g. "en", "zu", "und").
 */
data class TranscriptionResult(val text: String, val confidence: Float, val languageCode: String)

/**
 * Partial or final transcription produced during streaming recognition.
 * Mirrors C# `PartialTranscription`.
 *
 * @param text The recognised text so far.
 * @param isFinal True when this is the final transcription for the current utterance.
 * @param confidence Engine-reported confidence in [0, 1].
 */
data class PartialTranscription(val text: String, val isFinal: Boolean, val confidence: Float)

/**
 * Represents a single segment identified by a [IVoiceActivityDetector].
 * Mirrors C# `VadSegment`.
 *
 * @param audio The raw PCM audio bytes for this segment. Non-empty for speech segments.
 * @param isSpeech True when this segment contains detected speech.
 */
data class VadSegment(val audio: ByteArray, val isSpeech: Boolean) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is VadSegment) return false
        return isSpeech == other.isSpeech && audio.contentEquals(other.audio)
    }

    override fun hashCode(): Int = 31 * audio.contentHashCode() + isSpeech.hashCode()
}

// =====================================================================
// Wake-word event
// =====================================================================

/**
 * Payload describing a single wake-word detection event. Mirrors C#
 * `WakeWordDetectedEventArgs`.
 */
data class WakeWordDetectedEvent(
    val wakeWord: String,
    val detectedAt: Instant = Instant.now(),
    val confidence: Float = 0f,
)

/**
 * Payload describing a completed transcription produced by [VoicePipeline] after
 * a wake-word activation. Mirrors C# `TranscribedEventArgs`.
 */
data class TranscribedEvent(
    val result: TranscriptionResult,
    val completedAt: Instant = Instant.now(),
)

// =====================================================================
// TTS
// =====================================================================

/**
 * Result of a single-shot TTS synthesis operation. Mirrors C# `TtsSynthesisResult`.
 *
 * @param audioData The complete PCM audio buffer. Empty when no audio was produced.
 * @param sampleRate Samples per second (e.g. 24000 for 24 kHz).
 * @param channels Number of interleaved audio channels (1 = mono, 2 = stereo).
 * @param bitsPerSample Bit depth of each sample (e.g. 16 for signed 16-bit PCM).
 */
data class TtsSynthesisResult(
    val audioData: ByteArray,
    val sampleRate: Int,
    val channels: Int,
    val bitsPerSample: Int,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is TtsSynthesisResult) return false
        return sampleRate == other.sampleRate &&
            channels == other.channels &&
            bitsPerSample == other.bitsPerSample &&
            audioData.contentEquals(other.audioData)
    }

    override fun hashCode(): Int {
        var r = audioData.contentHashCode()
        r = 31 * r + sampleRate
        r = 31 * r + channels
        r = 31 * r + bitsPerSample
        return r
    }
}

/**
 * Text-to-speech engine that converts generated text into PCM audio. Mirrors C#
 * `ITtsEngine`.
 */
interface ITtsEngine {
    /** Synthesise [text] to a single PCM audio buffer. */
    suspend fun synthesiseAsync(text: String): TtsSynthesisResult

    /** Stream PCM audio chunks as they are synthesised. */
    fun streamSynthesiseAsync(text: String): Flow<ByteArray>
}

// =====================================================================
// Core interfaces
// =====================================================================

/**
 * Converts captured audio into text. Consumes PCM 16-bit, 16 kHz mono input.
 * Mirrors C# `IVoiceTranscriber`.
 */
interface IVoiceTranscriber : AutoCloseable {
    /** Transcribe a complete audio buffer (PCM 16-bit, 16 kHz mono). */
    suspend fun transcribeAsync(pcmAudio: ByteArray): TranscriptionResult

    /** Stream audio chunks and receive partial transcriptions. Final element has [PartialTranscription.isFinal]=true. */
    fun streamTranscribeAsync(audioChunks: Flow<ByteArray>): Flow<PartialTranscription>

    /** Async disposal (C# IAsyncDisposable). Default delegates to [close]. */
    suspend fun closeAsync() = close()
}

/**
 * Detects speech vs silence in a raw PCM audio stream (Voice Activity Detection).
 * Yields only the segments that contain speech. Mirrors C#
 * `IVoiceActivityDetector` (Voice).
 */
interface IVoiceActivityDetector {
    /**
     * Processes an incoming audio stream and yields only the segments that
     * contain speech. Each yielded [VadSegment] with [VadSegment.isSpeech]=true
     * represents a complete utterance.
     */
    fun detectAsync(audioStream: Flow<ByteArray>): Flow<VadSegment>
}

/**
 * Captures raw audio from a platform input (microphone) and exposes it as an
 * asynchronous stream of PCM byte chunks. Mirrors C# `IAudioCapture`.
 */
interface IAudioCapture : AutoCloseable {
    /** The PCM format produced by [captureAsync]. */
    val format: AudioFormat

    /** Begin capturing audio. The returned sequence yields PCM chunks until cancelled. */
    fun captureAsync(): Flow<ByteArray>

    /** Async disposal (C# IAsyncDisposable). Default delegates to [close]. */
    suspend fun closeAsync() = close()
}

/**
 * Detects a configured wake word in a continuous audio stream and raises
 * [wakeWordDetected] listeners when the phrase is recognised. Mirrors C#
 * `IWakeWordDetector` (Voice).
 */
interface IWakeWordDetector : AutoCloseable {
    /** The phrase the detector listens for (e.g. "Hey B"). */
    val wakeWord: String

    /** True when the detector is actively listening for the wake word. */
    val isListening: Boolean

    /**
     * Register a listener raised when the wake word is detected with sufficient
     * confidence. Kotlin equivalent of the C# `WakeWordDetected` event.
     */
    fun onWakeWordDetected(listener: (WakeWordDetectedEvent) -> Unit)

    /** Remove a previously registered listener. */
    fun removeWakeWordDetected(listener: (WakeWordDetectedEvent) -> Unit)

    /** Begin listening for the wake word. Idempotent. */
    suspend fun startAsync()

    /** Stop listening and release audio capture resources. Idempotent. */
    suspend fun stopAsync()

    /** Async disposal (C# IAsyncDisposable). Default delegates to [close]. */
    suspend fun closeAsync() = close()
}

// =====================================================================
// Null implementations
// =====================================================================

/**
 * No-op [ITtsEngine]. Returns empty audio and yields nothing. Mirrors C#
 * `NullTtsEngine`.
 */
class NullTtsEngine : ITtsEngine {
    override suspend fun synthesiseAsync(text: String): TtsSynthesisResult = EmptyResult

    override fun streamSynthesiseAsync(text: String): Flow<ByteArray> = flow { }

    companion object {
        /** Canonical Kokoro / Piper output metadata: 24 kHz, mono, 16-bit, empty audio. */
        val EmptyResult = TtsSynthesisResult(ByteArray(0), 24_000, 1, 16)
    }
}

/** No-op [IVoiceTranscriber]. Returns empty results without consuming audio. Mirrors C# `NullVoiceTranscriber`. */
class NullVoiceTranscriber : IVoiceTranscriber {
    @Volatile
    private var disposed = false

    override suspend fun transcribeAsync(pcmAudio: ByteArray): TranscriptionResult {
        check(!disposed) { "NullVoiceTranscriber is disposed" }
        return TranscriptionResult("", 0f, "und")
    }

    override fun streamTranscribeAsync(audioChunks: Flow<ByteArray>): Flow<PartialTranscription> = flow {
        check(!disposed) { "NullVoiceTranscriber is disposed" }
        // Drain the input so callers' producers are not blocked, but emit nothing.
        audioChunks.collect { /* discard */ }
    }

    override fun close() {
        disposed = true
    }
}

/**
 * No-op [IVoiceActivityDetector] that passes all audio chunks through as speech
 * segments without any analysis. Mirrors C# `NullVoiceActivityDetector` (Voice).
 */
class NullVoiceActivityDetector : IVoiceActivityDetector {
    override fun detectAsync(audioStream: Flow<ByteArray>): Flow<VadSegment> = flow {
        audioStream.collect { chunk -> emit(VadSegment(chunk, true)) }
    }
}

/**
 * No-op [IWakeWordDetector]. Tracks listening state but never raises detections.
 * Mirrors C# `NullWakeWordDetector` (Voice).
 */
class NullWakeWordDetector(override val wakeWord: String = "Hey B") : IWakeWordDetector {
    @Volatile
    private var disposed = false

    @Volatile
    private var listening = false

    init {
        require(wakeWord.isNotBlank()) { "wakeWord" }
    }

    override val isListening: Boolean get() = listening

    // Declared to satisfy the contract but never invoked (null implementation).
    override fun onWakeWordDetected(listener: (WakeWordDetectedEvent) -> Unit) {}
    override fun removeWakeWordDetected(listener: (WakeWordDetectedEvent) -> Unit) {}

    override suspend fun startAsync() {
        check(!disposed) { "NullWakeWordDetector is disposed" }
        listening = true
    }

    override suspend fun stopAsync() {
        check(!disposed) { "NullWakeWordDetector is disposed" }
        listening = false
    }

    override fun close() {
        if (disposed) return
        disposed = true
        listening = false
    }
}

// =====================================================================
// EnergyVadDetector
// =====================================================================

/**
 * Energy-based [IVoiceActivityDetector] that uses RMS energy to distinguish
 * speech from silence. Pure managed code, no external dependencies. Expects
 * PCM 16-bit, 16 kHz, mono. Mirrors C# `EnergyVadDetector`.
 */
class EnergyVadDetector(
    val energyThreshold: Float = 0.02f,
    silenceFrames: Int = 15,
    frameSizeBytes: Int = 640,
) : IVoiceActivityDetector {

    /** Number of consecutive below-threshold frames required to declare end-of-speech. */
    val silenceFrameCount: Int = silenceFrames

    /** Size of each analysis frame in bytes. At 16 kHz / 16-bit / mono, 640 bytes = 20 ms. */
    val frameSizeBytes: Int = frameSizeBytes

    init {
        require(silenceFrames > 0) { "silenceFrames" }
        require(frameSizeBytes > 0) { "frameSizeBytes" }
        require(energyThreshold >= 0f) { "energyThreshold" }
    }

    override fun detectAsync(audioStream: Flow<ByteArray>): Flow<VadSegment> = flow {
        // Carry-over buffer for bytes that don't fill a complete frame.
        var residual = ByteArray(0)
        // Accumulator for the current speech segment.
        val speechBuffer = ByteArrayOutputStream()

        var inSpeech = false
        var consecutiveSilenceFrames = 0

        audioStream.collect { chunk ->
            if (chunk.isEmpty()) return@collect

            // Append new data to the residual buffer.
            residual = if (residual.isEmpty()) chunk.copyOf() else residual + chunk

            var offset = 0
            while (residual.size - offset >= frameSizeBytes) {
                val rms = computeRmsEnergy(residual, offset, frameSizeBytes)
                val isSpeechFrame = rms >= energyThreshold

                if (isSpeechFrame) {
                    if (!inSpeech) {
                        inSpeech = true
                        consecutiveSilenceFrames = 0
                        speechBuffer.reset()
                    } else {
                        consecutiveSilenceFrames = 0
                    }
                    speechBuffer.write(residual, offset, frameSizeBytes)
                } else if (inSpeech) {
                    // Still in speech region; buffer silence frames in case
                    // speech resumes (avoids cutting off mid-word).
                    speechBuffer.write(residual, offset, frameSizeBytes)
                    consecutiveSilenceFrames++

                    if (consecutiveSilenceFrames >= silenceFrameCount) {
                        // End of speech — emit the buffered segment.
                        inSpeech = false
                        consecutiveSilenceFrames = 0
                        val audio = speechBuffer.toByteArray()
                        speechBuffer.reset()
                        emit(VadSegment(audio, isSpeech = true))
                    }
                }
                // else: silence while not in speech — discard.

                offset += frameSizeBytes
            }

            // Keep only unconsumed residual bytes.
            residual = if (offset > 0) residual.copyOfRange(offset, residual.size) else residual
        }

        // Stream ended — if we were mid-speech, emit what we have.
        if (inSpeech && speechBuffer.size() > 0) {
            emit(VadSegment(speechBuffer.toByteArray(), isSpeech = true))
        }
    }

    private companion object {
        /** RMS energy of a PCM 16-bit frame, normalised to [0, 1]. */
        fun computeRmsEnergy(buffer: ByteArray, offset: Int, length: Int): Float {
            val sampleCount = length / 2
            if (sampleCount == 0) return 0f
            var sumSquares = 0.0
            for (i in 0 until sampleCount) {
                val s = readInt16Le(buffer, offset + i * 2)
                val normalised = s / 32768.0
                sumSquares += normalised * normalised
            }
            return sqrt(sumSquares / sampleCount).toFloat()
        }
    }
}

// =====================================================================
// EnergyWakeWordDetector
// =====================================================================

/**
 * [IWakeWordDetector] that combines energy-based VAD with speech-to-text to
 * detect a configurable wake word. Audio is captured continuously via
 * [IAudioCapture], short speech segments are transcribed, and when the
 * transcription contains the wake word the listeners fire. Mirrors C#
 * `EnergyWakeWordDetector`.
 */
class EnergyWakeWordDetector(
    private val capture: IAudioCapture,
    private val transcriber: IVoiceTranscriber,
    wakeWord: String = "hey b",
    energyThreshold: Float = 0.02f,
) : IWakeWordDetector {

    private val vad = EnergyVadDetector(energyThreshold, silenceFrames = 10, frameSizeBytes = 640)
    private val listeners = CopyOnWriteArrayList<(WakeWordDetectedEvent) -> Unit>()
    private val gate = Any()

    @Volatile
    private var listening = false

    @Volatile
    private var disposed = false

    private var scope: CoroutineScope? = null
    private var listenJob: Job? = null

    override val wakeWord: String

    init {
        require(wakeWord.isNotBlank()) { "wakeWord" }
        this.wakeWord = wakeWord.trim()
    }

    override val isListening: Boolean get() = listening

    override fun onWakeWordDetected(listener: (WakeWordDetectedEvent) -> Unit) {
        listeners.add(listener)
    }

    override fun removeWakeWordDetected(listener: (WakeWordDetectedEvent) -> Unit) {
        listeners.remove(listener)
    }

    override suspend fun startAsync() {
        check(!disposed) { "EnergyWakeWordDetector is disposed" }
        synchronized(gate) {
            if (listening) return
            val s = CoroutineScope(Dispatchers.Default + Job())
            scope = s
            listening = true
            listenJob = s.launch { listenLoop() }
        }
    }

    override suspend fun stopAsync() {
        check(!disposed) { "EnergyWakeWordDetector is disposed" }
        val job: Job?
        val s: CoroutineScope?
        synchronized(gate) {
            if (!listening) return
            listening = false
            job = listenJob
            s = scope
        }
        job?.cancel()
        try {
            job?.join()
        } catch (_: CancellationException) {
            // expected
        }
        s?.cancel()
        synchronized(gate) {
            listenJob = null
            scope = null
        }
    }

    override fun close() {
        if (disposed) return
        disposed = true
        listening = false
        listenJob?.cancel()
        scope?.cancel()
        listenJob = null
        scope = null
    }

    override suspend fun closeAsync() {
        if (disposed) return
        try {
            stopAsync()
        } catch (_: CancellationException) {
            // swallow — we're disposing
        }
        disposed = true
    }

    private suspend fun listenLoop() {
        try {
            vad.detectAsync(capture.captureAsync()).collect { segment ->
                currentCoroutineContext().ensureActive()

                if (!segment.isSpeech || segment.audio.isEmpty()) return@collect

                val result: TranscriptionResult = try {
                    transcriber.transcribeAsync(segment.audio)
                } catch (ce: CancellationException) {
                    throw ce
                } catch (_: Exception) {
                    // Transcription failed for this segment — skip and keep listening.
                    return@collect
                }

                if (result.text.isBlank()) return@collect

                // Check for wake word (case-insensitive).
                if (result.text.contains(wakeWord, ignoreCase = true)) {
                    val evt = WakeWordDetectedEvent(
                        wakeWord = wakeWord,
                        detectedAt = Instant.now(),
                        confidence = result.confidence,
                    )
                    for (l in listeners) l(evt)
                }
            }
        } catch (_: CancellationException) {
            // Normal shutdown — swallow.
        } finally {
            listening = false
        }
    }
}

// =====================================================================
// VoicePipeline
// =====================================================================

/**
 * Convenience composition of [IWakeWordDetector], [IAudioCapture],
 * [IVoiceTranscriber], and optionally [IVoiceActivityDetector] and [ITtsEngine].
 * On wake-word detection the pipeline starts capturing audio, optionally filters
 * it through VAD, feeds the speech chunks to the transcriber, and raises
 * [onTranscribed] with the final result. Mirrors C# `VoicePipeline`.
 */
class VoicePipeline(
    private val wake: IWakeWordDetector,
    private val transcriber: IVoiceTranscriber,
    capture: IAudioCapture? = null,
    private val vad: IVoiceActivityDetector? = null,
    /** Optional TTS engine, exposed via [ttsEngine]; the pipeline never invokes it. */
    val ttsEngine: ITtsEngine? = null,
) : AutoCloseable {

    private val capture: IAudioCapture = capture ?: NullAudioCapture()

    private val transcribedListeners = CopyOnWriteArrayList<(TranscribedEvent) -> Unit>()
    private val activationFailedListeners = CopyOnWriteArrayList<(Throwable) -> Unit>()

    private val scope = CoroutineScope(Dispatchers.Default + Job())
    private val gate = Any()
    private var activationJob: Job? = null

    @Volatile
    private var disposed = false

    private val wakeListener: (WakeWordDetectedEvent) -> Unit = { onWakeWordDetected() }

    init {
        wake.onWakeWordDetected(wakeListener)
    }

    /** The wake-word detector this pipeline observes. */
    val wakeDetector: IWakeWordDetector get() = wake

    /** The transcriber this pipeline drives. */
    val theTranscriber: IVoiceTranscriber get() = transcriber

    /** The audio capture source this pipeline reads from. */
    val audioCapture: IAudioCapture get() = capture

    /** The optional voice activity detector supplied at construction. */
    val voiceActivityDetector: IVoiceActivityDetector? get() = vad

    /** Register a listener raised when a wake-word activation produces a final transcription. */
    fun onTranscribed(listener: (TranscribedEvent) -> Unit) {
        transcribedListeners.add(listener)
    }

    /** Register a listener raised when an activation fails (capture / transcription / cancellation). */
    fun onActivationFailed(listener: (Throwable) -> Unit) {
        activationFailedListeners.add(listener)
    }

    /** Begin listening for the wake word. Delegates to [IWakeWordDetector.startAsync]. */
    suspend fun startAsync() {
        check(!disposed) { "VoicePipeline is disposed" }
        wake.startAsync()
    }

    /** Stop listening for the wake word and cancel any in-flight activation. */
    suspend fun stopAsync() {
        check(!disposed) { "VoicePipeline is disposed" }
        cancelActivation()
        wake.stopAsync()
    }

    private fun onWakeWordDetected() {
        if (disposed) return

        // Cancel any previous activation still running, then start a new one.
        cancelActivation()

        val job = scope.launch { runActivation() }
        synchronized(gate) {
            activationJob = job
        }
    }

    private suspend fun runActivation() {
        try {
            // When VAD is configured, pipe raw audio through it and only pass
            // speech segments to the transcriber. Otherwise forward raw capture.
            val audioInput: Flow<ByteArray> = if (vad == null) {
                capture.captureAsync()
            } else {
                extractSpeechSegments(vad, capture.captureAsync())
            }

            val result = transcriber.streamTranscribeAsync(audioInput).toFinal()

            if (result != null) {
                val evt = TranscribedEvent(result = result)
                for (l in transcribedListeners) l(evt)
            }
            // else: transcriber yielded no final result (silence / noise / premature cancel) — normal, no event.
        } catch (_: CancellationException) {
            // Activation was cancelled (stop requested or new wake event). Swallow.
        } catch (ex: Exception) {
            for (l in activationFailedListeners) l(ex)
        }
    }

    private fun cancelActivation() {
        val toCancel: Job?
        synchronized(gate) {
            toCancel = activationJob
            activationJob = null
        }
        toCancel?.cancel()
    }

    override fun close() {
        if (disposed) return
        disposed = true
        wake.removeWakeWordDetected(wakeListener)
        cancelActivation()
        scope.cancel()
    }

    /** Async disposal (C# IAsyncDisposable): disposes wake, transcriber, and capture. */
    suspend fun closeAsync() {
        if (disposed) return
        disposed = true
        wake.removeWakeWordDetected(wakeListener)
        cancelActivation()
        scope.cancel()
        wake.closeAsync()
        transcriber.closeAsync()
        capture.closeAsync()
    }

    private companion object {
        /** Filter raw audio through VAD, yielding only speech-segment bytes. */
        fun extractSpeechSegments(vad: IVoiceActivityDetector, rawAudio: Flow<ByteArray>): Flow<ByteArray> =
            flow {
                vad.detectAsync(rawAudio).collect { segment ->
                    if (segment.isSpeech) emit(segment.audio)
                }
            }
    }
}

/**
 * No-op [IAudioCapture] that yields no audio. Safe default when no platform
 * microphone backend is available. Mirrors C# `NullAudioCapture`.
 */
class NullAudioCapture : IAudioCapture {
    override val format: AudioFormat = AudioFormat.Pcm16Mono16k

    override fun captureAsync(): Flow<ByteArray> = flow { }

    override fun close() {}
    override suspend fun closeAsync() {}
}

/**
 * Drain a partial-transcription stream and return the final result. Returns null
 * if the stream produces no items. Mirrors C# `PartialTranscriptionAsyncEnumerableExtensions.ToFinalAsync`.
 */
internal suspend fun Flow<PartialTranscription>.toFinal(): TranscriptionResult? {
    var last: PartialTranscription? = null
    try {
        collect { partial ->
            last = partial
            if (partial.isFinal) {
                // Stop collecting once the final item is seen (C# breaks the loop).
                throw StopCollect
            }
        }
    } catch (_: StopCollect) {
        // reached the final item
    }

    val l = last ?: return null
    // We do not know the language at this layer; callers can use the single-shot
    // transcribeAsync overload for richer metadata.
    return TranscriptionResult(l.text, l.confidence, "und")
}

private object StopCollect : CancellationException("final transcription reached")

// =====================================================================
// Speaker identity (Phase E5)
// =====================================================================

/** Per-user enrollment record used for cosine-similarity ID. Mirrors C# `EnrolledSpeaker`. */
data class EnrolledSpeaker(val userId: String, val centroid: FloatArray, val sampleCount: Int) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is EnrolledSpeaker) return false
        return userId == other.userId &&
            sampleCount == other.sampleCount &&
            centroid.contentEquals(other.centroid)
    }

    override fun hashCode(): Int {
        var r = userId.hashCode()
        r = 31 * r + centroid.contentHashCode()
        r = 31 * r + sampleCount
        return r
    }
}

/**
 * Identify-or-enroll surface. Mirrors C# `ISpeakerIdentity`.
 */
interface ISpeakerIdentity : AutoCloseable {
    suspend fun identifyAsync(audioPcm16: ByteArray, sampleRateHz: Int): String?
    suspend fun enrollAsync(userId: String, audioPcm16: ByteArray, sampleRateHz: Int)
    suspend fun closeAsync() = close()
}

/**
 * Host-supplied speaker embedding model (ONNX ECAPA-TDNN / TitaNet / CAM++).
 * Consumes a normalised float window at [sampleRateHz]; returns a fixed-length
 * L2-normalised embedding, or null if extraction was not possible. Injected so
 * the neural binding never leaks into this module.
 */
interface ISpeakerEmbedder {
    fun embed(window: FloatArray, sampleRateHz: Int): FloatArray?
}

/** Configuration for [SpeakerIdentity]. Mirrors C# `SpeakerIdentityConfig`. */
data class SpeakerIdentityConfig(
    val sampleRateHz: Int = 16_000,
    val minUtteranceMs: Int = 1_000,
    val maxUtteranceMs: Int = 8_000,
    val matchThreshold: Double = 0.55,
)

/**
 * Deterministic in-memory speaker identity. The neural embedding step is the
 * injected [ISpeakerEmbedder]; the enrollment (running-centroid averaging +
 * L2-normalise) and identification (cosine-similarity nearest-centroid over the
 * match threshold) logic is ported algorithm-for-algorithm from C#
 * `OnnxSpeakerIdentity`. Enrollment is kept in an in-memory concurrent map
 * (the C# JSON store is a host persistence concern, injected out).
 */
class SpeakerIdentity(
    private val embedder: ISpeakerEmbedder,
    private val config: SpeakerIdentityConfig = SpeakerIdentityConfig(),
) : ISpeakerIdentity {

    private val enrolled = ConcurrentHashMap<String, EnrolledSpeaker>()
    private val lowerToOriginal = ConcurrentHashMap<String, String>()

    @Volatile
    private var disposed = false

    /** Snapshot of currently enrolled speakers (for host persistence / inspection). */
    val enrolledSpeakers: List<EnrolledSpeaker> get() = enrolled.values.toList()

    override suspend fun identifyAsync(audioPcm16: ByteArray, sampleRateHz: Int): String? {
        check(!disposed) { "SpeakerIdentity is disposed" }
        if (audioPcm16.isEmpty()) return null
        if (enrolled.isEmpty()) return null

        val embedding = computeEmbedding(audioPcm16, sampleRateHz) ?: return null

        var best: String? = null
        var bestSim = Double.MIN_VALUE
        for ((userId, speaker) in enrolled) {
            val sim = cosineSimilarity(embedding, speaker.centroid)
            if (sim > bestSim) {
                bestSim = sim
                best = userId
            }
        }
        return if (bestSim >= config.matchThreshold) best else null
    }

    override suspend fun enrollAsync(userId: String, audioPcm16: ByteArray, sampleRateHz: Int) {
        check(!disposed) { "SpeakerIdentity is disposed" }
        require(userId.isNotBlank()) { "userId required" }
        require(audioPcm16.isNotEmpty()) { "audio required" }

        val embedding = computeEmbedding(audioPcm16, sampleRateHz)
            ?: throw IllegalStateException("Embedding extraction failed")

        // OrdinalIgnoreCase key semantics: remember the first-seen spelling.
        val key = userId.lowercase()
        val storedId = lowerToOriginal.putIfAbsent(key, userId) ?: userId

        enrolled.compute(storedId) { _, prev ->
            if (prev == null) {
                EnrolledSpeaker(storedId, embedding, 1)
            } else {
                val n = prev.sampleCount
                val newCentroid = FloatArray(prev.centroid.size)
                for (i in newCentroid.indices) {
                    newCentroid[i] = (prev.centroid[i] * n + embedding[i]) / (n + 1)
                }
                l2Normalise(newCentroid)
                prev.copy(centroid = newCentroid, sampleCount = n + 1)
            }
        }
    }

    override fun close() {
        disposed = true
    }

    private fun computeEmbedding(pcm16: ByteArray, sampleRateHz: Int): FloatArray? {
        if (sampleRateHz != config.sampleRateHz) return null
        val minSamples = sampleRateHz * config.minUtteranceMs / 1000
        val maxSamples = sampleRateHz * config.maxUtteranceMs / 1000
        var nSamples = pcm16.size / 2
        if (nSamples < minSamples) return null
        if (nSamples > maxSamples) nSamples = maxSamples

        val window = FloatArray(nSamples)
        for (i in 0 until nSamples) {
            val s = readInt16Le(pcm16, i * 2)
            window[i] = s / 32768f
        }

        val output = embedder.embed(window, sampleRateHz) ?: return null
        val normalised = output.copyOf()
        l2Normalise(normalised)
        return normalised
    }

    private companion object {
        fun l2Normalise(v: FloatArray) {
            var sumSq = 0.0
            for (x in v) sumSq += x.toDouble() * x.toDouble()
            val norm = sqrt(sumSq)
            if (norm < 1e-9) return
            for (i in v.indices) v[i] = (v[i] / norm).toFloat()
        }

        fun cosineSimilarity(a: FloatArray, b: FloatArray): Double {
            if (a.size != b.size) return -1.0
            var dot = 0.0
            for (i in a.indices) dot += a[i].toDouble() * b[i].toDouble()
            return dot
        }
    }
}

// =====================================================================
// Speech emotion (Phase E6)
// =====================================================================

/**
 * Output emotion frame from a speech-emotion model. Mirrors C# `SpeechEmotionFrame`.
 *
 * @param label Top-1 emotion label (lowercase, e.g. "happy", "angry").
 * @param arousal Russell-circumplex arousal coordinate in [-1, 1].
 * @param valence Russell-circumplex valence coordinate in [-1, 1].
 * @param probability Softmax probability of the winning class.
 */
data class SpeechEmotionFrame(
    val label: String,
    val arousal: Double,
    val valence: Double,
    val probability: Double,
)

/** Configuration for [SpeechEmotionDetector]. Mirrors C# `SpeechEmotionConfig`. */
data class SpeechEmotionConfig(
    val labels: List<String>? = null,
    val sampleRateHz: Int = 16_000,
    val maxClipMs: Int = 8_000,
)

/**
 * Host-supplied speech-emotion model (ONNX wav2vec2-style). Consumes a
 * normalised float window; returns raw class logits over the configured labels.
 * Injected so the neural binding never leaks into this module.
 */
interface IEmotionModelRunner {
    fun scoreLogits(window: FloatArray, sampleRateHz: Int): FloatArray
}

/** Sense speech emotion from a PCM buffer. Mirrors C# `ISpeechEmotionDetector`. */
interface ISpeechEmotionDetector : AutoCloseable {
    suspend fun senseAsync(audioPcm16: ByteArray, sampleRateHz: Int): SpeechEmotionFrame?
    suspend fun closeAsync() = close()
}

/**
 * Deterministic in-memory speech-emotion detector. The neural forward pass is the
 * injected [IEmotionModelRunner]; the argmax-softmax + Russell-circumplex
 * label->(arousal,valence) lookup is ported byte-for-byte from C#
 * `OnnxSpeechEmotionDetector`.
 */
class SpeechEmotionDetector(
    private val runner: IEmotionModelRunner,
    private val config: SpeechEmotionConfig = SpeechEmotionConfig(),
) : ISpeechEmotionDetector {

    private val labels: List<String> = config.labels ?: DefaultLabels

    @Volatile
    private var disposed = false

    override suspend fun senseAsync(audioPcm16: ByteArray, sampleRateHz: Int): SpeechEmotionFrame? {
        check(!disposed) { "SpeechEmotionDetector is disposed" }
        if (audioPcm16.isEmpty()) return null
        if (sampleRateHz != config.sampleRateHz) return null

        val maxSamples = sampleRateHz * config.maxClipMs / 1000
        val nSamples = min(audioPcm16.size / 2, maxSamples)
        if (nSamples == 0) return null

        val window = FloatArray(nSamples)
        for (i in 0 until nSamples) {
            val s = readInt16Le(audioPcm16, i * 2)
            window[i] = s / 32768f
        }

        val logits = runner.scoreLogits(window, sampleRateHz)
        val (bestIdx, bestProb) = softmax(logits)
        val label = (if (bestIdx in labels.indices) labels[bestIdx] else "unknown").lowercase()
        val coords = Circumplex[label] ?: (0.0 to 0.0)
        return SpeechEmotionFrame(label, coords.first, coords.second, bestProb)
    }

    override fun close() {
        disposed = true
    }

    private companion object {
        // SUPERB-ER + IEMOCAP standard 4-class layout (default).
        val DefaultLabels = listOf("neutral", "happy", "angry", "sad")

        // Russell circumplex coordinates for the standard discrete emotion labels.
        val Circumplex: Map<String, Pair<Double, Double>> = mapOf(
            "neutral" to (0.00 to 0.00),
            "happy" to (0.55 to 0.81),
            "happiness" to (0.55 to 0.81),
            "joy" to (0.60 to 0.82),
            "angry" to (0.74 to -0.62),
            "anger" to (0.74 to -0.62),
            "sad" to (-0.43 to -0.65),
            "sadness" to (-0.43 to -0.65),
            "fear" to (0.78 to -0.64),
            "fearful" to (0.78 to -0.64),
            "surprise" to (0.85 to 0.40),
            "surprised" to (0.85 to 0.40),
            "disgust" to (0.45 to -0.60),
            "disgusted" to (0.45 to -0.60),
            "calm" to (-0.40 to 0.45),
            "excited" to (0.82 to 0.70),
            "bored" to (-0.65 to -0.20),
            "frustrated" to (0.55 to -0.55),
            "contempt" to (0.20 to -0.55),
        )

        fun softmax(logits: FloatArray): Pair<Int, Double> {
            if (logits.isEmpty()) return -1 to 0.0
            var maxV = logits[0]
            for (i in 1 until logits.size) if (logits[i] > maxV) maxV = logits[i]
            var denom = 0.0
            for (v in logits) denom += exp((v - maxV).toDouble())

            var bestIdx = 0
            var bestProb = 0.0
            for (i in logits.indices) {
                val p = exp((logits[i] - maxV).toDouble()) / denom
                if (p > bestProb) {
                    bestProb = p
                    bestIdx = i
                }
            }
            return bestIdx to bestProb
        }
    }
}

// =====================================================================
// Little-endian PCM-16 helper
// =====================================================================

internal fun readInt16Le(buffer: ByteArray, offset: Int): Short {
    val lo = buffer[offset].toInt() and 0xFF
    val hi = buffer[offset + 1].toInt() and 0xFF
    return ((hi shl 8) or lo).toShort()
}
