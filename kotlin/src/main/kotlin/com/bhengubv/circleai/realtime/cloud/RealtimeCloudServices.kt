// RealtimeCloudServices.kt
//
// Kotlin port of the CircleAI.Realtime.Cloud connectors — the C# reference files
// are the EXACT spec (OpenAiRealtimeService, GeminiLiveService,
// ElevenLabsConvService, NovaSonicService, UltravoxService,
// RealtimeWebSocketSession, Options.cs). The transport seam
// (IRealtimeTransport / IRealtimeTransportFactory / NullRealtimeTransportFactory)
// lives in RealtimeCloud.kt; the realtime contracts + events
// (IRealtimeService, IRealtimeSession, RealtimeSessionConfig, RealtimeEvent…)
// live in com.bhengubv.circleai.realtime.Realtime. This file adds the per-vendor
// options, the WS-backed session with cross-vendor JSON envelope demux, and the
// 5 vendor services.
//
// C# -> Kotlin conventions:
//   IRealtimeService                        -> the Realtime.kt interface
//   ValueTask<IRealtimeSession>             -> suspend fun
//   ILogger / NullLogger.Instance           -> dropped (the C# only LogDebug's an
//                                              unparseable frame; the Kotlin session
//                                              silently skips it, same behaviour)
//   System.Text.Json (envelope parse)       -> kotlinx.serialization.json
//   HttpClient (Ultravox POST /api/calls)   -> injected [IRealtimeHttpTransport] seam
//                                              (mirrors the speech/vision cloud seams)
//   Uri.EscapeDataString                    -> URLEncoder.encode(..., "UTF-8")
//
// The lenient cross-vendor ParseEvent (OpenAI Realtime + Gemini Live + ElevenLabs
// shapes), the vendor-neutral send envelopes (user.text / tool.result /
// response.cancel), and every endpoint/header/credential shape are ported
// verbatim so the wire behaviour matches the C# reference.

package com.bhengubv.circleai.realtime.cloud

import com.bhengubv.circleai.realtime.IRealtimeService
import com.bhengubv.circleai.realtime.IRealtimeSession
import com.bhengubv.circleai.realtime.RealtimeAudioFrame
import com.bhengubv.circleai.realtime.RealtimeDirection
import com.bhengubv.circleai.realtime.RealtimeEvent
import com.bhengubv.circleai.realtime.RealtimeSessionConfig
import com.bhengubv.circleai.realtime.SessionErrorEvent
import com.bhengubv.circleai.realtime.SpeechEndedEvent
import com.bhengubv.circleai.realtime.SpeechStartedEvent
import com.bhengubv.circleai.realtime.ToolCallEvent
import com.bhengubv.circleai.realtime.TranscriptDeltaEvent
import com.bhengubv.circleai.realtime.TranscriptFinalEvent
import com.bhengubv.circleai.realtime.TurnCompleteEvent
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.net.URI
import java.net.URLEncoder
import java.time.Duration
import java.time.Instant
import java.util.UUID

// =====================================================================
// Options (Options.cs)
// =====================================================================

/** (3.3.0) OpenAI Realtime options. Bearer auth + WSS endpoint. Mirrors C# `OpenAiRealtimeOptions`. */
data class OpenAiRealtimeOptions(
    val webSocketEndpoint: String = "wss://api.openai.com/v1/realtime",
    val apiKey: String? = null,
    val defaultModel: String = "gpt-4o-realtime-preview-2024-12-17",
    /** Beta header value required by OpenAI Realtime. */
    val betaHeader: String = "realtime=v1",
)

/** (3.3.0) Google Gemini Live options. Mirrors C# `GeminiLiveOptions`. */
data class GeminiLiveOptions(
    val webSocketEndpoint: String =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent",
    val apiKey: String? = null,
    val defaultModel: String = "models/gemini-2.0-flash-exp",
)

