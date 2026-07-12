// VoiceAgent.kt
//
// Kotlin port of the CircleAI.Telephony (3.3.0) voice-agent layer — the
// carrier-agnostic conversation-shaping logic that sits ON TOP of the carrier
// contracts already ported in Telephony.kt. The C# reference files are the EXACT
// spec. Everything here is pure logic (no carrier network of its own); the two
// outward boundaries — HTTP (MCP import / consult webhook) and the local-dev tunnel
// (native ngrok / cloudflared process) — are injected behind interfaces, exactly as
// the carriers inject TelephonyHttpTransport.
//
// Ported C# files:
//   BargeInController.cs, AnsweringMachineDetector.cs, IvrLoopDetector.cs,
//   LlmJudge.cs, EvalSession.cs, LatencyTracker.cs, ReassuranceFiller.cs,
//   PromptVariableResolver.cs, FirstMessagePreamble.cs, SpeculativeGenerator.cs,
//   SentenceChunker.cs, Guardrails.cs, ToolCircuitBreaker.cs, VoiceLoopAsTool.cs,
//   StreamingToolProgress.cs, McpToolImporter.cs, ConsultEscalation.cs,
//   PhoneNumberProvisioner.cs, SpeechLifecycleEvents.cs, FalseInterruptionTracker.cs,
//   HoldMusicMixer.cs, StereoCallRecorder.cs, CallCostCalculator.cs, DashboardData.cs,
//   LocalDevTunnel.cs.
//
// Design fidelity notes (matching Telephony.kt + the carrier sibling ports):
//   * C# `enum`                         -> Kotlin `enum class`.
//   * C# `sealed record`                -> Kotlin `data class`; abstract record union
//                                          (SpeechLifecycleEvent) -> `sealed class`.
//   * C# `TimeSpan` / `DateTimeOffset`  -> `java.time.Duration` / `java.time.Instant`.
//   * C# `decimal`                      -> `java.math.BigDecimal`.
//   * C# `ReadOnlySpan<byte>` / `Span<byte>` -> `ByteArray` (+ explicit offset/length
//                                          where the reference slices in place).
//   * C# `Func<DateTimeOffset>` clock   -> `() -> Instant`, defaulting to Instant.now.
//   * C# `delegate ... Task<T>`         -> `fun interface` with a `suspend` operator.
//   * C# `Task` / `ValueTask` / `IAsyncEnumerable` -> `suspend fun` / `Flow`.
//   * C# `Interlocked` counters         -> `AtomicLong` / `synchronized`, same visibility.
//   * C# `ILogger` (Microsoft.Extensions.Logging — NOT on the Kotlin classpath) -> an
//     injected `TelephonyLogger` fun interface defaulting to a no-op sink, mirroring the
//     `PluginLogger` precedent in the plugins package and C#'s `NullLogger.Instance`.
//   * C# `HttpClient` (MCP importer / consult webhook) -> the existing injected
//     `TelephonyHttpTransport` from Telephony.kt — no real socket in tests.
//   * C# `System.Text.Json` reads       -> kotlinx.serialization JSON tree, the tree's
//     catalog/Json.kt + Telephony.kt convention.
//   * DTMF/WAV/PCM byte maths (HoldMusicMixer, StereoCallRecorder) reproduced
//     byte-for-byte: same little-endian 16-bit reads/writes, same 44-byte WAV header
//     layout, same clamp/gain arithmetic as BinaryPrimitives.
//   * The "native-tunnel seam" (LocalDevTunnel / Ngrok / Cloudflare) is ported as the
//     `LocalDevTunnel` interface + a `TunnelResolver` fun-interface boundary that a host
//     backs with the real ngrok/cloudflared process; logic ports, the process is injected.

package com.bhengubv.circleai.telephony

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.selects.onTimeout
import kotlinx.coroutines.selects.select
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.io.OutputStream
import java.math.BigDecimal
import java.math.RoundingMode
import java.net.URI
import java.net.URLEncoder
import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import kotlin.math.abs
import kotlin.math.ceil
import kotlin.math.sqrt

// =====================================================================
// Shared: logger seam (Microsoft.Extensions.Logging is not on the classpath).
// Mirrors the PluginLogger precedent + C#'s NullLogger.Instance default.
// =====================================================================

/**
 * Minimal logging seam for the voice-agent layer. The C# reference logs through
 * `Microsoft.Extensions.Logging.ILogger`, which is not on the Kotlin classpath; a host
 * that wants logs injects this, and everything defaults to [NoOp] (the analogue of
 * `NullLogger.Instance`).
 */
fun interface TelephonyLogger {
    fun log(level: TelephonyLogLevel, message: String, error: Throwable?)

    companion object {
        /** Discards every message. Mirrors `NullLogger.Instance`. */
        val NoOp: TelephonyLogger = TelephonyLogger { _, _, _ -> }
    }
}

/** Message-only convenience over [TelephonyLogger.log] (no associated error). */
fun TelephonyLogger.log(level: TelephonyLogLevel, message: String) = log(level, message, null)

/** Severity for [TelephonyLogger]. Mirrors the `LogLevel` values actually used. */
enum class TelephonyLogLevel { Information, Warning, Error }

// =====================================================================
// Shared: monotonic-ish wall clock seam. C# uses `Func<DateTimeOffset>`; the Kotlin
// convention is a `() -> Instant`. This alias documents intent at call sites.
// =====================================================================

/** A clock the deterministic tests can pin. Mirrors C# `Func<DateTimeOffset>`. */
typealias TelephonyClock = () -> Instant

private val SystemClock: TelephonyClock = { Instant.now() }

// =====================================================================
// Barge-in (BargeInController.cs)
// =====================================================================

/** State of the AI's current turn. Mirrors C# `BargeInState`. */
enum class BargeInState {
    /** AI is speaking. */
    Speaking,

    /** Caller interrupted; playback paused while we decide. */
    Paused,

    /** Confirmed real interruption — turn cancelled. */
    Cancelled,

    /** Decided false alarm — resumed speaking. */
    Resumed,
}

/** One state transition. Mirrors C# `BargeInTransition`. */
data class BargeInTransition(val from: BargeInState, val to: BargeInState, val at: Instant, val reason: String)

/**
 * Configuration for barge-in detection. Mirrors C# `BargeInOptions`.
 *
 * @param pauseAfter How long the caller must be talking before we pause. Default 100 ms.
 * @param cancelAfter Continued speech that confirms a real interruption. Default 600 ms.
 */
data class BargeInOptions(
    val pauseAfter: Duration? = null,
    val cancelAfter: Duration? = null,
) {
    val pauseAfterOrDefault: Duration get() = pauseAfter ?: Duration.ofMillis(100)
    val cancelAfterOrDefault: Duration get() = cancelAfter ?: Duration.ofMillis(600)
}

/** Drives barge-in pause/resume/cancel decisions. Mirrors C# `BargeInController`. */
class BargeInController(
    options: BargeInOptions? = null,
    private val clock: TelephonyClock = SystemClock,
) {
    private val options: BargeInOptions = options ?: BargeInOptions()
    private val gate = Any()
    private var stateField: BargeInState = BargeInState.Speaking
    private var callerSpeechStartedAt: Instant? = null

    /** The current state of the AI turn. */
    val state: BargeInState
        get() = synchronized(gate) { stateField }

    /** Call when AI playback begins. */
    fun onPlaybackStart() {
        synchronized(gate) {
            stateField = BargeInState.Speaking
            callerSpeechStartedAt = null
        }
    }

    /** Call on each frame where the VAD reports caller speech. */
    fun onCallerSpeech(): BargeInTransition? {
        val now = clock()
        synchronized(gate) {
            if (stateField == BargeInState.Cancelled) return null

            val started = callerSpeechStartedAt
            if (started == null) {
                callerSpeechStartedAt = now
                return null
            }

            val elapsed = Duration.between(started, now)
            if (stateField == BargeInState.Speaking && elapsed >= options.pauseAfterOrDefault) {
                val t = BargeInTransition(
                    stateField, BargeInState.Paused, now,
                    "Caller speech ${elapsed.toMillis()} ms",
                )
                stateField = BargeInState.Paused
                return t
            }
            if (stateField == BargeInState.Paused && elapsed >= options.cancelAfterOrDefault) {
                val t = BargeInTransition(
                    stateField, BargeInState.Cancelled, now,
                    "Confirmed barge-in after ${elapsed.toMillis()} ms",
                )
                stateField = BargeInState.Cancelled
                return t
            }
            return null
        }
    }

    /** Call on each frame where VAD reports silence. */
    fun onCallerSilence(): BargeInTransition? {
        val now = clock()
        synchronized(gate) {
            callerSpeechStartedAt = null

            if (stateField == BargeInState.Paused) {
                val t = BargeInTransition(stateField, BargeInState.Resumed, now, "Caller fell silent after pause")
                stateField = BargeInState.Speaking // resume
                return t
            }
            return null
        }
    }

    /** Whether the AI should keep emitting audio frames right now. */
    val shouldEmitAudio: Boolean
        get() = synchronized(gate) { stateField == BargeInState.Speaking }

    /** Whether the turn was confirmed barge-in (caller wins, AI should drop). */
    val wasBargedIn: Boolean
        get() = synchronized(gate) { stateField == BargeInState.Cancelled }
}

// =====================================================================
// Answering-machine detection (AnsweringMachineDetector.cs)
// =====================================================================

/** Verdict from the answering-machine detector. Mirrors C# `AmdVerdict`. */
enum class AmdVerdict { Unknown, Human, AnsweringMachine }

/**
 * Heuristic AMD configuration. Mirrors C# `AmdOptions`.
 *
 * @param humanMaxFirstUtteranceMs Above this length, likely a machine. Default 1800 ms.
 * @param humanMinFirstUtteranceMs Below this it's too short to decide. Default 300 ms.
 * @param maxObservationWindow Stop accumulating once this elapses. Default 3500 ms.
 * @param silenceFrameThresholdMs Silence for this long ends the current utterance. Default 250 ms.
 */
data class AmdOptions(
    val humanMaxFirstUtteranceMs: Int? = null,
    val humanMinFirstUtteranceMs: Int? = null,
    val maxObservationWindow: Int? = null,
    val silenceFrameThresholdMs: Int? = null,
) {
    val humanMaxFirstUtteranceMsOrDefault: Int get() = humanMaxFirstUtteranceMs ?: 1800
    val humanMinFirstUtteranceMsOrDefault: Int get() = humanMinFirstUtteranceMs ?: 300
    val maxObservationWindowOrDefault: Int get() = maxObservationWindow ?: 3500
    val silenceFrameThresholdMsOrDefault: Int get() = silenceFrameThresholdMs ?: 250
}

/**
 * Frame-by-frame AMD. Feed PCM-16 frames in until [currentVerdict] stabilises. Mirrors
 * C# `AnsweringMachineDetector`.
 */
class AnsweringMachineDetector(options: AmdOptions? = null) {
    private val options: AmdOptions = options ?: AmdOptions()
    private val gate = Any()
    private var firstUtteranceLength: Duration = Duration.ZERO
    private var accumulatedAudio: Duration = Duration.ZERO
    private var utteranceInProgress = false
    private var trailingSilence: Duration = Duration.ZERO
    private var verdict: AmdVerdict = AmdVerdict.Unknown

    val currentVerdict: AmdVerdict get() = synchronized(gate) { verdict }

