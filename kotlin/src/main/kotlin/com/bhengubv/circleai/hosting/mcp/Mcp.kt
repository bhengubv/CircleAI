// Mcp.kt
//
// Kotlin port of CircleAI.Hosting.Mcp — the C# reference is the EXACT spec
// (Contracts.cs, McpEndpoints.cs). MCP tool + resource provider contracts plus
// the JSON-RPC 2.0 dispatcher.
//
// The C# `McpEndpoints` maps ASP.NET Core routes (POST /mcp, GET /mcp/manifest)
// and pulls tools/providers out of the DI container via GetServices<T>(). The
// portable Kotlin core has no ASP.NET Core / DI container, so the dispatcher is
// exposed as [McpDispatcher] which takes the tool + provider collections
// directly (that is precisely what the C# DispatchAsync does — it is described
// as "Pure-DI dispatcher entry point — testable without a HttpContext"). The
// JSON-RPC wire shape and every error code are byte-for-byte identical.

package com.bhengubv.circleai.hosting.mcp

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import kotlinx.serialization.json.Json

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** One MCP resource descriptor. Mirrors C# `McpResource`. */
data class McpResource(
    val uri: String,
    val name: String,
    val description: String?,
    val mimeType: String,
)

/** One MCP resource content (returned by resources/read). Mirrors C# `McpResourceContent`. */
data class McpResourceContent(
    val uri: String,
    val mimeType: String,
    val text: String,
)

/**
 * Thrown from inside [IMcpTool.executeAsync] to signal a tool-level error (vs
 * an MCP protocol error). The dispatcher returns this as
 * `{content:[{type:"text",text:msg}], isError:true}`. Mirrors C# `McpToolException`.
 */
class McpToolException(message: String) : Exception(message)

/**
 * One MCP tool the host exposes. Mirrors C# `IMcpTool`.
 *
 * [inputSchema] is the JSON Schema describing the tool's `arguments` object; the
 * dispatcher includes it verbatim in `tools/list`. It is a [JsonElement] so the
 * schema round-trips through the wire without re-encoding.
 */
interface IMcpTool {
    /** Unique tool name (snake_case by convention). */
    val name: String

    /** One-line description shown in tool listings. */
    val description: String

    /** JSON Schema for the tool's `arguments` object. */
    val inputSchema: JsonElement

    /**
     * Execute the tool. Return any JSON value; the dispatcher wraps it in MCP's
     * `{content:[{type:"text",text:"..."}]}` envelope. Throw [McpToolException]
     * to signal a tool-level error (returned as `isError:true`).
     */
    suspend fun executeAsync(arguments: JsonObject): JsonElement
}

/**
 * One MCP resource provider. The dispatcher walks every registered provider for
 * `resources/list`; for `resources/read` it picks the first provider whose
 * [uriScheme] matches the leading scheme of the request. Mirrors C#
 * `IMcpResourceProvider`.
 */
interface IMcpResourceProvider {
    /** e.g. `"vault://"`, `"models://"`. */
    val uriScheme: String

    /** List every resource this provider serves. */
    suspend fun listAsync(): List<McpResource>

    /** Read one resource by uri. Returns null on not-found. */
    suspend fun readAsync(uri: String): McpResourceContent?
}

// =====================================================================
// Dispatcher (McpEndpoints.cs — DispatchAsync + handlers)
// =====================================================================

/** Server identity block. Mirrors C# `McpEndpoints.McpServerInfo`. */
data class McpServerInfo(
    val name: String = "circleai-mcp",
    val version: String = "3.2.0",
    val description: String = "CircleAI MCP endpoint",
)

/**
 * JSON-RPC 2.0 dispatcher for the MCP endpoint. Direct port of the C#
 * `McpEndpoints.DispatchAsync` + its private handlers. Tools + providers are
 * injected (the C# version resolves them from DI via `GetServices<T>()`).
 *
 * Returns the JSON-RPC response as a [JsonElement], or `null` for notifications
 * (matching the C# `Task<object?>` contract where notifications return null).
 */