/** (3.3.0) AWS Nova Sonic options. Uses SigV4 auth on the WS handshake. Mirrors C# `NovaSonicOptions`. */
data class NovaSonicOptions(
    val region: String = "us-east-1",
    val accessKeyId: String? = null,
    val secretAccessKey: String? = null,
    val sessionToken: String? = null,
    val defaultModel: String = "amazon.nova-sonic-v1:0",
)

/** (3.3.0) ElevenLabs Conversational AI options. Mirrors C# `ElevenLabsConvOptions`. */
data class ElevenLabsConvOptions(
    val webSocketEndpoint: String = "wss://api.elevenlabs.io/v1/convai/conversation",
    val apiKey: String? = null,
    /** ElevenLabs Agent id created in their dashboard. */
    val agentId: String? = null,
)

/** (3.3.0) Ultravox options. Mirrors C# `UltravoxOptions`. */
data class UltravoxOptions(
    val apiEndpoint: String = "https://api.ultravox.ai",
    val apiKey: String? = null,
    val defaultModel: String = "fixie-ai/ultravox-70B",
    val defaultVoice: String = "Mark",
)

// =====================================================================
// HTTP transport seam (for Ultravox's two-step call creation)
// =====================================================================

/** One HTTP response for the Ultravox call-creation POST. */
class RealtimeHttpResponse(val statusCode: Int, val body: String) {
    val isSuccess: Boolean get() = statusCode in 200..299
}

/**
 * The seam the Ultravox connector posts through to create a call. The C# service
 * uses a real HttpClient; the Kotlin port injects this so hosts wire a real
 * transport while tests supply a deterministic fake. Mirrors the speech/vision
 * cloud HTTP-seam pattern.
 */
interface IRealtimeHttpTransport {
    /** POST a JSON body to [path] on [baseAddress] with [headers]. */
    suspend fun postJson(
        baseAddress: String,
        path: String,
        headers: Map<String, String>,
        jsonBody: String,
    ): RealtimeHttpResponse
}

private fun urlEncode(s: String): String = URLEncoder.encode(s, "UTF-8")

private val REALTIME_JSON = Json { ignoreUnknownKeys = true; isLenient = true }

// =====================================================================
// RealtimeWebSocketSession (RealtimeWebSocketSession.cs)
// =====================================================================

/**
 * Concrete [IRealtimeSession] backed by an [IRealtimeTransport]. Vendor-specific
 * JSON envelope translation lives here: text frames are forwarded as
 * [RealtimeEvent]s via a lenient parser that recognises common shapes (OpenAI
 * Realtime, Gemini Live, ElevenLabs Conv); binary frames become
 * [RealtimeAudioFrame] in the format declared in [RealtimeSessionConfig]. Mirrors
 * C# `RealtimeWebSocketSession`.
 */