    /** Feed one frame of PCM-16 mono. Returns the (possibly updated) verdict. */
    fun observe(pcmFrame: ByteArray, sampleRateHz: Int): AmdVerdict {
        require(sampleRateHz > 0) { "sampleRateHz" }
        if (pcmFrame.size < 2) return currentVerdict

        // TimeSpan.FromMilliseconds keeps sub-ms precision; Duration.ofNanos preserves it.
        val frameMs = 1000.0 * (pcmFrame.size / 2) / sampleRateHz
        val frameDuration = Duration.ofNanos((frameMs * 1_000_000.0).toLong())
        val isSpeech = frameHasSpeech(pcmFrame)

        synchronized(gate) {
            if (verdict != AmdVerdict.Unknown) return verdict

            accumulatedAudio = accumulatedAudio.plus(frameDuration)

            if (isSpeech) {
                if (!utteranceInProgress) {
                    utteranceInProgress = true
                }
                firstUtteranceLength = firstUtteranceLength.plus(frameDuration)
                trailingSilence = Duration.ZERO
            } else if (utteranceInProgress) {
                trailingSilence = trailingSilence.plus(frameDuration)
                if (trailingSilence.toMillisDouble() >= options.silenceFrameThresholdMsOrDefault) {
                    utteranceInProgress = false
                }
            }

            // Decide.
            val firstMs = firstUtteranceLength.toMillisDouble()
            if (firstMs >= options.humanMaxFirstUtteranceMsOrDefault) {
                verdict = AmdVerdict.AnsweringMachine
            } else if (!utteranceInProgress &&
                firstMs >= options.humanMinFirstUtteranceMsOrDefault &&
                firstMs < options.humanMaxFirstUtteranceMsOrDefault
            ) {
                verdict = AmdVerdict.Human
            } else if (accumulatedAudio.toMillisDouble() >= options.maxObservationWindowOrDefault) {
                verdict = if (firstMs < options.humanMinFirstUtteranceMsOrDefault) {
                    AmdVerdict.Unknown
                } else {
                    AmdVerdict.AnsweringMachine
                }
            }
            return verdict
        }
    }

    fun reset() {
        synchronized(gate) {
            firstUtteranceLength = Duration.ZERO
            accumulatedAudio = Duration.ZERO
            utteranceInProgress = false
            trailingSilence = Duration.ZERO
            verdict = AmdVerdict.Unknown
        }
    }

    private companion object {
        const val ENERGY_THRESHOLD = 0.012f

        fun frameHasSpeech(pcm: ByteArray): Boolean {
            // Interpret bytes as little-endian 16-bit samples (MemoryMarshal.Cast<byte,short>).
            val sampleCount = pcm.size / 2
            if (sampleCount == 0) return false
            var sumSquares = 0.0
            for (i in 0 until sampleCount) {
                val lo = pcm[i * 2].toInt() and 0xFF
                val hi = pcm[i * 2 + 1].toInt()
                val s = (hi shl 8) or lo // sign-extends via hi being a signed Int
                sumSquares += (s * s).toDouble()
            }
            val rms = sqrt(sumSquares / sampleCount) / Short.MAX_VALUE
            return rms >= ENERGY_THRESHOLD
        }
    }
}

// =====================================================================
// IVR loop detection (IvrLoopDetector.cs)
// =====================================================================

/**
 * One observation in the IVR conversation. Mirrors C# `IvrRound`.
 *
 * @param speech Text heard from the IVR.
 * @param dtmfPressed Digits the AI sent in response, if any.
 * @param at When this round happened.
 */
data class IvrRound(val speech: String, val dtmfPressed: String?, val at: Instant)

/**
 * Verdict on IVR navigation health. Mirrors C# `IvrLoopVerdict`.
 *
 * @param isLooping True if the navigator looks stuck.
 * @param loopLength Estimated length of the repeating cycle (number of rounds).
 * @param reason Human-readable reason.
 */
data class IvrLoopVerdict(val isLooping: Boolean, val loopLength: Int, val reason: String)

/** Records IVR rounds and surfaces a loop verdict. Mirrors C# `IvrLoopDetector`. */
class IvrLoopDetector(
    private val maxRoundsToTrack: Int = 32,
    private val minRoundsForLoop: Int = 2,
    private val similarityThreshold: Double = 0.85,
) {
    private val rounds = ArrayList<IvrRound>()
    private val gate = Any()

    /** Append one round and return the current verdict. */
    fun observe(round: IvrRound): IvrLoopVerdict {
        synchronized(gate) {
            rounds.add(round)
            while (rounds.size > maxRoundsToTrack) {
                rounds.removeAt(0)
            }
            return evaluate()
        }
    }

    /** Current verdict without adding a new round. */
    fun currentVerdict(): IvrLoopVerdict = synchronized(gate) { evaluate() }

    /** Drop all history. */
    fun reset() {
        synchronized(gate) { rounds.clear() }
    }

    private fun evaluate(): IvrLoopVerdict {
        // Strong signal first — same DTMF + similar prompt three times in a row.
        if (rounds.size >= 3) {
            val tail = rounds.subList(rounds.size - 3, rounds.size)
            if (tail.all { it.dtmfPressed == tail[0].dtmfPressed } &&
                tail.all { similarTo(it.speech, tail[0].speech) }
            ) {
                return IvrLoopVerdict(true, 1, "Same prompt-and-press triple in a row.")
            }
        }

        if (rounds.size < minRoundsForLoop * 2) {
            return IvrLoopVerdict(false, 0, "Not enough rounds to evaluate.")
        }

        // Look for a repeating cycle of length L in the last N rounds.
        var l = minRoundsForLoop
        while (l <= rounds.size / 2) {
            val tail = rounds.subList(rounds.size - 2 * l, rounds.size)
            var looped = true
            for (i in 0 until l) {
                if (!similarTo(tail[i].speech, tail[l + i].speech) ||
                    tail[i].dtmfPressed != tail[l + i].dtmfPressed
                ) {
                    looped = false
                    break
                }
            }
            if (looped) {
                return IvrLoopVerdict(true, l, "Detected repeating cycle of length $l.")
            }
            l++
        }
        return IvrLoopVerdict(false, 0, "No loop detected.")
    }

    private fun similarTo(a: String, b: String): Boolean {
        if (a.equals(b, ignoreCase = true)) return true
        // Cheap Jaccard over word sets (case-insensitive).
        val setA = a.split(' ').filter { it.isNotEmpty() }.map { it.lowercase() }.toHashSet()
        val setB = b.split(' ').filter { it.isNotEmpty() }.map { it.lowercase() }.toHashSet()
        if (setA.isEmpty() || setB.isEmpty()) return false
        val inter = setA.count { setB.contains(it) }
        val union = (setA + setB).size
        return inter.toDouble() / union >= similarityThreshold
    }
}

// =====================================================================
// LLM-as-judge (LlmJudge.cs)
// =====================================================================

/**
 * One scoring dimension. Mirrors C# `JudgeDimension`.
 *
 * @param name Display name.
 * @param description Plain-English rubric the judge sees.
 */
data class JudgeDimension(val name: String, val description: String)

/**
 * Result of one judging call. Mirrors C# `JudgeVerdict`.
 *
 * @param scores 0..10 per dimension.
 * @param overall pass / borderline / fail.
 * @param reasoning One paragraph.
 */
data class JudgeVerdict(
    val scores: Map<String, Int>,
    val overall: String,
    val reasoning: String,
)

/** Delegate that asks the actual LLM to grade. Mirrors C# `delegate JudgeCompletion`. */
fun interface JudgeCompletion {
    suspend operator fun invoke(prompt: String): String
}

/** LLM-as-judge driver. Mirrors C# `LlmJudge`. */
class LlmJudge(private val completion: JudgeCompletion) {

    /** Build the rubric prompt, ask the judge, parse JSON, return the verdict. */
    suspend fun judge(
        userUtterance: String,
        assistantResponse: String,
        dimensions: List<JudgeDimension>,
    ): JudgeVerdict {
        val prompt = buildPrompt(userUtterance, assistantResponse, dimensions)
        val raw = completion(prompt)
        return parseVerdict(raw, dimensions)
    }

    private companion object {
        fun buildPrompt(user: String, assistant: String, dims: List<JudgeDimension>): String {
            val rubric = StringBuilder()
            rubric.appendLine("You are an evaluation judge. Score the assistant's reply across the rubric below.")
            rubric.appendLine("Reply ONLY in this JSON shape:")
            rubric.appendLine("""{ "scores": { "<dim_name>": <0-10>, ... }, "overall": "pass|borderline|fail", "reasoning": "<one paragraph>" }""")
            rubric.appendLine()
            rubric.appendLine("Rubric:")
            for (d in dims) {
                rubric.appendLine("- ${d.name}: ${d.description}")
            }
            rubric.appendLine()
            rubric.appendLine("User utterance:")
            rubric.appendLine(user)
            rubric.appendLine()
            rubric.appendLine("Assistant reply:")
            rubric.appendLine(assistant)
            return rubric.toString()
        }

        fun parseVerdict(raw: String, dims: List<JudgeDimension>): JudgeVerdict {
            val scores = LinkedHashMap<String, Int>()
            try {
                val trimmed = extractJson(raw)
                val root = TelephonyJson.parse(trimmed).jsonObject
                val s = root["scores"]
                if (s is JsonObject) {
                    for (dim in dims) {
                        val v = s[dim.name]?.jsonPrimitive
                        scores[dim.name] = when {
                            v == null -> 0
                            v.intOrNull != null -> v.intOrNull!!
                            v.contentOrNull?.toIntOrNull() != null -> v.contentOrNull!!.toInt()
                            else -> 0
                        }
                    }
                } else {
                    for (d in dims) scores[d.name] = 0
                }
                val overall = root["overall"]?.jsonPrimitive?.contentOrNull ?: "borderline"
                val reason = root["reasoning"]?.jsonPrimitive?.contentOrNull ?: ""
                return JudgeVerdict(scores, overall, reason)
            } catch (ex: Exception) {
                scores.clear()
                for (d in dims) scores[d.name] = 0
                return JudgeVerdict(scores, "borderline", "Judge response could not be parsed.")
            }
        }

        /** Tolerate models that wrap JSON in prose or fenced code blocks. */
        fun extractJson(raw: String): String {
            val start = raw.indexOf('{')
            val end = raw.lastIndexOf('}')
            if (start < 0 || end < 0 || end <= start) return raw
            return raw.substring(start, end + 1)
        }
    }
}

// =====================================================================
// Eval session (EvalSession.cs)
// =====================================================================

/**
 * One scripted turn from a fake caller. Mirrors C# `EvalTurn`.
 *
 * @param userTranscript What the caller said (already-transcribed).
 * @param expectedKeywords Optional keywords the AI's response should include.
 */
data class EvalTurn(val userTranscript: String, val expectedKeywords: List<String>? = null)

/** Outcome of one eval turn. Mirrors C# `EvalTurnResult`. */
data class EvalTurnResult(
    val assistantResponse: String,
    val missingKeywords: List<String>,
    val latency: Duration,
)

/** Overall eval result. Mirrors C# `EvalRunResult`. */
data class EvalRunResult(
    val turns: List<EvalTurnResult>,
    val allKeywordsHit: Boolean,
    val totalLatency: Duration,
)

/** Function that runs one turn through the AI under test. Mirrors C# `delegate EvalTurnHandler`. */
fun interface EvalTurnHandler {
    suspend operator fun invoke(userTranscript: String): String
}

/** Drives an EvalSession against a real LLM-based handler. Mirrors C# `EvalSession`. */
class EvalSession(
    private val handler: EvalTurnHandler,
    private val clock: TelephonyClock = SystemClock,
) {
    /** Run the script and assemble results. */
    suspend fun run(script: List<EvalTurn>): EvalRunResult {
        val results = ArrayList<EvalTurnResult>(script.size)
        var total = Duration.ZERO
        var allHit = true
        for (turn in script) {
            val started = clock()
            val response = handler(turn.userTranscript)
            val elapsed = Duration.between(started, clock())
            total = total.plus(elapsed)

            val missing = ArrayList<String>()
            if (turn.expectedKeywords != null) {
                for (kw in turn.expectedKeywords) {
                    if (response.indexOf(kw, ignoreCase = true) < 0) {
                        missing.add(kw)
                    }
                }
            }
            if (missing.isNotEmpty()) allHit = false
            results.add(EvalTurnResult(response, missing, elapsed))
        }
        return EvalRunResult(results, allHit, total)
    }
}

// =====================================================================
// Latency tracking (LatencyTracker.cs)
// =====================================================================

/** Stage names the voice loop tracks latency on. Mirrors C# `LatencyStage`. */
object LatencyStage {
    const val ASR_FIRST_WORD = "asr.first_word"
    const val ASR_FINAL = "asr.final"
    const val LLM_FIRST_TOKEN = "llm.first_token"
    const val LLM_FULL_RESPONSE = "llm.full_response"
    const val TTS_FIRST_AUDIO = "tts.first_audio"
    const val TTS_FULL_AUDIO = "tts.full_audio"
    const val END_TO_END = "voice_loop.end_to_end"
}

