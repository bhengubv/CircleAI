// Realtime.kt
//
// Kotlin port of CircleAI.Realtime (Contracts.cs + LoopbackRealtimeService.cs +
// NullImplementations.cs) — the C# reference is the EXACT spec. Carrier-agnostic
// contracts for streaming realtime AI voice services, plus a built-in in-process
// loopback implementation that makes the package usable end-to-end for tests + dev.
//
// Design fidelity notes:
//   * C# `enum` (RealtimeAudioFormat, RealtimeDirection) -> Kotlin `enum class`.
//   * C# `sealed record`                                 -> Kotlin `data class`.
//   * C# abstract-record union `RealtimeEvent` + subtypes -> Kotlin `sealed class`
//     `RealtimeEvent(at)` + `data class` subclasses (same discriminated union).
//   * C# `ReadOnlyMemory<byte>`                           -> `ByteArray`.
//   * C# `TimeSpan` / `DateTimeOffset`                    -> `java.time.Duration` /
//     `java.time.Instant`.
//   * C# `IAsyncEnumerable<T>`                            -> `kotlinx.coroutines.flow.Flow<T>`.
//   * C# `ValueTask<T>` / `ValueTask`                     -> `suspend fun`.
//   * C# `IAsyncDisposable`                               -> `AutoCloseable` + a
//     suspend `disposeAsync()` (the codebase convention).
//   * C# `Channel.CreateUnbounded<T>`                     -> `Channel(UNLIMITED)`,
//     drained to a `Flow` via `flow { for (x in ch) emit(x) }`; `TryWrite` ->
//     `trySend`, `TryComplete` -> `close()`.
//   * `delegate LoopbackTextToAudio`                      -> a `fun interface`.
//   * The silence-TTS sizing, RMS silence detector, and offset accounting are
//     reproduced byte-for-byte: integer `sr*durationMs/1000` sample count, 16-bit
//     zero PCM, RMS `< 250.0` threshold, and `offset += ms(pcm.len/2/sr*1000)`.

package com.bhengubv.circleai.realtime

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import java.time.Duration
import java.time.Instant
import java.util.UUID
import kotlin.math.sqrt

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** Audio format used in realtime sessions. Mirrors C# `RealtimeAudioFormat`. */
enum class RealtimeAudioFormat {
    /** 16-bit linear PCM, mono, 16 kHz. */
    Pcm16k,

    /** 16-bit linear PCM, mono, 24 kHz. */
    Pcm24k,

    /** G.711 μ-law, mono, 8 kHz (carrier-native). */
    Mulaw8k,
}

/** Direction of audio in a realtime session. Mirrors C# `RealtimeDirection`. */
enum class RealtimeDirection { Inbound, Outbound }

/**
 * Configuration for opening a realtime session. Mirrors C# `RealtimeSessionConfig`.
 *
 * @param model Vendor-specific model id (e.g. `gpt-4o-realtime-preview-2024-12-17`).
 * @param voiceId Vendor voice id (e.g. `alloy` for OpenAI, `Aoede` for Gemini).
 * @param systemPrompt Persona / instructions that shape the assistant's responses.
 * @param audioFormat Wire audio format. The host transcodes to/from this if the carrier differs.
 * @param languageHint ISO language hint (e.g. `en-US`); null = auto-detect.
 * @param tools Optional list of tool definitions exposed to the model.
 */
data class RealtimeSessionConfig(
    val model: String,
    val voiceId: String? = null,
    val systemPrompt: String? = null,
    val audioFormat: RealtimeAudioFormat = RealtimeAudioFormat.Pcm24k,
    val languageHint: String? = null,
    val tools: List<RealtimeTool>? = null,
)

/** One tool the model can call. Mirrors C# `RealtimeTool`. */
data class RealtimeTool(val name: String, val description: String, val jsonSchema: String)

/** One audio frame in a realtime session. Mirrors C# `RealtimeAudioFrame`. */
data class RealtimeAudioFrame(
    val pcm: ByteArray,
    val format: RealtimeAudioFormat,
    val offset: Duration,
) {
    // ByteArray uses reference equality by default; give data-class value semantics
    // over the content so frames compare structurally (the C# record compares the
    // ReadOnlyMemory segment — content parity is the meaningful contract in tests).
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is RealtimeAudioFrame) return false
        return format == other.format && offset == other.offset && pcm.contentEquals(other.pcm)
    }

    override fun hashCode(): Int {
        var result = pcm.contentHashCode()
        result = 31 * result + format.hashCode()
        result = 31 * result + offset.hashCode()
        return result
    }
}

/** Discriminated union of events emitted by the vendor session. Mirrors C# `RealtimeEvent`. */
sealed class RealtimeEvent(val at: Instant)

/** Caller speech started. */
class SpeechStartedEvent(at: Instant) : RealtimeEvent(at)

/** Caller speech ended (model is now processing). */
class SpeechEndedEvent(at: Instant) : RealtimeEvent(at)

/** Partial transcript of speech. */
class TranscriptDeltaEvent(at: Instant, val delta: String, val direction: RealtimeDirection) : RealtimeEvent(at)

