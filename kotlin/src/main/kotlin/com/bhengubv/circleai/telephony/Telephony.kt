// Telephony.kt
//
// Kotlin port of CircleAI.Telephony (the carrier-agnostic contract surface) —
// the C# reference is the EXACT spec. This is the module every consumer (txtMe,
// Panik, salon receptionist) talks to; the real Twilio / Telnyx / Plivo adapters
// ship as sibling packages (telephony.twilio / .telnyx / .plivo).
//
// Ported C# files: Primitives.cs, Contracts.cs, IMediaStream.cs, IDtmfSendable.cs,
// ToolCalling.cs, DtmfToneGenerator.cs, WarmTransferOrchestrator.cs,
// NullImplementations.cs, TestCallSession.cs. Plus a deterministic in-memory fake
// carrier + inbound dispatcher + HTTP transport boundary so the whole surface is
// usable end-to-end in tests with NO real network.
//
// Design fidelity notes (matching the sibling realtime/voice ports):
//   * C# `enum`                      -> Kotlin `enum class`.
//   * C# `sealed record`             -> Kotlin `data class`.
//   * C# `ReadOnlyMemory<byte>`      -> `ByteArray` (content equals/hashCode).
//   * C# `TimeSpan` / `DateTimeOffset` -> `java.time.Duration` / `java.time.Instant`.
//   * C# `decimal`                   -> `java.math.BigDecimal`.
//   * C# `IAsyncEnumerable<T>`       -> `kotlinx.coroutines.flow.Flow<T>`.
//   * C# `ValueTask<T>` / `ValueTask` -> `suspend fun`.
//   * C# `IAsyncDisposable`          -> `AutoCloseable` + suspend `disposeAsync()`.
//   * C# `event EventHandler<CallStatus>` -> registerable listener callbacks over a
//     `CopyOnWriteArrayList` (no lock held while a subscriber callback runs — the
//     status snapshot is taken under the gate, then listeners fire outside it).
//   * C# `Channel.CreateUnbounded<T>` -> `Channel(UNLIMITED)`, drained to a `Flow`;
//     `TryWrite` -> `trySend`, `TryComplete` -> `close()`.
//   * `delegate LocalToolHandler` / `BriefingSynthesiser` -> `fun interface`s.
//   * C# `HttpClient` (the network boundary the carriers use) -> an injected
//     `TelephonyHttpTransport` fun interface, mirroring how realtime.cloud injects
//     `IRealtimeTransport`. A deterministic in-memory transport backs the tests so
//     no real HTTP is issued.
//   * The DTMF dual-tone synthesis is reproduced sample-for-sample: same frequency
//     table, integer `sr*ms/1000` sample count, little-endian 16-bit PCM, the
//     `0.5*amp*(sin(lo)+sin(hi))` mix and `clamp(-1,1)*Short.MAX` scaling.

package com.bhengubv.circleai.telephony

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import java.math.BigDecimal
import java.net.URI
import java.time.Duration
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.math.PI
import kotlin.math.sin

// =====================================================================
// Primitives (Primitives.cs)
// =====================================================================

/** Call direction. Mirrors C# `CallDirection`. */
enum class CallDirection { Inbound, Outbound }

/** Call lifecycle states. Mirrors C# `CallStatus`. */
enum class CallStatus {
    /** Carrier accepted the dial but the other end has not picked up yet. */
    Ringing,

    /** Both sides connected; media flowing. */
    Active,

    /** Caller hung up. */
    EndedByCaller,

    /** Callee hung up. */
    EndedByCallee,

    /** AI agent (us) ended the call. */
    EndedByAgent,

    /** Carrier-detected voicemail / answering machine on outbound dial. */
    Voicemail,

    /** Call did not connect (busy, no answer, network). */
    Failed,

    /** Call transferred to a human or a different agent. */
    Transferred,
}

/** Audio wire formats supported across carriers. Mirrors C# `CallMediaFormat`. */
enum class CallMediaFormat {
    /** µ-law 8 kHz mono — Twilio default, Plivo default, fallback Telnyx. */
    Mulaw8000,

    /** A-law 8 kHz mono — some European carriers. */
    Alaw8000,

    /** Linear PCM 16-bit 16 kHz mono — Telnyx negotiated path. */
    Pcm16000,

    /** Linear PCM 16-bit 24 kHz mono — high-quality WebRTC, OpenAI Realtime. */
    Pcm24000,
}

/** Transfer mode the AI requests from the carrier. Mirrors C# `TransferMode`. */
enum class TransferMode {
    /** Drop the caller into the new line and hang up — fast, no context handover. */
    Cold,

    /** Park caller, dial human, brief human verbally, then bridge both — context preserved. */
    Warm,
}

/**
 * Information about one call. Captured once at call start, immutable. Mirrors C# `CallInfo`.
 *
 * @param callId Carrier-supplied unique id (Twilio CallSid, Telnyx call_control_id, etc.).
 * @param direction Direction — who initiated.
 * @param from Caller's phone number in E.164 format (e.g. +27821234567).
 * @param to Called party's phone number in E.164 format.
 * @param carrierId Carrier id (e.g. "twilio", "telnyx", "plivo").
 * @param mediaFormat Audio wire format the carrier is streaming.
 * @param startedAtUtc When the call started.
 */
data class CallInfo(
    val callId: String,
    val direction: CallDirection,
    val from: String,
    val to: String,
    val carrierId: String,
    val mediaFormat: CallMediaFormat,
    val startedAtUtc: Instant,
)

/**
 * A snapshot of a call's current state. Returned by lifecycle queries. Mirrors C# `CallSnapshot`.
 *
 * @param info Carrier-captured call metadata.
 * @param status Current lifecycle state.
 * @param duration How long since the call connected.
 * @param costSoFar Per-second cost so far (carrier minutes + any LLM/STT/TTS attached).
 * @param transferTarget If [CallStatus.Transferred], the E.164 number we transferred to.
 */
data class CallSnapshot(
    val info: CallInfo,
    val status: CallStatus,
    val duration: Duration,
    val costSoFar: BigDecimal,
    val transferTarget: String? = null,
)

