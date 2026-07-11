// FakeTransport.kt
//
// Test doubles for the injected [HttpTransport] / [ImapTransport] used by the
// Integration connectors. Deterministic, in-memory, no real network.

package com.bhengubv.circleai.integration.support

import com.bhengubv.circleai.integration.HttpRequest
import com.bhengubv.circleai.integration.HttpResponse
import com.bhengubv.circleai.integration.HttpTransport
import com.bhengubv.circleai.integrationemail.ImapFolderAccess
import com.bhengubv.circleai.integrationemail.ImapMessageSummary
import com.bhengubv.circleai.integrationemail.ImapTransport

/**
 * Records every request and replies from a handler. The handler receives the
 * request and returns [HttpResponse]; default is 200 with an empty body.
 */
class FakeTransport(
    private val handler: (HttpRequest) -> HttpResponse = { HttpResponse(200, "") },
) : HttpTransport {
    val requests = mutableListOf<HttpRequest>()

    override suspend fun send(request: HttpRequest): HttpResponse {
        requests += request
        return handler(request)
    }

    val last: HttpRequest get() = requests.last()
}

/** Convenience: reply 200 with [body] for every request. */
fun okTransport(body: String): FakeTransport = FakeTransport { HttpResponse(200, body) }

/** Convenience: map request URL (by `contains`) to a response body. */
fun routedTransport(vararg routes: Pair<String, String>, status: Int = 200): FakeTransport =
    FakeTransport { req ->
        val match = routes.firstOrNull { req.url.contains(it.first) }
        if (match != null) HttpResponse(status, match.second) else HttpResponse(404, "")
    }

/** In-memory IMAP transport. */
class FakeImapTransport(
    val unseen: List<ImapMessageSummary> = emptyList(),
    val textHits: List<ImapMessageSummary> = emptyList(),
) : ImapTransport {
    val markedSeen = mutableListOf<Long>()

    override suspend fun searchUnseen(folder: String, access: ImapFolderAccess): List<ImapMessageSummary> = unseen
    override suspend fun searchText(folder: String, access: ImapFolderAccess, query: String): List<ImapMessageSummary> = textHits
    override suspend fun markSeen(folder: String, uid: Long) { markedSeen += uid }
}
