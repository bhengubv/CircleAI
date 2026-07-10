// Speech.kt
//
// Kotlin port of CircleAI.Speech — the C# reference (Contracts.cs,
// NullImplementations.cs, EchoCancellers.cs, NoiseReducers.cs,
// VoiceActivityDetectors.cs, EndOfTurnDetectors.cs, AudioFormatConverter.cs)
// is the EXACT spec. ASR / TTS / wake-word / OCR + the real-time audio
// front-end primitives (AEC, noise reduction, VAD, end-of-turn) needed for
// B! Butler's voice loop.
//
// Design fidelity notes:
//   * C# `record`                         -> Kotlin `data class`.
//   * C# `ValueTask<T>`                   -> `suspend fun`.
//   * C# `ReadOnlyMemory<byte>`/Span      -> `ByteArray` (+ offset/length where a
//                                            C# Span slice is written in place, we
//                                            operate on a plain ByteArray destination).
//   * C# `TimeSpan`                       -> `java.time.Duration`.
//   * C# `DateTimeOffset`                 -> `java.time.Instant`.
//   * C# `IDisposable Subscribe(...)`     -> returns an [AutoCloseable] handle.
//   * C# `IAsyncDisposable`               -> `suspend fun closeAsync()`.
//   * Native/ONNX engines are INJECTED behind *ModelRunner interfaces exactly as
//     the C# reference injects them; the pure-managed algorithms (NLMS AEC,
//     spectral-subtraction gate, energy VAD, rule-based turn detector, G.711
//     codecs, linear resampler) are ported byte/algorithm-for-algorithm.
//
// Wire/byte formats (G.711 mu-law/a-law, PCM-16 LE, NLMS update) match the C#
// reference exactly so audio produced here is bit-identical.

package com.bhengubv.circleai.speech

import java.time.Duration
import java.time.Instant
import kotlin.math.abs
import kotlin.math.ceil
import kotlin.math.cos
import kotlin.math.floor
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sign
import kotlin.math.sqrt

// =====================================================================
// Records — transcription / synthesis / OCR results
// =====================================================================

/** One transcribed segment. Mirrors C# `TranscribedSegment`. */
data class TranscribedSegment(
    val text: String,
    val offset: Duration,
    val duration: Duration,
    val language: String? = null,
    val confidence: Float = 0f,
)

/** Outcome of one ASR call. Mirrors C# `TranscriptionResult`. */
data class TranscriptionResult(
    val text: String,
    val language: String?,
    val segments: List<TranscribedSegment>,
    val totalDuration: Duration,
)

/** Outcome of one TTS call. Mirrors C# `SynthesisResult`. */
data class SynthesisResult(
    val audioPcm16Mono: ByteArray,
    val sampleRateHz: Int,
    val duration: Duration,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is SynthesisResult) return false
        return sampleRateHz == other.sampleRateHz &&
            duration == other.duration &&
            audioPcm16Mono.contentEquals(other.audioPcm16Mono)
    }

    override fun hashCode(): Int {
        var result = audioPcm16Mono.contentHashCode()
        result = 31 * result + sampleRateHz
        result = 31 * result + duration.hashCode()
        return result
    }
}

/** One detected text block in an OCR result. Mirrors C# `OcrTextBlock`. */
data class OcrTextBlock(
    val text: String,
    val x: Int,
    val y: Int,
    val width: Int,
    val height: Int,
    val confidence: Float,
    val language: String? = null,
)

/** One OCR result. Mirrors C# `OcrResult`. */
data class OcrResult(
    val text: String,
    val blocks: List<OcrTextBlock>,
)

/** One wake-word fire. Mirrors C# `WakeWordEvent`. */
data class WakeWordEvent(
    val keyword: String,
    val confidence: Float,
    val detectedAtUtc: Instant,
)

/**
 * Verdict on whether a partial transcript represents a finished thought.
 * Mirrors C# `EndOfTurnResult`.
 *
 * @param isComplete True if the speaker likely finished their turn.
 * @param confidence 0..1 confidence.
 * @param waitMoreMs If [isComplete] is false, how many extra ms to wait before re-asking.
 */
data class EndOfTurnResult(val isComplete: Boolean, val confidence: Float, val waitMoreMs: Int)

/**
 * One verdict from a voice-activity detector. Mirrors C# `VadFrameResult`.
 *
 * @param isSpeech True if this frame contains speech.
 * @param speechProbability 0..1 confidence the frame is speech.
 * @param offset Frame start offset relative to the stream start.
 */
data class VadFrameResult(val isSpeech: Boolean, val speechProbability: Float, val offset: Duration)

// =====================================================================
// Contract interfaces
// =====================================================================