/**
 * Audio chunk flowing from caller → AI or AI → caller. Mirrors C# `AudioFrame`
 * (a `ReadOnlyMemory<byte>` record). [ByteArray] gets value semantics over content
 * so frames compare structurally, as the C# record compares the memory segment.
 */
data class AudioFrame(
    val pcm: ByteArray,
    val format: CallMediaFormat,
    val offset: Duration,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is AudioFrame) return false
        return format == other.format && offset == other.offset && pcm.contentEquals(other.pcm)
    }

    override fun hashCode(): Int {
        var result = pcm.contentHashCode()
        result = 31 * result + format.hashCode()
        result = 31 * result + offset.hashCode()
        return result
    }
}

/**
 * DTMF tone from the caller. Mirrors C# `DtmfEvent`.
 *
 * @param digit The digit (0-9, *, #).
 * @param duration How long the caller held it.
 * @param offset When (relative to call start).
 */
data class DtmfEvent(
    val digit: Char,
    val duration: Duration,
    val offset: Duration,
)

/** Result of a number-provisioning request. Mirrors C# `ProvisionedNumber`. */
data class ProvisionedNumber(
    val phoneNumber: String,
    val carrierId: String,
    val provisionedAtUtc: Instant,
    val monthlyRecurringCost: BigDecimal,
)

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** Optional knobs for an outbound dial. Mirrors C# `OutboundDialOptions`. */
data class OutboundDialOptions(
    /** If true, detect voicemail and surface [CallStatus.Voicemail]. */
    val detectAnsweringMachine: Boolean = false,

    /** How long to ring before treating it as no-answer. Default 30 s. */
    val ringTimeoutSeconds: Int = 30,

    /** Optional caller-id override (must be a number you own). */
    val callerIdOverride: String? = null,

    /** Optional list of E.164 numbers to also dial if the primary doesn't answer (round-robin). */
    val followMeNumbers: List<String>? = null,
)

/**
 * Carrier integration — the place where CircleAI talks to a phone-network operator
 * (Twilio, Telnyx, Plivo, or a SIP gateway). Inbound: carrier delivers a call to us
 * → carrier emits [ICallSession] via the host's webhook plumbing. Outbound: caller
 * asks us to dial → we call [dialAsync]. Mirrors C# `ITelephonyCarrier`.
 */
interface ITelephonyCarrier {
    /** Stable carrier id — "twilio" / "telnyx" / "plivo" / "null". */
    val carrierId: String

    /** True when the carrier has the credentials + base addresses it needs. */
    val isConfigured: Boolean

    /**
     * Buy a new phone number from this carrier for the given country code (ISO
     * 3166-1 alpha-2, e.g. "ZA"). Caller chooses one of the offered area codes via
     * [areaCode]; pass null for "any".
     */
    suspend fun provisionNumberAsync(countryCode: String, areaCode: String? = null): ProvisionedNumber

    /**
     * Configure a number we already own to route inbound calls to our host-provided
     * WebSocket endpoint.
     */
    suspend fun configureInboundWebhookAsync(phoneNumber: String, inboundWebhook: URI)

    /**
     * Place an outbound call. [streamUrl] is where the carrier should stream the live
     * media (WebSocket URL on our host). Returns a session the caller can attach an
     * agent to.
     */
    suspend fun dialAsync(
        fromNumber: String,
        toNumber: String,
        streamUrl: URI,
        options: OutboundDialOptions? = null,
    ): ICallSession

    /** List the numbers we own on this carrier. */
    suspend fun listNumbersAsync(): List<ProvisionedNumber>
}

/**
 * Live call session. The agent talks to this — it doesn't know or care which carrier
 * is on the other side. Audio in / audio out / hang up / transfer / DTMF. Mirrors C#
 * `ICallSession` (which is `IAsyncDisposable`).
 */
interface ICallSession : AutoCloseable {
    /** Stable carrier-supplied info captured at call start. */
    val info: CallInfo

    /** Current lifecycle status (Active / EndedByCaller / Transferred / ...). */
    val status: CallStatus

    /** Audio frames arriving from the caller. */
    fun receiveAudioAsync(): Flow<AudioFrame>

    /** Send an audio frame to the caller. */
    suspend fun sendAudioAsync(frame: AudioFrame)

    /** DTMF tones the caller is pressing. */
    fun receiveDtmfAsync(): Flow<DtmfEvent>

    /** Send DTMF tones from the AI side (for navigating other people's menus). */
    suspend fun sendDtmfAsync(digits: String)

    /**
     * Transfer the call to [targetNumber]. Cold = drop and forget. Warm = park the
     * caller, dial the human, brief them, bridge both.
     */
    suspend fun transferAsync(targetNumber: String, mode: TransferMode, briefing: String? = null)

    /** End the call from our side. */
    suspend fun hangUpAsync()

    /** Subscribe to lifecycle status changes. Mirrors C# `event EventHandler<CallStatus>`. */
    fun onStatusChanged(listener: (CallStatus) -> Unit)

    /** Remove a previously registered status-change listener. */
    fun removeStatusChanged(listener: (CallStatus) -> Unit)

    /** Release the session. Mirrors C# `IAsyncDisposable.DisposeAsync`. */
    suspend fun disposeAsync()

    /** [AutoCloseable] bridge — runs [disposeAsync] synchronously. */
    override fun close() {
        kotlinx.coroutines.runBlocking { disposeAsync() }
    }
}

/**
 * Inbound webhook dispatcher — the carrier-provided HTTP handler (host wires this
 * into routing) calls into the dispatcher to materialise an [ICallSession] the agent
 * can attach to. Mirrors C# `IInboundCallDispatcher`.
 */
interface IInboundCallDispatcher {
    /** Stable id of the carrier feeding inbound calls into this dispatcher. */
    val carrierId: String

    /**
     * Subscribe to inbound call sessions. Each new call yields a session the consumer
     * attaches their agent to. Returns a handle whose [AutoCloseable.close] unsubscribes.
     */
    fun subscribe(handler: InboundCallHandler): AutoCloseable
}

