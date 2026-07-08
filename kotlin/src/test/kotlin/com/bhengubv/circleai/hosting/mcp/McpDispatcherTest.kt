// McpDispatcherTest.kt
//
// Verifies the MCP JSON-RPC 2.0 dispatcher against the C# reference: initialize,
// tools/list, tools/call (success + tool-error), resources/list, resources/read
// (found + scheme-miss + not-found), notification returns null, method-not-found
// and invalid-request error codes, and batch handling.

package com.bhengubv.circleai.hosting.mcp

import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class McpDispatcherTest {

    private val json = Json

    private class EchoTool : IMcpTool {
        override val name = "echo"
        override val description = "echoes its input"
        override val inputSchema: JsonElement = buildJsonObject { put("type", "object") }
        override suspend fun executeAsync(arguments: JsonObject): JsonElement =
            buildJsonObject { put("echo", (arguments["msg"] as? JsonPrimitive)?.contentOrNull ?: "") }
    }

    private class FailingTool : IMcpTool {
        override val name = "boom"
        override val description = "always fails"
        override val inputSchema: JsonElement = buildJsonObject { put("type", "object") }
        override suspend fun executeAsync(arguments: JsonObject): JsonElement =
            throw McpToolException("kaboom")
    }

    private class VaultProvider : IMcpResourceProvider {
        override val uriScheme = "vault://"
        override suspend fun listAsync(): List<McpResource> =
            listOf(McpResource("vault://a", "A", "first", "text/plain"))
        override suspend fun readAsync(uri: String): McpResourceContent? =
            if (uri == "vault://a") McpResourceContent(uri, "text/plain", "body-A") else null
    }

    private fun req(id: Int?, method: String, params: JsonObject? = null): JsonObject = buildJsonObject {
        put("jsonrpc", "2.0")
        if (id != null) put("id", id)
        put("method", method)
        if (params != null) put("params", params)
    }

    @Test
    fun `initialize returns protocol version and server info`() = runTest {
        val d = McpDispatcher(info = McpServerInfo(name = "n", version = "9.9.9"))
        val resp = d.dispatchAsync(req(1, "initialize")) as JsonObject
        val result = resp["result"]!!.jsonObject
        assertEquals("2024-11-05", result["protocolVersion"]!!.jsonPrimitive.content)
        assertEquals("n", result["serverInfo"]!!.jsonObject["name"]!!.jsonPrimitive.content)
        // id is re-emitted as a JSON-text string ("1").
        assertEquals("1", resp["id"]!!.jsonPrimitive.content)
    }

    @Test
    fun `tools list surfaces registered tools`() = runTest {
        val d = McpDispatcher(tools = listOf(EchoTool()))
        val resp = d.dispatchAsync(req(2, "tools/list")) as JsonObject
        val tools = resp["result"]!!.jsonObject["tools"]!!.jsonArray
        assertEquals(1, tools.size)
        assertEquals("echo", tools[0].jsonObject["name"]!!.jsonPrimitive.content)
    }

    @Test
    fun `tools call success wraps result in text content`() = runTest {
        val d = McpDispatcher(tools = listOf(EchoTool()))
        val params = buildJsonObject {
            put("name", "echo")
            put("arguments", buildJsonObject { put("msg", "hi") })
        }
        val resp = d.dispatchAsync(req(3, "tools/call", params)) as JsonObject
        val result = resp["result"]!!.jsonObject
        assertEquals(false, result["isError"]!!.jsonPrimitive.content.toBoolean())
        val text = result["content"]!!.jsonArray[0].jsonObject["text"]!!.jsonPrimitive.content
        assertTrue(text.contains("\"echo\":\"hi\""))
    }

    @Test
    fun `tools call unknown tool is invalid params`() = runTest {
        val d = McpDispatcher(tools = listOf(EchoTool()))
        val params = buildJsonObject { put("name", "nope") }
        val resp = d.dispatchAsync(req(4, "tools/call", params)) as JsonObject
        assertEquals(-32602, resp["error"]!!.jsonObject["code"]!!.jsonPrimitive.int)
    }

    @Test
    fun `tool exception maps to isError true`() = runTest {
        val d = McpDispatcher(tools = listOf(FailingTool()))
        val params = buildJsonObject { put("name", "boom") }
        val resp = d.dispatchAsync(req(5, "tools/call", params)) as JsonObject
        val result = resp["result"]!!.jsonObject
        assertEquals(true, result["isError"]!!.jsonPrimitive.content.toBoolean())
        assertEquals("kaboom", result["content"]!!.jsonArray[0].jsonObject["text"]!!.jsonPrimitive.content)
    }

    @Test
    fun `resources list and read`() = runTest {
        val d = McpDispatcher(resourceProviders = listOf(VaultProvider()))
        val listResp = d.dispatchAsync(req(6, "resources/list")) as JsonObject
        val resources = listResp["result"]!!.jsonObject["resources"]!!.jsonArray
        assertEquals("vault://a", resources[0].jsonObject["uri"]!!.jsonPrimitive.content)

        val readResp = d.dispatchAsync(
            req(7, "resources/read", buildJsonObject { put("uri", "vault://a") }),
        ) as JsonObject
        val contents = readResp["result"]!!.jsonObject["contents"]!!.jsonArray
        assertEquals("body-A", contents[0].jsonObject["text"]!!.jsonPrimitive.content)
    }

    @Test
    fun `resources read scheme miss and not found`() = runTest {
        val d = McpDispatcher(resourceProviders = listOf(VaultProvider()))
        val miss = d.dispatchAsync(
            req(8, "resources/read", buildJsonObject { put("uri", "models://x") }),
        ) as JsonObject
        assertEquals(-32602, miss["error"]!!.jsonObject["code"]!!.jsonPrimitive.int)

        val nf = d.dispatchAsync(
            req(9, "resources/read", buildJsonObject { put("uri", "vault://missing") }),
        ) as JsonObject
        assertEquals(-32602, nf["error"]!!.jsonObject["code"]!!.jsonPrimitive.int)
    }

    @Test
    fun `notification returns null and unknown method is method not found`() = runTest {
        val d = McpDispatcher()
        assertNull(d.dispatchAsync(req(null, "notifications/initialized")))
        val resp = d.dispatchAsync(req(10, "does/not/exist")) as JsonObject
        assertEquals(-32601, resp["error"]!!.jsonObject["code"]!!.jsonPrimitive.int)
    }

    @Test
    fun `missing method is invalid request`() = runTest {
        val d = McpDispatcher()
        val bad = buildJsonObject { put("jsonrpc", "2.0"); put("id", 1) }
        val resp = d.dispatchAsync(bad) as JsonObject
        assertEquals(-32600, resp["error"]!!.jsonObject["code"]!!.jsonPrimitive.int)
    }

    @Test
    fun `batch drops notification responses`() = runTest {
        val d = McpDispatcher(tools = listOf(EchoTool()))
        val batch = JsonArray(
            listOf(
                req(1, "tools/list"),
                req(null, "notifications/initialized"),
            ),
        )
        val resp = d.handleAsync(batch) as JsonArray
        // Only the tools/list response survives (notification -> null -> dropped).
        assertEquals(1, resp.size)
    }
}