/** Snapshot of latency for one stage. Mirrors C# `LatencySnapshot`. */
data class LatencySnapshot(
    val stage: String,
    val samples: Int,
    val min: Duration,
    val p50: Duration,
    val p95: Duration,
    val p99: Duration,
    val max: Duration,
)

/** Records latency observations and produces percentiles. Mirrors C# `LatencyTracker`. */
class LatencyTracker(private val windowSize: Int = 256) {

    init {
        require(windowSize > 0) { "windowSize" }
    }

    // A bounded FIFO of milliseconds per stage; guarded by its own monitor (matches the
    // C# per-queue lock inside a ConcurrentDictionary).
    private val observations = ConcurrentHashMap<String, ArrayDeque<Long>>()

    /** Record one observation. */
    fun record(stage: String, latency: Duration) {
        require(stage.isNotBlank()) { "stage required" }
        if (latency < Duration.ZERO) return

        val queue = observations.computeIfAbsent(stage) { ArrayDeque() }
        synchronized(queue) {
            queue.addLast(latency.toMillis())
            while (queue.size > windowSize) queue.removeFirst()
        }
    }

    /** Snapshot percentiles for one stage. */
    fun snapshot(stage: String): LatencySnapshot? {
        val queue = observations[stage] ?: return null
        val sortedArr: LongArray
        synchronized(queue) {
            if (queue.isEmpty()) return null
            sortedArr = queue.toLongArray()
        }
        sortedArr.sort()

        fun percentile(p: Double): Duration {
            if (sortedArr.isEmpty()) return Duration.ZERO
            var idx = ceil(p * sortedArr.size).toInt() - 1
            if (idx < 0) idx = 0
            if (idx >= sortedArr.size) idx = sortedArr.size - 1
            return Duration.ofMillis(sortedArr[idx])
        }

        return LatencySnapshot(
            stage = stage,
            samples = sortedArr.size,
            min = Duration.ofMillis(sortedArr[0]),
            p50 = percentile(0.50),
            p95 = percentile(0.95),
            p99 = percentile(0.99),
            max = Duration.ofMillis(sortedArr[sortedArr.size - 1]),
        )
    }

    /** Snapshot every tracked stage. */
    fun snapshotAll(): List<LatencySnapshot> {
        val list = ArrayList<LatencySnapshot>()
        for (stage in observations.keys.toList()) {
            snapshot(stage)?.let { list.add(it) }
        }
        return list
    }

    fun reset(stage: String) {
        val queue = observations[stage] ?: return
        synchronized(queue) { queue.clear() }
    }

    fun resetAll() = observations.clear()
}

// =====================================================================
// Prompt-variable resolver (PromptVariableResolver.cs) — dependency of
// FirstMessagePreamble; not previously ported.
// =====================================================================

/** Resolves the value for one prompt variable. Mirrors C# `delegate PromptVariableProvider`. */
fun interface PromptVariableProvider {
    suspend operator fun invoke(variableName: String): String?
}

/**
 * Render a template with `{{var}}` placeholders against a set of providers. Mirrors C#
 * `PromptVariableResolver`. Static values win over providers; unknown variables render
 * as [defaultMissing]. Variable names are matched case-insensitively.
 */
class PromptVariableResolver(private val defaultMissing: String = "") {

    private val providers = HashMap<String, PromptVariableProvider>()
    private val statics = HashMap<String, String>()

    /** Register a static value. */
    fun set(name: String, value: String): PromptVariableResolver {
        require(name.isNotBlank()) { "name required" }
        statics[name.lowercase()] = value
        return this
    }

    /** Register a dynamic value provider (e.g. CRM lookup). */
    fun setProvider(name: String, provider: PromptVariableProvider): PromptVariableResolver {
        require(name.isNotBlank()) { "name required" }
        providers[name.lowercase()] = provider
        return this
    }

    /** Render [template] by substituting every `{{var}}`. */
    suspend fun render(template: String): String {
        if (template.isEmpty()) return ""

        val matches = VARIABLE_PATTERN.findAll(template).toList()
        if (matches.isEmpty()) return template

        val replacements = HashMap<String, String>()
        for (m in matches) {
            val name = m.groupValues[1]
            val key = name.lowercase()
            if (replacements.containsKey(key)) continue

            val stat = statics[key]
            if (stat != null) {
                replacements[key] = stat
                continue
            }
            val provider = providers[key]
            if (provider != null) {
                val resolved = provider(name)
                replacements[key] = resolved ?: defaultMissing
                continue
            }
            replacements[key] = defaultMissing
        }

        return VARIABLE_PATTERN.replace(template) { m ->
            // Regex.replace treats $ / \ in the replacement specially; escape them so the
            // substituted value is emitted literally (C# uses a plain MatchEvaluator).
            Regex.escapeReplacement(replacements[m.groupValues[1].lowercase()] ?: defaultMissing)
        }
    }

    private companion object {
        val VARIABLE_PATTERN = Regex("""\{\{\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\}\}""")
    }
}

// =====================================================================
// Warm-transfer briefing synthesiser reuse — the TTS delegate the preamble/
// filler layer speaks through is the same `BriefingSynthesiser` defined in
// Telephony.kt (text -> PCM-24k bytes).
// =====================================================================

// =====================================================================
// First-message preamble (FirstMessagePreamble.cs)
// =====================================================================

/**
 * Configuration for the first-message preamble. Mirrors C# `FirstMessagePreambleOptions`.
 *
 * @param template Template with `{{var}}` placeholders.
 * @param maxLatency If the LLM responds before this elapses, skip the preamble. Default 250 ms.
 */
data class FirstMessagePreambleOptions(
    val template: String,
    val maxLatency: Duration? = null,
) {
    val maxLatencyOrDefault: Duration get() = maxLatency ?: Duration.ofMillis(250)
}

/** Speaks a greeting at call-start. Mirrors C# `IFirstMessagePreamble`. */
interface IFirstMessagePreamble {
    /**
     * Speak the preamble. [modelReady] is awaited concurrently — if it completes before
     * [FirstMessagePreambleOptions.maxLatency] the preamble is skipped (the model has its
     * own greeting).
     */
    suspend fun speak(
        session: ICallSession,
        tts: BriefingSynthesiser,
        modelReady: Deferred<*>,
    )
}

/**
 * Default driver that resolves [FirstMessagePreambleOptions.template] via a
 * [PromptVariableResolver]. Mirrors C# `DefaultFirstMessagePreamble`.
 *
 * C#'s `Task modelReady` (an already-running task the caller supplies) is represented as
 * a [Deferred] the caller has already launched; the "model won within the latency window"
 * race is reproduced with `select { onAwait / onTimeout }`.
 */
class DefaultFirstMessagePreamble(
    private val options: FirstMessagePreambleOptions,
    resolver: PromptVariableResolver? = null,
) : IFirstMessagePreamble {

    private val resolver: PromptVariableResolver = resolver ?: PromptVariableResolver()

    @OptIn(ExperimentalCoroutinesApi::class)
    override suspend fun speak(
        session: ICallSession,
        tts: BriefingSynthesiser,
        modelReady: Deferred<*>,
    ) {
        // Race the model. If it wins within the latency window, skip the preamble.
        val modelWon = select<Boolean> {
            modelReady.onAwait { true }
            onTimeout(options.maxLatencyOrDefault.toMillis()) { false }
        }
        if (modelWon && modelReady.isCompleted && modelReady.getCompletionExceptionOrNull() == null) {
            return
        }

        val rendered = resolver.render(options.template)
        if (rendered.isBlank()) return

        val audio = tts.invoke(rendered)
        if (audio.isEmpty()) return

        session.sendAudioAsync(AudioFrame(audio, CallMediaFormat.Pcm24000, Duration.ZERO))
    }
}

// =====================================================================
// Reassurance filler (ReassuranceFiller.cs)
// =====================================================================

/** Phrases the filler picks from. Rotated to avoid repetition. Mirrors C# `ReassuranceVocabulary`. */
data class ReassuranceVocabulary(
    val shortFillers: List<String>,
    val longFillers: List<String>,
) {
    companion object {
        /** Sensible English defaults. Mirrors C# `ReassuranceVocabulary.Default`. */
        val Default: ReassuranceVocabulary = ReassuranceVocabulary(
            shortFillers = listOf(
                "One moment.",
                "Let me check.",
                "Give me a sec.",
                "Just a moment.",
            ),
            longFillers = listOf(
                "Still looking that up for you.",
                "This is taking a bit longer than usual — bear with me.",
                "Almost there — still pulling that information.",
                "Thanks for your patience, I'm checking that now.",
            ),
        )
    }
}

/**
 * Configuration for the filler driver. Mirrors C# `ReassuranceFillerOptions`.
 *
 * @param shortFillerAfter Silence after which to play a short filler. Default 600 ms.
 * @param longFillerEvery Cadence for long fillers after the first short one. Default 3 s.
 * @param vocabulary Phrase pool.
 */
data class ReassuranceFillerOptions(
    val shortFillerAfter: Duration? = null,
    val longFillerEvery: Duration? = null,
    val vocabulary: ReassuranceVocabulary? = null,
) {
    val shortFillerAfterOrDefault: Duration get() = shortFillerAfter ?: Duration.ofMillis(600)
    val longFillerEveryOrDefault: Duration get() = longFillerEvery ?: Duration.ofSeconds(3)
    val vocabularyOrDefault: ReassuranceVocabulary get() = vocabulary ?: ReassuranceVocabulary.Default
}

/** Driver that plays fillers while a long task runs. Mirrors C# `IReassuranceFiller`. */
interface IReassuranceFiller {
    /**
     * Run [work]. If it doesn't complete before the short-filler threshold, speak a short
     * phrase via [tts]; while still pending speak long phrases on the configured cadence.
     * Returns the work's result.
     */
    suspend fun <T> runWithFiller(
        session: ICallSession,
        tts: BriefingSynthesiser,
        work: suspend () -> T,
    ): T
}

/**
 * Default in-memory filler driver. Mirrors C# `DefaultReassuranceFiller`.
 *
 * C# links a `CancellationTokenSource` to run the filler loop alongside the work and
 * cancels it when the work finishes/throws; the Kotlin port launches the filler loop as a
 * child job inside a `coroutineScope` and cancels that job in a `finally`, so the loop is
 * always torn down whether the work completes or fails.
 */
class DefaultReassuranceFiller(options: ReassuranceFillerOptions? = null) : IReassuranceFiller {

    private val options: ReassuranceFillerOptions = options ?: ReassuranceFillerOptions()
    private val shortRotation = AtomicInteger(0)
    private val longRotation = AtomicInteger(0)

    override suspend fun <T> runWithFiller(
        session: ICallSession,
        tts: BriefingSynthesiser,
        work: suspend () -> T,
    ): T = coroutineScope {
        val fillerJob: Job = launch { speakFillers(session, tts) }
        try {
            work()
        } finally {
            fillerJob.cancel()
            try {
                fillerJob.join()
            } catch (_: CancellationException) {
                // expected when work finishes
            }
        }
    }

    private suspend fun speakFillers(session: ICallSession, tts: BriefingSynthesiser) {
        val vocab = options.vocabularyOrDefault
        try {
            delay(options.shortFillerAfterOrDefault.toMillis())
            speak(session, tts, nextShort(vocab))

            while (currentCoroutineIsActive()) {
                delay(options.longFillerEveryOrDefault.toMillis())
                speak(session, tts, nextLong(vocab))
            }
        } catch (_: CancellationException) {
            // expected when work finishes
        }
    }

    private fun nextShort(v: ReassuranceVocabulary): String {
        if (v.shortFillers.isEmpty()) return "One moment."
        val idx = shortRotation.getAndIncrement()
        return v.shortFillers[abs(idx) % v.shortFillers.size]
    }

    private fun nextLong(v: ReassuranceVocabulary): String {
        if (v.longFillers.isEmpty()) return "Almost there."
        val idx = longRotation.getAndIncrement()
        return v.longFillers[abs(idx) % v.longFillers.size]
    }