/** Full transcript of an utterance (final). */
class TranscriptFinalEvent(at: Instant, val text: String, val direction: RealtimeDirection) : RealtimeEvent(at)

/** The model wants to call a tool. */
class ToolCallEvent(at: Instant, val callId: String, val toolName: String, val argumentsJson: String) : RealtimeEvent(at)

/** The assistant turn is complete. */
class TurnCompleteEvent(at: Instant) : RealtimeEvent(at)

/** Vendor reported an error mid-session. */
class SessionErrorEvent(at: Instant, val message: String) : RealtimeEvent(at)

/**
 * One open conversation with a realtime vendor. Audio flows in both directions
 * concurrently; control + transcripts surface as [RealtimeEvent]s. Mirrors C#
 * `IRealtimeSession` (which is `IAsyncDisposable`).
 */
interface IRealtimeSession : AutoCloseable {
    /** Session identifier from the vendor. */
    val sessionId: String

    /** Inbound audio (from caller → us). */
    fun receiveAudioAsync(): Flow<RealtimeAudioFrame>

    /** Send one audio frame to the model. */
    suspend fun sendAudioAsync(frame: RealtimeAudioFrame)

    /** Send a text turn to the model (no audio, e.g. for a TTS-only turn). */
    suspend fun sendTextAsync(text: String)

    /** Reply to a tool call with its result. */
    suspend fun sendToolResultAsync(callId: String, resultJson: String)

    /** Cancel the current model response (e.g. on barge-in). */
    suspend fun cancelResponseAsync()

    /** Control + transcript events from the vendor. */
    fun receiveEventsAsync(): Flow<RealtimeEvent>

    /** Release the session. Mirrors C# `IAsyncDisposable.DisposeAsync`. */
    suspend fun disposeAsync()

    /** [AutoCloseable] bridge — runs [disposeAsync] synchronously. */
    override fun close() {
        kotlinx.coroutines.runBlocking { disposeAsync() }
    }
}

/** Vendor connector — opens realtime sessions. Mirrors C# `IRealtimeService`. */
interface IRealtimeService {
    /** Vendor self-id (e.g. `openai-realtime`). */
    val providerId: String

    /** True when credentials are present. */
    val isConfigured: Boolean

    /** Open one realtime session per the supplied config. */
    suspend fun startSessionAsync(config: RealtimeSessionConfig): IRealtimeSession
}

// =====================================================================
// LoopbackRealtimeService (LoopbackRealtimeService.cs)
// =====================================================================

/**
 * Synthesise outbound audio for text. The default produces real silence frames
 * matching the text's expected speech duration (~80 ms per word). Hosts with a
 * real TTS engine plug it in via [LoopbackRealtimeService]'s constructor. Mirrors
 * C# `delegate LoopbackTextToAudio`.
 */
fun interface LoopbackTextToAudio {
    suspend fun invoke(text: String, format: RealtimeAudioFormat): ByteArray
}

/**
 * Built-in, in-process [IRealtimeService] — connects audio in to audio out
 * (loopback), surfaces speech-started/ended events from silence detection, and
 * replies to [IRealtimeSession.sendTextAsync] with a synthesised PCM stream.
 * Mirrors C# `LoopbackRealtimeService`.
 */
class LoopbackRealtimeService(
    private val textToAudio: LoopbackTextToAudio = SilenceTextToAudio,
) : IRealtimeService {

    override val providerId: String get() = "loopback"
    override val isConfigured: Boolean get() = true

    override suspend fun startSessionAsync(config: RealtimeSessionConfig): IRealtimeSession =
        LoopbackRealtimeSession(config, textToAudio)

    companion object {
        /**
         * Default: emit real silence frames sized to ~80 ms per word. Real audio
         * bytes (zero amplitude) so downstream signal processing / duration
         * accounting works. Mirrors C# `SilenceTextToAudio`.
         */
        val SilenceTextToAudio = LoopbackTextToAudio { text, format ->
            val sr = LoopbackRealtimeSession.sampleRateOf(format)
            val wordCount =
                if (text.isBlank()) 0
                else text.split(' ', '\t', '\n').count { it.isNotEmpty() }
            val durationMs = maxOf(50, wordCount * 80)
            val sampleCount = sr * durationMs / 1000        // integer division — matches C#
            ByteArray(sampleCount * 2)                       // 16-bit silence (already zeros)
        }
    }
}

/**
 * Built-in loopback [IRealtimeSession]. Echoes inbound audio back as outbound,
 * derives speech-started/ended from an RMS silence detector, and synthesises a
 * transcript + audio + turn-complete sequence in response to text. Mirrors C#
 * `LoopbackRealtimeSession`.
 */