/** Handler invoked with each inbound [ICallSession]. Mirrors C# `Func<ICallSession, ValueTask>`. */
fun interface InboundCallHandler {
    suspend fun handle(session: ICallSession)
}

// =====================================================================
// Media stream (IMediaStream.cs) + optional DTMF (IDtmfSendable.cs)
// =====================================================================

/**
 * A live media channel for one call. The carrier host's WebSocket handler implements
 * this; the carrier session consumes it. Mirrors C# `IMediaStream` (an `IAsyncDisposable`).
 */
interface IMediaStream : AutoCloseable {
    /** The carrier call id + metadata captured at connect. */
    val callInfo: CallInfo

    /** Inbound audio frames from the caller. */
    fun receiveAudioAsync(): Flow<AudioFrame>

    /** Outbound audio frames to the caller. */
    suspend fun sendAudioAsync(frame: AudioFrame)

    /** Inbound DTMF events. */
    fun receiveDtmfAsync(): Flow<DtmfEvent>

    /** Mark the call ended from our side. Closes the WebSocket. */
    suspend fun endAsync()

    /** Register a status-change listener. Mirrors C# `event EventHandler<CallStatus>`. */
    fun onStatusChanged(listener: (CallStatus) -> Unit)

    /** Remove a previously registered status-change listener. */
    fun removeStatusChanged(listener: (CallStatus) -> Unit)

    /** The current lifecycle state. */
    val currentStatus: CallStatus

    /** Release the stream. Mirrors C# `IAsyncDisposable.DisposeAsync`. */
    suspend fun disposeAsync()

    /** [AutoCloseable] bridge — runs [disposeAsync] synchronously. */
    override fun close() {
        kotlinx.coroutines.runBlocking { disposeAsync() }
    }
}

/**
 * Optional sister interface a host can layer on its [IMediaStream] to support
 * carrier-native out-of-band DTMF (Twilio mark control frame, Telnyx Call Control
 * send_dtmf, Plivo Audio Streaming control event). When the media stream doesn't
 * implement this, the session falls back to in-band tones via [DtmfToneGenerator].
 * Mirrors C# `IDtmfSendable`.
 */
interface IDtmfSendable {
    suspend fun sendDtmfAsync(digits: String)
}

// =====================================================================
// Tool-calling (ToolCalling.cs)
// =====================================================================

/**
 * Tool definition surfaced to the LLM. Mirrors C# `ToolDefinition`.
 *
 * @param name Tool name (function call name).
 * @param description Human description used to pick the tool.
 * @param argumentsJsonSchema JSON Schema describing the arguments.
 */
data class ToolDefinition(val name: String, val description: String, val argumentsJsonSchema: String)

/** An invocation of one tool by the model. Mirrors C# `ToolInvocation`. */
data class ToolInvocation(val callId: String, val toolName: String, val argumentsJson: String)

/** Result of a tool invocation. Mirrors C# `ToolResult`. */
data class ToolResult(val callId: String, val succeeded: Boolean, val resultJson: String, val error: String? = null)

/** In-process tool handler. Mirrors C# `delegate LocalToolHandler`. */
fun interface LocalToolHandler {
    suspend fun invoke(argumentsJson: String): String
}

/**
 * Tool registry: register local handlers OR HTTPS webhook URLs against a tool name;
 * the orchestrator dispatches. Mirrors C# `IToolCallRegistry`.
 */
interface IToolCallRegistry {
    /** All registered tool definitions. */
    val definitions: List<ToolDefinition>

    /** Register a local handler for [definition]. */
    fun registerLocal(definition: ToolDefinition, handler: LocalToolHandler)

    /** Register a webhook URL; the orchestrator POSTs arguments JSON. */
    fun registerWebhook(definition: ToolDefinition, webhook: URI)

    /** Invoke one tool call. */
    suspend fun invokeAsync(invocation: ToolInvocation): ToolResult
}

/**
 * Default in-memory registry. Thread-safe. Mirrors C# `DefaultToolCallRegistry`.
 * The C# reference dispatches webhooks through `HttpClient`; here the network boundary
 * is the injected [TelephonyHttpTransport], so tests run with no real HTTP. The webhook
 * request body reproduces the reference shape: `{ call_id, tool, arguments }` where
 * `arguments` is the parsed (re-emitted) argument JSON rather than a nested string.
 */
class DefaultToolCallRegistry(
    private val http: TelephonyHttpTransport,
) : IToolCallRegistry {

    private data class Entry(val def: ToolDefinition, val local: LocalToolHandler?, val webhook: URI?)

    // Case-insensitive keys, matching C# StringComparer.OrdinalIgnoreCase.
    private val tools = ConcurrentHashMap<String, Entry>()

    private fun keyOf(name: String) = name.lowercase()

    override val definitions: List<ToolDefinition>
        get() = tools.values.map { it.def }

    override fun registerLocal(definition: ToolDefinition, handler: LocalToolHandler) {
        require(definition.name.isNotBlank()) { "Tool name is required" }
        tools[keyOf(definition.name)] = Entry(definition, handler, null)
    }

    override fun registerWebhook(definition: ToolDefinition, webhook: URI) {
        require(webhook.isAbsolute) { "Webhook URL must be absolute." }
        require(definition.name.isNotBlank()) { "Tool name is required" }
        tools[keyOf(definition.name)] = Entry(definition, null, webhook)
    }

    override suspend fun invokeAsync(invocation: ToolInvocation): ToolResult {
        val entry = tools[keyOf(invocation.toolName)]
            ?: return ToolResult(invocation.callId, false, "{}", "Tool '${invocation.toolName}' is not registered.")

        return try {
            when {
                entry.local != null -> {
                    val resultJson = entry.local.invoke(invocation.argumentsJson)
                    ToolResult(invocation.callId, true, resultJson.ifBlankOr("{}"))
                }

                entry.webhook != null -> {
                    val bodyJson = TelephonyJson.webhookBody(invocation.callId, invocation.toolName, invocation.argumentsJson)
                    val resp = http.sendAsync(
                        TelephonyHttpRequest(
                            method = "POST",
                            uri = entry.webhook,
                            headers = mapOf("Content-Type" to "application/json"),
                            body = bodyJson,
                        ),
                    )
                    if (!resp.isSuccess) {
                        ToolResult(
                            invocation.callId, false, "{}",
                            "Webhook ${resp.statusCode}: ${truncate(resp.body, 240)}",
                        )
                    } else {
                        ToolResult(invocation.callId, true, resp.body.ifBlankOr("{}"))
                    }
                }

                else -> ToolResult(
                    invocation.callId, false, "{}",
                    "Tool '${invocation.toolName}' is registered without a local handler or webhook.",
                )
            }
        } catch (ex: Exception) {
            ToolResult(invocation.callId, false, "{}", ex.message ?: ex.toString())
        }
    }

    private fun String.ifBlankOr(fallback: String): String = if (this.isBlank()) fallback else this

    companion object {
        private fun truncate(s: String, max: Int): String = if (s.length <= max) s else s.substring(0, max) + "…"
    }
}

