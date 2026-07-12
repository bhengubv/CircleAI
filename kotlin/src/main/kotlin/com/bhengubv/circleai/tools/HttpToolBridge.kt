// HttpToolBridge.kt
//
// Kotlin port of CircleAI.Tools/HttpToolBridge.cs + ComposioToolBridge.cs.
//
// Both bridges route tool calls over an INJECTED HTTP seam ([ToolHttpTransport])
// rather than owning a concrete HTTP client — mirroring the "HTTP plumbing is
// host-supplied" convention used across the Kotlin port. No real sockets are
// opened by this code; the host supplies a transport that does.
//
//   HttpToolBridge     — routes tgn.* tool calls to the TheGeekNetwork REST APIs
//                        using a static tool-name -> endpoint mapping table.
//   ComposioToolBridge — routes tool calls to a Composio MCP server via
//                        JSON-RPC 2.0, with dynamic tool discovery.
//
// C# -> Kotlin conventions:
//   HttpClient / HttpRequestMessage -> ToolHttpTransport seam + ToolHttpRequest
//   System.Text.Json JsonElement    -> kotlinx.serialization.json.JsonElement
//   Task<ToolResult>                -> suspend fun (): ToolResult

package com.bhengubv.circleai.tools

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonObject
import java.net.URLEncoder

// ===========================================================================
// Injected HTTP seam
// ===========================================================================

/** HTTP method verbs used by the tool bridges. */
enum class ToolHttpVerb { GET, POST, PATCH, PUT, DELETE }

/**
 * A single HTTP request assembled by a bridge. [url] is the fully-resolved
 * absolute request URL (query string already appended when applicable).
 */
data class ToolHttpRequest(
    val verb: ToolHttpVerb,
    val url: String,
    val headers: Map<String, String> = emptyMap(),
    /** Serialised JSON request body, or `null` for no body. */
    val jsonBody: String? = null,
)

/**
 * An HTTP response. [status] is the numeric status code, [contentType] the
 * response media type (used to decide whether [body] is JSON).
 */
data class ToolHttpResponse(
    val status: Int,
    val body: String,
    val contentType: String? = null,
) {
    /** True for 2xx. Mirrors C# `HttpResponseMessage.IsSuccessStatusCode`. */
    val isSuccess: Boolean get() = status in 200..299

    /** Best-effort reason label derived from the status code. */
    val reasonPhrase: String get() = "HTTP $status"
}

/** Injected transport standing in for the real network. */
fun interface ToolHttpTransport {
    suspend fun send(request: ToolHttpRequest): ToolHttpResponse
}

private val toolJson = Json { ignoreUnknownKeys = true }

private fun urlEncode(s: String): String = URLEncoder.encode(s, Charsets.UTF_8).replace("+", "%20")

/** Renders an argument value as a query-string / path scalar. */
private fun renderScalar(value: Any?): String? = when (value) {
    null -> null
    is String -> value
    is Boolean -> if (value) "true" else "false"
    else -> value.toString()
}

/** Serialises an arbitrary argument value into a [JsonElement] for a JSON body. */
private fun argToJson(value: Any?): JsonElement = when (value) {
    null -> JsonNull
    is JsonElement -> value
    is Boolean -> JsonPrimitive(value)
    is Number -> JsonPrimitive(value)
    is String -> JsonPrimitive(value)
    else -> JsonPrimitive(value.toString())
}

private fun buildJsonBody(args: Map<String, Any?>): String {
    val obj = JsonObject(args.mapValues { argToJson(it.value) })
    return toolJson.encodeToString(JsonObject.serializer(), obj)
}

// ===========================================================================
// HttpToolBridge  (HttpToolBridge.cs)
// ===========================================================================

/**
 * HTTP-backed [IToolBridge] that routes tool calls to the TheGeekNetwork APIs
 * over REST. Tool-name -> endpoint mapping is provided for the representative
 * operations defined in [TheGeekNetworkTools]; unmapped tools return a
 * structured error rather than throwing.
 *
 * No network calls happen during construction or via the [availableTools]
 * property — only [invokeAsync] hits the injected transport.
 *
 * @param baseUrl Absolute base URL (a trailing slash is added if absent).
 * @param transport Injected HTTP seam performing the actual request.
 * @param tools The tool catalogue this bridge advertises (defaults to the full
 *   TheGeekNetwork catalogue).
 */