class LoopbackRealtimeSession(
    private val config: RealtimeSessionConfig,
    private val textToAudio: LoopbackTextToAudio = LoopbackRealtimeService.SilenceTextToAudio,
) : IRealtimeSession {

    private val audio = Channel<RealtimeAudioFrame>(Channel.UNLIMITED)
    private val events = Channel<RealtimeEvent>(Channel.UNLIMITED)
    private var offset: Duration = Duration.ZERO
    private var speaking = false

    override val sessionId: String = "loop-" + UUID.randomUUID().toString().replace("-", "")

    override fun receiveAudioAsync(): Flow<RealtimeAudioFrame> = flow {
        for (f in audio) emit(f)
    }

    override suspend fun sendAudioAsync(frame: RealtimeAudioFrame) {
        // ArgumentNullException.ThrowIfNull(frame) — Kotlin non-null type enforces this.
        val nowSpeaking = !isSilent(frame.pcm)
        if (nowSpeaking != speaking) {
            events.trySend(
                if (nowSpeaking) SpeechStartedEvent(Instant.now())
                else SpeechEndedEvent(Instant.now()),
            )
            speaking = nowSpeaking
        }
        // Loopback: echo received audio back as outbound.
        audio.trySend(frame)
    }

    override suspend fun sendTextAsync(text: String) {
        // C#: null text throws; Kotlin non-null String enforces this.
        events.trySend(TranscriptDeltaEvent(Instant.now(), text, RealtimeDirection.Outbound))
        val pcm = textToAudio.invoke(text, config.audioFormat)
        if (pcm.isNotEmpty()) {
            audio.trySend(RealtimeAudioFrame(pcm, config.audioFormat, offset))
            val ms = pcm.size / 2.0 / sampleRateOf(config.audioFormat) * 1000.0
            offset = offset.plus(Duration.ofNanos(Math.round(ms * 1_000_000.0)))
        }
        events.trySend(TranscriptFinalEvent(Instant.now(), text, RealtimeDirection.Outbound))
        events.trySend(TurnCompleteEvent(Instant.now()))
    }

    override suspend fun sendToolResultAsync(callId: String, resultJson: String) {
        require(callId.isNotBlank()) { "callId required" }
        // C#: null resultJson throws; Kotlin non-null String enforces this.
        events.trySend(
            TranscriptDeltaEvent(
                Instant.now(),
                "[tool $callId: ${truncate(resultJson, 60)}]",
                RealtimeDirection.Outbound,
            ),
        )
    }

    override suspend fun cancelResponseAsync() {
        events.trySend(TurnCompleteEvent(Instant.now()))
    }

    override fun receiveEventsAsync(): Flow<RealtimeEvent> = flow {
        for (e in events) emit(e)
    }

    override suspend fun disposeAsync() {
        audio.close()
        events.close()
    }

    companion object {
        /** Mirrors C# `SampleRateOf`. */
        fun sampleRateOf(f: RealtimeAudioFormat): Int = when (f) {
            RealtimeAudioFormat.Pcm16k -> 16_000
            RealtimeAudioFormat.Pcm24k -> 24_000
            RealtimeAudioFormat.Mulaw8k -> 8_000
        }

        /** RMS-based silence detector over 16-bit linear PCM. Mirrors C# `IsSilent`. */
        private fun isSilent(pcm: ByteArray): Boolean {
            if (pcm.size < 64) return true
            var sumSq = 0L
            val samples = pcm.size / 2
            var i = 0
            while (i < pcm.size) {
                // Little-endian 16-bit signed sample. `(pcm[i] and 0xFF)` masks the
                // low byte; `(pcm[i+1].toInt() shl 8)` is sign-extended so the
                // combined Short is signed — identical to C# `(short)(lo | (hi<<8))`.
                val s = ((pcm[i].toInt() and 0xFF) or (pcm[i + 1].toInt() shl 8)).toShort().toInt()
                sumSq += (s * s).toLong()
                i += 2
            }
            val rms = sqrt(sumSq / samples.toDouble())
            return rms < 250.0  // ~ -42 dBFS
        }

        private fun truncate(s: String, max: Int): String = if (s.length <= max) s else s.substring(0, max) + "…"
    }
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

/** Throws on [startSessionAsync]; reports [isConfigured] = false. Mirrors C# `NullRealtimeService`. */
class NullRealtimeService private constructor() : IRealtimeService {
    companion object {
        val Instance = NullRealtimeService()
    }

    override val providerId: String get() = "null"
    override val isConfigured: Boolean get() = false

    override suspend fun startSessionAsync(config: RealtimeSessionConfig): IRealtimeSession =
        throw IllegalStateException(
            "No realtime vendor is registered. Add CircleAI.Realtime.Cloud connectors (OpenAI, Gemini, Nova, ElevenLabs, Ultravox).",
        )
}

/** A session that yields nothing — fully muted. Mirrors C# `NullRealtimeSession`. */
class NullRealtimeSession : IRealtimeSession {
    override val sessionId: String get() = "null"

    override fun receiveAudioAsync(): Flow<RealtimeAudioFrame> = flow { }
    override suspend fun sendAudioAsync(frame: RealtimeAudioFrame) {}
    override suspend fun sendTextAsync(text: String) {}
    override suspend fun sendToolResultAsync(callId: String, resultJson: String) {}
    override suspend fun cancelResponseAsync() {}
    override fun receiveEventsAsync(): Flow<RealtimeEvent> = flow { }
    override suspend fun disposeAsync() {}
}