// =====================================================================
// HTTP transport boundary — the injected network dependency.
// The C# adapters use HttpClient directly; the Kotlin port injects this fun
// interface (mirroring how realtime.cloud injects IRealtimeTransport) so carriers
// stay framework-free and every test is deterministic + offline.
// =====================================================================

/** One HTTP request the carriers issue. Body is null for GET/DELETE without payload. */
data class TelephonyHttpRequest(
    val method: String,
    val uri: URI,
    val headers: Map<String, String> = emptyMap(),
    val body: String? = null,
)

/** One HTTP response. [isSuccess] mirrors `HttpResponseMessage.IsSuccessStatusCode` (2xx). */
data class TelephonyHttpResponse(val statusCode: Int, val body: String) {
    val isSuccess: Boolean get() = statusCode in 200..299
}

/**
 * The network boundary the carriers depend on in place of C#'s `HttpClient`. A host
 * supplies a real HTTP-backed implementation; tests supply a deterministic in-memory
 * one. Mirrors the injection style of `IRealtimeTransportFactory` in realtime.cloud.
 */
fun interface TelephonyHttpTransport {
    suspend fun sendAsync(request: TelephonyHttpRequest): TelephonyHttpResponse
}

/**
 * Throws on every call — the documented "no HTTP transport wired" line. Mirrors the
 * spirit of C# `Null*` fallbacks that fail with a helpful message.
 */
class NullTelephonyHttpTransport private constructor() : TelephonyHttpTransport {
    companion object {
        val Instance = NullTelephonyHttpTransport()
    }

    override suspend fun sendAsync(request: TelephonyHttpRequest): TelephonyHttpResponse =
        throw IllegalStateException(
            "No TelephonyHttpTransport is registered. Inject a host HTTP transport (or FakeTelephonyHttpTransport for tests).",
        )
}

/**
 * Deterministic in-memory [TelephonyHttpTransport] for tests + dev. Records every
 * request and returns queued/registered responses keyed by "METHOD path" — no real
 * socket is opened. A missing route yields 404 so carrier fail-soft paths are covered.
 */
class FakeTelephonyHttpTransport : TelephonyHttpTransport {

    /** Every request that was issued, in order. */
    val requests: MutableList<TelephonyHttpRequest> = CopyOnWriteArrayList()

    private data class Route(val method: String, val pathPrefix: String, val response: TelephonyHttpResponse)

    private val routes = CopyOnWriteArrayList<Route>()

    /**
     * Register a canned response for requests whose method matches and whose
     * URI path (path only — query ignored) starts with [pathPrefix]. Later
     * registrations for the same prefix take precedence (checked first).
     */
    fun on(method: String, pathPrefix: String, statusCode: Int = 200, body: String = "{}"): FakeTelephonyHttpTransport {
        routes.add(0, Route(method.uppercase(), pathPrefix, TelephonyHttpResponse(statusCode, body)))
        return this
    }

    override suspend fun sendAsync(request: TelephonyHttpRequest): TelephonyHttpResponse {
        requests.add(request)
        val path = request.uri.path ?: ""
        val match = routes.firstOrNull {
            it.method.equals(request.method, ignoreCase = true) && path.startsWith(it.pathPrefix)
        }
        return match?.response ?: TelephonyHttpResponse(404, "{\"error\":\"no route\"}")
    }
}

// =====================================================================
// JSON helpers — kotlinx.serialization, the tree convention.
// =====================================================================

/** Parse a JSON document — the entry the carrier adapters use. Matches catalog/Json.kt style. */
internal fun internalJsonParse(raw: String): kotlinx.serialization.json.JsonElement = TelephonyJson.parse(raw)

/** Minimal JSON helpers for the carrier adapters, matching the tree's catalog/Json.kt style. */
internal object TelephonyJson {
    private val json = kotlinx.serialization.json.Json { ignoreUnknownKeys = true }

    fun parse(raw: String): kotlinx.serialization.json.JsonElement = json.parseToJsonElement(raw)

    /** Build the tool-webhook body `{ "call_id":..., "tool":..., "arguments": <parsed args> }`. */
    fun webhookBody(callId: String, tool: String, argumentsJson: String): String {
        val args = runCatching { json.parseToJsonElement(argumentsJson) }
            .getOrElse { kotlinx.serialization.json.JsonObject(emptyMap()) }
        val obj = kotlinx.serialization.json.buildJsonObject {
            put("call_id", kotlinx.serialization.json.JsonPrimitive(callId))
            put("tool", kotlinx.serialization.json.JsonPrimitive(tool))
            put("arguments", args)
        }
        return obj.toString()
    }
}

// =====================================================================
// DTMF tone generator (DtmfToneGenerator.cs)
// =====================================================================

/**
 * Stateless DTMF audio generator. Mirrors C# `DtmfToneGenerator`. The dual-tone
 * synthesis is reproduced sample-for-sample against the reference.
 */
object DtmfToneGenerator {