class HttpToolBridge(
    baseUrl: String,
    private val transport: ToolHttpTransport,
    private val tools: List<ToolDefinition> = TheGeekNetworkTools.getAllTools(),
) : IToolBridge {

    private val baseUri: String
    private val routes: Map<String, EndpointMapping> = buildRoutes()

    init {
        require(baseUrl.isNotBlank()) { "baseUrl required" }
        baseUri = if (baseUrl.endsWith("/")) baseUrl else "$baseUrl/"
    }

    override val availableTools: List<ToolDefinition> get() = tools

    override suspend fun invokeAsync(invocation: ToolInvocation): ToolResult {
        val mapping = routes[invocation.toolName]
            ?: return ToolResult(
                toolName = invocation.toolName,
                success = false,
                error = "Tool '${invocation.toolName}' is not registered in this bridge instance.",
            )

        return try {
            val url = resolveUrl(mapping, invocation.arguments)
            val request = buildRequest(mapping, url, invocation.arguments)
            val response = transport.send(request)

            val body: Any? = when {
                response.body.isEmpty() -> null
                response.contentType?.contains("json", ignoreCase = true) == true ->
                    runCatching { toolJson.parseToJsonElement(response.body) }.getOrDefault(response.body)
                else -> response.body
            }

            if (!response.isSuccess) {
                ToolResult(
                    toolName = invocation.toolName,
                    success = false,
                    result = body,
                    error = "HTTP ${response.status} ${response.reasonPhrase}",
                )
            } else {
                ToolResult(toolName = invocation.toolName, success = true, result = body)
            }
        } catch (ex: Exception) {
            ToolResult(toolName = invocation.toolName, success = false, error = ex.message)
        }
    }

    // ── URL / request building ──────────────────────────────────────────────

    private data class EndpointMapping(val verb: ToolHttpVerb, val pathTemplate: String, val body: String)

    private fun resolveUrl(mapping: EndpointMapping, arguments: Map<String, Any?>): String {
        var path = mapping.pathTemplate
        for (placeholder in extractPlaceholders(mapping.pathTemplate)) {
            val raw = arguments[placeholder]
                ?: throw IllegalStateException(
                    "Tool argument '$placeholder' is required to build URL '${mapping.pathTemplate}'.",
                )
            path = path.replace("{$placeholder}", urlEncode(raw.toString()))
        }

        var url = baseUri + path

        if (mapping.body == BODY_QUERY) {
            val query = buildQueryString(buildBodyArgs(mapping, arguments))
            if (query.isNotEmpty()) {
                url += if (url.contains('?')) "&$query" else "?$query"
            }
        }

        return url
    }

    private fun buildRequest(mapping: EndpointMapping, url: String, arguments: Map<String, Any?>): ToolHttpRequest {
        val jsonBody = if (mapping.body == BODY_JSON) buildJsonBody(buildBodyArgs(mapping, arguments)) else null
        val headers = if (jsonBody != null) mapOf("Content-Type" to "application/json") else emptyMap()
        return ToolHttpRequest(mapping.verb, url, headers, jsonBody)
    }

    private fun buildBodyArgs(mapping: EndpointMapping, arguments: Map<String, Any?>): Map<String, Any?> {
        // Drop placeholders from the body/query — they're already in the URL.
        val placeholders = extractPlaceholders(mapping.pathTemplate).toHashSet()
        return arguments.filterKeys { it !in placeholders }
    }

    private fun buildQueryString(args: Map<String, Any?>): String {
        if (args.isEmpty()) return ""
        val sb = StringBuilder()
        var first = true
        for ((key, value) in args) {
            val rendered = renderScalar(value) ?: continue
            if (!first) sb.append('&')
            sb.append(urlEncode(key)).append('=').append(urlEncode(rendered))
            first = false
        }
        return sb.toString()
    }

    private fun extractPlaceholders(template: String): List<String> {
        val result = ArrayList<String>()
        var i = 0
        while (i < template.length) {
            val open = template.indexOf('{', i)
            if (open < 0) break
            val close = template.indexOf('}', open + 1)
            if (close < 0) break
            result.add(template.substring(open + 1, close))
            i = close + 1
        }
        return result
    }

    companion object {
        // Body strategy values.
        private const val BODY_NONE = "none"
        private const val BODY_QUERY = "query"
        private const val BODY_JSON = "json"

        private fun buildRoutes(): Map<String, EndpointMapping> {
            fun get(path: String, body: String) = EndpointMapping(ToolHttpVerb.GET, path, body)
            fun post(path: String) = EndpointMapping(ToolHttpVerb.POST, path, BODY_JSON)
            fun patch(path: String) = EndpointMapping(ToolHttpVerb.PATCH, path, BODY_JSON)
            return linkedMapOf(
                // Account
                "tgn.account.get_profile" to get("account/v1/users/{user_id}", BODY_NONE),
                "tgn.account.update_profile" to patch("account/v1/users/me"),
                // Audit
                "tgn.audit.list_events" to get("audit/v1/events", BODY_QUERY),
                // Auth
                "tgn.auth.request_otp" to post("auth/v1/otp/request"),
                "tgn.auth.verify_otp" to post("auth/v1/otp/verify"),
                "tgn.auth.push_to_app" to post("auth/v1/push-to-app"),
                // BidBaas
                "tgn.bidbaas.list_active_auctions" to get("bidbaas/v1/auctions/active", BODY_QUERY),
                "tgn.bidbaas.place_bid" to post("bidbaas/v1/auctions/{auction_id}/bids"),
                "tgn.bidbaas.get_auction_details" to get("bidbaas/v1/auctions/{auction_id}", BODY_NONE),
                // BillPayment
                "tgn.billpayment.list_billers" to get("billpayment/v1/billers", BODY_QUERY),
                "tgn.billpayment.pay_bill" to post("billpayment/v1/payments"),
                // Blockchain
                "tgn.blockchain.get_transaction" to get("blockchain/v1/transactions/{tx_hash}", BODY_NONE),
                "tgn.blockchain.get_address_info" to get("blockchain/v1/addresses/{address}", BODY_NONE),
                // Butler
                "tgn.butler.log_interaction" to post("butler/v1/interactions"),
                "tgn.butler.get_user_context" to get("butler/v1/users/{user_id}/context", BODY_NONE),
                // CircleAether
                "tgn.circleaether.get_node_status" to get("circleaether/v1/nodes/{device_id}/status", BODY_NONE),
                "tgn.circleaether.list_nearby_peers" to get("circleaether/v1/peers/nearby", BODY_QUERY),
                // Ecommerce
                "tgn.ecommerce.search_products" to get("ecommerce/v1/products/search", BODY_QUERY),
                "tgn.ecommerce.get_product" to get("ecommerce/v1/products/{product_id}", BODY_NONE),
                // Electricity
                "tgn.electricity.buy_token" to post("electricity/v1/tokens"),
                "tgn.electricity.list_recent_purchases" to get("electricity/v1/purchases", BODY_QUERY),
                // Geo
                "tgn.geo.get_user_location" to get("geo/v1/users/me/location", BODY_NONE),
                "tgn.geo.geocode_address" to get("geo/v1/geocode", BODY_QUERY),
                // Glocell
                "tgn.glocell.list_products" to get("glocell/v1/products", BODY_QUERY),
                // Incentives
                "tgn.incentives.get_qi_balance" to get("incentives/v1/qi/balance", BODY_NONE),
                "tgn.incentives.list_active_quests" to get("incentives/v1/quests/active", BODY_QUERY),
                // KiffStore
                "tgn.kiffstore.search_items" to get("kiffstore/v1/items/search", BODY_QUERY),
                // Ledger
                "tgn.ledger.get_account_balance" to get("ledger/v1/accounts/{account_id}/balance", BODY_NONE),
                "tgn.ledger.list_entries" to get("ledger/v1/accounts/{account_id}/entries", BODY_QUERY),
                // Localization
                "tgn.localization.translate_text" to post("localization/v1/translate"),
                "tgn.localization.list_supported_languages" to get("localization/v1/languages", BODY_NONE),
                // Maps
                "tgn.maps.geocode" to get("maps/v1/geocode", BODY_QUERY),
                "tgn.maps.reverse_geocode" to get("maps/v1/reverse-geocode", BODY_QUERY),
                // MapsData
                "tgn.mapsdata.search_pois" to get("mapsdata/v1/pois/search", BODY_QUERY),
                // Media
                "tgn.media.create_upload_url" to post("media/v1/uploads"),
                "tgn.media.get_media" to get("media/v1/media/{media_id}", BODY_NONE),
                // Messaging
                "tgn.messaging.send_message" to post("messaging/v1/messages"),
                "tgn.messaging.list_conversations" to get("messaging/v1/conversations", BODY_QUERY),
                "tgn.messaging.get_messages" to get("messaging/v1/conversations/{conversation_id}/messages", BODY_QUERY),
                // Notification
                "tgn.notification.send_push" to post("notification/v1/push"),
                "tgn.notification.list_for_user" to get("notification/v1/notifications", BODY_QUERY),
                // OpSupport
                "tgn.opsupport.create_ticket" to post("opsupport/v1/tickets"),
                "tgn.opsupport.get_system_status" to get("opsupport/v1/status", BODY_NONE),
                // Panik
                "tgn.panik.trigger_sos" to post("panik/v1/alerts"),
                "tgn.panik.cancel_sos" to post("panik/v1/alerts/{alert_id}/cancel"),
                // Payfast
                "tgn.payfast.create_payment" to post("payfast/v1/payments"),
                // Sdpkt
                "tgn.sdpkt.get_balance" to get("sdpkt/v1/wallet/balance", BODY_NONE),
                "tgn.sdpkt.send_payment" to post("sdpkt/v1/wallet/transfers"),
                "tgn.sdpkt.get_transactions" to get("sdpkt/v1/wallet/transactions", BODY_QUERY),
                // ShhMoney
                "tgn.shhmoney.create_discreet_payment" to post("shhmoney/v1/payments"),
                // SleptOn
                "tgn.slepton.list_stories" to get("slepton/v1/stories", BODY_QUERY),
                "tgn.slepton.get_story" to get("slepton/v1/stories/{story_id}", BODY_NONE),
                // SortedClothing
                "tgn.sortedclothing.search_items" to get("sortedclothing/v1/items/search", BODY_QUERY),
                // TagMe
                "tgn.tagme.create_tag" to post("tagme/v1/tags"),
                "tgn.tagme.list_nearby_tags" to get("tagme/v1/tags/nearby", BODY_QUERY),
                // Takemehome
                "tgn.takemehome.search_flights" to get("takemehome/v1/flights/search", BODY_QUERY),
                "tgn.takemehome.search_stays" to get("takemehome/v1/stays/search", BODY_QUERY),
                // TheHotList
                "tgn.thehotlist.list_entries" to get("thehotlist/v1/entries", BODY_QUERY),
                // TheJobCenter
                "tgn.thejobcenter.search_jobs" to get("thejobcenter/v1/jobs/search", BODY_QUERY),
                "tgn.thejobcenter.apply" to post("thejobcenter/v1/jobs/{job_id}/applications"),
                // ThirdParty
                "tgn.thirdparty.list_integrations" to get("thirdparty/v1/integrations", BODY_NONE),
                "tgn.thirdparty.invoke_integration" to post("thirdparty/v1/integrations/{integration_name}/invoke"),
                // TrustSeal
                "tgn.trustseal.get_status" to get("trustseal/v1/status", BODY_NONE),
                "tgn.trustseal.start_verification" to post("trustseal/v1/verifications"),
                // Wallet
                "tgn.wallet.get_balance" to get("wallet/v1/balance", BODY_QUERY),
                "tgn.wallet.get_transactions" to get("wallet/v1/transactions", BODY_QUERY),
                // WhatWeWant
                "tgn.whatwewant.list_stories" to get("whatwewant/v1/stories", BODY_QUERY),
                "tgn.whatwewant.get_story" to get("whatwewant/v1/stories/{story_id}", BODY_NONE),
                // Wolverine
                "tgn.wolverine.list_jobs" to get("wolverine/v1/jobs", BODY_QUERY),
            )
        }
    }
}

