// Endpoints.kt
//
// Kotlin port of the CircleAI.Hosting transport surface — the C# reference is the
// EXACT spec (IAIEndpoint.cs, Endpoints/InProcessEndpoint.cs,
// Endpoints/HttpLoopbackEndpoint.cs, Endpoints/AIHttpClient.cs, AIApiClient.cs).
//
// InProcessEndpoint holds the service directly. HttpLoopbackEndpoint is a tiny
// loopback HTTP server (C# uses System.Net.HttpListener; here the JDK
// com.sun.net.httpserver.HttpServer, bound only to 127.0.0.1). AIHttpClient +
// AIApiClient are the out-of-process clients, using java.net.http.HttpClient.
// Routes, the X-Butler-Token shared-secret auth, and the SSE framing are
// byte-identical.

package com.bhengubv.circleai.hosting

import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.models.ChatMessage
import com.bhengubv.circleai.tools.ToolInvocation
import com.bhengubv.circleai.tools.ToolResult
import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.floatOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.put
import java.io.OutputStream
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.URI
import java.net.http.HttpClient
import java.net.http.HttpRequest
import java.net.http.HttpResponse
import java.nio.charset.StandardCharsets
import java.time.Duration
import java.util.UUID
import java.util.concurrent.Executors

// =====================================================================
// IAIEndpoint (IAIEndpoint.cs)
// =====================================================================

/**
 * Transport-agnostic endpoint that exposes an [IAIService]. Mirrors C#
 * `IAIEndpoint` (which is `IAsyncDisposable` → [AutoCloseable] + suspend
 * [disposeAsync] here).
 */
interface IAIEndpoint {
    /** Begins serving requests against [service]. Idempotent. */
    suspend fun startAsync(service: IAIService)

    /** Stops accepting new requests and waits for in-flight ones to drain. */
    suspend fun stopAsync()

    /** Releases all resources. */
    suspend fun disposeAsync()
}

// =====================================================================
// InProcessEndpoint (InProcessEndpoint.cs)
// =====================================================================

/**
 * In-process endpoint. No transport — just exposes the underlying [IAIService]
 * directly via [serviceAccessor]. Mirrors C# `InProcessEndpoint`.
 */
class InProcessEndpoint : IAIEndpoint {
    private var service: IAIService? = null
    private var started = false
    private var disposed = false

    /** The wrapped service. null until [startAsync] has run. */
    val serviceAccessor: IAIService? get() = service

    override suspend fun startAsync(service: IAIService) {
        check(!disposed) { "InProcessEndpoint is disposed." }
        if (started) return
        this.service = service
        started = true
    }

    override suspend fun stopAsync() {
        started = false
        service = null
    }

    override suspend fun disposeAsync() {
        if (disposed) return
        disposed = true
        service = null
        started = false
    }
}

// =====================================================================
// HttpLoopbackEndpoint (HttpLoopbackEndpoint.cs)
// =====================================================================

/**
 * Loopback HTTP transport for [IAIService]. Binds only to 127.0.0.1 so Butler is
 * never exposed on the network. Mirrors C# `HttpLoopbackEndpoint`.
 *
 * Routes:
 *  - POST /butler/ask    -> { "question": string }                  -> text/plain
 *  - POST /butler/chat   -> { "messages": [...], "options": {...} }  -> { "content": string }
 *  - POST /butler/stream -> { "messages": [...], "options": {...} }  -> text/event-stream
 *  - POST /butler/tool   -> { "toolName": string, "arguments": {} }  -> ToolResult JSON
 *
 * Auth is the shared-secret header `X-Butler-Token`.
 */