class RealtimeWebSocketSession(
    private val transport: IRealtimeTransport,
    private val config: RealtimeSessionConfig,
    private val providerId: String,
) : IRealtimeSession {

    override val sessionId: String = UUID.randomUUID().toString().replace("-", "")

    override fun receiveAudioAsync(): Flow<RealtimeAudioFrame> = flow {
        transport.receiveBinaryAsync().collect { frame ->
            emit(RealtimeAudioFrame(frame, config.audioFormat, Duration.ZERO))
        }
    }

    override suspend fun sendAudioAsync(frame: RealtimeAudioFrame) {
        transport.sendBinaryAsync(frame.pcm)
    }

    override suspend fun sendTextAsync(text: String) {
        // Vendor-neutral envelope. Host-specific shims may translate.
        val json = buildJson(
            "type" to JsonPrimitive("user.text"),
            "provider" to JsonPrimitive(providerId),
            "text" to JsonPrimitive(text),
        )
        transport.sendTextAsync(json)
    }

    override suspend fun sendToolResultAsync(callId: String, resultJson: String) {
        val json = buildJson(
            "type" to JsonPrimitive("tool.result"),
            "provider" to JsonPrimitive(providerId),
            "call_id" to JsonPrimitive(callId),
            "result_json" to JsonPrimitive(resultJson),
        )
        transport.sendTextAsync(json)
    }

    override suspend fun cancelResponseAsync() {
        val json = buildJson(
            "type" to JsonPrimitive("response.cancel"),
            "provider" to JsonPrimitive(providerId),
        )
        transport.sendTextAsync(json)
    }

    override fun receiveEventsAsync(): Flow<RealtimeEvent> = flow {
        transport.receiveTextAsync().collect { text ->
            val ev = try {
                parseEvent(text)
            } catch (_: Exception) {
                // Could not parse vendor frame; skip (C# LogDebug + skip).
                null
            }
            if (ev != null) emit(ev)
        }
    }

    override suspend fun disposeAsync() {
        try {
            transport.closeAsync()
        } catch (_: Exception) {
            // ignore
        }
        transport.disposeAsync()
    }

    companion object {
        /** Lenient cross-vendor JSON event parser. Mirrors C# `RealtimeWebSocketSession.ParseEvent`. */
        fun parseEvent(json: String): RealtimeEvent? {
            if (json.isBlank()) return null
            val root = REALTIME_JSON.parseToJsonElement(json) as? JsonObject ?: return null
            val at = Instant.now()

            // OpenAI Realtime uses "type" = "input_audio_buffer.speech_started" etc.
            val typeProp = root["type"]?.jsonPrimitive
            if (typeProp != null && typeProp.isString) {
                val type = typeProp.contentOrNull ?: ""
                return when (type) {
                    "input_audio_buffer.speech_started", "speech_started" -> SpeechStartedEvent(at)
                    "input_audio_buffer.speech_stopped", "speech_stopped" -> SpeechEndedEvent(at)

                    "conversation.item.input_audio_transcription.delta", "transcript.delta" ->
                        TranscriptDeltaEvent(at, str(root, "delta"), RealtimeDirection.Inbound)

                    "conversation.item.input_audio_transcription.completed", "transcript.final" ->
                        TranscriptFinalEvent(
                            at,
                            strOrNull(root, "transcript") ?: str(root, "text"),
                            RealtimeDirection.Inbound,
                        )

                    "response.audio_transcript.delta" ->
                        TranscriptDeltaEvent(at, str(root, "delta"), RealtimeDirection.Outbound)

                    "response.audio_transcript.done" ->
                        TranscriptFinalEvent(at, str(root, "transcript"), RealtimeDirection.Outbound)

                    "response.function_call_arguments.done", "tool.call" ->
                        ToolCallEvent(
                            at,
                            str(root, "call_id"),
                            str(root, "name"),
                            root["arguments"]?.toString() ?: "{}",
                        )

                    "response.done", "turn.complete" -> TurnCompleteEvent(at)

                    "error" -> SessionErrorEvent(
                        at,
                        (root["error"] as? JsonObject)?.get("message")?.jsonPrimitive?.contentOrNull ?: json,
                    )

                    else -> null
                }
            }

            // Gemini Live emits { serverContent: { modelTurn: { parts: [{ text: "..." }] } } }
            val sc = root["serverContent"] as? JsonObject
            if (sc != null) {
                val tc = sc["turnComplete"]?.jsonPrimitive
                if (tc != null && tc.contentOrNull == "true") {
                    return TurnCompleteEvent(at)
                }
                val mt = sc["modelTurn"] as? JsonObject
                val parts = mt?.get("parts") as? JsonArray
                if (parts != null) {
                    for (part in parts) {
                        val text = (part as? JsonObject)?.get("text")?.jsonPrimitive?.contentOrNull
                        if (text != null) {
                            return TranscriptDeltaEvent(at, text, RealtimeDirection.Outbound)
                        }
                    }
                }
            }

            return null
        }

        private fun str(root: JsonObject, key: String): String =
            root[key]?.jsonPrimitive?.contentOrNull ?: ""

        private fun strOrNull(root: JsonObject, key: String): String? =
            root[key]?.jsonPrimitive?.contentOrNull
    }
}