/** (2.3.0) Convert audio to text. Mirrors C# `ISpeechRecognizer`. */
interface ISpeechRecognizer {
    /** Backend self-identification — "funasr-1.x" / "yapsnap" / "null". */
    val backendId: String

    /** Recognise one buffer of PCM-16 mono audio. */
    suspend fun transcribeAsync(
        audioPcm16Mono: ByteArray,
        sampleRateHz: Int,
        languageHint: String? = null,
    ): TranscriptionResult
}

/** (2.3.0) Convert text to spoken audio. Mirrors C# `ISpeechSynthesizer`. */
interface ISpeechSynthesizer {
    /** Backend self-identification — "chattts" / "null". */
    val backendId: String

    /** Synthesise one utterance. Returns PCM-16 mono. */
    suspend fun synthesizeAsync(
        text: String,
        voiceId: String? = null,
        languageHint: String? = null,
    ): SynthesisResult
}

/**
 * (2.3.0) Spot a wake word ("Hey B") in a continuous audio stream.
 * Implementations are long-running (`startAsync`/`stopAsync`).
 * Mirrors C# `IWakeWordDetector` (Speech).
 */
interface IWakeWordDetector : AutoCloseable {
    /** Backend self-identification — "hey-snips" / "null". */
    val backendId: String

    /** Subscribe to wake-word fire events. Returns a handle whose close unsubscribes. */
    fun subscribe(handler: suspend (WakeWordEvent) -> Unit): AutoCloseable

    /** Begin listening on the system mic. Idempotent. */
    suspend fun startAsync()

    /** Stop listening. Idempotent. */
    suspend fun stopAsync()

    /** Async disposal (C# IAsyncDisposable). Default drains via [close]. */
    suspend fun closeAsync() = close()
}

/** (3.3.0) Acoustic echo canceller — subtracts the far-end reference from the near-end mic input. */
interface IEchoCanceller {
    /** Backend self-identification — "nlms" / "webrtc-aec3" / "null". */
    val backendId: String

    /**
     * Cancel echo of [farEndReference] out of [nearEndMicrophone]. Writes the
     * result into [destination]. Both inputs must be the same sample rate and
     * length (PCM-16 mono). Returns the number of bytes written.
     */
    fun cancel(
        nearEndMicrophone: ByteArray,
        farEndReference: ByteArray,
        sampleRateHz: Int,
        destination: ByteArray,
    ): Int

    /** Reset adaptive-filter state at the start of a new call. */
    fun reset()
}

/** (3.3.0) Audio noise reducer — cleans a frame of PCM-16 mono audio. */
interface INoiseReducer {
    /** Backend self-identification — "krisp" / "deepfilternet" / "passthrough" / "null". */
    val backendId: String

    /** True when the underlying model / runtime is available. */
    val isAvailable: Boolean

    /**
     * Reduce noise in [audioPcm16Mono] and write into [destination]. The
     * destination buffer must be at least as long as the input. Returns the
     * number of bytes written.
     */
    fun reduce(audioPcm16Mono: ByteArray, sampleRateHz: Int, destination: ByteArray): Int
}

/**
 * (3.3.0) Decide whether the caller has finished their turn given the latest
 * partial transcript + the trailing-silence duration. VAD says "they're silent
 * now"; this says "they're DONE." Mirrors C# `IEndOfTurnDetector`.
 */
interface IEndOfTurnDetector {
    /** Backend self-identification — "rules" / "smart-turn-v2" / "null". */
    val backendId: String

    /** Classify the current state. */
    fun predict(partialTranscript: String, trailingSilence: Duration): EndOfTurnResult

    /** Reset internal state at the start of a fresh turn. */
    fun reset()
}

/**
 * (3.3.0) Voice-activity detector. Implementations classify each 10-30 ms audio
 * frame as speech or silence so a voice loop knows when the caller has
 * started/stopped talking. Mirrors C# `IVoiceActivityDetector` (Speech).
 */
interface IVoiceActivityDetector {
    /** Backend self-identification — "energy" / "silero" / "null". */
    val backendId: String

    /** Speech probability threshold for [VadFrameResult.isSpeech]. */
    val speechThreshold: Float

    /** Classify one frame of PCM-16 mono audio. */
    fun classify(audioPcm16Mono: ByteArray, sampleRateHz: Int, offset: Duration): VadFrameResult

    /** Reset any internal hangover state at the start of a fresh utterance. */
    fun reset()
}

/** (2.3.0) Read text out of an image. Mirrors C# `IOpticalCharacterRecognizer`. */
interface IOpticalCharacterRecognizer {
    /** Backend self-identification — "paddleocr-2.x" / "null". */
    val backendId: String