    /** Standard DTMF frequencies (low row × high column). Mirrors C# `Frequencies`. */
    private val frequencies: Map<Char, Pair<Int, Int>> = mapOf(
        '1' to (697 to 1209),
        '2' to (697 to 1336),
        '3' to (697 to 1477),
        'A' to (697 to 1633),
        '4' to (770 to 1209),
        '5' to (770 to 1336),
        '6' to (770 to 1477),
        'B' to (770 to 1633),
        '7' to (852 to 1209),
        '8' to (852 to 1336),
        '9' to (852 to 1477),
        'C' to (852 to 1633),
        '*' to (941 to 1209),
        '0' to (941 to 1336),
        '#' to (941 to 1477),
        'D' to (941 to 1633),
    )

    /**
     * Generate one PCM-16 mono buffer for the digit at the given sample rate. Mirrors
     * C# `Generate`.
     *
     * @param digit DTMF digit: 0-9, *, #, A, B, C, D.
     * @param sampleRateHz Output sample rate.
     * @param durationMs Tone duration. Default 150 ms.
     * @param amplitude Peak amplitude 0..1. Default 0.5.
     */
    fun generate(digit: Char, sampleRateHz: Int, durationMs: Int = 150, amplitude: Float = 0.5f): ByteArray {
        require(sampleRateHz > 0) { "sampleRateHz" }
        require(durationMs > 0) { "durationMs" }
        val key = digit.uppercaseChar()
        val pair = frequencies[key] ?: throw IllegalArgumentException("Unsupported DTMF digit '$digit'.")
        val (low, high) = pair

        val samples = sampleRateHz * durationMs / 1000
        val buf = ByteArray(samples * 2)
        for (i in 0 until samples) {
            val t = i.toDouble() / sampleRateHz
            val s = 0.5 * amplitude * (sin(2 * PI * low * t) + sin(2 * PI * high * t))
            val clamped = s.coerceIn(-1.0, 1.0)
            val value = (clamped * Short.MAX_VALUE).toInt().toShort()
            // Little-endian 16-bit, matching BinaryPrimitives.WriteInt16LittleEndian.
            buf[i * 2] = (value.toInt() and 0xFF).toByte()
            buf[i * 2 + 1] = ((value.toInt() shr 8) and 0xFF).toByte()
        }
        return buf
    }

    /**
     * Generate a full string of digits with gap silence between them. Mirrors C#
     * `GenerateSequence`.
     */
    fun generateSequence(
        digits: String,
        sampleRateHz: Int,
        toneDurationMs: Int = 150,
        interDigitGapMs: Int = 50,
        amplitude: Float = 0.5f,
    ): ByteArray {
        if (digits.isEmpty()) return ByteArray(0)
        val gapSamples = sampleRateHz * interDigitGapMs / 1000
        val gap = ByteArray(gapSamples * 2)

        val out = java.io.ByteArrayOutputStream()
        for (i in digits.indices) {
            val tone = generate(digits[i], sampleRateHz, toneDurationMs, amplitude)
            out.write(tone)
            if (i < digits.length - 1) {
                out.write(gap)
            }
        }
        return out.toByteArray()
    }

    /**
     * Send [digits] over the call via in-band tones. Mirrors C# `SendThroughSessionAsync`.
     */
    suspend fun sendThroughSessionAsync(
        session: ICallSession,
        digits: String,
        sampleRateHz: Int = 8000,
        toneDurationMs: Int = 150,
        interDigitGapMs: Int = 50,
    ) {
        if (digits.isEmpty()) return
        val pcm = generateSequence(digits, sampleRateHz, toneDurationMs, interDigitGapMs)
        val format = when (sampleRateHz) {
            8000 -> CallMediaFormat.Mulaw8000
            16000 -> CallMediaFormat.Pcm16000
            24000 -> CallMediaFormat.Pcm24000
            else -> CallMediaFormat.Pcm16000
        }
        session.sendAudioAsync(AudioFrame(pcm, format, Duration.ZERO))
    }
}

// =====================================================================
// Warm-transfer orchestrator (WarmTransferOrchestrator.cs)
// =====================================================================

/**
 * One warm-transfer request. Mirrors C# `WarmTransferRequest`.
 *
 * @param sourceSession The active call we want to transfer.
 * @param targetNumber E.164 number of the person we're transferring to.
 * @param briefingText What the AI should say to the target before the bridge.
 * @param bridgeStreamUrl WSS endpoint the carrier will hand the target leg to.
 */
data class WarmTransferRequest(
    val sourceSession: ICallSession,
    val targetNumber: String,
    val briefingText: String,
    val bridgeStreamUrl: URI,
)

/** Outcome of a warm transfer. Mirrors C# `WarmTransferResult`. */
data class WarmTransferResult(
    val succeeded: Boolean,
    val failureReason: String?,
    val bridgeSession: ICallSession?,
)

/** Park caller, dial target, brief, bridge. Mirrors C# `IWarmTransferOrchestrator`. */
interface IWarmTransferOrchestrator {
    suspend fun executeAsync(request: WarmTransferRequest): WarmTransferResult
}

/** Synthesise the briefing text to PCM-16 mono. Mirrors C# `delegate BriefingSynthesiser`. */
fun interface BriefingSynthesiser {
    suspend fun invoke(text: String): ByteArray
}

/**
 * Carrier-agnostic warm-transfer driver. Mirrors C# `DefaultWarmTransferOrchestrator`:
 * dial target on a fresh leg → speak briefing → cold-transfer the caller to the target
 * (the bridge moment) → hang up the AI leg. Failures at each step hang up the bridge
 * leg and report a reason, exactly like the reference.
 */