// =====================================================================
// Vendor services
// =====================================================================

/** (3.3.0) [IRealtimeService] backed by OpenAI Realtime. Mirrors C# `OpenAiRealtimeService`. */
class OpenAiRealtimeService(
    private val options: OpenAiRealtimeOptions,
    private val transports: IRealtimeTransportFactory = NullRealtimeTransportFactory.Instance,
) : IRealtimeService {

    override val providerId: String get() = "openai-realtime"
    override val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank()

    override suspend fun startSessionAsync(config: RealtimeSessionConfig): IRealtimeSession {
        ensureConfigured()
        val modelToUse = config.model.ifBlank { options.defaultModel }
        val endpoint = URI("${options.webSocketEndpoint}?model=${urlEncode(modelToUse)}")
        val headers = mapOf(
            "Authorization" to "Bearer ${options.apiKey}",
            "OpenAI-Beta" to options.betaHeader,
        )
        val transport = transports.connectAsync(endpoint, headers)
        return RealtimeWebSocketSession(transport, config, providerId)
    }

    private fun ensureConfigured() {
        check(isConfigured) {
            "OpenAI Realtime is not configured. Set OpenAiRealtimeOptions.apiKey before calling startSessionAsync."
        }
    }
}

/** (3.3.0) [IRealtimeService] backed by Gemini Live. Mirrors C# `GeminiLiveService`. */
class GeminiLiveService(
    private val options: GeminiLiveOptions,
    private val transports: IRealtimeTransportFactory = NullRealtimeTransportFactory.Instance,
) : IRealtimeService {

    override val providerId: String get() = "gemini-live"
    override val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank()

    override suspend fun startSessionAsync(config: RealtimeSessionConfig): IRealtimeSession {
        ensureConfigured()
        val endpoint = URI("${options.webSocketEndpoint}?key=${urlEncode(options.apiKey!!)}")
        val transport = transports.connectAsync(endpoint, headers = null)
        return RealtimeWebSocketSession(transport, config, providerId)
    }

    private fun ensureConfigured() {
        check(isConfigured) {
            "Gemini Live is not configured. Set GeminiLiveOptions.apiKey before calling startSessionAsync."
        }
    }
}

/** (3.3.0) [IRealtimeService] backed by ElevenLabs Conversational AI. Mirrors C# `ElevenLabsConvService`. */
class ElevenLabsConvService(
    private val options: ElevenLabsConvOptions,
    private val transports: IRealtimeTransportFactory = NullRealtimeTransportFactory.Instance,
) : IRealtimeService {

    override val providerId: String get() = "elevenlabs-conv"
    override val isConfigured: Boolean
        get() = !options.apiKey.isNullOrBlank() && !options.agentId.isNullOrBlank()

    override suspend fun startSessionAsync(config: RealtimeSessionConfig): IRealtimeSession {
        ensureConfigured()
        val endpoint = URI("${options.webSocketEndpoint}?agent_id=${urlEncode(options.agentId!!)}")
        val headers = mapOf("xi-api-key" to options.apiKey!!)
        val transport = transports.connectAsync(endpoint, headers)
        return RealtimeWebSocketSession(transport, config, providerId)
    }

    private fun ensureConfigured() {
        check(isConfigured) {
            "ElevenLabs Conversational AI is not configured. Set ElevenLabsConvOptions.apiKey AND agentId before calling startSessionAsync."
        }
    }
}