// ===========================================================================
// ComposioToolBridge  (ComposioToolBridge.cs)
// ===========================================================================

/**
 * Routes tool calls to a Composio MCP server via JSON-RPC 2.0 over the injected
 * [ToolHttpTransport]. Composio provides 250+ integrations (Gmail, Slack,
 * GitHub, Calendar, etc.) through a single endpoint.
 *
 * The bridge sends every tool invocation as a `tools/call` JSON-RPC 2.0 request
 * and interprets the response envelope to produce a [ToolResult]. Tool discovery
 * calls `GET {serverUri}/tools` and maps each returned entry to a
 * [ToolDefinition].
 *
 * @param composioApiKey API key sent in the `X-API-Key` header.
 * @param transport Injected HTTP seam performing the actual request.
 * @param serverUri Base URI of the Composio MCP endpoint.
 */
class ComposioToolBridge(
    private val composioApiKey: String,
    private val transport: ToolHttpTransport,
    serverUri: String = DEFAULT_SERVER_URI,
) : IToolBridge {

    private val serverUri: String

    init {
        require(composioApiKey.isNotBlank()) { "composioApiKey required" }
        this.serverUri = if (serverUri.endsWith("/")) serverUri else "$serverUri/"
    }

    /** Synchronous available-tools list. Empty until [getAvailableToolsAsync] runs. */
    @Volatile
    override var availableTools: List<ToolDefinition> = emptyList()
        private set

    override suspend fun invokeAsync(invocation: ToolInvocation): ToolResult {
        require(invocation.toolName.isNotBlank()) { "toolName must not be null or whitespace" }

        val requestBody = buildJsonObject {
            put("jsonrpc", "2.0")
            put("method", "tools/call")
            put("id", 1)
            putJsonObject("params") {
                put("name", invocation.toolName)
                put("arguments", JsonObject(invocation.arguments.mapValues { argToJson(it.value) }))
            }
        }

        val endpoint = serverUri + "tools/" + urlEncode(invocation.toolName) + "/invoke"

        return try {
            val response = transport.send(
                ToolHttpRequest(
                    ToolHttpVerb.POST,
                    endpoint,
                    headers = mapOf("X-API-Key" to composioApiKey, "Content-Type" to "application/json"),
                    jsonBody = toolJson.encodeToString(JsonObject.serializer(), requestBody),
                ),
            )

            val body = runCatching { toolJson.parseToJsonElement(response.body) as? JsonObject }.getOrNull()

            if (!response.isSuccess) {
                val httpError = "HTTP ${response.status} ${response.reasonPhrase}"
                return ToolResult.failure(invocation.toolName, extractError(body, httpError))
            }

            // Standard JSON-RPC 2.0 response: { "result": ..., "error": ... }
            val errNode = body?.get("error")
            if (errNode != null && errNode !is JsonNull) {
                val msg = (errNode as? JsonObject)?.get("message")?.let { (it as? JsonPrimitive)?.content }
                    ?: errNode.toString()
                return ToolResult.failure(invocation.toolName, msg)
            }

            val resultNode = body?.get("result")
            if (resultNode != null) {
                return ToolResult.ok(invocation.toolName, resultNode)
            }

            // No result / error — treat as success with null payload.
            ToolResult.ok(invocation.toolName)
        } catch (ex: Exception) {
            ToolResult.failure(invocation.toolName, ex.message ?: ex.javaClass.simpleName)
        }
    }

    override suspend fun getAvailableToolsAsync(): List<ToolDefinition> {
        val endpoint = serverUri + "tools"
        return try {
            val response = transport.send(
                ToolHttpRequest(ToolHttpVerb.GET, endpoint, headers = mapOf("X-API-Key" to composioApiKey)),
            )
            if (!response.isSuccess) return emptyList()
            val root = toolJson.parseToJsonElement(response.body)
            val parsed = parseToolList(root)
            availableTools = parsed
            parsed
        } catch (_: Exception) {
            emptyList()
        }
    }

    private fun parseToolList(root: JsonElement): List<ToolDefinition> {
        // Composio may return an array at root, or { "tools": [...] }.
        val toolsArray: JsonArray = when {
            root is JsonArray -> root
            root is JsonObject && root["tools"] is JsonArray -> root["tools"] as JsonArray
            else -> return emptyList()
        }

        val result = ArrayList<ToolDefinition>(toolsArray.size)
        for (item in toolsArray) {
            if (item !is JsonObject) continue
            val name = (item["name"] as? JsonPrimitive)?.content
            val desc = (item["description"] as? JsonPrimitive)?.content ?: ""
            if (name.isNullOrBlank()) continue

            val parameters = LinkedHashMap<String, ToolParameter>()
            val required = ArrayList<String>()

            val schema = item["inputSchema"] as? JsonObject
            val props = schema?.get("properties") as? JsonObject
            if (props != null) {
                for ((propName, propValue) in props) {
                    val po = propValue as? JsonObject ?: continue
                    val type = (po["type"] as? JsonPrimitive)?.content ?: "string"
                    val propDesc = (po["description"] as? JsonPrimitive)?.content ?: ""
                    parameters[propName] = ToolParameter(type = type, description = propDesc)
                }
                val req = schema["required"] as? JsonArray
                if (req != null) {
                    for (r in req) {
                        val rName = (r as? JsonPrimitive)?.content
                        if (!rName.isNullOrBlank()) required.add(rName)
                    }
                }
            }

            result.add(
                ToolDefinition(
                    name = name,
                    description = desc,
                    parameters = parameters,
                    requiredParameters = required,
                ),
            )
        }
        return result
    }

    private fun extractError(body: JsonObject?, fallback: String): String {
        val e = body?.get("error") ?: return fallback
        if (e is JsonObject) {
            val m = e["message"]
            if (m is JsonPrimitive) return m.content
            return e.toString()
        }
        return fallback
    }

    companion object {
        const val DEFAULT_SERVER_URI = "https://mcp.composio.dev/"
    }
}