class DefaultWarmTransferOrchestrator(
    private val carrier: ITelephonyCarrier,
    private val briefingTts: BriefingSynthesiser,
) : IWarmTransferOrchestrator {

    override suspend fun executeAsync(request: WarmTransferRequest): WarmTransferResult {
        if (request.targetNumber.isBlank()) {
            return WarmTransferResult(false, "TargetNumber is required", null)
        }

        // 1) Dial target on a fresh leg.
        val bridgeLeg: ICallSession = try {
            carrier.dialAsync(
                fromNumber = request.sourceSession.info.to,
                toNumber = request.targetNumber,
                streamUrl = request.bridgeStreamUrl,
            )
        } catch (ex: Exception) {
            return WarmTransferResult(false, "Failed to dial target: ${ex.message}", null)
        }

        // 2) Speak briefing to target.
        try {
            val briefingAudio = briefingTts.invoke(request.briefingText)
            if (briefingAudio.isNotEmpty()) {
                bridgeLeg.sendAudioAsync(AudioFrame(briefingAudio, CallMediaFormat.Pcm24000, Duration.ZERO))
            }
        } catch (ex: Exception) {
            bridgeLeg.hangUpAsync()
            return WarmTransferResult(false, "Failed to brief target: ${ex.message}", null)
        }

        // 3) Hand caller off to target — the bridge moment.
        try {
            request.sourceSession.transferAsync(request.targetNumber, TransferMode.Cold, briefing = null)
        } catch (ex: Exception) {
            bridgeLeg.hangUpAsync()
            return WarmTransferResult(false, "Failed to bridge caller: ${ex.message}", null)
        }

        // 4) AI leg ends; caller and target stay connected.
        bridgeLeg.hangUpAsync()
        return WarmTransferResult(true, null, bridgeLeg)
    }
}

// =====================================================================
// Status-change fan-out — shared listener bag for sessions/streams.
// Mirrors C# `event EventHandler<CallStatus>` add/remove + safe invoke.
// =====================================================================

/**
 * Thread-safe listener bag used by media streams + sessions to reproduce C#'s
 * `event EventHandler<CallStatus>`. Listeners live in a [CopyOnWriteArrayList] so
 * NO lock is held while a subscriber callback runs — the callers snapshot the new
 * status under their own gate, release it, then call [raise].
 */
internal class StatusListeners {
    private val listeners = CopyOnWriteArrayList<(CallStatus) -> Unit>()

    fun add(listener: (CallStatus) -> Unit) {
        listeners.add(listener)
    }

    fun remove(listener: (CallStatus) -> Unit) {
        listeners.remove(listener)
    }

    fun raise(status: CallStatus) {
        for (l in listeners) l(status)
    }
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

/** Null carrier — fail-soft on every operation. Mirrors C# `NullTelephonyCarrier`. */
class NullTelephonyCarrier private constructor() : ITelephonyCarrier {
    companion object {
        val Instance = NullTelephonyCarrier()
    }

    override val carrierId: String get() = "null"
    override val isConfigured: Boolean get() = false

    override suspend fun provisionNumberAsync(countryCode: String, areaCode: String?): ProvisionedNumber =
        throw IllegalStateException(
            "Null carrier cannot provision phone numbers. Register a real ITelephonyCarrier (telephony.twilio / .telnyx / .plivo).",
        )

    override suspend fun configureInboundWebhookAsync(phoneNumber: String, inboundWebhook: URI) {
        // No-op — mirrors ValueTask.CompletedTask.
    }

    override suspend fun dialAsync(
        fromNumber: String,
        toNumber: String,
        streamUrl: URI,
        options: OutboundDialOptions?,
    ): ICallSession =
        throw IllegalStateException("Null carrier cannot place outbound calls. Register a real ITelephonyCarrier.")

    override suspend fun listNumbersAsync(): List<ProvisionedNumber> = emptyList()
}

/** Null inbound dispatcher — never fires. Mirrors C# `NullInboundCallDispatcher`. */
class NullInboundCallDispatcher private constructor() : IInboundCallDispatcher {
    companion object {
        val Instance = NullInboundCallDispatcher()
    }

    override val carrierId: String get() = "null"

    override fun subscribe(handler: InboundCallHandler): AutoCloseable = NoopCloseable

    private object NoopCloseable : AutoCloseable {
        override fun close() {}
    }
}

// =====================================================================
// In-memory inbound dispatcher — deterministic fan-out for tests + hosts.
// (Kotlin dev/test helper; unbounded fan-out, subscribe-before-publish safe.)
// =====================================================================

/**
 * Deterministic in-memory [IInboundCallDispatcher]. A host (or a test) calls
 * [dispatch] with a freshly materialised [ICallSession]; every current subscriber's
 * handler is invoked. Handlers are held in a [CopyOnWriteArrayList] so subscribing +
 * unsubscribing never blocks a dispatch, and dispatch holds no lock while a handler
 * runs. There is no internal buffering: dispatch delivers only to subscribers present
 * at dispatch time (the caller subscribes before dispatching), matching the C#
 * `Subscribe`/handler contract (which likewise fans out live, not replayed).
 */
class InMemoryInboundCallDispatcher(override val carrierId: String) : IInboundCallDispatcher {

    private val handlers = CopyOnWriteArrayList<InboundCallHandler>()

    override fun subscribe(handler: InboundCallHandler): AutoCloseable {
        handlers.add(handler)
        return AutoCloseable { handlers.remove(handler) }
    }

    /** Deliver [session] to every current subscriber. */
    suspend fun dispatch(session: ICallSession) {
        for (h in handlers) h.handle(session)
    }

    /** Number of live subscribers (test aid). */
    val subscriberCount: Int get() = handlers.size
}

// =====================================================================
// In-memory media stream — a live, attachable IMediaStream for tests/dev.
// Not in the C# reference (whose stream is host/WebSocket-backed); this is the
// deterministic in-memory equivalent the fake carrier hands out so a session is
// immediately usable end-to-end without a host WebSocket.
// =====================================================================

/**
 * Deterministic in-memory [IMediaStream]. A test injects inbound audio + DTMF and
 * reads captured outbound audio; status changes are raised to listeners with no lock
 * held while a callback runs (the [StatusListeners] bag is copy-on-write). Unbounded
 * channels back the inbound streams so an injection made before a consumer attaches is
 * retained until read (matching the C# unbounded-channel retention semantics).
 */
class InMemoryMediaStream(override val callInfo: CallInfo) : IMediaStream {