class HttpLoopbackEndpoint(
    private val options: AIOptions,
) : IAIEndpoint {

    private var server: HttpServer? = null
    private var service: IAIService? = null
    private var token: String? = null
    private var boundPort: Int = 0
    private var started = false
    private var disposed = false

    /** Port the listener is currently bound to. 0 when not started. */
    val port: Int get() = boundPort

    /** Effective shared-secret token. null when not started. */
    val effectiveToken: String? get() = token

    override suspend fun startAsync(service: IAIService) {
        check(!disposed) { "HttpLoopbackEndpoint is disposed." }
        if (started) return

        this.service = service
        token = if (options.loopbackToken.isNullOrEmpty()) AIOptions.generateRandomToken() else options.loopbackToken

        val configuredPort = options.loopbackPort
        val port = if (configuredPort > 0) configuredPort else pickFreeLoopbackPort()
        boundPort = port

        val srv = HttpServer.create(InetSocketAddress(InetAddress.getByName("127.0.0.1"), port), 0)
        srv.executor = Executors.newCachedThreadPool()
        srv.createContext("/butler/") { exchange -> handleRequest(exchange) }
        srv.start()
        server = srv
        started = true
    }

    override suspend fun stopAsync() {
        if (!started) return
        started = false
        server?.stop(0)
        server = null
        service = null
    }

    override suspend fun disposeAsync() {
        if (disposed) return
        disposed = true
        runCatching { stopAsync() }
    }

    // ── Request handling ──────────────────────────────────────────────────────

    private fun handleRequest(exchange: HttpExchange) {
        try {
            if (!authorise(exchange)) {
                writePlain(exchange, 401, "unauthorised")
                return
            }
            if (!exchange.requestMethod.equals("POST", ignoreCase = true)) {
                writePlain(exchange, 405, "method not allowed")
                return
            }
            when (exchange.requestURI.path) {
                "/butler/ask" -> handleAsk(exchange)
                "/butler/chat" -> handleChat(exchange)
                "/butler/stream" -> handleStream(exchange)
                "/butler/tool" -> handleTool(exchange)
                else -> writePlain(exchange, 404, "not found")
            }
        } catch (_: Exception) {
            runCatching { writePlain(exchange, 500, "internal error") }
        } finally {
            exchange.close()
        }
    }

    private fun handleAsk(exchange: HttpExchange) {
        val svc = requireService()
        val body = readBody(exchange)
        val question = (parseObject(body)?.get("question") as? JsonPrimitive)?.contentOrNull
        if (question.isNullOrBlank()) {
            writePlain(exchange, 400, "missing 'question'")
            return
        }
        val answer = runBlocking { svc.askAsync(question) }
        writePlain(exchange, 200, answer)
    }

    private fun handleChat(exchange: HttpExchange) {
        val svc = requireService()
        val body = readBody(exchange)
        val payload = parseObject(body)
        val messages = parseMessages(payload)
        if (messages.isEmpty()) {
            writePlain(exchange, 400, "missing 'messages'")
            return
        }
        val genOptions = parseGenerationOptions(payload?.get("options") as? JsonObject)
        val content = runBlocking { svc.chatAsync(messages, genOptions) }
        writeJson(exchange, 200, buildJsonObject { put("content", content) })
    }

    private fun handleStream(exchange: HttpExchange) {
        val svc = requireService()
        val body = readBody(exchange)
        val payload = parseObject(body)
        val messages = parseMessages(payload)
        if (messages.isEmpty()) {
            writePlain(exchange, 400, "missing 'messages'")
            return
        }
        val genOptions = parseGenerationOptions(payload?.get("options") as? JsonObject)

        exchange.responseHeaders.add("Content-Type", "text/event-stream")
        exchange.responseHeaders.add("Cache-Control", "no-cache")
        exchange.responseHeaders.add("X-Accel-Buffering", "no")
        exchange.sendResponseHeaders(200, 0) // chunked

        val os = exchange.responseBody
        try {
            runBlocking {
                svc.streamAsync(messages, genOptions).collect { piece ->
                    // SSE framing; JSON-encode the payload so newlines/quotes are safe.
                    os.write("data: ".toByteArray(StandardCharsets.UTF_8))
                    os.write((JSON.encodeToString(JsonPrimitive.serializer(), JsonPrimitive(piece)) + "\n").toByteArray(StandardCharsets.UTF_8))
                    os.write("\n".toByteArray(StandardCharsets.UTF_8))
                    os.flush()
                }
            }
            os.write("event: done\n".toByteArray(StandardCharsets.UTF_8))
            os.write("data: {}\n\n".toByteArray(StandardCharsets.UTF_8))
            os.flush()
        } finally {
            runCatching { os.close() }
        }
    }

    private fun handleTool(exchange: HttpExchange) {
        val svc = requireService()
        val body = readBody(exchange)
        val payload = parseObject(body)
        val toolName = (payload?.get("toolName") as? JsonPrimitive)?.contentOrNull
        if (toolName.isNullOrBlank()) {
            writePlain(exchange, 400, "missing 'toolName'")
            return
        }
        val args = LinkedHashMap<String, Any?>()
        (payload["arguments"] as? JsonObject)?.forEach { (k, v) ->
            args[k] = (v as? JsonPrimitive)?.contentOrNull
        }
        val invocation = ToolInvocation(toolName = toolName, arguments = args)
        val result = runBlocking { svc.invokeToolAsync(invocation) }
        writeJson(exchange, if (result.success) 200 else 502, toolResultJson(result))
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private fun authorise(exchange: HttpExchange): Boolean {
        val expected = token ?: return false
        val supplied = exchange.requestHeaders.getFirst("X-Butler-Token") ?: return false
        return cryptographicEquals(supplied, expected)
    }

    private fun requireService(): IAIService =
        service ?: throw IllegalStateException("HttpLoopbackEndpoint has no service bound.")

    private fun readBody(exchange: HttpExchange): String =
        exchange.requestBody.readBytes().toString(StandardCharsets.UTF_8)

    private fun writePlain(exchange: HttpExchange, status: Int, text: String) {
        val bytes = text.toByteArray(StandardCharsets.UTF_8)
        exchange.responseHeaders.add("Content-Type", "text/plain; charset=utf-8")
        exchange.sendResponseHeaders(status, bytes.size.toLong())
        exchange.responseBody.use { it.write(bytes) }
    }

    private fun writeJson(exchange: HttpExchange, status: Int, payload: JsonElement) {
        val bytes = JSON.encodeToString(JsonElement.serializer(), payload).toByteArray(StandardCharsets.UTF_8)
        exchange.responseHeaders.add("Content-Type", "application/json; charset=utf-8")
        exchange.sendResponseHeaders(status, bytes.size.toLong())
        exchange.responseBody.use { it.write(bytes) }
    }

    companion object {
        private val JSON = Json { encodeDefaults = true }

        internal fun cryptographicEquals(a: String, b: String): Boolean {
            if (a.length != b.length) return false
            var diff = 0
            for (i in a.indices) diff = diff or (a[i].code xor b[i].code)
            return diff == 0
        }

        internal fun pickFreeLoopbackPort(): Int {
            ServerSocket(0, 0, InetAddress.getByName("127.0.0.1")).use { return it.localPort }
        }

        internal fun parseObject(body: String): JsonObject? {
            if (body.isBlank()) return null
            return try {
                Json.parseToJsonElement(body) as? JsonObject
            } catch (_: Exception) {
                null
            }
        }

        internal fun parseMessages(payload: JsonObject?): List<ChatMessage> {
            val arr = payload?.get("messages") as? kotlinx.serialization.json.JsonArray ?: return emptyList()
            return arr.mapNotNull { el ->
                val obj = el as? JsonObject ?: return@mapNotNull null
                val role = (obj["role"] as? JsonPrimitive)?.contentOrNull ?: "user"
                val content = (obj["content"] as? JsonPrimitive)?.contentOrNull ?: ""
                ChatMessage(id = UUID.randomUUID().toString(), role = role, content = content)
            }
        }

        internal fun parseGenerationOptions(obj: JsonObject?): GenerationOptions? {
            if (obj == null) return null
            val defaults = GenerationOptions()
            return GenerationOptions(
                maxTokens = (obj["maxTokens"] as? JsonPrimitive)?.intOrNull ?: defaults.maxTokens,
                temperature = (obj["temperature"] as? JsonPrimitive)?.floatOrNull ?: defaults.temperature,
                topP = (obj["topP"] as? JsonPrimitive)?.floatOrNull ?: defaults.topP,
                topK = (obj["topK"] as? JsonPrimitive)?.intOrNull ?: defaults.topK,
                seed = (obj["seed"] as? JsonPrimitive)?.intOrNull,
                stopSequences = (obj["stopSequences"] as? kotlinx.serialization.json.JsonArray)
                    ?.mapNotNull { (it as? JsonPrimitive)?.contentOrNull } ?: emptyList(),
            )
        }

        internal fun toolResultJson(result: ToolResult): JsonObject = buildJsonObject {
            put("toolName", result.toolName)
            put("success", result.success)
            put("result", result.result?.toString())
            put("error", result.error)
        }
    }
}

// =====================================================================
// AIHttpClient (Endpoints/AIHttpClient.cs)
// =====================================================================

/**
 * HTTP client that talks to a [HttpLoopbackEndpoint]. Methods mirror [IAIService]
 * so the same call sites work in-process or out-of-process. Mirrors C#
 * `AIHttpClient`.
 */
class AIHttpClient private constructor(
    private val baseUri: URI,
    private val token: String,
    private val http: HttpClient,
) : AutoCloseable {

    /**
     * Connects to a loopback Butler endpoint at `http://127.0.0.1:{port}/`.
     * Mirrors C# `AIHttpClient(int port, string token)`.
     */
    constructor(port: Int, token: String) : this(
        loopbackUri(port, token),
        token,
        HttpClient.newBuilder().connectTimeout(Duration.ofMinutes(5)).build(),
    )

    /** Mirrors [IAIService.askAsync]. */
    suspend fun askAsync(question: String): String {
        require(question.isNotEmpty()) { "question is required" }
        val body = buildJsonObject { put("question", question) }
        val response = post("butler/ask", body)
        checkSuccess(response)
        return response.body()
    }

    /** Mirrors [IAIService.chatAsync]. */
    suspend fun chatAsync(messages: List<ChatMessage>, options: GenerationOptions? = null): String {
        val payload = chatPayload(messages, options)
        val response = post("butler/chat", payload)
        checkSuccess(response)
        val obj = JSON.parseToJsonElement(response.body()) as? JsonObject
        return (obj?.get("content") as? JsonPrimitive)?.contentOrNull ?: ""
    }

    /** Mirrors [IAIService.streamAsync]. Parses the SSE `data:`/`event: done` framing. */
    fun streamAsync(messages: List<ChatMessage>, options: GenerationOptions? = null): Flow<String> = flow {
        val payload = chatPayload(messages, options)
        val request = HttpRequest.newBuilder(baseUri.resolve("butler/stream"))
            .header("X-Butler-Token", token)
            .header("Content-Type", "application/json")
            .POST(HttpRequest.BodyPublishers.ofString(JSON.encodeToString(JsonElement.serializer(), payload)))
            .build()

        val response = http.send(request, HttpResponse.BodyHandlers.ofLines())
        if (response.statusCode() !in 200..299) throw IllegalStateException("stream failed: ${response.statusCode()}")

        val iterator = response.body().iterator()
        while (iterator.hasNext()) {
            val line = iterator.next()
            if (line.isEmpty()) continue
            if (line.startsWith("event:")) {
                if (line.substring(6).trim() == "done") return@flow
                continue
            }
            if (!line.startsWith("data:")) continue
            val dataPart = line.substring(5).trimStart()
            if (dataPart.isEmpty()) continue
            val piece = try {
                (JSON.parseToJsonElement(dataPart) as? JsonPrimitive)?.contentOrNull ?: dataPart
            } catch (_: Exception) {
                dataPart
            }
            if (piece.isNotEmpty()) emit(piece)
        }
    }

    /** Mirrors [IAIService.invokeToolAsync]. */
    suspend fun invokeToolAsync(invocation: ToolInvocation): ToolResult {
        val payload = buildJsonObject {
            put("toolName", invocation.toolName)
            put("arguments", buildJsonObject {
                for ((k, v) in invocation.arguments) put(k, v?.toString())
            })
        }
        val response = post("butler/tool", payload)
        // Accept both 200 (success) and 502 (tool failure) — the body is a ToolResult either way.
        if (response.statusCode() != 200 && response.statusCode() != 502) checkSuccess(response)
        val obj = JSON.parseToJsonElement(response.body()) as? JsonObject
            ?: return ToolResult(invocation.toolName, false, error = "Empty response from Butler endpoint.")
        return ToolResult(
            toolName = (obj["toolName"] as? JsonPrimitive)?.contentOrNull ?: invocation.toolName,
            success = (obj["success"] as? JsonPrimitive)?.booleanOrNull ?: false,
            result = (obj["result"] as? JsonPrimitive)?.contentOrNull,
            error = (obj["error"] as? JsonPrimitive)?.contentOrNull,
        )
    }

    override fun close() { /* java.net.http.HttpClient needs no explicit close */ }

    private fun chatPayload(messages: List<ChatMessage>, options: GenerationOptions?): JsonObject =
        buildJsonObject {
            put("messages", buildJsonArray {
                for (m in messages) add(buildJsonObject {
                    put("role", m.role)
                    put("content", m.content)
                })
            })
            if (options != null) {
                put("options", buildJsonObject {
                    put("maxTokens", options.maxTokens)
                    put("temperature", options.temperature)
                    put("topP", options.topP)
                    put("topK", options.topK)
                    options.seed?.let { put("seed", it) }
                    put("stopSequences", buildJsonArray { for (s in options.stopSequences) add(JsonPrimitive(s)) })
                })
            }
        }

    private fun post(path: String, body: JsonObject): HttpResponse<String> {
        val request = HttpRequest.newBuilder(baseUri.resolve(path))
            .header("X-Butler-Token", token)
            .header("Content-Type", "application/json")
            .POST(HttpRequest.BodyPublishers.ofString(JSON.encodeToString(JsonElement.serializer(), body)))
            .build()
        return http.send(request, HttpResponse.BodyHandlers.ofString())
    }

    private fun checkSuccess(response: HttpResponse<String>) {
        if (response.statusCode() !in 200..299) {
            throw IllegalStateException("request failed: ${response.statusCode()}")
        }
    }

    private companion object {
        val JSON = Json { encodeDefaults = true }

        fun loopbackUri(port: Int, token: String): URI {
            require(port > 0) { "port must be positive" }
            require(token.isNotEmpty()) { "token is required" }
            return URI.create("http://127.0.0.1:$port/")
        }
    }
}

// =====================================================================
// AIApiClient (AIApiClient.cs)
// =====================================================================

/**
 * [IAIService] that proxies requests to a remote ButlerAPI over HTTP/JSON.
 * Streaming responses use Server-Sent Events. Mirrors C# `AIApiClient`.
 * `IsReady` becomes true once a `/api/butler/health` check succeeds.
 */
class AIApiClient(
    private val endpoint: URI,
    bearerToken: String? = null,
    private val http: HttpClient = HttpClient.newBuilder().connectTimeout(Duration.ofMinutes(5)).build(),
) : IAIService {

    private val bearer: String? = bearerToken?.takeIf { it.isNotBlank() }
    private var ready = false
    private var disposed = false

    override val isReady: Boolean get() = ready

    override suspend fun startAsync() {
        val request = baseGet("api/butler/health")
        val resp = http.send(request, HttpResponse.BodyHandlers.discarding())
        if (resp.statusCode() !in 200..299) throw IllegalStateException("health check failed: ${resp.statusCode()}")
        ready = true
    }

    override suspend fun stopAsync() {
        ready = false
    }

    override suspend fun askAsync(question: String): String {
        val resp = post("api/butler/ask", buildJsonObject { put("question", question) })
        return (parseObject(resp)?.get("text") as? JsonPrimitive)?.contentOrNull ?: ""
    }

    override suspend fun chatAsync(messages: List<ChatMessage>, options: GenerationOptions?): String {
        val resp = post("api/butler/chat", chatRequest(messages, options))
        return (parseObject(resp)?.get("text") as? JsonPrimitive)?.contentOrNull ?: ""
    }

    override fun streamAsync(messages: List<ChatMessage>, options: GenerationOptions?): Flow<String> = flow {
        val request = HttpRequest.newBuilder(endpoint.resolve("api/butler/stream"))
            .apply { if (bearer != null) header("Authorization", "Bearer $bearer") }
            .header("Content-Type", "application/json")
            .header("Accept", "text/event-stream")
            .POST(HttpRequest.BodyPublishers.ofString(JSON.encodeToString(JsonElement.serializer(), chatRequest(messages, options))))
            .build()
        val response = http.send(request, HttpResponse.BodyHandlers.ofLines())
        if (response.statusCode() !in 200..299) throw IllegalStateException("stream failed: ${response.statusCode()}")
        val iterator = response.body().iterator()
        while (iterator.hasNext()) {
            val line = iterator.next()
            if (!line.startsWith("data:")) continue
            val tokenPart = line.substring("data:".length).trim()
            if (tokenPart == "[DONE]") return@flow
            if (tokenPart.isNotEmpty()) emit(tokenPart)
        }
    }

    override suspend fun agenticChatAsync(prompt: String, options: GenerationOptions?): String {
        val resp = post("api/butler/agentic", buildJsonObject {
            put("prompt", prompt)
            options?.let { put("options", generationOptionsJson(it)) }
        })
        return (parseObject(resp)?.get("text") as? JsonPrimitive)?.contentOrNull ?: ""
    }

    override suspend fun invokeToolAsync(invocation: ToolInvocation): ToolResult {
        val resp = post("api/butler/tool", buildJsonObject {
            put("name", invocation.toolName)
            put("arguments", buildJsonObject { for ((k, v) in invocation.arguments) put(k, v?.toString()) })
        })
        val obj = parseObject(resp) ?: return ToolResult.failure(invocation.toolName, "Empty response from cloud")
        return ToolResult(
            toolName = (obj["toolName"] as? JsonPrimitive)?.contentOrNull ?: invocation.toolName,
            success = (obj["success"] as? JsonPrimitive)?.booleanOrNull ?: false,
            result = (obj["result"] as? JsonPrimitive)?.contentOrNull,
            error = (obj["error"] as? JsonPrimitive)?.contentOrNull,
        )
    }

    override suspend fun submitFeedbackAsync(signal: com.bhengubv.circleai.memory.FeedbackSignal) {
        post("api/butler/feedback", buildJsonObject {
            put("id", signal.id)
            put("polarity", signal.polarity.name)
            put("userText", signal.turnId)
            put("comment", signal.note)
        })
    }

    override suspend fun disposeAsync() {
        disposed = true
    }

    // ── Helpers ──

    private fun baseGet(path: String): HttpRequest =
        HttpRequest.newBuilder(endpoint.resolve(path))
            .apply { if (bearer != null) header("Authorization", "Bearer $bearer") }
            .GET().build()

    private fun post(path: String, body: JsonObject): String {
        val request = HttpRequest.newBuilder(endpoint.resolve(path))
            .apply { if (bearer != null) header("Authorization", "Bearer $bearer") }
            .header("Content-Type", "application/json")
            .POST(HttpRequest.BodyPublishers.ofString(JSON.encodeToString(JsonElement.serializer(), body)))
            .build()
        val resp = http.send(request, HttpResponse.BodyHandlers.ofString())
        if (resp.statusCode() !in 200..299) throw IllegalStateException("request failed: ${resp.statusCode()}")
        return resp.body()
    }

    private fun chatRequest(messages: List<ChatMessage>, options: GenerationOptions?): JsonObject =
        buildJsonObject {
            put("messages", buildJsonArray {
                for (m in messages) add(buildJsonObject {
                    put("role", m.role)
                    put("content", m.content)
                })
            })
            options?.let { put("options", generationOptionsJson(it)) }
        }

    private fun generationOptionsJson(o: GenerationOptions): JsonObject = buildJsonObject {
        put("maxTokens", o.maxTokens)
        put("temperature", o.temperature)
        put("topP", o.topP)
        put("topK", o.topK)
        o.seed?.let { put("seed", it) }
    }

    private fun parseObject(body: String): JsonObject? =
        try {
            JSON.parseToJsonElement(body) as? JsonObject
        } catch (_: Exception) {
            null
        }

    private companion object {
        val JSON = Json { encodeDefaults = true }
    }
}