/** (3.3.0) [IRealtimeService] backed by AWS Nova Sonic. Mirrors C# `NovaSonicService`. */
class NovaSonicService(
    private val options: NovaSonicOptions,
    private val transports: IRealtimeTransportFactory = NullRealtimeTransportFactory.Instance,
) : IRealtimeService {

    override val providerId: String get() = "aws-nova-sonic"
    override val isConfigured: Boolean
        get() = !options.accessKeyId.isNullOrBlank() && !options.secretAccessKey.isNullOrBlank()

    override suspend fun startSessionAsync(config: RealtimeSessionConfig): IRealtimeSession {
        ensureConfigured()
        val endpoint = URI(
            "wss://bedrock-runtime.${options.region}.amazonaws.com/model/${urlEncode(config.model)}/invoke-with-bidirectional-stream",
        )
        // Expose the credentials via headers; the host's transport factory is
        // responsible for SigV4-signing the request.
        val headers = LinkedHashMap<String, String>()
        headers["X-Amz-Access-Key"] = options.accessKeyId!!
        headers["X-Amz-Secret-Key"] = options.secretAccessKey!!
        headers["X-Amz-Region"] = options.region
        if (!options.sessionToken.isNullOrBlank()) {
            headers["X-Amz-Security-Token"] = options.sessionToken
        }
        val transport = transports.connectAsync(endpoint, headers)
        return RealtimeWebSocketSession(transport, config, providerId)
    }

    private fun ensureConfigured() {
        check(isConfigured) {
            "AWS Nova Sonic is not configured. Set NovaSonicOptions.accessKeyId and secretAccessKey before calling startSessionAsync."
        }
    }
}

/**
 * (3.3.0) [IRealtimeService] backed by Ultravox. Two-step: POST /api/calls to
 * create a call → returns joinUrl → open WS to joinUrl. The HTTP step goes
 * through the injected [IRealtimeHttpTransport] seam. Mirrors C# `UltravoxService`.
 */
class UltravoxService(
    private val http: IRealtimeHttpTransport,
    private val options: UltravoxOptions,
    private val transports: IRealtimeTransportFactory = NullRealtimeTransportFactory.Instance,
) : IRealtimeService {

    override val providerId: String get() = "ultravox"
    override val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank()

    override suspend fun startSessionAsync(config: RealtimeSessionConfig): IRealtimeSession {
        ensureConfigured()

        val modelToUse = config.model.ifBlank { options.defaultModel }
        val voiceToUse = (config.voiceId ?: "").ifBlank { options.defaultVoice }

        val body = buildJson(
            "model" to JsonPrimitive(modelToUse),
            "voice" to JsonPrimitive(voiceToUse),
            "systemPrompt" to JsonPrimitive(config.systemPrompt ?: ""),
            "medium" to JsonObject(
                linkedMapOf(
                    "serverWebSocket" to JsonObject(
                        linkedMapOf(
                            "inputSampleRate" to JsonPrimitive(16000),
                            "outputSampleRate" to JsonPrimitive(24000),
                        ),
                    ),
                ),
            ),
        )

        val resp = http.postJson(
            options.apiEndpoint,
            "/api/calls",
            mapOf("X-API-Key" to options.apiKey!!),
            body,
        )
        check(resp.isSuccess) { "Ultravox call creation failed with HTTP ${resp.statusCode}." }

        val joinUrl = (REALTIME_JSON.parseToJsonElement(resp.body) as? JsonObject)
            ?.get("joinUrl")?.jsonPrimitive?.contentOrNull
        checkNotNull(joinUrl?.takeIf { it.isNotBlank() }) { "Ultravox API did not return a joinUrl." }

        val transport = transports.connectAsync(URI(joinUrl), headers = null)
        return RealtimeWebSocketSession(transport, config, providerId)
    }

    private fun ensureConfigured() {
        check(isConfigured) {
            "Ultravox is not configured. Set UltravoxOptions.apiKey before calling startSessionAsync."
        }
    }
}

// =====================================================================
// JSON helper
// =====================================================================

private fun buildJson(vararg pairs: Pair<String, kotlinx.serialization.json.JsonElement>): String =
    JsonObject(linkedMapOf(*pairs)).toString()