class McpDispatcher(
    private val tools: List<IMcpTool> = emptyList(),
    private val resourceProviders: List<IMcpResourceProvider> = emptyList(),
    private val info: McpServerInfo = McpServerInfo(),
) {
    private val json = Json { encodeDefaults = true }

    /**
     * Handle a single request or a JSON-RPC batch. For a batch, returns a
     * [JsonArray] of the non-null responses (mirrors the C# POST /mcp handler);
     * for a single request, returns its response element or null.
     */
    suspend fun handleAsync(req: JsonElement?): JsonElement? {
        if (req is JsonArray) {
            val responses = ArrayList<JsonElement>()
            for (item in req) {
                val r = dispatchAsync(item)
                if (r != null) responses.add(r)
            }
            return JsonArray(responses)
        }
        return dispatchAsync(req)
    }

    /**
     * Pure dispatcher for one JSON-RPC message. Returns null for notifications.
     * Mirrors C# `DispatchAsync`.
     */
    suspend fun dispatchAsync(req: JsonElement?): JsonElement? {
        if (req == null || req !is JsonObject) return mcpErrorObj(null, -32600, "Invalid Request")

        val id = req["id"]
        val jsonrpc = (req["jsonrpc"] as? JsonPrimitive)?.contentOrNull
        val method = if (jsonrpc == "2.0") (req["method"] as? JsonPrimitive)?.contentOrNull else null
        if (method == null) return mcpErrorObj(id, -32600, "Invalid Request: missing jsonrpc or method")

        val params = req["params"]
        return try {
            when (method) {
                "initialize" -> handleInitialize(id)
                "notifications/initialized" -> null
                "tools/list" -> handleToolsList(id)
                "tools/call" -> handleToolsCallAsync(id, params)
                "resources/list" -> handleResourcesListAsync(id)
                "resources/read" -> handleResourcesReadAsync(id, params)
                else -> mcpErrorObj(id, -32601, "Method not found: $method")
            }
        } catch (ex: Exception) {
            mcpErrorObj(id, -32603, "Internal error: ${ex.message}")
        }
    }

    private fun handleInitialize(id: JsonElement?): JsonElement =
        mcpResult(id) {
            put("protocolVersion", "2024-11-05")
            put("serverInfo", buildJsonObject {
                put("name", info.name)
                put("version", info.version)
            })
            put("capabilities", buildJsonObject {
                put("tools", buildJsonObject { put("listChanged", false) })
                put("resources", buildJsonObject {
                    put("listChanged", false)
                    put("subscribe", false)
                })
            })
        }

    private fun handleToolsList(id: JsonElement?): JsonElement =
        mcpResult(id) {
            put("tools", buildJsonArray {
                for (t in tools) {
                    add(buildJsonObject {
                        put("name", t.name)
                        put("description", t.description)
                        put("inputSchema", t.inputSchema)
                    })
                }
            })
        }

    private suspend fun handleToolsCallAsync(id: JsonElement?, params: JsonElement?): JsonElement {
        val toolName = ((params as? JsonObject)?.get("name") as? JsonPrimitive)?.contentOrNull
        if (toolName.isNullOrBlank()) return mcpErrorObj(id, -32602, "Invalid params: 'name' is required")

        val tool = tools.firstOrNull { it.name == toolName }
            ?: return mcpErrorObj(id, -32602, "Unknown tool: $toolName")

        val args = (params as? JsonObject)?.get("arguments") as? JsonObject ?: JsonObject(emptyMap())
        return try {
            val result = tool.executeAsync(args)
            mcpToolResult(id, result)
        } catch (ex: McpToolException) {
            mcpToolError(id, ex.message ?: "tool error")
        }
    }

    private suspend fun handleResourcesListAsync(id: JsonElement?): JsonElement {
        val resources = ArrayList<McpResource>()
        for (p in resourceProviders) {
            resources.addAll(p.listAsync())
        }
        return mcpResult(id) {
            put("resources", buildJsonArray {
                for (r in resources) {
                    add(buildJsonObject {
                        put("uri", r.uri)
                        put("name", r.name)
                        put("description", r.description ?: r.name)
                        put("mimeType", r.mimeType)
                    })
                }
            })
        }
    }

    private suspend fun handleResourcesReadAsync(id: JsonElement?, params: JsonElement?): JsonElement {
        val uri = ((params as? JsonObject)?.get("uri") as? JsonPrimitive)?.contentOrNull
        if (uri.isNullOrBlank()) return mcpErrorObj(id, -32602, "Invalid params: 'uri' is required")

        val provider = resourceProviders.firstOrNull { uri.startsWith(it.uriScheme, ignoreCase = true) }
            ?: return mcpErrorObj(id, -32602, "No provider for URI scheme: $uri")

        val content = provider.readAsync(uri)
            ?: return mcpErrorObj(id, -32602, "Resource not found: $uri")

        return mcpResult(id) {
            put("contents", buildJsonArray {
                add(buildJsonObject {
                    put("uri", content.uri)
                    put("mimeType", content.mimeType)
                    put("text", content.text)
                })
            })
        }
    }

    // ── JSON-RPC helpers ────────────────────────────────────────────────────
    //
    // C# serialises `id` back via `id?.ToJsonString()` — i.e. the id is
    // re-emitted as a STRING containing the JSON text of the original id
    // (e.g. an int id 7 comes back as "7"). We reproduce that exactly.

    private fun idToString(id: JsonElement?): JsonElement =
        if (id == null || id is JsonNull) JsonNull else JsonPrimitive(json.encodeToString(JsonElement.serializer(), id))

    private fun mcpResult(id: JsonElement?, buildResult: kotlinx.serialization.json.JsonObjectBuilder.() -> Unit): JsonObject =
        buildJsonObject {
            put("jsonrpc", "2.0")
            put("id", idToString(id))
            put("result", buildJsonObject(buildResult))
        }

    private fun mcpToolResult(id: JsonElement?, data: JsonElement): JsonObject =
        mcpResult(id) {
            put("content", buildJsonArray {
                add(buildJsonObject {
                    put("type", "text")
                    // C# serialises the tool's return value into a text string.
                    put("text", json.encodeToString(JsonElement.serializer(), data))
                })
            })
            put("isError", false)
        }

    private fun mcpToolError(id: JsonElement?, message: String): JsonObject =
        mcpResult(id) {
            put("content", buildJsonArray {
                add(buildJsonObject {
                    put("type", "text")
                    put("text", message)
                })
            })
            put("isError", true)
        }

    private fun mcpErrorObj(id: JsonElement?, code: Int, message: String): JsonObject =
        buildJsonObject {
            put("jsonrpc", "2.0")
            put("id", idToString(id))
            put("error", buildJsonObject {
                put("code", code)
                put("message", message)
            })
        }
}