    private val inboundAudio = Channel<AudioFrame>(Channel.UNLIMITED)
    private val inboundDtmf = Channel<DtmfEvent>(Channel.UNLIMITED)
    private val outboundAudio = CopyOnWriteArrayList<AudioFrame>()
    private val statusListeners = StatusListeners()
    private val gate = Any()

    @Volatile
    private var statusField: CallStatus = CallStatus.Active

    override val currentStatus: CallStatus
        get() = synchronized(gate) { statusField }

    /** Outbound audio the session emitted, captured for assertions. */
    val sentAudioFrames: List<AudioFrame> get() = outboundAudio.toList()

    /** Inject one inbound audio frame. */
    fun injectInboundAudio(frame: AudioFrame) {
        inboundAudio.trySend(frame)
    }

    /** Inject one inbound DTMF event. */
    fun injectInboundDtmf(ev: DtmfEvent) {
        inboundDtmf.trySend(ev)
    }

    /** Move the stream to a new status and notify listeners (e.g. carrier reports Active). */
    fun setStatus(newStatus: CallStatus) {
        synchronized(gate) { statusField = newStatus }
        statusListeners.raise(newStatus)
    }

    override fun onStatusChanged(listener: (CallStatus) -> Unit) = statusListeners.add(listener)
    override fun removeStatusChanged(listener: (CallStatus) -> Unit) = statusListeners.remove(listener)

    override fun receiveAudioAsync(): Flow<AudioFrame> = flow {
        for (f in inboundAudio) emit(f)
    }

    override fun receiveDtmfAsync(): Flow<DtmfEvent> = flow {
        for (d in inboundDtmf) emit(d)
    }

    override suspend fun sendAudioAsync(frame: AudioFrame) {
        outboundAudio.add(frame)
    }

    override suspend fun endAsync() {
        setStatus(CallStatus.EndedByAgent)
        inboundAudio.close()
        inboundDtmf.close()
    }

    override suspend fun disposeAsync() {
        inboundAudio.close()
        inboundDtmf.close()
    }
}

/**
 * Deterministic, fully in-memory [ITelephonyCarrier] — no HTTP, no host WebSocket. Use
 * it to exercise the whole telephony surface offline. Provisioned numbers, dialled
 * sessions, and inbound sessions are all tracked in memory. Each [dialAsync] returns a
 * live session over an [InMemoryMediaStream] moved straight to [CallStatus.Active], so
 * the caller can send/receive audio and drive transfer/hang-up immediately.
 *
 * Not part of the C# public surface (the reference ships Null* + Test* + real carriers);
 * this is the Kotlin dev/test carrier the task's "deterministic in-memory fake carrier"
 * calls for, built entirely from the ported contracts.
 */
class FakeTelephonyCarrier(
    override val carrierId: String = "fake",
    private val defaultFormat: CallMediaFormat = CallMediaFormat.Pcm16000,
) : ITelephonyCarrier {

    private val numbers = CopyOnWriteArrayList<ProvisionedNumber>()
    private val configuredWebhooks = ConcurrentHashMap<String, URI>()

    /** Every session handed out by [dialAsync], newest last (test aid). */
    val dialledSessions: MutableList<FakeCallSession> = CopyOnWriteArrayList()

    override val isConfigured: Boolean get() = true

    override suspend fun provisionNumberAsync(countryCode: String, areaCode: String?): ProvisionedNumber {
        val suffix = (numbers.size + 1).toString().padStart(4, '0')
        val prefix = areaCode?.takeIf { it.isNotBlank() } ?: "555000"
        val pn = ProvisionedNumber(
            phoneNumber = "+$prefix$suffix",
            carrierId = carrierId,
            provisionedAtUtc = Instant.now(),
            monthlyRecurringCost = BigDecimal.ZERO,
        )
        numbers.add(pn)
        return pn
    }

    override suspend fun configureInboundWebhookAsync(phoneNumber: String, inboundWebhook: URI) {
        configuredWebhooks[phoneNumber] = inboundWebhook
    }

    /** The webhook configured for [phoneNumber], or null (test aid). */
    fun webhookFor(phoneNumber: String): URI? = configuredWebhooks[phoneNumber]

    override suspend fun dialAsync(
        fromNumber: String,
        toNumber: String,
        streamUrl: URI,
        options: OutboundDialOptions?,
    ): ICallSession {
        val callInfo = CallInfo(
            callId = UUID.randomUUID().toString().replace("-", ""),
            direction = CallDirection.Outbound,
            from = options?.callerIdOverride ?: fromNumber,
            to = toNumber,
            carrierId = carrierId,
            mediaFormat = defaultFormat,
            startedAtUtc = Instant.now(),
        )
        val media = InMemoryMediaStream(callInfo)
        val session = FakeCallSession(media, this)
        dialledSessions.add(session)
        return session
    }

    override suspend fun listNumbersAsync(): List<ProvisionedNumber> = numbers.toList()

    /**
     * Materialise an inbound [ICallSession] as if the carrier delivered a call — the
     * in-memory analogue of the host webhook firing. Returns the session and its
     * backing stream so a test can inject caller audio.
     */
    fun receiveInboundCall(
        from: String,
        to: String,
        format: CallMediaFormat = defaultFormat,
    ): FakeCallSession {
        val callInfo = CallInfo(
            callId = UUID.randomUUID().toString().replace("-", ""),
            direction = CallDirection.Inbound,
            from = from,
            to = to,
            carrierId = carrierId,
            mediaFormat = format,
            startedAtUtc = Instant.now(),
        )
        return FakeCallSession(InMemoryMediaStream(callInfo), this)
    }
}

/**
 * [ICallSession] over an [InMemoryMediaStream], returned by [FakeTelephonyCarrier].
 * Audio/DTMF delegate to the media stream; transfer/hang-up flip status. Mirrors the
 * real carrier sessions' shape (session wraps a media stream + carrier) but with a
 * fully in-memory backing. The stream starts [CallStatus.Active].
 */
class FakeCallSession internal constructor(
    private val media: InMemoryMediaStream,
    @Suppress("unused") private val carrier: FakeTelephonyCarrier,
) : ICallSession {

    private val statusListeners = StatusListeners()

    @Volatile
    private var statusField: CallStatus = CallStatus.Active

    private val mediaListener: (CallStatus) -> Unit = { setStatus(it) }

    init {
        media.setStatus(CallStatus.Active)
        media.onStatusChanged(mediaListener)
    }

    /** The backing media stream — inject inbound audio / DTMF through it in tests. */
    val mediaStream: InMemoryMediaStream get() = media

    override val info: CallInfo get() = media.callInfo

    override val status: CallStatus get() = statusField

    override fun onStatusChanged(listener: (CallStatus) -> Unit) = statusListeners.add(listener)
    override fun removeStatusChanged(listener: (CallStatus) -> Unit) = statusListeners.remove(listener)

    override fun receiveAudioAsync(): Flow<AudioFrame> = media.receiveAudioAsync()
    override fun receiveDtmfAsync(): Flow<DtmfEvent> = media.receiveDtmfAsync()

    override suspend fun sendAudioAsync(frame: AudioFrame) = media.sendAudioAsync(frame)

    override suspend fun sendDtmfAsync(digits: String) {
        if (digits.isEmpty()) return
        // Prefer carrier-native out-of-band DTMF if the stream supports it; otherwise
        // fall back to in-band tones — exactly like the real carrier sessions.
        val native = media as? IDtmfSendable
        if (native != null) {
            native.sendDtmfAsync(digits)
            return
        }
        val sampleRate = when (info.mediaFormat) {
            CallMediaFormat.Pcm16000 -> 16000
            CallMediaFormat.Pcm24000 -> 24000
            CallMediaFormat.Mulaw8000 -> 8000
            else -> 8000
        }
        DtmfToneGenerator.sendThroughSessionAsync(this, digits, sampleRate)
    }

    override suspend fun transferAsync(targetNumber: String, mode: TransferMode, briefing: String?) {
        setStatus(CallStatus.Transferred)
    }

    override suspend fun hangUpAsync() {
        setStatus(CallStatus.EndedByAgent)
        media.removeStatusChanged(mediaListener)
        runCatching { media.endAsync() }
    }

    override suspend fun disposeAsync() {
        media.removeStatusChanged(mediaListener)
        media.disposeAsync()
    }

    private fun setStatus(status: CallStatus) {
        if (statusField == status) return
        statusField = status
        statusListeners.raise(status)
    }
}

// =====================================================================
// TestCallSession (TestCallSession.cs)
// =====================================================================

/**
 * In-memory [ICallSession] for harnesses + unit tests. Lets a test harness inject
 * inbound audio + DTMF, capture outbound audio, and drive lifecycle events on demand.
 * Mirrors C# `TestCallSession`.
 */
class TestCallSession(info: CallInfo? = null) : ICallSession {

