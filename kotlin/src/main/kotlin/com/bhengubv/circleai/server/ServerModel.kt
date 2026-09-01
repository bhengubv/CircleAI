// ServerModel.kt
//
// The inference server's data: what /v1/diagnostics answers, and how a streamed
// chunk is framed.
//
// The ENDPOINTS are ASP.NET minimal APIs and do not cross; these do, because
// they are the contract a client is written against. A JVM client talking to the
// server, or a JVM host answering the same shapes, needs exactly this and
// nothing of ASP.NET.
//
// THE JSON KEYS ARE THE WIRE FORMAT — snake_case, because that is what the
// server sends and what every existing client parses. Renaming one to suit
// Kotlin conventions breaks those clients silently: the field simply arrives
// null.
//
// Ported from src/CircleAI.Inference.Server/{Models/Diagnostics/DiagnosticsDtos,
// Streaming/ServerSentEventsWriter}.cs.

package com.bhengubv.circleai.server

import java.time.Instant

/** Always "model" for `object` — the OpenAI-shaped clients expect the field. */
data class LoadedModelInfo(
    val id: String,
    val objectType: String = "model",
    val ownedBy: String = "circleai",
    val supportsStreaming: Boolean = true
) {
    fun toJsonPairs(): List<Pair<String, Any?>> = listOf(
        "id" to id,
        "object" to objectType,
        "owned_by" to ownedBy,
        "supports_streaming" to supportsStreaming
    )
}

data class HostProfileDto(
    val os: String = "",
    val osVersion: String = "",
    val arch: String = "",
    val cpuModel: String = "",
    val logicalCores: Int = 0,
    val physicalCores: Int = 0,
    val ramBytes: Long = 0,
    val gpuVendor: String? = null,
    val gpuModel: String? = null,
    val gpuVramBytes: Long? = null,
    val npuVendor: String? = null,
    val npuModel: String? = null
) {
    fun toJsonPairs(): List<Pair<String, Any?>> = listOf(
        "os" to os,
        "os_version" to osVersion,
        "arch" to arch,
        "cpu_model" to cpuModel,
        "logical_cores" to logicalCores,
        "physical_cores" to physicalCores,
        "ram_bytes" to ramBytes,
        "gpu_vendor" to gpuVendor,
        "gpu_model" to gpuModel,
        "gpu_vram_bytes" to gpuVramBytes,
        "npu_vendor" to npuVendor,
        "npu_model" to npuModel
    )
}

data class BackendSelectionDto(
    val backend: String = "",
    val tier: String = "",
    /** WHY that backend was chosen, in words. The most useful field on this
     *  endpoint: "which backend" without "why" turns every performance question
     *  into a guess. */
    val rationale: String = ""
) {
    fun toJsonPairs(): List<Pair<String, Any?>> =
        listOf("backend" to backend, "tier" to tier, "rationale" to rationale)
}

data class CounterSnapshot(
    val totalRequests: Long = 0,
    val activeRequests: Int = 0,
    /**
     * Turned away at the door (over the concurrency cap). Counted separately
     * from failures on purpose: rejected means the server is HEALTHY and busy,
     * failed means it is not, and one number for both hides which.
     */
    val rejectedRequests: Long = 0,
    val failedRequests: Long = 0
) {
    fun toJsonPairs(): List<Pair<String, Any?>> = listOf(
        "total_requests" to totalRequests,
        "active_requests" to activeRequests,
        "rejected_requests" to rejectedRequests,
        "failed_requests" to failedRequests
    )
}

data class NativeRuntimePathsDto(
    val rid: String = "",
    val expectedNativeDir: String = "",
    val mnnBridgePath: String = "",
    val mnnBridgeLoaded: Boolean = false,
    val mnnCoreFetchedPath: String = "",
    val mnnCoreFlattenedPath: String = "",
    val mnnCorePreloaded: Boolean = false,
    /** Separate nullable fields rather than one "error": a runtime that
     *  flattened and failed to preload is a different problem from one that
     *  never unpacked, and collapsing them loses which stage broke. */
    val flattenError: String? = null,
    val preloadError: String? = null
) {
    fun toJsonPairs(): List<Pair<String, Any?>> = listOf(
        "rid" to rid,
        "expected_native_dir" to expectedNativeDir,
        // "mnnbridge_path", not "mnn_bridge_path": it is what the server sends,
        // and exactly the kind of thing a tidy-up changes silently.
        "mnnbridge_path" to mnnBridgePath,
        "mnnbridge_loaded" to mnnBridgeLoaded,
        "mnn_core_fetched_path" to mnnCoreFetchedPath,
        "mnn_core_flattened_path" to mnnCoreFlattenedPath,
        "mnn_core_preloaded" to mnnCorePreloaded,
        "flatten_error" to flattenError,
        "preload_error" to preloadError
    )
}