    /** Recognise text in an image. [languageHint] e.g. "eng" / "chi" / "auto". */
    suspend fun recognizeAsync(imageBytes: ByteArray, languageHint: String? = "auto"): OcrResult
}

// =====================================================================
// Null implementations — fail-closed DI defaults
// =====================================================================

/** Fail-closed ASR default. Mirrors C# `NullSpeechRecognizer`. */
class NullSpeechRecognizer private constructor() : ISpeechRecognizer {
    companion object {
        val Instance = NullSpeechRecognizer()
    }

    override val backendId: String get() = "null"

    override suspend fun transcribeAsync(
        audioPcm16Mono: ByteArray,
        sampleRateHz: Int,
        languageHint: String?,
    ): TranscriptionResult = TranscriptionResult(
        text = "",
        language = languageHint,
        segments = emptyList(),
        totalDuration = Duration.ZERO,
    )
}

/** Fail-closed TTS default. Mirrors C# `NullSpeechSynthesizer`. */
class NullSpeechSynthesizer private constructor() : ISpeechSynthesizer {
    companion object {
        val Instance = NullSpeechSynthesizer()
    }

    override val backendId: String get() = "null"

    override suspend fun synthesizeAsync(
        text: String,
        voiceId: String?,
        languageHint: String?,
    ): SynthesisResult = SynthesisResult(
        audioPcm16Mono = ByteArray(0),
        sampleRateHz = 16_000,
        duration = Duration.ZERO,
    )
}

/** Fail-closed wake-word default — never fires. Mirrors C# `NullWakeWordDetector` (Speech). */
class NullWakeWordDetector : IWakeWordDetector {
    override val backendId: String get() = "null"

    override fun subscribe(handler: suspend (WakeWordEvent) -> Unit): AutoCloseable =
        AutoCloseable { }

    override suspend fun startAsync() {}
    override suspend fun stopAsync() {}
    override fun close() {}
    override suspend fun closeAsync() {}
}

/** Fail-closed OCR default. Mirrors C# `NullOpticalCharacterRecognizer`. */
class NullOpticalCharacterRecognizer private constructor() : IOpticalCharacterRecognizer {
    companion object {
        val Instance = NullOpticalCharacterRecognizer()
    }

    override val backendId: String get() = "null"

    override suspend fun recognizeAsync(imageBytes: ByteArray, languageHint: String?): OcrResult =
        OcrResult(text = "", blocks = emptyList())
}

// =====================================================================
// Echo cancellers
// =====================================================================

/** Pass-through DI default. Mirrors C# `NullEchoCanceller`. */
class NullEchoCanceller private constructor() : IEchoCanceller {
    companion object {
        val Instance = NullEchoCanceller()
    }

    override val backendId: String get() = "null"

    override fun cancel(
        nearEndMicrophone: ByteArray,
        farEndReference: ByteArray,
        sampleRateHz: Int,
        destination: ByteArray,
    ): Int {
        nearEndMicrophone.copyInto(destination)
        return nearEndMicrophone.size
    }

    override fun reset() {}
}

/**
 * Normalised LMS adaptive-filter AEC. Pure Kotlin, no model downloads, runs on
 * every device. Filter length defaults to 256 taps (~16 ms @ 16 kHz) which
 * covers typical phone-call echo paths. Mirrors C# `NlmsEchoCanceller`.
 */
class NlmsEchoCanceller(
    private val filterLength: Int = 256,
    private val stepSize: Float = 0.4f,
    private val epsilon: Float = 1e-6f,
) : IEchoCanceller {

    private val w = FloatArray(filterLength)
    private val refBuffer = FloatArray(filterLength)
    private var refIndex = 0

    override val backendId: String get() = "nlms"

    override fun cancel(
        nearEndMicrophone: ByteArray,
        farEndReference: ByteArray,
        sampleRateHz: Int,
        destination: ByteArray,
    ): Int {
        require(nearEndMicrophone.size == farEndReference.size) {
            "near-end and far-end must be the same length."
        }
        require(destination.size >= nearEndMicrophone.size) {
            "destination must be at least as long as input."
        }

        val sampleCount = nearEndMicrophone.size / 2
        for (n in 0 until sampleCount) {
            val micSample = readInt16Le(nearEndMicrophone, n * 2) / Short.MAX_VALUE.toFloat()
            val farSample = readInt16Le(farEndReference, n * 2) / Short.MAX_VALUE.toFloat()

            // Push far-end into circular reference buffer.
            refBuffer[refIndex] = farSample

            // Estimated echo: dot(w, ref).
            var echoEstimate = 0f
            var power = epsilon
            for (k in 0 until filterLength) {
                val rIdx = (refIndex - k + filterLength) % filterLength
                val x = refBuffer[rIdx]
                echoEstimate += w[k] * x
                power += x * x
            }

            // Error = mic - echo estimate.
            val error = micSample - echoEstimate

            // Update filter weights.
            val mu = stepSize / power
            for (k in 0 until filterLength) {
                val rIdx = (refIndex - k + filterLength) % filterLength
                w[k] += mu * error * refBuffer[rIdx]
            }

            refIndex = (refIndex + 1) % filterLength

            // Clamp + write.
            val outSample = (error * Short.MAX_VALUE)
                .coerceIn(Short.MIN_VALUE.toFloat(), Short.MAX_VALUE.toFloat())
                .toInt()
            writeInt16Le(destination, n * 2, outSample.toShort())
        }

        return nearEndMicrophone.size
    }

    override fun reset() {
        w.fill(0f)
        refBuffer.fill(0f)
        refIndex = 0
    }
}

