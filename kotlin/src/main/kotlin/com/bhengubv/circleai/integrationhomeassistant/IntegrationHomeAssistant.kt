// IntegrationHomeAssistant.kt
//
// Kotlin port of CircleAI.Integration.HomeAssistant (HomeAssistantConnector.cs)
// — the C# reference is the EXACT spec. A HomeAssistant REST client that lists
// entities, calls services, and reads state via a long-lived access token.
//
// Fidelity notes:
//   * The network is injected via [HttpTransport]; the base URL is composed as
//     `BaseUrl` + relative path exactly as the C# `HttpClient.BaseAddress` code.
//   * `ListEntities` walks the /api/states array: entity_id (skipped if empty),
//     state, domain = entity_id split on '.', attributes stringified per kind
//     (string/number/true/false/other), friendly_name pulled out.
//   * `CallService` POSTs data (or {}) to /api/services/{domain}/{service}.
//   * TurnOn/TurnOff convenience helpers call homeassistant.turn_on/turn_off.

package com.bhengubv.circleai.integrationhomeassistant

import com.bhengubv.circleai.integration.HaEntity
import com.bhengubv.circleai.integration.HttpRequest
import com.bhengubv.circleai.integration.HttpTransport
import com.bhengubv.circleai.integration.HttpVerb
import com.bhengubv.circleai.integration.IHomeAutomationConnector
import com.bhengubv.circleai.integration.ensureSuccess
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import java.net.URI
import java.net.URLEncoder

internal val HA_JSON = Json { ignoreUnknownKeys = true; isLenient = true }

internal fun haEsc(s: String): String =
    URLEncoder.encode(s, Charsets.UTF_8).replace("+", "%20")

/**
 * HomeAssistant connector config. Mirrors C# `HomeAssistantOptions`.
 * @param baseUrl HA base URL, e.g. http://homeassistant.local:8123/ — must
 *   include a trailing slash.
 * @param accessToken Long-lived access token.
 */
data class HomeAssistantOptions(val baseUrl: URI, val accessToken: String)

/** HomeAssistant REST connector. Mirrors C# `HomeAssistantConnector`. */
class HomeAssistantConnector(
    private val opts: HomeAssistantOptions,
    private val http: HttpTransport,
) : IHomeAutomationConnector {

    private val base: String = opts.baseUrl.toString().let { if (it.endsWith("/")) it else "$it/" }
    private val authHeaders: Map<String, String> =
        if (opts.accessToken.isNotBlank()) mapOf("Authorization" to "Bearer ${opts.accessToken}") else emptyMap()

    override val providerId: String get() = "home-assistant"
    override val isConfigured: Boolean get() = opts.accessToken.isNotBlank()

    override suspend fun listEntities(): List<HaEntity> {
        val resp = http.send(HttpRequest(HttpVerb.GET, base + "api/states", authHeaders)).ensureSuccess()
        val root = HA_JSON.parseToJsonElement(resp.body)
        val list = ArrayList<HaEntity>()
        val arr = root as? JsonArray ?: return list
        for (el in arr) {
            val st = el as? JsonObject ?: continue
            val entityId = (st["entity_id"] as? JsonPrimitive)?.content ?: ""
            if (entityId.isEmpty()) continue
            val state = (st["state"] as? JsonPrimitive)?.content ?: ""
            val domain = entityId.split('.', limit = 2)[0]
            val attrs = LinkedHashMap<String, String>()
            var friendly = entityId
            (st["attributes"] as? JsonObject)?.forEach { (name, value) ->
                val rendered = when (value) {
                    is JsonPrimitive -> when {
                        value.isString -> value.content
                        value.content == "true" -> "true"
                        value.content == "false" -> "false"
                        else -> value.content
                    }
                    is JsonNull -> value.toString()
                    else -> value.toString()
                }
                attrs[name] = rendered
                if (name == "friendly_name" && value is JsonPrimitive && value.isString) {
                    friendly = value.content
                }
            }
            list += HaEntity(entityId, friendly, domain, state, attrs)
        }
        return list
    }

    override suspend fun callService(domain: String, service: String, data: Map<String, Any?>?) {
        require(domain.isNotBlank()) { "domain required" }
        require(service.isNotBlank()) { "service required" }
        val payload = data ?: emptyMap()
        val body = buildJsonObject {
            for ((k, v) in payload) put(k, jsonScalar(v))
        }
        http.send(
            HttpRequest(
                HttpVerb.POST,
                base + "api/services/${haEsc(domain)}/${haEsc(service)}",
                authHeaders,
                body.toString(),
                "application/json",
            ),
        ).ensureSuccess()
    }

    /** Turn an entity on via homeassistant.turn_on. Mirrors C# `TurnOnAsync`. */
    suspend fun turnOn(entityId: String) =
        callService("homeassistant", "turn_on", mapOf("entity_id" to entityId))

    /** Turn an entity off via homeassistant.turn_off. Mirrors C# `TurnOffAsync`. */
    suspend fun turnOff(entityId: String) =
        callService("homeassistant", "turn_off", mapOf("entity_id" to entityId))

    private companion object {
        fun jsonScalar(v: Any?): kotlinx.serialization.json.JsonElement = when (v) {
            null -> JsonNull
            is Boolean -> JsonPrimitive(v)
            is Number -> JsonPrimitive(v)
            is String -> JsonPrimitive(v)
            else -> JsonPrimitive(v.toString())
        }
    }
}