    private companion object {
        suspend fun speak(session: ICallSession, tts: BriefingSynthesiser, text: String) {
            val audio = tts.invoke(text)
            if (audio.isNotEmpty()) {
                session.sendAudioAsync(AudioFrame(audio, CallMediaFormat.Pcm24000, Duration.ZERO))
            }
        }
    }
}

// =====================================================================
// Speculative generation (SpeculativeGenerator.cs)
// =====================================================================

/**
 * One in-flight speculative branch. Mirrors C# `SpeculativeBranch`.
 *
 * @param partialTranscript The partial transcript this branch was started from.
 * @param responseTask The in-flight (or completed) draft response.
 * @param startedAt When the branch was started.
 */
data class SpeculativeBranch(
    val partialTranscript: String,
    val responseTask: Deferred<String>,
    val startedAt: Instant,
)

/** Drives a response generation given a partial transcript. Mirrors C# `delegate ResponseGenerator`. */
fun interface ResponseGenerator {
    suspend operator fun invoke(transcript: String): String
}

/** Manages speculative-generation branches. Mirrors C# `ISpeculativeGenerator`. */
interface ISpeculativeGenerator {
    /** The branch currently considered most likely to commit. */
    val activeBranch: SpeculativeBranch?

    /**
     * Start (or restart) the speculative branch using [partialTranscript]. [scope] hosts the
     * in-flight draft coroutine (the C# reference starts the generation task eagerly; the
     * Kotlin port needs a scope to launch it into).
     */
    fun speculate(partialTranscript: String, generator: ResponseGenerator, scope: CoroutineScope)

    /** Commit to a final transcript and return the matching response. */
    suspend fun commit(finalTranscript: String, generator: ResponseGenerator): String

    /** Abort any active speculation. */
    fun abort()
}

/**
 * Default driver. Cancels older branches when the partial diverges. Mirrors C#
 * `DefaultSpeculativeGenerator`.
 *
 * The C# reference starts a `Task<string>` eagerly inside `Speculate`. Kotlin has no
 * ambient task scheduler, so [speculate] takes the [CoroutineScope] to launch the draft
 * into (a lazy `async`, started immediately). Each branch owns the `Deferred`; superseding
 * a branch cancels the previous `Deferred` (the analogue of cancelling the previous CTS).
 */
class DefaultSpeculativeGenerator(
    private val clock: TelephonyClock = SystemClock,
    private val minPartialLength: Int = 8,
) : ISpeculativeGenerator {

    private val gate = Any()
    private var active: SpeculativeBranch? = null

    override val activeBranch: SpeculativeBranch?
        get() = synchronized(gate) { active }

    override fun speculate(partialTranscript: String, generator: ResponseGenerator, scope: CoroutineScope) {
        if (partialTranscript.isBlank()) return
        if (partialTranscript.length < minPartialLength) return

        var toCancel: Deferred<String>? = null
        synchronized(gate) {
            val current = active
            // If the new partial is just an extension of the active one, keep it.
            if (current != null && partialTranscript.startsWith(current.partialTranscript, ignoreCase = true)) {
                return
            }
            toCancel = current?.responseTask
            val task = scope.async { generator(partialTranscript) }
            active = SpeculativeBranch(partialTranscript, task, clock())
        }
        toCancel?.cancel()
    }

    override suspend fun commit(finalTranscript: String, generator: ResponseGenerator): String {
        if (finalTranscript.isBlank()) return ""

        val current: SpeculativeBranch?
        synchronized(gate) { current = active }

        if (current != null && finalTranscript.startsWith(current.partialTranscript, ignoreCase = true)) {
            try {
                val draft = current.responseTask.await()
                if (finalTranscript.equals(current.partialTranscript, ignoreCase = true)) {
                    return draft
                }
                // Final extended the partial — finalize via a fresh generation (our contract).
            } catch (_: CancellationException) {
                // superseded — fall through
            } catch (_: Exception) {
                // swallow draft errors
            }
        }

        // No usable speculative draft — generate fresh.
        var toCancel: Deferred<String>? = null
        synchronized(gate) {
            toCancel = active?.responseTask
            active = null
        }
        toCancel?.cancel()

        return generator(finalTranscript)
    }

    override fun abort() {
        var toCancel: Deferred<String>? = null
        synchronized(gate) {
            toCancel = active?.responseTask
            active = null
        }
        toCancel?.cancel()
    }
}

// =====================================================================
// Sentence chunker (SentenceChunker.cs)
// =====================================================================

/** Streaming sentence chunker. Mirrors C# `SentenceChunker`. */
class SentenceChunker(private val minSentenceLength: Int = 4) {

    private val buffer = StringBuilder()
    private val gate = Any()

    /** Push a token; receive any complete sentences ready to emit. */
    fun pushToken(token: String): List<String> {
        if (token.isEmpty()) return emptyList()
        var ready: MutableList<String>? = null
        synchronized(gate) {
            buffer.append(token)
            while (true) {
                val (chunk, kept) = extractNext(buffer.toString())
                if (chunk == null) break
                buffer.setLength(0)
                buffer.append(kept)
                (ready ?: ArrayList<String>().also { ready = it }).add(chunk)
            }
        }
        return ready ?: emptyList()
    }

    /** Flush whatever's buffered as a final fragment, regardless of punctuation. */
    fun flush(): String {
        synchronized(gate) {
            val s = buffer.toString()
            buffer.setLength(0)
            return s
        }
    }

    private fun extractNext(buffer: String): Pair<String?, String> {
        var searchFrom = 0
        while (searchFrom < buffer.length) {
            val idx = indexOfAny(buffer, TERMINAL_PUNCTUATION, searchFrom)
            if (idx < 0) return null to buffer

            // Consume any trailing whitespace + closing quotes after the punctuation.
            var end = idx + 1
            while (end < buffer.length &&
                (buffer[end].isWhitespace() || buffer[end] == '"' || buffer[end] == '\'' || buffer[end] == ')')
            ) {
                end++
            }

            val candidate = buffer.substring(0, end).trim()
            if (candidate.length >= minSentenceLength) {
                return candidate to buffer.substring(end)
            }
            // Too short — keep extending past this punctuation.
            searchFrom = end
        }
        return null to buffer
    }

    private companion object {
        val TERMINAL_PUNCTUATION = charArrayOf('.', '!', '?', '。', '！', '？')

        fun indexOfAny(s: String, any: CharArray, from: Int): Int {
            var i = from
            while (i < s.length) {
                val c = s[i]
                for (a in any) if (c == a) return i
                i++
            }
            return -1
        }
    }
}

// =====================================================================
// Guardrails (Guardrails.cs)
// =====================================================================

/** What a guardrail does on match. Mirrors C# `GuardrailAction`. */
enum class GuardrailAction {
    /** Block the turn entirely — the AI says [GuardrailRule.fallbackMessage] instead. */
    Replace,

    /** Redact only the matched text (e.g. credit-card numbers -> "[redacted]"). */
    Redact,

    /** Pass through but flag in the audit log. */
    Warn,
}

/**
 * One rule the guardrail checks. Mirrors C# `GuardrailRule`.
 *
 * @param name Display name for logging.
 * @param pattern Regex pattern (case-insensitive).
 * @param action What to do when the pattern matches.
 * @param replaceWith Replacement text for [GuardrailAction.Redact].
 * @param fallbackMessage Speak this instead when [GuardrailAction.Replace].
 */
data class GuardrailRule(
    val name: String,
    val pattern: String,
    val action: GuardrailAction,
    val replaceWith: String? = null,
    val fallbackMessage: String? = null,
)

/** Outcome of running guardrails on one text draft. Mirrors C# `GuardrailResult`. */
data class GuardrailResult(
    val finalText: String,
    val wasModified: Boolean,
    val wasBlocked: Boolean,
    val triggeredRules: List<String>,
)

/** Pre-TTS guardrail engine. Mirrors C# `Guardrails`. */
class Guardrails(
    rules: Iterable<GuardrailRule>? = null,
    private val defaultFallback: String = "I'm sorry, I can't help with that right now.",
) {
    private val rules: List<Pair<GuardrailRule, Regex>> =
        (rules ?: emptyList()).map { it to Regex(it.pattern, RegexOption.IGNORE_CASE) }

    /** Run the guardrails against a draft response. */
    fun apply(draft: String): GuardrailResult {
        if (draft.isEmpty()) {
            return GuardrailResult(draft, false, false, emptyList())
        }

        val triggered = ArrayList<String>()
        var text = draft
        var blocked = false

        for ((rule, regex) in rules) {
            if (!regex.containsMatchIn(text)) continue
            triggered.add(rule.name)

            when (rule.action) {
                GuardrailAction.Replace -> {
                    blocked = true
                    text = rule.fallbackMessage ?: defaultFallback
                    return GuardrailResult(text, true, true, triggered)
                }

                GuardrailAction.Redact -> {
                    val replacement = rule.replaceWith ?: "[redacted]"
                    text = regex.replace(text, Regex.escapeReplacement(replacement))
                }

                GuardrailAction.Warn -> {
                    // No mutation; just flag.
                }
            }
        }

        val modified = text != draft
        return GuardrailResult(text, modified, blocked, triggered)
    }
}

/** Common guardrails out of the box. Mirrors C# `CommonGuardrails`. */
object CommonGuardrails {
    /** Redact 13-19 digit credit-card numbers. */
    val creditCardRedactor: GuardrailRule =
        GuardrailRule(
            name = "credit-card",
            pattern = """\b(?:\d[ -]*?){13,19}\b""",
            action = GuardrailAction.Redact,
            replaceWith = "[redacted card number]",
        )

    /** Block US SSN-shaped sequences (xxx-xx-xxxx). */
    val ssnBlocker: GuardrailRule =
        GuardrailRule(
            name = "ssn",
            pattern = """\b\d{3}-\d{2}-\d{4}\b""",
            action = GuardrailAction.Replace,
            fallbackMessage = "For security I can't share that information.",
        )

    /** Block competitor mentions — supply names per deployment. */
    fun competitorMention(vararg competitors: String): GuardrailRule =
        GuardrailRule(
            name = "competitor",
            pattern = """\b(?:""" + competitors.joinToString("|") { Regex.escape(it) } + """)\b""",
            action = GuardrailAction.Replace,
            fallbackMessage = "I can't comment on other providers, but I can help with your account.",
        )
}

// =====================================================================
// Tool circuit breaker (ToolCircuitBreaker.cs)
// =====================================================================

/**
 * Per-tool timeout + breaker thresholds. Mirrors C# `ToolCallPolicy`.
 *
 * @param timeout Wall-clock ceiling for the call. Default 5 s.
 * @param failureThreshold Consecutive failures that trip the breaker. Default 3.
 * @param openDuration How long the breaker stays open before half-opening. Default 30 s.
 */
data class ToolCallPolicy(
    val timeout: Duration? = null,
    val failureThreshold: Int = 3,
    val openDuration: Duration? = null,
) {
    val timeoutOrDefault: Duration get() = timeout ?: Duration.ofSeconds(5)
    val openDurationOrDefault: Duration get() = openDuration ?: Duration.ofSeconds(30)
}

/** Breaker state. Mirrors C# `ToolBreakerState`. */
enum class ToolBreakerState { Closed, Open, HalfOpen }

/**
 * Decorates an [IToolCallRegistry] with per-tool timeouts and a circuit breaker. Mirrors
 * C# `CircuitBreakerToolRegistry`. Pass a [clock] for deterministic tests.
 *
 * The C# timeout uses a linked `CancellationTokenSource(timeout)`; the Kotlin port wraps
 * the inner invoke in `withTimeoutOrNull`, and a null result (timeout) records a failure
 * and returns a timeout [ToolResult], preserving the reference's timeout-vs-caller-cancel
 * distinction (only the tool timeout trips the breaker; upstream cancellation propagates).
 */