/** (3.3.0) Host-supplied AEC model runner (e.g. WebRTC AEC3). Mirrors C# `IEchoCancellerModelRunner`. */
interface IEchoCancellerModelRunner {
    fun process(
        nearEnd: ByteArray,
        farEnd: ByteArray,
        sampleRateHz: Int,
        destination: ByteArray,
    ): Int

    fun reset()
}

/** WebRTC AEC3 wrapper — falls back to NLMS when no runner is wired. Mirrors C# `WebRtcEchoCanceller`. */
class WebRtcEchoCanceller(private val runner: IEchoCancellerModelRunner? = null) : IEchoCanceller {
    private val fallback = NlmsEchoCanceller()

    override val backendId: String
        get() = if (runner == null) "webrtc-aec3 (fallback)" else "webrtc-aec3"

    override fun cancel(
        nearEndMicrophone: ByteArray,
        farEndReference: ByteArray,
        sampleRateHz: Int,
        destination: ByteArray,
    ): Int =
        if (runner == null) {
            fallback.cancel(nearEndMicrophone, farEndReference, sampleRateHz, destination)
        } else {
            runner.process(nearEndMicrophone, farEndReference, sampleRateHz, destination)
        }

    override fun reset() {
        fallback.reset()
        runner?.reset()
    }
}

// =====================================================================
// Noise reducers
// =====================================================================

/** No-op reducer — DI default. Mirrors C# `NullNoiseReducer`. */
class NullNoiseReducer private constructor() : INoiseReducer {
    companion object {
        val Instance = NullNoiseReducer()
    }

    override val backendId: String get() = "null"
    override val isAvailable: Boolean get() = true

    override fun reduce(audioPcm16Mono: ByteArray, sampleRateHz: Int, destination: ByteArray): Int {
        audioPcm16Mono.copyInto(destination)
        return audioPcm16Mono.size
    }
}

/**
 * Lightweight time-domain noise gate: attenuates samples below a fixed noise
 * floor with a soft knee. Not as clean as a DNN but adds zero runtime cost and
 * works on every device. Mirrors C# `SpectralSubtractionNoiseReducer`.
 */
class SpectralSubtractionNoiseReducer(
    private val floorEstimate: Float = 0.008f,
    private val attenuation: Float = 0.25f,
) : INoiseReducer {

    override val backendId: String get() = "passthrough"
    override val isAvailable: Boolean get() = true

    override fun reduce(audioPcm16Mono: ByteArray, sampleRateHz: Int, destination: ByteArray): Int {
        require(destination.size >= audioPcm16Mono.size) {
            "destination must be at least as long as input."
        }

        val floor = (floorEstimate * Short.MAX_VALUE).toInt()
        val sampleCount = audioPcm16Mono.size / 2
        for (i in 0 until sampleCount) {
            val s = readInt16Le(audioPcm16Mono, i * 2).toInt()
            val absVal = abs(s)
            val out = if (absVal <= floor) (s * attenuation).toInt().toShort() else s.toShort()
            writeInt16Le(destination, i * 2, out)
        }
        return audioPcm16Mono.size
    }
}

/** (3.3.0) Host-supplied DNN runner for noise reduction. Mirrors C# `INoiseReducerModelRunner`. */
interface INoiseReducerModelRunner {
    /** Process one frame; write cleaned PCM-16 mono into the destination array. */
    fun process(audioPcm16Mono: ByteArray, sampleRateHz: Int, destination: ByteArray): Int
}

/** Krisp wrapper — uses the host's runner when present. Mirrors C# `KrispNoiseReducer`. */
class KrispNoiseReducer(private val runner: INoiseReducerModelRunner? = null) : INoiseReducer {
    private val fallback = SpectralSubtractionNoiseReducer()