    private val inboundAudio = Channel<AudioFrame>(Channel.UNLIMITED)
    private val inboundDtmf = Channel<DtmfEvent>(Channel.UNLIMITED)
    private val outboundAudio = CopyOnWriteArrayList<AudioFrame>()
    private val outboundDtmf = CopyOnWriteArrayList<String>()
    private val gate = Any()
    private val statusListeners = StatusListeners()

    @Volatile
    private var statusField: CallStatus = CallStatus.Active

    override val info: CallInfo = info ?: CallInfo(
        callId = UUID.randomUUID().toString().replace("-", ""),
        direction = CallDirection.Inbound,
        from = "+15555550100",
        to = "+15555550200",
        carrierId = "test",
        mediaFormat = CallMediaFormat.Pcm16000,
        startedAtUtc = Instant.now(),
    )

    override val status: CallStatus
        get() = synchronized(gate) { statusField }

    override fun onStatusChanged(listener: (CallStatus) -> Unit) = statusListeners.add(listener)
    override fun removeStatusChanged(listener: (CallStatus) -> Unit) = statusListeners.remove(listener)

    /** Outbound audio frames the AI has emitted, captured for assertions. */
    val sentAudioFrames: List<AudioFrame> get() = outboundAudio.toList()

    /** Outbound DTMF strings the AI has emitted. */
    val sentDtmf: List<String> get() = outboundDtmf.toList()

    /** Inject one inbound audio frame for the AI to consume via [receiveAudioAsync]. */
    fun injectInboundAudio(frame: AudioFrame) {
        inboundAudio.trySend(frame)
    }

    /** Inject one inbound DTMF event. */
    fun injectInboundDtmf(ev: DtmfEvent) {
        inboundDtmf.trySend(ev)
    }

    /** Stop the inbound streams cleanly. */
    fun endInboundStreams() {
        inboundAudio.close()
        inboundDtmf.close()
    }

    /** Trigger a status change (e.g. caller hangs up). */
    fun triggerStatusChange(newStatus: CallStatus) {
        // Update state under the gate, then raise OUTSIDE it: the listener bag is a
        // CopyOnWriteArrayList, so no lock is held while a subscriber callback runs.
        synchronized(gate) { statusField = newStatus }
        statusListeners.raise(newStatus)
    }

    override fun receiveAudioAsync(): Flow<AudioFrame> = flow {
        for (f in inboundAudio) emit(f)
    }

    override fun receiveDtmfAsync(): Flow<DtmfEvent> = flow {
        for (d in inboundDtmf) emit(d)
    }

    override suspend fun sendAudioAsync(frame: AudioFrame) {
        outboundAudio.add(frame)
    }

    override suspend fun sendDtmfAsync(digits: String) {
        outboundDtmf.add(digits)
    }

    override suspend fun transferAsync(targetNumber: String, mode: TransferMode, briefing: String?) {
        triggerStatusChange(CallStatus.Transferred)
    }

    override suspend fun hangUpAsync() {
        triggerStatusChange(CallStatus.EndedByAgent)
        endInboundStreams()
    }

    override suspend fun disposeAsync() {
        endInboundStreams()
    }
}