class CircuitBreakerToolRegistry(
    private val inner: IToolCallRegistry,
    defaultPolicy: ToolCallPolicy? = null,
    private val clock: TelephonyClock = SystemClock,
) : IToolCallRegistry {

    private val defaultPolicy: ToolCallPolicy = defaultPolicy ?: ToolCallPolicy()
    private val policies = ConcurrentHashMap<String, ToolCallPolicy>()
    private val breakers = ConcurrentHashMap<String, BreakerEntry>()

    /** Override the policy for a specific tool. */
    fun setPolicy(toolName: String, policy: ToolCallPolicy) {
        policies[toolName.lowercase()] = policy
    }

    /** Inspect the current breaker state for a tool. */
    fun getState(toolName: String): ToolBreakerState {
        val entry = breakers[toolName.lowercase()] ?: return ToolBreakerState.Closed
        return entry.currentState(clock(), getPolicy(toolName).openDurationOrDefault)
    }

    override val definitions: List<ToolDefinition> get() = inner.definitions

    override fun registerLocal(definition: ToolDefinition, handler: LocalToolHandler) =
        inner.registerLocal(definition, handler)

    override fun registerWebhook(definition: ToolDefinition, webhook: URI) =
        inner.registerWebhook(definition, webhook)

    override suspend fun invokeAsync(invocation: ToolInvocation): ToolResult {
        val policy = getPolicy(invocation.toolName)
        val entry = breakers.computeIfAbsent(invocation.toolName.lowercase()) { BreakerEntry() }

        val state = entry.currentState(clock(), policy.openDurationOrDefault)
        if (state == ToolBreakerState.Open) {
            return ToolResult(
                invocation.callId, false, "{}",
                "Tool '${invocation.toolName}' is circuit-broken; retry after the breaker resets.",
            )
        }

        return try {
            val result = withTimeoutOrNull(policy.timeoutOrDefault.toMillis()) {
                inner.invokeAsync(invocation)
            }
            if (result == null) {
                entry.recordFailure(policy.failureThreshold, clock())
                ToolResult(
                    invocation.callId, false, "{}",
                    "Tool '${invocation.toolName}' timed out after ${policy.timeoutOrDefault.toMillis()} ms.",
                )
            } else {
                if (result.succeeded) {
                    entry.recordSuccess()
                } else {
                    entry.recordFailure(policy.failureThreshold, clock())
                }
                result
            }
        } catch (ex: CancellationException) {
            // Upstream cancellation — do not trip the breaker; propagate.
            throw ex
        } catch (ex: Exception) {
            entry.recordFailure(policy.failureThreshold, clock())
            ToolResult(invocation.callId, false, "{}", ex.message ?: ex.toString())
        }
    }

    private fun getPolicy(toolName: String): ToolCallPolicy =
        policies[toolName.lowercase()] ?: defaultPolicy

    private class BreakerEntry {
        private val gate = Any()
        private var consecutiveFailures = 0
        private var openedAt: Instant = Instant.EPOCH
        private var isOpen = false

        fun currentState(now: Instant, openDuration: Duration): ToolBreakerState {
            synchronized(gate) {
                if (!isOpen) return ToolBreakerState.Closed
                return if (Duration.between(openedAt, now) >= openDuration) {
                    ToolBreakerState.HalfOpen
                } else {
                    ToolBreakerState.Open
                }
            }
        }

        fun recordSuccess() {
            synchronized(gate) {
                consecutiveFailures = 0
                isOpen = false
            }
        }

        fun recordFailure(threshold: Int, now: Instant) {
            synchronized(gate) {
                consecutiveFailures++
                if (consecutiveFailures >= threshold) {
                    isOpen = true
                    openedAt = now
                }
            }
        }
    }
}

// =====================================================================
// Voice-loop-as-tool (VoiceLoopAsTool.cs)
// =====================================================================

/**
 * Request to make one outbound voice call as a tool invocation. Mirrors C# `VoiceLoopToolRequest`.
 *
 * @param toNumber E.164 destination number.
 * @param goal Plain-English goal ("Book a haircut for Sipho on Saturday").
 * @param contextJson Extra structured context the agent needs.
 * @param systemPrompt Persona / script for the voice agent.
 * @param maxDuration Hard ceiling on call length.
 */
data class VoiceLoopToolRequest(
    val toNumber: String,
    val goal: String,
    val contextJson: String? = null,
    val systemPrompt: String? = null,
    val maxDuration: Duration? = null,
)

/**
 * Result of the call returned to the calling agent. Mirrors C# `VoiceLoopToolResult`.
 *
 * @param goalAchieved True if the AI reports it completed the goal.
 * @param summary Natural-language summary the AI wrote.
 * @param callId Carrier call id.
 * @param duration Actual call duration.
 * @param transcript Full conversation transcript.
 * @param structuredOutputJson Optional JSON the AI extracted (e.g. appointment time).
 */
data class VoiceLoopToolResult(
    val goalAchieved: Boolean,
    val summary: String,
    val callId: String,
    val duration: Duration,
    val transcript: String,
    val structuredOutputJson: String?,
)

/** Voice-loop-as-a-tool surface. Mirrors C# `IVoiceLoopTool`. */
interface IVoiceLoopTool {
    /** Make the call and report back. */
    suspend fun invoke(request: VoiceLoopToolRequest): VoiceLoopToolResult
}

/** Drives the actual call. Mirrors the C# `Func<VoiceLoopToolRequest, CancellationToken, Task<...>>` runner. */
fun interface VoiceLoopRunner {
    suspend operator fun invoke(request: VoiceLoopToolRequest): VoiceLoopToolResult
}

/**
 * Driver that delegates the actual call to a host-supplied runner. Mirrors C#
 * `VoiceLoopAsTool`. The C# `MaxDuration` timeout is enforced with `withTimeoutOrNull`;
 * a timeout yields the same "timed out" result shape as the reference.
 */
class VoiceLoopAsTool(
    private val runner: VoiceLoopRunner,
    defaultMaxDuration: Duration? = null,
) : IVoiceLoopTool {

    private val defaultMaxDuration: Duration = defaultMaxDuration ?: Duration.ofMinutes(5)

    override suspend fun invoke(request: VoiceLoopToolRequest): VoiceLoopToolResult {
        require(request.toNumber.isNotBlank()) { "ToNumber is required." }
        require(request.goal.isNotBlank()) { "Goal is required." }

        val maxDuration = request.maxDuration ?: defaultMaxDuration
        return withTimeoutOrNull(maxDuration.toMillis()) {
            runner(request)
        } ?: VoiceLoopToolResult(
            goalAchieved = false,
            summary = "Call timed out after ${formatMinutes(maxDuration)} minutes.",
            callId = "",
            duration = maxDuration,
            transcript = "",
            structuredOutputJson = null,
        )
    }

    companion object {
        /** Tool descriptor for use with [IToolCallRegistry]. Mirrors C# `VoiceLoopAsTool.Descriptor`. */
        val Descriptor: ToolDefinition = ToolDefinition(
            name = "make_voice_call",
            description = "Place an outbound phone call and follow the supplied goal/script. Returns whether the goal was achieved.",
            argumentsJsonSchema = """
            {
              "type": "object",
              "properties": {
                "to_number":     { "type": "string", "description": "E.164 destination." },
                "goal":          { "type": "string" },
                "context_json":  { "type": "string", "nullable": true },
                "system_prompt": { "type": "string", "nullable": true },
                "max_duration_seconds": { "type": "integer", "nullable": true }
              },
              "required": ["to_number", "goal"]
            }
            """.trimIndent(),
        )

        private fun formatMinutes(d: Duration): String {
            val minutes = d.toMillis() / 60_000.0
            return String.format("%.1f", minutes)
        }
    }
}

// =====================================================================
// Streaming tool progress (StreamingToolProgress.cs)
// =====================================================================

/**
 * One progress update from a streaming tool. Mirrors C# `ToolProgressUpdate`.
 *
 * @param callId The tool-call id this update belongs to.
 * @param percentComplete 0..100 progress fraction.
 * @param statusText Optional status to speak to the caller.
 * @param emittedAt Server time the update was created.
 */
data class ToolProgressUpdate(
    val callId: String,
    val percentComplete: Float,
    val statusText: String?,
    val emittedAt: Instant,
)

/** The sink a tool pushes progress updates into. Mirrors C# `IToolProgressSink`. */
interface IToolProgressSink {
    /** Emit one update. Implementations decide whether to forward to the caller. */
    suspend fun emit(update: ToolProgressUpdate)
}

/** Streaming tool handler — accepts a progress sink it can push updates into. Mirrors C# `delegate StreamingToolHandler`. */
fun interface StreamingToolHandler {
    suspend operator fun invoke(argumentsJson: String, progressSink: IToolProgressSink): String
}

/**
 * Default sink that throttles updates (>= [minInterval] apart) and speaks each via TTS to
 * the active call session. Mirrors C# `SpokenToolProgressSink`.
 */
class SpokenToolProgressSink(
    private val session: ICallSession,
    private val tts: BriefingSynthesiser,
    minInterval: Duration? = null,
    private val clock: TelephonyClock = SystemClock,
) : IToolProgressSink {

    private val minInterval: Duration = minInterval ?: Duration.ofSeconds(2)
    private val gate = Any()
    private var lastSpoken: Instant = Instant.EPOCH

    override suspend fun emit(update: ToolProgressUpdate) {
        if (update.statusText.isNullOrBlank()) return

        val now = clock()
        val shouldSpeak: Boolean
        synchronized(gate) {
            shouldSpeak = Duration.between(lastSpoken, now) >= minInterval
            if (shouldSpeak) lastSpoken = now
        }
        if (!shouldSpeak) return

        val audio = tts.invoke(update.statusText)
        if (audio.isNotEmpty()) {
            session.sendAudioAsync(AudioFrame(audio, CallMediaFormat.Pcm24000, Duration.ZERO))
        }
    }
}

/** Sink that records updates for observability without speaking them. Mirrors C# `RecordingToolProgressSink`. */
class RecordingToolProgressSink : IToolProgressSink {
    private val gate = Any()
    private val updatesList = ArrayList<ToolProgressUpdate>()

    val updates: List<ToolProgressUpdate>
        get() = synchronized(gate) { ArrayList(updatesList) }

    override suspend fun emit(update: ToolProgressUpdate) {
        synchronized(gate) { updatesList.add(update) }
    }
}

/** Run a streaming tool handler against a progress sink. Mirrors C# `StreamingToolRunner`. */
object StreamingToolRunner {
    suspend fun run(
        invocation: ToolInvocation,
        handler: StreamingToolHandler,
        sink: IToolProgressSink,
    ): ToolResult {
        return try {
            val resultJson = handler(invocation.argumentsJson, sink)
            ToolResult(invocation.callId, true, resultJson.ifBlank { "{}" })
        } catch (ex: Exception) {
            ToolResult(invocation.callId, false, "{}", ex.message ?: ex.toString())
        }
    }
}

// =====================================================================
// MCP tool importer (McpToolImporter.cs)
// =====================================================================

/** Description of one MCP tool returned from `tools/list`. Mirrors C# `McpToolDescriptor`. */
data class McpToolDescriptor(val name: String, val description: String, val inputJsonSchema: String)

/**
 * MCP server descriptor. Mirrors C# `McpServerConfig`.
 *
 * @param serverEndpoint HTTP endpoint of the MCP server.
 * @param authorizationHeader Optional `Authorization` header to attach (e.g. `Bearer ...`).
 * @param toolNamePrefix Optional prefix applied to imported tool names to avoid collisions.
 */
data class McpServerConfig(
    val serverEndpoint: URI,
    val authorizationHeader: String? = null,
    val toolNamePrefix: String? = null,
)

/** Imports tools from MCP servers into a tool registry. Mirrors C# `IMcpToolImporter`. */
interface IMcpToolImporter {
    suspend fun import(registry: IToolCallRegistry, server: McpServerConfig): List<ToolDefinition>
}

/**
 * HTTP-backed importer (tools list + invoke via JSON-RPC over HTTP). Mirrors C#
 * `HttpMcpToolImporter`. The C# reference uses `HttpClient`; the Kotlin port routes through
 * the injected [TelephonyHttpTransport] so it is deterministic + offline in tests. Imported
 * tools register as webhook-style entries forwarding back to the MCP server (a `remote_tool`
 * query param carries the un-prefixed tool name, matching the reference).
 */