    override val backendId: String get() = if (runner == null) "krisp (fallback)" else "krisp"
    override val isAvailable: Boolean get() = true

    override fun reduce(audioPcm16Mono: ByteArray, sampleRateHz: Int, destination: ByteArray): Int =
        if (runner == null) {
            fallback.reduce(audioPcm16Mono, sampleRateHz, destination)
        } else {
            runner.process(audioPcm16Mono, sampleRateHz, destination)
        }
}

/** DeepFilterNet wrapper. Mirrors C# `DeepFilterNetNoiseReducer`. */
class DeepFilterNetNoiseReducer(private val runner: INoiseReducerModelRunner? = null) : INoiseReducer {
    private val fallback = SpectralSubtractionNoiseReducer()

    override val backendId: String
        get() = if (runner == null) "deepfilternet (fallback)" else "deepfilternet"
    override val isAvailable: Boolean get() = true

    override fun reduce(audioPcm16Mono: ByteArray, sampleRateHz: Int, destination: ByteArray): Int =
        if (runner == null) {
            fallback.reduce(audioPcm16Mono, sampleRateHz, destination)
        } else {
            runner.process(audioPcm16Mono, sampleRateHz, destination)
        }
}

// =====================================================================
// Voice-activity detectors
// =====================================================================

/** Always reports speech — DI default so nothing breaks before a real VAD is wired. */
class NullVoiceActivityDetector private constructor() : IVoiceActivityDetector {
    companion object {
        val Instance = NullVoiceActivityDetector()
    }

    override val backendId: String get() = "null"
    override val speechThreshold: Float get() = 0.5f

    override fun classify(audioPcm16Mono: ByteArray, sampleRateHz: Int, offset: Duration): VadFrameResult =
        VadFrameResult(isSpeech = true, speechProbability = 1f, offset = offset)

    override fun reset() {}
}

/**
 * Production-grade VAD using RMS energy + zero-crossing rate + hangover-frame
 * smoothing. No ML model required — works on every device. Mirrors C#
 * `EnergyVoiceActivityDetector`.
 */
class EnergyVoiceActivityDetector(
    override val speechThreshold: Float = 0.55f,
    private val energyThreshold: Float = 0.012f,
    private val hangoverFrames: Int = 8,
) : IVoiceActivityDetector {

    private var hangoverRemaining = 0

    override val backendId: String get() = "energy"

    override fun classify(audioPcm16Mono: ByteArray, sampleRateHz: Int, offset: Duration): VadFrameResult {
        if (audioPcm16Mono.size < 2) {
            return VadFrameResult(isSpeech = false, speechProbability = 0f, offset = offset)
        }

        val sampleCount = audioPcm16Mono.size / 2
        var sumSquares = 0.0
        var zeroCrossings = 0
        var previous: Short = 0
        for (i in 0 until sampleCount) {
            val s = readInt16Le(audioPcm16Mono, i * 2)
            sumSquares += s.toDouble() * s.toDouble()
            if (i > 0 && sign(s.toDouble()) != sign(previous.toDouble()) && s.toInt() != 0 && previous.toInt() != 0) {
                zeroCrossings++
            }
            previous = s
        }
        val rms = sqrt(sumSquares / sampleCount) / Short.MAX_VALUE // 0..1
        val zcrRate = zeroCrossings.toFloat() / sampleCount

        // Speech: high RMS + moderate ZCR (~0.05-0.25 for voiced speech).
        val energyGood = rms >= energyThreshold
        val zcrGood = zcrRate in 0.02f..0.30f
        var rawProb = if (energyGood) (if (zcrGood) 0.85f else 0.6f) else 0.1f

        val isSpeech: Boolean
        if (rawProb >= speechThreshold) {
            isSpeech = true
            hangoverRemaining = hangoverFrames
        } else if (hangoverRemaining > 0) {
            isSpeech = true
            hangoverRemaining--
            rawProb = max(rawProb, speechThreshold)
        } else {
            isSpeech = false
        }

        return VadFrameResult(isSpeech, rawProb, offset)
    }

    override fun reset() {
        hangoverRemaining = 0
    }
}

/** (3.3.0) ONNX model runner contract supplied by the host package. Mirrors C# `IVadModelRunner`. */
interface IVadModelRunner {
    /** Score one 30 ms / 16 kHz PCM-16 frame; result is 0..1. */
    fun scoreFrame(audioPcm16Mono: ByteArray, sampleRateHz: Int): Float
}

/**
 * Silero VAD wrapper. Delegates the per-frame score to a host [IVadModelRunner];
 * when no runner is wired it transparently falls back to
 * [EnergyVoiceActivityDetector]'s scoring. Mirrors C# `SileroVoiceActivityDetector`.
 */
