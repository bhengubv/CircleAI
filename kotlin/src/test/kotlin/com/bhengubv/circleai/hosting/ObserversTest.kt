// ObserversTest.kt
//
// Verifies the PushAIObserver + AetherAIObserver bridges against the C#
// reference: push delivery of chat responses (truncated at 100 chars) + error
// pushes, and Aether publish of a {response} blob on butler/response + a
// {error,message} blob on butler/error.

package com.bhengubv.circleai.hosting

import com.bhengubv.circleai.models.ChatMessage
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class ObserversTest {

    private class CapturingSender : IPushNotificationSender {
        data class Push(val token: String, val title: String, val body: String)
        val sent = ArrayList<Push>()
        override suspend fun sendAsync(deviceToken: String, title: String, body: String) {
            sent.add(Push(deviceToken, title, body))
        }
    }

    private class CapturingTransport : ICircleAetherTransport {
        data class Pub(val topic: String, val payload: String)
        val published = ArrayList<Pub>()
        override suspend fun publishAsync(topic: String, payload: ByteArray) {
            published.add(Pub(topic, String(payload, Charsets.UTF_8)))
        }
    }

    private fun chatEvent(response: String) =
        AIChatEvent(UUID.randomUUID(), listOf(ChatMessage(UUID.randomUUID().toString(), "user", "q")), response, Duration.ZERO, Instant.now())

    @Test
    fun `push observer delivers the chat response titled B!`() = runTest {
        val sender = CapturingSender()
        val observer = PushAIObserver(sender, "device-123")
        observer.onChatCompletedAsync(chatEvent("short reply"))
        assertEquals(1, sender.sent.size)
        assertEquals("device-123", sender.sent[0].token)
        assertEquals("B!", sender.sent[0].title)
        assertEquals("short reply", sender.sent[0].body)
    }

    @Test
    fun `push observer truncates long bodies at 100 chars`() = runTest {
        val sender = CapturingSender()
        val observer = PushAIObserver(sender, "d")
        val long = "x".repeat(150)
        observer.onChatCompletedAsync(chatEvent(long))
        val body = sender.sent[0].body
        assertEquals(101, body.length) // 100 chars + ellipsis
        assertTrue(body.endsWith("…"))
    }

    @Test
    fun `push observer surfaces errors`() = runTest {
        val sender = CapturingSender()
        val observer = PushAIObserver(sender, "d")
        observer.onError(RuntimeException("boom"))
        assertEquals("B! Error", sender.sent[0].title)
        assertEquals("boom", sender.sent[0].body)
    }

    @Test
    fun `aether observer publishes response blob on the response topic`() = runTest {
        val transport = CapturingTransport()
        val observer = AetherAIObserver(transport)
        observer.onChatCompletedAsync(chatEvent("hi there"))
        assertEquals(1, transport.published.size)
        assertEquals("butler/response", transport.published[0].topic)
        val json = Json.parseToJsonElement(transport.published[0].payload).jsonObject
        assertEquals("hi there", json["response"]!!.jsonPrimitive.content)
    }

    @Test
    fun `aether observer publishes error blob on the error topic`() = runTest {
        val transport = CapturingTransport()
        val observer = AetherAIObserver(transport)
        observer.onError(IllegalStateException("bad state"))
        assertEquals("butler/error", transport.published[0].topic)
        val json = Json.parseToJsonElement(transport.published[0].payload).jsonObject
        assertEquals("IllegalStateException", json["error"]!!.jsonPrimitive.content)
        assertEquals("bad state", json["message"]!!.jsonPrimitive.content)
    }
}
