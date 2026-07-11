// IntegrationEmail.kt
//
// Kotlin port of CircleAI.Integration.Email (GmailEmailConnector.cs +
// ImapEmailConnector.cs + MsGraphEmailConnector.cs) — the C# reference is the
// EXACT spec. Three [IEmailConnector] implementations:
//   * Gmail API v1 — host-supplied OAuth bearer; base64url body decode.
//   * Generic IMAP — the MailKit socket dependency is injected behind
//     [ImapTransport] (no real sockets), preserving the fetch/search/mark-read
//     semantics of the C# code.
//   * Microsoft Graph 1.0 — host-supplied OAuth bearer.
//
// Fidelity notes:
//   * Gmail: ListUnread == Search("is:unread"); message walk pulls labelIds,
//     From/To/Subject headers (case-insensitive), text/plain body (recursing
//     parts), internalDate epoch-ms; base64url decode mirrors the C# padding.
//   * IMAP: injected transport yields ordered UID summaries; ListUnread takes
//     NotSeen, Search matches subject OR body, both newest-UID-first, Take(max);
//     MarkRead requires a numeric UID.
//   * Graph: unread via $filter isRead eq false; search via $search; body from
//     body.content else bodyPreview; Unread == isRead == false.

package com.bhengubv.circleai.integrationemail

import com.bhengubv.circleai.integration.AccessTokenProvider
import com.bhengubv.circleai.integration.EmailMessage
import com.bhengubv.circleai.integration.HttpRequest
import com.bhengubv.circleai.integration.HttpTransport
import com.bhengubv.circleai.integration.HttpVerb
import com.bhengubv.circleai.integration.IEmailConnector
import com.bhengubv.circleai.integration.ensureSuccess
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import java.net.URLEncoder
import java.time.Instant
import java.time.OffsetDateTime
import kotlin.math.min

internal val MAIL_JSON = Json { ignoreUnknownKeys = true; isLenient = true }

internal fun mailEsc(s: String): String =
    URLEncoder.encode(s, Charsets.UTF_8).replace("+", "%20")

internal fun JsonObject.strOrNull(key: String): String? {
    val p = this[key] as? JsonPrimitive ?: return null
    return if (p.content == "null" && !p.isString) null else p.content
}

// =====================================================================
// Gmail API v1 (GmailEmailConnector.cs)
// =====================================================================

/** Gmail connector config. Mirrors C# `GmailOptions`. */
data class GmailOptions(val accessTokenProvider: AccessTokenProvider)

/** Gmail API v1 connector. Mirrors C# `GmailEmailConnector`. */
class GmailEmailConnector(
    private val opts: GmailOptions,
    private val http: HttpTransport,
) : IEmailConnector {

    private val baseUri = "https://gmail.googleapis.com/gmail/v1/users/me/"

    override val providerId: String get() = "gmail"
    override val isConfigured: Boolean get() = true

    override suspend fun listUnread(max: Int): List<EmailMessage> = search("is:unread", max)

    override suspend fun search(query: String, max: Int): List<EmailMessage> {
        require(query.isNotBlank()) { "query required" }
        require(max > 0) { "max" }
        val token = ensureAuth()
        val listPath = "messages?q=${mailEsc(query)}&maxResults=${min(max, 100)}"
        val listResp = http.send(HttpRequest(HttpVerb.GET, baseUri + listPath, bearer(token))).ensureSuccess()
        val listRoot = MAIL_JSON.parseToJsonElement(listResp.body).jsonObjectOrEmpty()

        val ids = ArrayList<String>()
        (listRoot["messages"] as? JsonArray)?.forEach { m ->
            (m as? JsonObject)?.strOrNull("id")?.let { ids += it }
        }

        val result = ArrayList<EmailMessage>(ids.size)
        for (id in ids) {
            val getResp = http.send(HttpRequest(HttpVerb.GET, baseUri + "messages/${mailEsc(id)}?format=full", bearer(token)))
            if (!getResp.isSuccess) continue
            result += parseGmailMessage(MAIL_JSON.parseToJsonElement(getResp.body).jsonObjectOrEmpty())
        }
        return result
    }

    override suspend fun markRead(messageId: String) {
        require(messageId.isNotBlank()) { "messageId required" }
        val token = ensureAuth()
        val body = buildJsonObject {
            put("removeLabelIds", buildJsonArray { add(JsonPrimitive("UNREAD")) })
        }
        http.send(
            HttpRequest(
                HttpVerb.POST,
                baseUri + "messages/${mailEsc(messageId)}/modify",
                bearer(token),
                body.toString(),
                "application/json",
            ),
        ).ensureSuccess()
    }

    private suspend fun ensureAuth(): String {
        val token = opts.accessTokenProvider.getToken()
        if (token.isNullOrBlank()) error("Gmail access token unavailable; refresh OAuth.")
        return token
    }

    private fun bearer(token: String) = mapOf("Authorization" to "Bearer $token")

    private companion object {
        fun parseGmailMessage(msg: JsonObject): EmailMessage {
            val id = msg.strOrNull("id") ?: ""
            val labels = ArrayList<String>()
            (msg["labelIds"] as? JsonArray)?.forEach { l -> (l as? JsonPrimitive)?.let { labels += it.content } }
            val unread = labels.any { it.equals("UNREAD", ignoreCase = true) }
            val headers = HashMap<String, String>() // case-insensitive lookup below
            val payload = msg["payload"] as? JsonObject
            (payload?.get("headers") as? JsonArray)?.forEach { h ->
                val ho = h as? JsonObject ?: return@forEach
                val name = ho.strOrNull("name")
                val value = ho.strOrNull("value")
                if (name != null) headers[name.lowercase()] = value ?: ""
            }
            val bodyText = extractBody(payload)
            val receivedMs = (msg["internalDate"] as? JsonPrimitive)?.content?.toLongOrNull() ?: 0L
            // C#: Split(',', RemoveEmptyEntries).Select(Trim) — drop empties on the
            // raw split (pre-trim), then trim, exactly like the reference.
            val to = headers["to"]?.split(",")?.filter { it.isNotEmpty() }?.map { it.trim() } ?: emptyList()
            return EmailMessage(
                messageId = id,
                from = headers["from"] ?: "",
                to = to,
                subject = headers["subject"] ?: "",
                bodyText = bodyText,
                receivedUtc = Instant.ofEpochMilli(receivedMs),
                unread = unread,
                labels = labels,
            )
        }

        fun extractBody(payload: JsonObject?): String {
            if (payload == null) return ""
            val body = payload["body"] as? JsonObject
            (body?.get("data") as? JsonPrimitive)?.let { d ->
                if (d.isString) return decodeBase64Url(d.content)
            }
            val parts = payload["parts"] as? JsonArray
            if (parts != null) {
                for (part in parts) {
                    val po = part as? JsonObject ?: continue
                    if ((po["mimeType"] as? JsonPrimitive)?.content.equals("text/plain", ignoreCase = true)) {
                        return extractBody(po)
                    }
                }
                for (part in parts) {
                    val content = extractBody(part as? JsonObject)
                    if (content.isNotEmpty()) return content
                }
            }
            return ""
        }

        fun decodeBase64Url(s: String): String {
            if (s.isEmpty()) return ""
            var t = s.replace('-', '+').replace('_', '/')
            val padding = t.length % 4
            if (padding > 0) t = t.padEnd(t.length + 4 - padding, '=')
            return runCatching { String(java.util.Base64.getDecoder().decode(t), Charsets.UTF_8) }.getOrDefault("")
        }
    }
}