class SileroVoiceActivityDetector(
    private val runner: IVadModelRunner? = null,
    override val speechThreshold: Float = 0.5f,
    private val hangoverFrames: Int = 8,
) : IVoiceActivityDetector {

    private val fallback = EnergyVoiceActivityDetector(speechThreshold)
    private var hangoverRemaining = 0

    override val backendId: String get() = if (runner == null) "silero (fallback)" else "silero"

    override fun classify(audioPcm16Mono: ByteArray, sampleRateHz: Int, offset: Duration): VadFrameResult {
        if (runner == null) {
            return fallback.classify(audioPcm16Mono, sampleRateHz, offset)
        }

        val prob = runner.scoreFrame(audioPcm16Mono, sampleRateHz)
        val isSpeech: Boolean
        if (prob >= speechThreshold) {
            isSpeech = true
            hangoverRemaining = hangoverFrames
        } else if (hangoverRemaining > 0) {
            isSpeech = true
            hangoverRemaining--
        } else {
            isSpeech = false
        }
        return VadFrameResult(isSpeech, prob, offset)
    }

    override fun reset() {
        hangoverRemaining = 0
        fallback.reset()
    }
}

// =====================================================================
// End-of-turn detectors
// =====================================================================

/** Always says "they finished" — DI default. Mirrors C# `NullEndOfTurnDetector`. */
class NullEndOfTurnDetector private constructor() : IEndOfTurnDetector {
    companion object {
        val Instance = NullEndOfTurnDetector()
    }

    override val backendId: String get() = "null"

    override fun predict(partialTranscript: String, trailingSilence: Duration): EndOfTurnResult =
        EndOfTurnResult(isComplete = true, confidence = 1f, waitMoreMs = 0)

    override fun reset() {}
}

/**
 * Rule-based detector. Considers a turn complete when the transcript ends with
 * terminal punctuation AND the user has been silent for at least the minimum
 * hangover, OR when silence exceeds the maximum-wait ceiling regardless of text.
 * Recognises common "thinking" connectors (and, but, so, um, like...) to extend
 * the wait when present at the tail. Mirrors C# `RuleBasedEndOfTurnDetector`.
 */
class RuleBasedEndOfTurnDetector(
    minSilence: Duration? = null,
    hangingSilence: Duration? = null,
    maxSilence: Duration? = null,
) : IEndOfTurnDetector {

    private val minSilence: Duration = minSilence ?: Duration.ofMillis(400)
    private val hangingSilence: Duration = hangingSilence ?: Duration.ofMillis(900)
    private val maxSilence: Duration = maxSilence ?: Duration.ofMillis(2500)

    override val backendId: String get() = "rules"

    override fun predict(partialTranscript: String, trailingSilence: Duration): EndOfTurnResult {
        val text = (partialTranscript).trim()
        if (trailingSilence >= maxSilence) {
            return EndOfTurnResult(isComplete = true, confidence = 0.7f, waitMoreMs = 0)
        }

        if (text.isEmpty()) {
            val remainingMs = (minSilence - trailingSilence).toMillis().toDouble()
            return EndOfTurnResult(
                isComplete = false,
                confidence = 0.2f,
                waitMoreMs = max(150.0, remainingMs).toInt(),
            )
        }

        val endsTerminal = TERMINAL_PUNCTUATION.any { text.endsWith(it) }
        val lastWord = text.split(' ', '\t', '\n').filter { it.isNotEmpty() }.lastOrNull() ?: ""
        val endsHanging = HANGING_WORDS.contains(lastWord.trimEnd('.', ',', '!', '?').lowercase())

        if (endsHanging) {
            val remaining = hangingSilence - trailingSilence
            if (remaining <= Duration.ZERO) {
                return EndOfTurnResult(isComplete = true, confidence = 0.6f, waitMoreMs = 0)
            }
            return EndOfTurnResult(
                isComplete = false,
                confidence = 0.4f,
                waitMoreMs = ceil(remaining.toMillis().toDouble()).toInt(),
            )
        }

        if (endsTerminal && trailingSilence >= minSilence) {
            return EndOfTurnResult(isComplete = true, confidence = 0.9f, waitMoreMs = 0)
        }

        if (trailingSilence >= minSilence) {
            return EndOfTurnResult(isComplete = true, confidence = 0.75f, waitMoreMs = 0)
        }

        val ms = max(50.0, (minSilence - trailingSilence).toMillis().toDouble()).toInt()
        return EndOfTurnResult(isComplete = false, confidence = 0.6f, waitMoreMs = ms)
    }

    override fun reset() {}

    companion object {
        private val TERMINAL_PUNCTUATION = arrayOf(".", "!", "?", "。", "！", "？")
        private val HANGING_WORDS = setOf(
            "and", "but", "so", "or", "because", "if", "when", "while",
            "though", "however", "um", "uh", "like", "you", "the", "a", "an",
        )
    }
}