class HttpMcpToolImporter(
    private val http: TelephonyHttpTransport,
    private val logger: TelephonyLogger = TelephonyLogger.NoOp,
) : IMcpToolImporter {

    override suspend fun import(registry: IToolCallRegistry, server: McpServerConfig): List<ToolDefinition> {
        val listRequest = buildJsonObject {
            put("jsonrpc", JsonPrimitive("2.0"))
            put("id", JsonPrimitive(1))
            put("method", JsonPrimitive("tools/list"))
            put("params", JsonObject(emptyMap()))
        }.toString()

        val headers = HashMap<String, String>()
        headers["Content-Type"] = "application/json"
        if (!server.authorizationHeader.isNullOrBlank()) {
            headers["Authorization"] = server.authorizationHeader
        }

        val resp = http.sendAsync(
            TelephonyHttpRequest(
                method = "POST",
                uri = server.serverEndpoint,
                headers = headers,
                body = listRequest,
            ),
        )
        if (!resp.isSuccess) {
            logger.log(
                TelephonyLogLevel.Warning,
                "MCP server ${server.serverEndpoint} returned ${resp.statusCode}",
            )
            return emptyList()
        }

        val root = runCatching { TelephonyJson.parse(resp.body).jsonObject }.getOrNull()
            ?: return emptyList()
        val result = root["result"] as? JsonObject ?: return emptyList()
        val tools = result["tools"] as? JsonArray ?: return emptyList()

        val imported = ArrayList<ToolDefinition>()
        for (entry in tools) {
            val obj = entry as? JsonObject ?: continue
            val name = obj["name"]?.jsonPrimitive?.contentOrNull
            val description = obj["description"]?.jsonPrimitive?.contentOrNull ?: ""
            val schema = when (val s = obj["inputSchema"]) {
                null -> "{}"
                else -> s.toString()
            }
            if (name.isNullOrBlank()) continue

            val localName = if (server.toolNamePrefix.isNullOrBlank()) name else "${server.toolNamePrefix}$name"
            val def = ToolDefinition(localName, description, schema)

            // Register a webhook-style entry forwarding back to the MCP server's tools/call.
            val invokeUrl = appendQuery(server.serverEndpoint, "remote_tool", name)
            registry.registerWebhook(def, invokeUrl)
            imported.add(def)
        }

        return imported
    }

    private companion object {
        fun appendQuery(baseUri: URI, key: String, value: String): URI {
            val existing = baseUri.rawQuery ?: ""
            val separator = if (existing.isEmpty()) "" else "&"
            val newQuery = existing + separator + key + "=" + URLEncoder.encode(value, "UTF-8")
            return URI(
                baseUri.scheme,
                baseUri.authority,
                baseUri.path,
                newQuery,
                baseUri.fragment,
            )
        }
    }
}

// =====================================================================
// Consult escalation (ConsultEscalation.cs)
// =====================================================================

/**
 * Question the AI asks a human expert. Mirrors C# `ConsultRequest`.
 *
 * @param callId Source call id for the audit trail.
 * @param question Plain-English question text.
 * @param contextJson Structured context (caller intent, last few utterances, customer record).
 * @param urgency "normal" / "high".
 */
data class ConsultRequest(
    val callId: String,
    val question: String,
    val contextJson: String,
    val urgency: String = "normal",
)

/** Human reply. Mirrors C# `ConsultAnswer` ([confidence] true = expert confirmed). */
data class ConsultAnswer(val answer: String, val confidence: Boolean, val notes: String? = null)

/** Channel for asking a human expert. Mirrors C# `IConsultChannel`. */
interface IConsultChannel {
    val name: String
    suspend fun ask(request: ConsultRequest, timeout: Duration): ConsultAnswer?
}

/** Default escalation driver: try channels in order until one returns within the timeout. Mirrors C# `ConsultEscalator`. */
class ConsultEscalator(
    private val channels: Array<IConsultChannel>,
    private val logger: TelephonyLogger = TelephonyLogger.NoOp,
) {
    /** Walk channels in order; first one to return a non-null answer wins. */
    suspend fun escalate(request: ConsultRequest, timeoutPerChannel: Duration): ConsultAnswer? {
        for (channel in channels) {
            try {
                val answer = channel.ask(request, timeoutPerChannel)
                if (answer != null) {
                    logger.log(
                        TelephonyLogLevel.Information,
                        "Consult ${request.callId} answered by ${channel.name}",
                    )
                    return answer
                }
            } catch (ex: CancellationException) {
                throw ex
            } catch (ex: Exception) {
                logger.log(TelephonyLogLevel.Warning, "Consult channel ${channel.name} threw", ex)
            }
        }
        return null
    }
}

/**
 * HTTP webhook channel — POSTs the request, expects a JSON reply. Mirrors C#
 * `HttpWebhookConsultChannel`. Routes through the injected [TelephonyHttpTransport]; a
 * per-call timeout is applied with `withTimeoutOrNull` (a timeout yields null, exactly like
 * the reference's linked-CTS timeout returning null).
 */
class HttpWebhookConsultChannel(
    private val http: TelephonyHttpTransport,
    private val endpoint: URI,
    override val name: String = "webhook",
) : IConsultChannel {

    override suspend fun ask(request: ConsultRequest, timeout: Duration): ConsultAnswer? {
        val bodyJson = buildJsonObject {
            put("call_id", JsonPrimitive(request.callId))
            put("question", JsonPrimitive(request.question))
            put("context_json", JsonPrimitive(request.contextJson))
            put("urgency", JsonPrimitive(request.urgency))
        }.toString()

        val resp = withTimeoutOrNull(timeout.toMillis()) {
            http.sendAsync(
                TelephonyHttpRequest(
                    method = "POST",
                    uri = endpoint,
                    headers = mapOf("Content-Type" to "application/json"),
                    body = bodyJson,
                ),
            )
        } ?: return null

        if (!resp.isSuccess) return null

        val root = runCatching { TelephonyJson.parse(resp.body).jsonObject }.getOrNull() ?: return null
        val answer = root["answer"]?.jsonPrimitive?.contentOrNull
        if (answer.isNullOrBlank()) return null
        val confidence = root["confidence"]?.jsonPrimitive?.booleanOrNull ?: false
        val notes = root["notes"]?.jsonPrimitive?.contentOrNull
        return ConsultAnswer(answer, confidence, notes)
    }
}

// =====================================================================
// Phone-number provisioner (PhoneNumberProvisioner.cs)
// =====================================================================

/**
 * Persistence contract for assigned numbers. Mirrors C# `IProvisionedNumberStore`. Default
 * in-memory implementation is fine for dev; production hosts plug in a database-backed store.
 */
interface IProvisionedNumberStore {
    suspend fun save(number: ProvisionedNumber)
    suspend fun list(): List<ProvisionedNumber>
    suspend fun find(phoneNumber: String): ProvisionedNumber?
    suspend fun remove(phoneNumber: String)
}

/** Default in-memory store. Thread-safe. Mirrors C# `InMemoryProvisionedNumberStore`. */
class InMemoryProvisionedNumberStore : IProvisionedNumberStore {
    private val byNumber = LinkedHashMap<String, ProvisionedNumber>()
    private val gate = Any()

    override suspend fun save(number: ProvisionedNumber) {
        synchronized(gate) { byNumber[number.phoneNumber] = number }
    }

    override suspend fun list(): List<ProvisionedNumber> =
        synchronized(gate) { ArrayList(byNumber.values) }

    override suspend fun find(phoneNumber: String): ProvisionedNumber? =
        synchronized(gate) { byNumber[phoneNumber] }

    override suspend fun remove(phoneNumber: String) {
        synchronized(gate) { byNumber.remove(phoneNumber) }
    }
}

/**
 * Service that buys + configures + persists phone numbers from any carrier behind
 * [ITelephonyCarrier]. Mirrors C# `PhoneNumberProvisioner`.
 */
class PhoneNumberProvisioner(
    private val carrier: ITelephonyCarrier,
    store: IProvisionedNumberStore? = null,
    private val logger: TelephonyLogger = TelephonyLogger.NoOp,
) {
    private val store: IProvisionedNumberStore = store ?: InMemoryProvisionedNumberStore()

    /**
     * Buy a number, wire its inbound webhook, persist it, return the metadata.
     *
     * @param countryCode ISO country code (e.g. "US", "ZA", "NG").
     * @param inboundWebhook HTTPS URL the carrier will hit when the number rings.
     * @param areaCode Optional area code / prefix preference.
     */
    suspend fun provision(
        countryCode: String,
        inboundWebhook: URI,
        areaCode: String? = null,
    ): ProvisionedNumber {
        require(countryCode.isNotBlank()) { "countryCode is required" }
        require(inboundWebhook.isAbsolute) { "inboundWebhook must be an absolute URI" }

        logger.log(
            TelephonyLogLevel.Information,
            "Provisioning number on ${carrier.carrierId} for $countryCode/${areaCode ?: "(any)"}",
        )

        val provisioned = carrier.provisionNumberAsync(countryCode, areaCode)

        try {
            carrier.configureInboundWebhookAsync(provisioned.phoneNumber, inboundWebhook)
        } catch (ex: CancellationException) {
            throw ex
        } catch (ex: Exception) {
            logger.log(
                TelephonyLogLevel.Error,
                "Webhook configuration failed for ${provisioned.phoneNumber} on ${carrier.carrierId}",
                ex,
            )
            throw ex
        }

        store.save(provisioned)
        return provisioned
    }

    /** The provisioned numbers we know about, locally + via the carrier. */
    suspend fun list(): List<ProvisionedNumber> {
        val stored = store.list()
        // Merge with carrier authoritative list — store may be stale.
        val carrierNumbers = carrier.listNumbersAsync()
        val merged = LinkedHashMap<String, ProvisionedNumber>()
        for (n in stored) merged[n.phoneNumber] = n
        for (n in carrierNumbers) merged[n.phoneNumber] = n
        return ArrayList(merged.values)
    }
}

// =====================================================================
// Speech lifecycle events (SpeechLifecycleEvents.cs)
// =====================================================================

/** Base of the union of lifecycle events. Mirrors C# `abstract record SpeechLifecycleEvent`. */
sealed class SpeechLifecycleEvent(val callId: String, val at: Instant)

class CallerSpeechStartedEvent(callId: String, at: Instant) : SpeechLifecycleEvent(callId, at)
class CallerSpeechEndedEvent(callId: String, at: Instant) : SpeechLifecycleEvent(callId, at)
class TranscriptInterimEvent(callId: String, at: Instant, val text: String) : SpeechLifecycleEvent(callId, at)
class TranscriptFinalEventV2(callId: String, at: Instant, val text: String) : SpeechLifecycleEvent(callId, at)
class AgentThinkingEvent(callId: String, at: Instant) : SpeechLifecycleEvent(callId, at)
class AgentSpeakingStartedEvent(callId: String, at: Instant) : SpeechLifecycleEvent(callId, at)
class AgentSpeakingFinishedEvent(callId: String, at: Instant, val spokenDuration: Duration) : SpeechLifecycleEvent(callId, at)
class SpeechErrorEvent(callId: String, at: Instant, val stage: String, val message: String) : SpeechLifecycleEvent(callId, at)

/** Subscription handle. Mirrors C# `ISpeechSubscription : IDisposable`. */
interface ISpeechSubscription : AutoCloseable

/** Speech lifecycle pub/sub. Mirrors C# `ISpeechLifecycleBus`. */
interface ISpeechLifecycleBus {
    /**
     * Subscribe to a specific event type. Use [SpeechLifecycleEvent] as [eventType] for all.
     * Mirrors C# `Subscribe<TEvent>(Action<TEvent>)`; Kotlin has no reified type on an
     * interface method, so the event [Class] is passed explicitly.
     */
    fun <T : SpeechLifecycleEvent> subscribe(eventType: Class<T>, handler: (T) -> Unit): ISpeechSubscription

    /** Publish one event. All matching subscribers are invoked synchronously. */
    fun publish(ev: SpeechLifecycleEvent)
}

