// EndpointsTest.kt
//
// Verifies the transport surface against the C# reference: InProcessEndpoint
// direct exposure, and a real HttpLoopbackEndpoint <-> AIHttpClient round-trip
// over 127.0.0.1 covering ask / chat / stream (SSE framing) / tool, plus the
// shared-secret X-Butler-Token auth rejection. Uses real threads/time
// (runBlocking) since the endpoint runs on its own HTTP server thread pool.

package com.bhengubv.circleai.hosting

import com.bhengubv.circleai.models.ChatMessage
import com.bhengubv.circleai.tools.IToolBridge
import com.bhengubv.circleai.tools.ToolDefinition
import com.bhengubv.circleai.tools.ToolInvocation
import com.bhengubv.circleai.tools.ToolResult
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withContext
import org.junit.jupiter.api.Test
import java.net.URI
import java.net.http.HttpClient
import java.net.http.HttpRequest
import java.net.http.HttpResponse
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class EndpointsTest {

    @Test
    fun `in-process endpoint exposes the service directly`() = runTest {
        val ep = InProcessEndpoint()
        val svc = FakeAIService()
        assertNull(ep.serviceAccessor)
        ep.startAsync(svc)
        assertEquals(svc, ep.serviceAccessor)
        ep.stopAsync()
        assertNull(ep.serviceAccessor)
        ep.disposeAsync()
    }

    @Test
    fun `crypto-equals is constant-shape and length-sensitive`() {
        assertTrue(HttpLoopbackEndpoint.cryptographicEquals("abc", "abc"))
        assertTrue(!HttpLoopbackEndpoint.cryptographicEquals("abc", "abd"))
        assertTrue(!HttpLoopbackEndpoint.cryptographicEquals("abc", "ab"))
    }

    @Test
    fun `loopback endpoint round-trips ask, chat, stream, and tool`() = runBlocking {
        val bridge = object : IToolBridge {
            override val availableTools: List<ToolDefinition> = emptyList()
            override suspend fun invokeAsync(invocation: ToolInvocation): ToolResult =
                ToolResult.ok(invocation.toolName, "tool-ran")
        }
        val svc = FakeAIService(replyFor = { "R:$it" })
        // Give the service a bridge by wrapping: FakeAIService.invokeTool returns ok already.
        val endpoint = HttpLoopbackEndpoint(AIOptions(modelId = "m", toolBridge = bridge))
        endpoint.startAsync(svc)
        try {
            val port = endpoint.port
            val token = endpoint.effectiveToken!!
            val client = AIHttpClient(port, token)

            withContext(Dispatchers.IO) {
                assertEquals("R:hello", client.askAsync("hello"))

                val chat = client.chatAsync(listOf(ChatMessage(UUID.randomUUID().toString(), "user", "chatq")))
                assertEquals("R:chatq", chat)

                val streamed = client.streamAsync(listOf(ChatMessage(UUID.randomUUID().toString(), "user", "sq"))).toList()
                assertEquals(listOf("R:sq"), streamed)

                val tool = client.invokeToolAsync(ToolInvocation("mytool", mapOf("a" to "b")))
                assertTrue(tool.success)
                assertEquals("mytool", tool.toolName)
            }
            client.close()
        } finally {
            endpoint.stopAsync()
            endpoint.disposeAsync()
        }
    }

    @Test
    fun `loopback endpoint rejects a bad token with 401`() = runBlocking {
        val svc = FakeAIService()
        val endpoint = HttpLoopbackEndpoint(AIOptions(modelId = "m", loopbackToken = "secret"))
        endpoint.startAsync(svc)
        try {
            val http = HttpClient.newHttpClient()
            val req = HttpRequest.newBuilder(URI.create("http://127.0.0.1:${endpoint.port}/butler/ask"))
                .header("X-Butler-Token", "wrong")
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString("""{"question":"x"}"""))
                .build()
            val resp = withContext(Dispatchers.IO) { http.send(req, HttpResponse.BodyHandlers.ofString()) }
            assertEquals(401, resp.statusCode())
        } finally {
            endpoint.stopAsync()
            endpoint.disposeAsync()
        }
    }
}