/** (3.3.0) Host-supplied semantic turn model. Mirrors C# `ITurnModelRunner`. */
interface ITurnModelRunner {
    /** Score the current state; 0..1 = probability the turn is complete. */
    fun scoreCompletion(partialTranscript: String, trailingSilence: Duration): Float
}

/**
 * Smart-turn wrapper. Uses the supplied semantic model when present; otherwise
 * falls back to [RuleBasedEndOfTurnDetector]. Mirrors C# `SmartTurnDetector`.
 */
class SmartTurnDetector(
    private val runner: ITurnModelRunner? = null,
    private val threshold: Float = 0.5f,
) : IEndOfTurnDetector {

    private val fallback = RuleBasedEndOfTurnDetector()

    override val backendId: String get() = if (runner == null) "smart-turn (fallback)" else "smart-turn-v2"

    override fun predict(partialTranscript: String, trailingSilence: Duration): EndOfTurnResult {
        if (runner == null) {
            return fallback.predict(partialTranscript, trailingSilence)
        }

        val prob = runner.scoreCompletion(partialTranscript, trailingSilence).coerceIn(0f, 1f)
        if (prob >= threshold) {
            return EndOfTurnResult(isComplete = true, confidence = prob, waitMoreMs = 0)
        }
        val waitMs = Math.round((1f - prob) * 1000f)
        return EndOfTurnResult(isComplete = false, confidence = prob, waitMoreMs = waitMs)
    }

    override fun reset() = fallback.reset()
}

// =====================================================================
// Audio-format conversion (G.711 mu-law / a-law + linear resample)
// =====================================================================

/** (3.3.0) Carrier-native audio formats we know how to convert. Mirrors C# `AudioCodec`. */
enum class AudioCodec {
    /** 16-bit signed linear PCM, little-endian, mono. */
    Pcm16,

    /** G.711 mu-law (telephony, North America / Japan). */
    MuLaw,

    /** G.711 A-law (telephony, Europe). */
    ALaw,
}

/** (3.3.0) Stateless audio-format converter. Mirrors C# `AudioFormatConverter`. */
object AudioFormatConverter {
    /**
     * Convert audio from one (codec, sample rate) to another. Returns the
     * freshly allocated output buffer; caller does NOT need to size it.
     */
    fun convert(
        input: ByteArray,
        inputCodec: AudioCodec,
        inputSampleRateHz: Int,
        outputCodec: AudioCodec,
        outputSampleRateHz: Int,
    ): ByteArray {
        require(inputSampleRateHz > 0) { "inputSampleRateHz" }
        require(outputSampleRateHz > 0) { "outputSampleRateHz" }

        // 1) Decode source to PCM-16.
        val pcmIn = when (inputCodec) {
            AudioCodec.Pcm16 -> input.copyOf()
            AudioCodec.MuLaw -> decodeMuLawToPcm16(input)
            AudioCodec.ALaw -> decodeALawToPcm16(input)
        }

        // 2) Resample if needed.
        val pcmResampled = if (inputSampleRateHz == outputSampleRateHz) {
            pcmIn
        } else {
            resamplePcm16Linear(pcmIn, inputSampleRateHz, outputSampleRateHz)
        }

        // 3) Encode to target codec.
        return when (outputCodec) {
            AudioCodec.Pcm16 -> pcmResampled
            AudioCodec.MuLaw -> encodePcm16ToMuLaw(pcmResampled)
            AudioCodec.ALaw -> encodePcm16ToALaw(pcmResampled)
        }
    }

    // ===== mu-law =====

    fun decodeMuLawToPcm16(mulaw: ByteArray): ByteArray {
        val pcm = ByteArray(mulaw.size * 2)
        for (i in mulaw.indices) {
            val s = muLawToLinear(mulaw[i])
            writeInt16Le(pcm, i * 2, s)
        }
        return pcm
    }

    fun encodePcm16ToMuLaw(pcm: ByteArray): ByteArray {
        val samples = pcm.size / 2
        val mulaw = ByteArray(samples)
        for (i in 0 until samples) {
            val s = readInt16Le(pcm, i * 2)
            mulaw[i] = linearToMuLaw(s)
        }
        return mulaw
    }