/** Reified convenience over [ISpeechLifecycleBus.subscribe]. */
inline fun <reified T : SpeechLifecycleEvent> ISpeechLifecycleBus.subscribe(noinline handler: (T) -> Unit): ISpeechSubscription =
    subscribe(T::class.java, handler)

/**
 * Default in-memory bus. Mirrors C# `InMemorySpeechLifecycleBus`. Publishing walks the
 * event's class hierarchy up to (but not including) [SpeechLifecycleEvent]'s own supertype,
 * so a [SpeechLifecycleEvent] subscriber receives every concrete type — matching the C#
 * BaseType walk that stops at `object`.
 */
class InMemorySpeechLifecycleBus : ISpeechLifecycleBus {
    private val subscribers = ConcurrentHashMap<Class<*>, ConcurrentHashMap<Long, (SpeechLifecycleEvent) -> Unit>>()
    private val nextHandle = AtomicLong(0)

    @Suppress("UNCHECKED_CAST")
    override fun <T : SpeechLifecycleEvent> subscribe(eventType: Class<T>, handler: (T) -> Unit): ISpeechSubscription {
        val bucket = subscribers.computeIfAbsent(eventType) { ConcurrentHashMap() }
        val id = nextHandle.incrementAndGet()
        bucket[id] = handler as (SpeechLifecycleEvent) -> Unit
        return SubHandle { bucket.remove(id) }
    }

    override fun publish(ev: SpeechLifecycleEvent) {
        // Walk class hierarchy so a SpeechLifecycleEvent subscriber receives every concrete type.
        var t: Class<*>? = ev.javaClass
        while (t != null && t != Any::class.java) {
            val bucket = subscribers[t]
            if (bucket != null) {
                for (del in bucket.values) {
                    del(ev)
                }
            }
            if (t == SpeechLifecycleEvent::class.java) break
            t = t.superclass
        }
    }

    private class SubHandle(private val dispose: () -> Unit) : ISpeechSubscription {
        override fun close() = dispose()
    }
}

// =====================================================================
// False-interruption tracker (FalseInterruptionTracker.cs)
// =====================================================================

/** Counters for false-interruption monitoring. Mirrors C# `InterruptionStats`. */
data class InterruptionStats(
    val totalPauseEvents: Long,
    val confirmedBargeIns: Long,
    val falseAlarms: Long,
    val falseAlarmRate: Float,
)

/** Tracks barge-in transitions and surfaces a false-alarm rate. Mirrors C# `IFalseInterruptionTracker`. */
interface IFalseInterruptionTracker {
    /** Record one transition emitted by [BargeInController]. */
    fun record(transition: BargeInTransition)

    /** Current cumulative stats. */
    fun getStats(): InterruptionStats

    /** Reset all counters. */
    fun reset()
}

/** Default in-memory tracker. Thread-safe. Mirrors C# `InMemoryFalseInterruptionTracker`. */
class InMemoryFalseInterruptionTracker : IFalseInterruptionTracker {
    private val totalPauses = AtomicLong(0)
    private val confirmed = AtomicLong(0)
    private val falseAlarms = AtomicLong(0)

    override fun record(transition: BargeInTransition) {
        when (transition.to) {
            BargeInState.Paused -> totalPauses.incrementAndGet()
            BargeInState.Cancelled -> confirmed.incrementAndGet()
            BargeInState.Resumed -> falseAlarms.incrementAndGet()
            else -> { /* Speaking — not counted */ }
        }
    }

    override fun getStats(): InterruptionStats {
        val tp = totalPauses.get()
        val c = confirmed.get()
        val fa = falseAlarms.get()
        val rate = if (tp > 0) fa.toFloat() / tp else 0f
        return InterruptionStats(tp, c, fa, rate)
    }

    override fun reset() {
        totalPauses.set(0)
        confirmed.set(0)
        falseAlarms.set(0)
    }
}

// =====================================================================
// Hold-music mixer (HoldMusicMixer.cs)
// =====================================================================

/**
 * Background-audio mixer for hold music. Loops a track and mixes the AI's speech on top at
 * adjustable gain, ducking the background when speech frames arrive. Mirrors C#
 * `HoldMusicMixer`. All 16-bit reads/writes are little-endian, matching BinaryPrimitives.
 *
 * @param backgroundLoop PCM-16 mono buffer that the mixer loops over.
 * @param backgroundGain Gain when no speech (0..1). Default 0.6.
 * @param duckedGain Gain while speech is being mixed (0..1). Default 0.15.
 */
class HoldMusicMixer(
    private val backgroundLoop: ByteArray,
    private val backgroundGain: Float = 0.6f,
    private val duckedGain: Float = 0.15f,
) {
    private var loopCursor = 0

    init {
        require(backgroundLoop.size >= 2) { "Background loop must contain at least one PCM-16 sample." }
        require(backgroundGain in 0f..1f) { "backgroundGain" }
        require(duckedGain in 0f..1f) { "duckedGain" }
    }

    /** Reset the loop cursor to the start. */
    fun reset() {
        loopCursor = 0
    }

    /**
     * Mix [speechFrame] (its first [speechLength] bytes) on top of looped background into
     * [destination]. Pass an empty speech buffer (or [speechLength] < 2) to render plain
     * background across the whole [destination]. Returns the number of bytes written.
     *
     * C# takes `ReadOnlySpan`/`Span`; the Kotlin port takes arrays plus explicit lengths so
     * callers can reuse buffers without allocating slices, preserving the reference's in-place
     * semantics.
     */
    fun mixFrame(
        speechFrame: ByteArray,
        destination: ByteArray,
        speechLength: Int = speechFrame.size,
        destinationLength: Int = destination.size,
    ): Int {
        if (destinationLength < 2) return 0
        val hasSpeech = speechLength >= 2
        val frameLength = if (hasSpeech) speechLength else destinationLength
        require(destinationLength >= frameLength) { "destination must be at least as long as the speech frame." }

        val gain = if (hasSpeech) duckedGain else backgroundGain

        var i = 0
        while (i < frameLength) {
            val speechSample: Int = if (hasSpeech) readInt16LE(speechFrame, i) else 0

            // Pull background sample from the loop, wrapping as needed.
            val bgSample = readInt16LE(backgroundLoop, loopCursor)
            loopCursor = (loopCursor + 2) % backgroundLoop.size
            if (loopCursor % 2 != 0) loopCursor-- // align to 16-bit boundary

            var mixed = speechSample + (bgSample * gain).toInt()
            mixed = mixed.coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt())
            writeInt16LE(destination, i, mixed.toShort())
            i += 2
        }
        return frameLength
    }
}

// =====================================================================
// Stereo call recorder (StereoCallRecorder.cs)
// =====================================================================

/**
 * Interleaves inbound (caller, left channel) and outbound (agent, right channel) PCM-16
 * mono audio into a single stereo PCM-16 WAV. Mirrors C# `StereoCallRecorder`.
 *
 * The C# reference writes to a seekable `Stream` and backfills the 44-byte header in
 * [finalizeRecording]. The Kotlin port targets a [SeekableWavSink] (an injected boundary
 * over the destination) so a plain [OutputStream] or a file channel can back it; a sink
 * that cannot seek keeps the placeholder header for live appends, exactly like the reference.
 */
class StereoCallRecorder(
    private val output: SeekableWavSink,
    private val sampleRateHz: Int,
    private val leaveOpen: Boolean = false,
) : AutoCloseable {

    private val gate = Any()
    private var samplesWritten: Long = 0 // total interleaved sample pairs
    private var headerWritten = false

    init {
        require(sampleRateHz > 0) { "sampleRateHz" }
    }

    /** Write inbound (caller) PCM-16 mono audio. Caller side is left channel. */
    fun writeCallerFrame(pcmFrame: ByteArray, length: Int = pcmFrame.size) =
        writeSide(pcmFrame, length, isCaller = true)

    /** Write outbound (agent) PCM-16 mono audio. Agent side is right channel. */
    fun writeAgentFrame(pcmFrame: ByteArray, length: Int = pcmFrame.size) =
        writeSide(pcmFrame, length, isCaller = false)

    /** Finalise the WAV header. After this, no more writes are allowed. */
    fun finalizeRecording() {
        synchronized(gate) { finaliseLocked() }
    }

    private fun writeSide(pcmFrame: ByteArray, length: Int, isCaller: Boolean) {
        if (length < 2) return
        synchronized(gate) {
            ensureHeader()
            val samples = length / 2
            val stereo = ByteArray(4)
            for (i in 0 until samples) {
                val mono = readInt16LE(pcmFrame, i * 2)
                if (isCaller) {
                    writeInt16LE(stereo, 0, mono.toShort())
                    writeInt16LE(stereo, 2, 0)
                } else {
                    writeInt16LE(stereo, 0, 0)
                    writeInt16LE(stereo, 2, mono.toShort())
                }
                output.write(stereo, 0, 4)
                samplesWritten++
            }
        }
    }

    private fun ensureHeader() {
        if (headerWritten) return
        // Reserve 44 bytes for the WAV header — values backfilled in finalize.
        output.write(ByteArray(44), 0, 44)
        headerWritten = true
    }

    private fun finaliseLocked() {
        if (!headerWritten) return
        val dataSize = samplesWritten * 4 // 2 channels × 2 bytes
        val chunkSize = 36 + dataSize
        if (!output.canSeek) {
            // Streams that can't seek can't backfill — accept the placeholder for live appends.
            return
        }
        val saved = output.position
        output.position = 0
        val header = ByteArray(44)
        header[0] = 'R'.code.toByte(); header[1] = 'I'.code.toByte(); header[2] = 'F'.code.toByte(); header[3] = 'F'.code.toByte()
        writeInt32LE(header, 4, chunkSize.toInt())
        header[8] = 'W'.code.toByte(); header[9] = 'A'.code.toByte(); header[10] = 'V'.code.toByte(); header[11] = 'E'.code.toByte()
        header[12] = 'f'.code.toByte(); header[13] = 'm'.code.toByte(); header[14] = 't'.code.toByte(); header[15] = ' '.code.toByte()
        writeInt32LE(header, 16, 16) // Subchunk1Size
        writeInt16LE(header, 20, 1) // PCM
        writeInt16LE(header, 22, 2) // channels
        writeInt32LE(header, 24, sampleRateHz)
        writeInt32LE(header, 28, sampleRateHz * 4) // byte rate
        writeInt16LE(header, 32, 4) // block align
        writeInt16LE(header, 34, 16) // bits per sample
        header[36] = 'd'.code.toByte(); header[37] = 'a'.code.toByte(); header[38] = 't'.code.toByte(); header[39] = 'a'.code.toByte()
        writeInt32LE(header, 40, dataSize.toInt())
        output.write(header, 0, 44)
        output.position = saved
        output.flush()
    }

    override fun close() {
        finalizeRecording()
        if (!leaveOpen) output.close()
    }
}

/**
 * Seekable byte sink the [StereoCallRecorder] writes into. Mirrors the seekable-`Stream`
 * surface the C# reference relies on (position get/set, seekability, flush). A non-seekable
 * sink reports [canSeek] = false and the recorder leaves the placeholder header in place.
 */
interface SeekableWavSink : AutoCloseable {
    val canSeek: Boolean
    var position: Long
    fun write(bytes: ByteArray, offset: Int, length: Int)
    fun flush()
}

/**
 * In-memory [SeekableWavSink] — a growable, seekable byte buffer. Deterministic dev/test
 * backing for [StereoCallRecorder]; [toByteArray] returns the finished WAV. Not part of the
 * C# surface (whose stream is host-supplied), the in-memory analogue for offline use.
 */
class ByteBufferWavSink : SeekableWavSink {
    private var buffer = ByteArray(64)
    private var length = 0
    private var pos = 0L

    override val canSeek: Boolean get() = true

    override var position: Long
        get() = pos
        set(value) {
            require(value >= 0) { "position" }
            pos = value
        }

    override fun write(bytes: ByteArray, offset: Int, length: Int) {
        val end = pos + length
        ensureCapacity(end.toInt())
        System.arraycopy(bytes, offset, buffer, pos.toInt(), length)
        pos = end
        if (pos > this.length) this.length = pos.toInt()
    }

    override fun flush() { /* in-memory — nothing to flush */ }

    override fun close() { /* in-memory — nothing to close */ }

