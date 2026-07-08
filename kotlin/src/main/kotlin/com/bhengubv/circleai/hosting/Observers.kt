// Observers.kt
//
// Kotlin port of the CircleAI.Hosting observer bridges — the C# reference is the
// EXACT spec (PushAIObserver.cs, AetherAIObserver.cs). Thin IAIObserver bridges
// to a push-notification sender and to the CircleAether mesh transport.

package com.bhengubv.circleai.hosting

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

// =====================================================================
// IPushNotificationSender + PushAIObserver (PushAIObserver.cs)
// =====================================================================

/**
 * Platform-agnostic push notification sender abstraction. Implement with an APN
 * or FCM SDK for real delivery. Mirrors C# `IPushNotificationSender`.
 */
interface IPushNotificationSender {
    /** Send a push notification to the device identified by [deviceToken]. */
    suspend fun sendAsync(deviceToken: String, title: String, body: String)
}

/**
 * [IAIObserver] that delivers butler responses as push notifications via
 * [IPushNotificationSender]. Mirrors C# `PushAIObserver`.
 *
 * Delivery is fire-and-forget on a detached scope so the observer callback stays
 * non-blocking (C# uses `_ = _sender.SendAsync(...)`).
 */
class PushAIObserver(
    private val sender: IPushNotificationSender,
    deviceToken: String,
) : IAIObserver {

    private val deviceToken: String

    init {
        require(deviceToken.isNotBlank()) { "Device token is required." }
        this.deviceToken = deviceToken
    }

    override suspend fun onChatCompletedAsync(event: AIChatEvent) {
        sendResponse(event.response)
    }

    /**
     * Sends an error push notification. Call from error-handling code that cannot
     * surface through the standard [IAIObserver] lifecycle. Mirrors C# `OnError`.
     */
    suspend fun onError(ex: Throwable) {
        val msg = ex.message ?: ex.javaClass.simpleName
        val body = if (msg.length > MAX_BODY_LENGTH) msg.substring(0, MAX_BODY_LENGTH) + "…" else msg
        sender.sendAsync(deviceToken, "B! Error", body)
    }

    private suspend fun sendResponse(fullResponse: String) {
        val body = if (fullResponse.length > MAX_BODY_LENGTH) {
            fullResponse.substring(0, MAX_BODY_LENGTH) + "…"
        } else {
            fullResponse
        }
        sender.sendAsync(deviceToken, "B!", body)
    }

    private companion object {
        const val MAX_BODY_LENGTH = 100
    }
}

// =====================================================================
// ICircleAetherTransport + AetherAIObserver (AetherAIObserver.cs)
// =====================================================================

/**
 * (3.3.0) Publish/subscribe transport contract for the CircleAether mesh. Host
 * packages register an implementation (AetherNet, Bluetooth, NearLink, gRPC).
 * Mirrors C# `ICircleAetherTransport`.
 */
interface ICircleAetherTransport {
    /** Publish a payload to the given topic. */
    suspend fun publishAsync(topic: String, payload: ByteArray)
}

/**
 * [IAIObserver] implementation that forwards butler events to a CircleAether mesh
 * transport. Mirrors C# `AetherAIObserver` — publishes a JSON `{response}` blob
 * on `butler/response` and a `{error,message}` blob on `butler/error`.
 */
class AetherAIObserver(
    private val transport: ICircleAetherTransport,
) : IAIObserver {

    override suspend fun onChatCompletedAsync(event: AIChatEvent) {
        val payload = JSON.encodeToString(
            kotlinx.serialization.json.JsonElement.serializer(),
            buildJsonObject { put("response", event.response) },
        ).toByteArray(Charsets.UTF_8)
        transport.publishAsync("butler/response", payload)
    }

    /**
     * Publishes an error payload to the `butler/error` topic. Mirrors C# `OnError`.
     */
    suspend fun onError(ex: Throwable) {
        val payload = JSON.encodeToString(
            kotlinx.serialization.json.JsonElement.serializer(),
            buildJsonObject {
                put("error", ex.javaClass.simpleName)
                put("message", ex.message ?: "")
            },
        ).toByteArray(Charsets.UTF_8)
        transport.publishAsync("butler/error", payload)
    }

    private companion object {
        val JSON = Json
    }
}