// =====================================================================
// IMAP (ImapEmailConnector.cs) — MailKit socket dep injected
// =====================================================================

/**
 * One fetched IMAP message summary. Standing in for MailKit's
 * `IMessageSummary` + envelope. [uid] is the numeric IMAP UID.
 */
data class ImapMessageSummary(
    val uid: Long,
    val from: String,
    val to: List<String>,
    val subject: String,
    val bodyText: String,
    val date: Instant?,
    val seen: Boolean,
    val flags: List<String>,
)

/** Folder access mode. Mirrors MailKit `FolderAccess`. */
enum class ImapFolderAccess { READ_ONLY, READ_WRITE }

/**
 * Injected IMAP transport standing in for MailKit's socket client. The host
 * supplies a real implementation; this port drives it deterministically.
 */
interface ImapTransport {
    /** Fetch summaries of unseen messages (NotSeen), any order. */
    suspend fun searchUnseen(folder: String, access: ImapFolderAccess): List<ImapMessageSummary>

    /** Fetch summaries where subject OR body contains [query], any order. */
    suspend fun searchText(folder: String, access: ImapFolderAccess, query: String): List<ImapMessageSummary>

    /** Set the Seen flag on the given UID. */
    suspend fun markSeen(folder: String, uid: Long)
}

/**
 * IMAP connector config. Mirrors C# `ImapOptions`.
 * @param folder Folder to read. Default INBOX.
 */
data class ImapOptions(
    val host: String,
    val port: Int,
    val useSsl: Boolean,
    val username: String,
    val password: String,
    val folder: String = "INBOX",
)

/** Generic IMAP connector. Mirrors C# `ImapEmailConnector`. */
class ImapEmailConnector(
    private val opts: ImapOptions,
    private val imap: ImapTransport,
) : IEmailConnector {

    override val providerId: String get() = "imap"
    override val isConfigured: Boolean
        get() = opts.host.isNotBlank() && opts.username.isNotBlank() && opts.password.isNotBlank()

    override suspend fun listUnread(max: Int): List<EmailMessage> {
        require(max > 0) { "max" }
        val summaries = imap.searchUnseen(opts.folder, ImapFolderAccess.READ_ONLY)
        return toMessages(summaries.sortedByDescending { it.uid }.take(max))
    }

    override suspend fun search(query: String, max: Int): List<EmailMessage> {
        require(query.isNotBlank()) { "query required" }
        require(max > 0) { "max" }
        val summaries = imap.searchText(opts.folder, ImapFolderAccess.READ_ONLY, query)
        return toMessages(summaries.sortedByDescending { it.uid }.take(max))
    }

    override suspend fun markRead(messageId: String) {
        require(messageId.isNotBlank()) { "messageId required" }
        val raw = messageId.toUIntOrNull() ?: throw IllegalArgumentException("Expected an IMAP UID")
        imap.markSeen(opts.folder, raw.toLong())
    }

    private fun toMessages(summaries: List<ImapMessageSummary>): List<EmailMessage> =
        summaries.map { s ->
            EmailMessage(
                messageId = s.uid.toString(),
                from = s.from,
                to = s.to,
                subject = s.subject,
                bodyText = s.bodyText,
                receivedUtc = s.date ?: Instant.now(),
                unread = !s.seen,
                labels = s.flags,
            )
        }
}