    /** The bytes written so far (the finished WAV once the recorder finalises). */
    fun toByteArray(): ByteArray = buffer.copyOf(length)

    private fun ensureCapacity(needed: Int) {
        if (needed <= buffer.size) return
        var newSize = buffer.size
        while (newSize < needed) newSize *= 2
        buffer = buffer.copyOf(newSize)
    }
}

/** Streaming (non-seekable) [SeekableWavSink] over a plain [OutputStream] (live appends). */
class OutputStreamWavSink(private val out: OutputStream, private val closeUnderlying: Boolean = true) : SeekableWavSink {
    override val canSeek: Boolean get() = false

    override var position: Long
        get() = 0
        set(_) { /* not seekable */ }

    override fun write(bytes: ByteArray, offset: Int, length: Int) = out.write(bytes, offset, length)
    override fun flush() = out.flush()
    override fun close() {
        out.flush()
        if (closeUnderlying) out.close()
    }
}

// =====================================================================
// Call-cost calculator (CallCostCalculator.cs)
// =====================================================================

/**
 * Per-unit prices (USD or any consistent currency). Mirrors C# `CallPricing`.
 *
 * @param carrierPerMinute Cost per minute of carrier telephony.
 * @param sttPerSecond Cost per second of STT.
 * @param ttsPerThousandChars Cost per 1000 characters of TTS.
 * @param llmInputPerKToken Cost per 1000 input tokens.
 * @param llmOutputPerKToken Cost per 1000 output tokens.
 */
data class CallPricing(
    val carrierPerMinute: BigDecimal,
    val sttPerSecond: BigDecimal,
    val ttsPerThousandChars: BigDecimal,
    val llmInputPerKToken: BigDecimal,
    val llmOutputPerKToken: BigDecimal,
)

/** Breakdown of where the money went. Mirrors C# `CallCostBreakdown`. */
data class CallCostBreakdown(
    val carrier: BigDecimal,
    val stt: BigDecimal,
    val tts: BigDecimal,
    val llmInput: BigDecimal,
    val llmOutput: BigDecimal,
    val total: BigDecimal,
)

/** Tracks cost for one call. Mirrors C# `CallCostCalculator`. */
class CallCostCalculator(private val pricing: CallPricing) {
    private val carrierMs = AtomicLong(0)
    private val sttMs = AtomicLong(0)
    private val ttsChars = AtomicLong(0)
    private val llmInputTokens = AtomicLong(0)
    private val llmOutputTokens = AtomicLong(0)

    /** Add carrier telephony usage. */
    fun addCarrierTime(duration: Duration) {
        if (duration < Duration.ZERO) return
        carrierMs.addAndGet(duration.toMillis())
    }

    /** Add STT usage. */
    fun addSttTime(duration: Duration) {
        if (duration < Duration.ZERO) return
        sttMs.addAndGet(duration.toMillis())
    }

    /** Add TTS usage in characters. */
    fun addTtsCharacters(chars: Int) {
        if (chars <= 0) return
        ttsChars.addAndGet(chars.toLong())
    }

    /** Add LLM tokens. */
    fun addLlmTokens(inputTokens: Int, outputTokens: Int) {
        if (inputTokens > 0) llmInputTokens.addAndGet(inputTokens.toLong())
        if (outputTokens > 0) llmOutputTokens.addAndGet(outputTokens.toLong())
    }

    /** Snapshot the current total cost breakdown. */
    fun currentBreakdown(): CallCostBreakdown {
        val carrierMin = BigDecimal(carrierMs.get()).divide(BigDecimal(60_000), MC_SCALE, RoundingMode.HALF_UP)
        val sttSec = BigDecimal(sttMs.get()).divide(BigDecimal(1000), MC_SCALE, RoundingMode.HALF_UP)
        val ttsK = BigDecimal(ttsChars.get()).divide(BigDecimal(1000), MC_SCALE, RoundingMode.HALF_UP)
        val llmInputK = BigDecimal(llmInputTokens.get()).divide(BigDecimal(1000), MC_SCALE, RoundingMode.HALF_UP)
        val llmOutputK = BigDecimal(llmOutputTokens.get()).divide(BigDecimal(1000), MC_SCALE, RoundingMode.HALF_UP)

        val carrier = carrierMin.multiply(pricing.carrierPerMinute)
        val stt = sttSec.multiply(pricing.sttPerSecond)
        val tts = ttsK.multiply(pricing.ttsPerThousandChars)
        val llmIn = llmInputK.multiply(pricing.llmInputPerKToken)
        val llmOut = llmOutputK.multiply(pricing.llmOutputPerKToken)
        val total = carrier.add(stt).add(tts).add(llmIn).add(llmOut)

        return CallCostBreakdown(carrier, stt, tts, llmIn, llmOut, total)
    }

    fun reset() {
        carrierMs.set(0)
        sttMs.set(0)
        ttsChars.set(0)
        llmInputTokens.set(0)
        llmOutputTokens.set(0)
    }

    private companion object {
        // Enough scale to hold the intermediate division precisely for realistic pricing.
        const val MC_SCALE = 12
    }
}

// =====================================================================
// Dashboard data (DashboardData.cs)
// =====================================================================

/** One row in the live-calls panel. Mirrors C# `LiveCallRow`. */
data class LiveCallRow(
    val callId: String,
    val carrier: String,
    val from: String,
    val to: String,
    val status: CallStatus,
    val startedAtUtc: Instant,
    val duration: Duration,
    val costSoFar: BigDecimal,
)

/** One row in the recent-calls panel. Mirrors C# `RecentCallRow`. */
data class RecentCallRow(
    val callId: String,
    val carrier: String,
    val from: String,
    val to: String,
    val finalStatus: CallStatus,
    val endedAtUtc: Instant,
    val duration: Duration,
    val totalCost: BigDecimal,
)

/** Agent health summary row. Mirrors C# `AgentHealthRow` ([health] "Healthy"/"Degraded"/"CoolingDown"). */
data class AgentHealthRow(
    val agentLabel: String,
    val health: String,
    val consecutiveFailures: Int,
)

/** Top-of-page summary card. Mirrors C# `DashboardSummary`. */
data class DashboardSummary(
    val liveCallCount: Int,
    val currentSpendUsd: BigDecimal,
    val callsLast24h: Int,
    val pauseFalseAlarmRate: Float,
)

/** Full dashboard snapshot. Mirrors C# `DashboardSnapshot`. */
data class DashboardSnapshot(
    val summary: DashboardSummary,
    val liveCalls: List<LiveCallRow>,
    val recentCalls: List<RecentCallRow>,
    val agentHealth: List<AgentHealthRow>,
    val latencyByStage: List<LatencySnapshot>,
)

/** Dashboard data source: hosts compose live + recent + health + latency feeds. Mirrors C# `IDashboardDataSource`. */
interface IDashboardDataSource {
    suspend fun snapshot(): DashboardSnapshot
}

/** Default composed data source — pulls from supplied feeds. Mirrors C# `DefaultDashboardDataSource`. */
class DefaultDashboardDataSource(
    private val liveCalls: () -> List<LiveCallRow>,
    private val recentCalls: () -> List<RecentCallRow>,
    private val agentHealth: () -> List<AgentHealthRow>,
    private val latency: () -> List<LatencySnapshot>,
    private val summary: () -> DashboardSummary,
) : IDashboardDataSource {

    override suspend fun snapshot(): DashboardSnapshot =
        DashboardSnapshot(
            summary = summary(),
            liveCalls = liveCalls(),
            recentCalls = recentCalls(),
            agentHealth = agentHealth(),
            latencyByStage = latency(),
        )
}

// =====================================================================
// Local-dev tunnel (LocalDevTunnel.cs) — the "native-tunnel seam".
// The tunnel process (ngrok / cloudflared) is a native/out-of-process dependency; the
// LOGIC ports here and the process is injected behind `TunnelResolver`.
// =====================================================================

/** Resolves a public URL forwarding to a local port. Mirrors the C# `Func<int, CancellationToken, ValueTask<Uri>>`. */
fun interface TunnelResolver {
    suspend operator fun invoke(localPort: Int): URI
}

/** Resolves a public, internet-reachable URL that maps to a local port. Mirrors C# `ILocalDevTunnel`. */
interface LocalDevTunnel {
    /** Identifier — "cloudflare", "ngrok", "static", "null". */
    val providerId: String

    /** Whether this resolver is configured/available. */
    val isAvailable: Boolean

    /** Resolve the public URL forwarding to [localPort]. */
    suspend fun getPublicUrl(localPort: Int): URI
}

/** DI-default that throws — host wires a real tunnel. Mirrors C# `NullLocalDevTunnel`. */
class NullLocalDevTunnel private constructor() : LocalDevTunnel {
    companion object {
        val Instance = NullLocalDevTunnel()
    }

    override val providerId: String get() = "null"
    override val isAvailable: Boolean get() = false
    override suspend fun getPublicUrl(localPort: Int): URI =
        throw IllegalStateException(
            "No local-dev tunnel is configured. Register a CloudflareTunnel / NgrokTunnel / StaticTunnel.",
        )
}

/** Static-URL tunnel — caller supplies the public URL up front (best for CI). Mirrors C# `StaticLocalDevTunnel`. */
class StaticLocalDevTunnel(private val publicUrl: URI) : LocalDevTunnel {
    init {
        require(publicUrl.isAbsolute) { "publicUrl must be absolute." }
    }

    override val providerId: String get() = "static"
    override val isAvailable: Boolean get() = true
    override suspend fun getPublicUrl(localPort: Int): URI = publicUrl
}

/** Cloudflare Tunnel resolver. Host points at the cloudflared output URL. Mirrors C# `CloudflareTunnel`. */
class CloudflareTunnel(private val resolver: TunnelResolver) : LocalDevTunnel {
    override val providerId: String get() = "cloudflare"
    override val isAvailable: Boolean get() = true
    override suspend fun getPublicUrl(localPort: Int): URI = resolver(localPort)
}

/** ngrok tunnel resolver. Mirrors C# `NgrokTunnel`. */
class NgrokTunnel(private val resolver: TunnelResolver) : LocalDevTunnel {
    override val providerId: String get() = "ngrok"
    override val isAvailable: Boolean get() = true
    override suspend fun getPublicUrl(localPort: Int): URI = resolver(localPort)
}

// =====================================================================
// Shared little-endian PCM/byte helpers (BinaryPrimitives equivalents) + misc.
// =====================================================================

/** Read a signed little-endian 16-bit sample at [offset]. Mirrors BinaryPrimitives.ReadInt16LittleEndian. */
private fun readInt16LE(b: ByteArray, offset: Int): Int {
    val lo = b[offset].toInt() and 0xFF
    val hi = b[offset + 1].toInt() // signed high byte -> sign-extends the sample
    return (hi shl 8) or lo
}

/** Write a signed little-endian 16-bit sample at [offset]. Mirrors BinaryPrimitives.WriteInt16LittleEndian. */
private fun writeInt16LE(b: ByteArray, offset: Int, value: Short) {
    val v = value.toInt()
    b[offset] = (v and 0xFF).toByte()
    b[offset + 1] = ((v shr 8) and 0xFF).toByte()
}

private fun writeInt16LE(b: ByteArray, offset: Int, value: Int) = writeInt16LE(b, offset, value.toShort())

/** Write a little-endian 32-bit int at [offset]. Mirrors BinaryPrimitives.WriteInt32LittleEndian. */
private fun writeInt32LE(b: ByteArray, offset: Int, value: Int) {
    b[offset] = (value and 0xFF).toByte()
    b[offset + 1] = ((value shr 8) and 0xFF).toByte()
    b[offset + 2] = ((value shr 16) and 0xFF).toByte()
    b[offset + 3] = ((value shr 24) and 0xFF).toByte()
}

/** Milliseconds as a double, preserving sub-ms precision (C# TimeSpan.TotalMilliseconds). */
private fun Duration.toMillisDouble(): Double = this.toNanos() / 1_000_000.0

/** True while the current coroutine has not been cancelled (helper for filler loop). */
private suspend fun currentCoroutineIsActive(): Boolean = coroutineScope { this.isActive }