    private fun muLawToLinear(mu: Byte): Short {
        // G.711 mu-law decode (ITU-T G.711).
        val inv = (mu.toInt() and 0xFF).inv() and 0xFF
        val sign = inv and 0x80
        val exponent = (inv shr 4) and 0x07
        val mantissa = inv and 0x0F
        val magnitude = ((mantissa shl 3) + 0x84) shl exponent
        val sample = magnitude - 0x84
        return (if (sign != 0) -sample else sample).toShort()
    }

    private fun linearToMuLaw(pcm: Short): Byte {
        val bias = 0x84
        val clip = 32635
        val p = pcm.toInt()
        val sign = (p shr 8) and 0x80
        var v = p
        if (sign != 0) v = -v
        if (v > clip) v = clip
        v += bias

        val exponent = when {
            v >= 0x4000 -> 7
            v >= 0x2000 -> 6
            v >= 0x1000 -> 5
            v >= 0x0800 -> 4
            v >= 0x0400 -> 3
            v >= 0x0200 -> 2
            v >= 0x0100 -> 1
            else -> 0
        }

        val mantissa = (v shr (exponent + 3)) and 0x0F
        return ((sign or (exponent shl 4) or mantissa).inv()).toByte()
    }

    // ===== a-law =====

    fun decodeALawToPcm16(alaw: ByteArray): ByteArray {
        val pcm = ByteArray(alaw.size * 2)
        for (i in alaw.indices) {
            val s = aLawToLinear(alaw[i])
            writeInt16Le(pcm, i * 2, s)
        }
        return pcm
    }

    fun encodePcm16ToALaw(pcm: ByteArray): ByteArray {
        val samples = pcm.size / 2
        val alaw = ByteArray(samples)
        for (i in 0 until samples) {
            val s = readInt16Le(pcm, i * 2)
            alaw[i] = linearToALaw(s)
        }
        return alaw
    }

    private fun aLawToLinear(a: Byte): Short {
        val x = (a.toInt() and 0xFF) xor 0x55
        val sign = x and 0x80
        val exponent = (x shr 4) and 0x07
        val mantissa = x and 0x0F
        val magnitude = if (exponent != 0) {
            ((mantissa shl 4) + 0x108) shl (exponent - 1)
        } else {
            (mantissa shl 4) + 0x08
        }
        return (if (sign != 0) -magnitude else magnitude).toShort()
    }

    private fun linearToALaw(pcm: Short): Byte {
        val p = pcm.toInt()
        val sign = (p shr 8) and 0x80
        var v = p
        if (sign != 0) v = -v
        if (v > 0x7FFF) v = 0x7FFF

        val exponent: Int
        val mantissa: Int
        if (v < 256) {
            exponent = 0
            mantissa = v shr 4
        } else {
            exponent = when {
                v >= 0x4000 -> 7
                v >= 0x2000 -> 6
                v >= 0x1000 -> 5
                v >= 0x0800 -> 4
                v >= 0x0400 -> 3
                v >= 0x0200 -> 2
                else -> 1
            }
            mantissa = (v shr (exponent + 3)) and 0x0F
        }
        return ((sign or (exponent shl 4) or mantissa) xor 0x55).toByte()
    }

    // ===== resample (linear interpolation) =====

    fun resamplePcm16Linear(pcm: ByteArray, fromHz: Int, toHz: Int): ByteArray {
        if (fromHz == toHz) return pcm
        val srcSamples = pcm.size / 2
        val dstSamples = (srcSamples.toLong() * toHz / fromHz).toInt()
        val dst = ByteArray(dstSamples * 2)
        for (i in 0 until dstSamples) {
            val srcIdx = i.toDouble() * fromHz / toHz
            val idx0 = floor(srcIdx).toInt()
            val idx1 = min(idx0 + 1, srcSamples - 1)
            val frac = srcIdx - idx0
            val s0 = readInt16Le(pcm, idx0 * 2)
            val s1 = readInt16Le(pcm, idx1 * 2)
            val s = (s0 + (s1 - s0) * frac).toInt().toShort()
            writeInt16Le(dst, i * 2, s)
        }
        return dst
    }
}

// =====================================================================
// Little-endian PCM-16 helpers (BinaryPrimitives.{Read,Write}Int16LittleEndian)
// =====================================================================

internal fun readInt16Le(buffer: ByteArray, offset: Int): Short {
    val lo = buffer[offset].toInt() and 0xFF
    val hi = buffer[offset + 1].toInt() and 0xFF
    return ((hi shl 8) or lo).toShort()
}

internal fun writeInt16Le(buffer: ByteArray, offset: Int, value: Short) {
    val v = value.toInt()
    buffer[offset] = (v and 0xFF).toByte()
    buffer[offset + 1] = ((v shr 8) and 0xFF).toByte()
}