// =====================================================================
// Microsoft Graph 1.0 (MsGraphEmailConnector.cs)
// =====================================================================

/** Microsoft Graph mail connector config. Mirrors C# `MsGraphEmailOptions`. */
data class MsGraphEmailOptions(val accessTokenProvider: AccessTokenProvider)

/** Microsoft Graph 1.0 mail connector. Mirrors C# `MsGraphEmailConnector`. */
class MsGraphEmailConnector(
    private val opts: MsGraphEmailOptions,
    private val http: HttpTransport,
) : IEmailConnector {

    private val baseUri = "https://graph.microsoft.com/v1.0/"

    override val providerId: String get() = "ms-graph-mail"
    override val isConfigured: Boolean get() = true

    override suspend fun listUnread(max: Int): List<EmailMessage> {
        val token = ensureAuth()
        val path = "me/mailFolders('Inbox')/messages?\$filter=isRead+eq+false&\$top=${min(max, 50)}&\$orderby=receivedDateTime+desc"
        val resp = http.send(HttpRequest(HttpVerb.GET, baseUri + path, bearer(token))).ensureSuccess()
        return readMessages(MAIL_JSON.parseToJsonElement(resp.body).jsonObjectOrEmpty())
    }

    override suspend fun search(query: String, max: Int): List<EmailMessage> {
        require(query.isNotBlank()) { "query required" }
        val token = ensureAuth()
        val path = "me/messages?\$search=${mailEsc(query)}&\$top=${min(max, 50)}"
        val resp = http.send(HttpRequest(HttpVerb.GET, baseUri + path, bearer(token))).ensureSuccess()
        return readMessages(MAIL_JSON.parseToJsonElement(resp.body).jsonObjectOrEmpty())
    }

    override suspend fun markRead(messageId: String) {
        require(messageId.isNotBlank()) { "messageId required" }
        val token = ensureAuth()
        val body = buildJsonObject { put("isRead", true) }
        http.send(
            HttpRequest(
                HttpVerb.PATCH,
                baseUri + "me/messages/${mailEsc(messageId)}",
                bearer(token),
                body.toString(),
                "application/json",
            ),
        ).ensureSuccess()
    }

    private suspend fun ensureAuth(): String {
        val token = opts.accessTokenProvider.getToken()
        if (token.isNullOrBlank()) error("Microsoft Graph access token unavailable; refresh OAuth.")
        return token
    }

    private fun bearer(token: String) = mapOf("Authorization" to "Bearer $token")

    private companion object {
        fun readMessages(root: JsonObject): List<EmailMessage> {
            val list = ArrayList<EmailMessage>()
            val arr = root["value"] as? JsonArray ?: return list
            for (m in arr) {
                val o = m as? JsonObject ?: continue
                val to = ArrayList<String>()
                (o["toRecipients"] as? JsonArray)?.forEach { r ->
                    val addr = ((r as? JsonObject)?.get("emailAddress") as? JsonObject)?.strOrNull("address")
                    if (addr != null) to += addr
                }
                val fromAddr = ((o["from"] as? JsonObject)?.get("emailAddress") as? JsonObject)?.strOrNull("address") ?: ""
                var received = Instant.MIN
                (o["receivedDateTime"] as? JsonPrimitive)?.let { rd ->
                    if (rd.isString) runCatching { received = OffsetDateTime.parse(rd.content).toInstant() }
                }
                val labels = ArrayList<String>()
                (o["categories"] as? JsonArray)?.forEach { c -> (c as? JsonPrimitive)?.let { labels += it.content } }
                val body = ((o["body"] as? JsonObject)?.strOrNull("content"))
                    ?: o.strOrNull("bodyPreview") ?: ""
                // C#: Unread = TryGetProperty("isRead") && ValueKind == False — i.e.
                // unread only when isRead is PRESENT and explicitly false (absent -> false).
                val unread = (o["isRead"] as? JsonPrimitive)?.content == "false"
                list += EmailMessage(
                    messageId = o.strOrNull("id") ?: "",
                    from = fromAddr,
                    to = to,
                    subject = o.strOrNull("subject") ?: "",
                    bodyText = body,
                    receivedUtc = received,
                    unread = unread,
                    labels = labels,
                )
            }
            return list
        }
    }
}

// ── shared JSON convenience ───────────────────────────────────────────────

internal fun kotlinx.serialization.json.JsonElement.jsonObjectOrEmpty(): JsonObject =
    this as? JsonObject ?: JsonObject(emptyMap())