data class DiagnosticsResponse(
    val serverVersion: String = "",
    val uptimeSeconds: Double = 0.0,
    val startedAt: Instant = Instant.EPOCH,
    val loadedModels: List<LoadedModelInfo> = emptyList(),
    val hostProfile: HostProfileDto? = null,
    val backendSelection: BackendSelectionDto? = null,
    val counters: CounterSnapshot = CounterSnapshot(),
    val nativeRuntime: NativeRuntimePathsDto? = null
) {
    fun toJson(): String = ServerJson.obj(
        listOf(
            "server_version" to serverVersion,
            "uptime_seconds" to uptimeSeconds,
            "started_at" to startedAt.toString(),
            "loaded_models" to loadedModels.map { ServerJson.obj(it.toJsonPairs()) },
            "host_profile" to hostProfile?.let { ServerJson.obj(it.toJsonPairs()) },
            "backend_selection" to backendSelection?.let { ServerJson.obj(it.toJsonPairs()) },
            "counters" to ServerJson.obj(counters.toJsonPairs()),
            "native_runtime" to nativeRuntime?.let { ServerJson.obj(it.toJsonPairs()) }
        )
    )
}

data class HealthResponse(val status: String = "ok", val at: Instant = Instant.now()) {
    fun toJson(): String = ServerJson.obj(listOf("status" to status, "at" to at.toString()))
}

/**
 * Just enough JSON to write these shapes.
 *
 * Hand-rolled because this package has no dependencies and the shapes are
 * fixed. Nulls are OMITTED, matching the server: a client that treats a present
 * null differently from an absent key sees a different message otherwise.
 */
internal object ServerJson {

    fun obj(pairs: List<Pair<String, Any?>>): String =
        pairs.filter { it.second != null }
            .joinToString(",", prefix = "{", postfix = "}") { (k, v) ->
                "\"${escape(k)}\":${value(v)}"
            }

    private fun value(v: Any?): String = when (v) {
        null -> "null"
        is Number, is Boolean -> v.toString()
        is List<*> -> v.joinToString(",", "[", "]") { value(it) }
        // A pre-rendered object is spliced in RAW. Re-encoding would turn it
        // into a JSON string containing JSON and the client would decode twice.
        is String -> if (v.startsWith("{") && v.endsWith("}")) v else "\"${escape(v)}\""
        else -> "\"${escape(v.toString())}\""
    }

    private fun escape(s: String): String = buildString(s.length) {
        for (c in s) when (c) {
            '"' -> append("\\\"")
            '\\' -> append("\\\\")
            '\n' -> append("\\n")
            '\r' -> append("\\r")
            '\t' -> append("\\t")
            else -> if (c < ' ') append("\\u%04x".format(c.code)) else append(c)
        }
    }
}

/**
 * Frames a payload as a server-sent-event chunk.
 *
 * Separated from any response object so the FRAMING can be tested and reused:
 * the bytes are the contract, and every client parsing them cares only that
 * "data: " prefixes the JSON and a blank line ends the event.
 */
object ServerSentEventsWriter {

    /**
     * The headers an SSE response must carry.
     *
     * `X-Accel-Buffering: no` is the one easy to leave out and impossible to
     * debug: nginx buffers the whole stream by default, so streaming works
     * perfectly in development and arrives all at once, at the end, in
     * production.
     */
    val headers: Map<String, String> = mapOf(
        "Content-Type" to "text/event-stream; charset=utf-8",
        "Cache-Control" to "no-cache, no-store",
        "Connection" to "keep-alive",
        "X-Accel-Buffering" to "no"
    )

    /** The terminator every OpenAI-shaped client waits for. A stream that just
     *  closes leaves them hanging until their own timeout. */
    const val TERMINATOR = "data: [DONE]\n\n"

    fun frame(json: String): ByteArray = "data: $json\n\n".toByteArray(Charsets.UTF_8)

    fun terminatorFrame(): ByteArray = TERMINATOR.toByteArray(Charsets.UTF_8)
}
