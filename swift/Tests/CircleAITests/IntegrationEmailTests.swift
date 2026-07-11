// IntegrationEmailTests.swift
//
// Exercises the Gmail + MsGraph email connectors against
// FakeIntegrationHttpTransport, and the IMAP connector against the deterministic
// InMemoryImapClient. Also unit-tests the Gmail base64url decode / body
// extraction and message parse. Mirrors src/CircleAI.Integration.Email/.

import XCTest
import Foundation
@testable import CircleAI

final class IntegrationEmailTests: XCTestCase {

    // ── Gmail ────────────────────────────────────────────────────────────────

    private func gmail(_ http: IIntegrationHttpTransport, token: String? = "tok") -> GmailEmailConnector {
        GmailEmailConnector(opts: GmailOptions(accessTokenProvider: { token }), http: http)
    }

    func testGmailProviderId() {
        XCTAssertEqual(gmail(FakeIntegrationHttpTransport()).providerId, "gmail")
    }

    func testGmailListUnreadListsThenFetchesEach() async throws {
        let http = FakeIntegrationHttpTransport()
        // list step
        http.on(.get, where: { $0.contains("/messages?q=") }, respond: { _ in
            .json(#"{"messages":[{"id":"m1"},{"id":"m2"}]}"#)
        })
        // per-message fetch step
        let bodyData = GmailEmailConnectorTestSupport.base64Url("Hello body")
        http.on(.get, where: { $0.contains("/messages/m1?format=full") }, respond: { _ in
            .json("""
            {"id":"m1","labelIds":["INBOX","UNREAD"],
             "internalDate":"1700000000000",
             "payload":{"headers":[
                {"name":"From","value":"a@x.com"},
                {"name":"To","value":"me@x.com, you@x.com"},
                {"name":"Subject","value":"Hi"}],
               "body":{"data":"\(bodyData)"}}}
            """)
        })
        http.on(.get, where: { $0.contains("/messages/m2?format=full") }, respond: { _ in
            .json("""
            {"id":"m2","labelIds":["INBOX"],"internalDate":"1700000001000",
             "payload":{"headers":[{"name":"From","value":"b@x.com"},{"name":"Subject","value":"Yo"}],
               "body":{"data":"\(GmailEmailConnectorTestSupport.base64Url("Second"))"}}}
            """)
        })

        let msgs = try await gmail(http).listUnread(max: 10)
        XCTAssertEqual(msgs.map { $0.messageId }, ["m1", "m2"])
        XCTAssertEqual(msgs[0].from, "a@x.com")
        XCTAssertEqual(msgs[0].to, ["me@x.com", "you@x.com"])
        XCTAssertEqual(msgs[0].subject, "Hi")
        XCTAssertEqual(msgs[0].bodyText, "Hello body")
        XCTAssertTrue(msgs[0].unread)   // UNREAD label present
        XCTAssertFalse(msgs[1].unread)  // no UNREAD label
        // the list query used is:unread
        XCTAssertTrue(http.requests.first?.url.contains("q=is%3Aunread") ?? false)
    }

    func testGmailSearchValidatesArgs() async {
        let c = gmail(FakeIntegrationHttpTransport())
        do { _ = try await c.search(query: "  ", max: 5); XCTFail() }
        catch IntegrationError.argument {} catch { XCTFail("wrong \(error)") }
        do { _ = try await c.search(query: "x", max: 0); XCTFail() }
        catch IntegrationError.argumentOutOfRange {} catch { XCTFail("wrong \(error)") }
    }

    func testGmailMarkReadPostsModify() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.post, urlContains: "/messages/m1/modify", json: "{}")
        try await gmail(http).markRead(messageId: "m1")
        let req = try XCTUnwrap(http.lastRequest)
        XCTAssertEqual(req.method, .post)
        let body = try IntegrationJson.parseObject(req.body)
        XCTAssertEqual(IntegrationJson.array(body, "removeLabelIds")?.first as? String, "UNREAD")
    }

    func testGmailBase64UrlDecode() {
        // "SGVsbG8" is base64url for "Hello" (no padding).
        XCTAssertEqual(GmailEmailConnector.decodeBase64Url("SGVsbG8"), "Hello")
        XCTAssertEqual(GmailEmailConnector.decodeBase64Url(""), "")
        // URL-safe chars.
        let enc = GmailEmailConnectorTestSupport.base64Url("a+b/c?")
        XCTAssertEqual(GmailEmailConnector.decodeBase64Url(enc), "a+b/c?")
    }

    func testGmailExtractBodyPrefersTextPlainPart() {
        let payload: [String: Any] = [
            "parts": [
                ["mimeType": "text/html", "body": ["data": GmailEmailConnectorTestSupport.base64Url("<b>html</b>")]],
                ["mimeType": "text/plain", "body": ["data": GmailEmailConnectorTestSupport.base64Url("plain wins")]],
            ],
        ]
        XCTAssertEqual(GmailEmailConnector.extractBody(payload), "plain wins")
    }

    // ── MS Graph mail ────────────────────────────────────────────────────────

    private func graphMail(_ http: IIntegrationHttpTransport, token: String? = "tok") -> MsGraphEmailConnector {
        MsGraphEmailConnector(opts: MsGraphEmailOptions(accessTokenProvider: { token }), http: http)
    }

    func testGraphMailListUnreadParses() async throws {
        let http = FakeIntegrationHttpTransport()
        let json = """
        {"value":[
          {"id":"m1","subject":"Report","isRead":false,
           "from":{"emailAddress":{"address":"a@x.com"}},
           "toRecipients":[{"emailAddress":{"address":"me@x.com"}}],
           "receivedDateTime":"2024-10-02T09:00:00Z",
           "categories":["Work"],
           "body":{"content":"the body"}}
        ]}
        """
        http.on(.get, urlContains: "/me/mailFolders('Inbox')/messages", json: json)
        let msgs = try await graphMail(http).listUnread(max: 10)
        XCTAssertEqual(msgs.count, 1)
        XCTAssertEqual(msgs[0].from, "a@x.com")
        XCTAssertEqual(msgs[0].to, ["me@x.com"])
        XCTAssertEqual(msgs[0].subject, "Report")
        XCTAssertEqual(msgs[0].bodyText, "the body")
        XCTAssertEqual(msgs[0].labels, ["Work"])
        XCTAssertTrue(msgs[0].unread) // isRead == false
    }

    func testGraphMailSearchUsesSearchParam() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.get, urlContains: "/me/messages?$search=", json: #"{"value":[]}"#)
        _ = try await graphMail(http).search(query: "invoice", max: 5)
        XCTAssertTrue(http.lastRequest?.url.contains("$search=invoice") ?? false)
    }

    func testGraphMailMarkReadPatchesIsRead() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.patch, urlContains: "/me/messages/m1", json: "{}")
        try await graphMail(http).markRead(messageId: "m1")
        let req = try XCTUnwrap(http.lastRequest)
        XCTAssertEqual(req.method, .patch)
        let body = try IntegrationJson.parseObject(req.body)
        XCTAssertEqual(IntegrationJson.bool(body, "isRead"), true)
    }

    func testGraphMailBodyPreviewFallback() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.get, urlContains: "/messages", json: """
        {"value":[{"id":"x","subject":"S","isRead":true,"bodyPreview":"preview only"}]}
        """)
        let msgs = try await graphMail(http).search(query: "q", max: 1)
        XCTAssertEqual(msgs[0].bodyText, "preview only")
        XCTAssertFalse(msgs[0].unread) // isRead true
    }

    // ── IMAP ─────────────────────────────────────────────────────────────────

    private func summary(uid: UInt32, subject: String, body: String, seen: Bool) -> ImapMessageSummary {
        ImapMessageSummary(
            uid: uid, from: "s@x.com", to: ["me@x.com"], subject: subject,
            dateUtc: Date(timeIntervalSince1970: TimeInterval(uid)), seen: seen,
            flags: seen ? ["Seen"] : [], bodyText: body)
    }

    func testImapConfigured() {
        let c = ImapEmailConnector(
            opts: ImapOptions(host: "imap.x", port: 993, useSsl: true, username: "u", password: "p"),
            client: InMemoryImapClient())
        XCTAssertEqual(c.providerId, "imap")
        XCTAssertTrue(c.isConfigured)

        let unconf = ImapEmailConnector(
            opts: ImapOptions(host: "", port: 993, useSsl: true, username: "u", password: "p"),
            client: InMemoryImapClient())
        XCTAssertFalse(unconf.isConfigured)
    }

    func testImapListUnreadOrdersDescByUidAndLimits() async throws {
        let client = InMemoryImapClient(messages: [
            summary(uid: 1, subject: "one", body: "b1", seen: false),
            summary(uid: 5, subject: "five", body: "b5", seen: false),
            summary(uid: 3, subject: "three", body: "b3", seen: false),
            summary(uid: 9, subject: "seen", body: "b9", seen: true), // excluded (seen)
        ])
        let c = ImapEmailConnector(
            opts: ImapOptions(host: "imap.x", port: 993, useSsl: true, username: "u", password: "p"),
            client: client)
        let msgs = try await c.listUnread(max: 2)
        // Newest (highest UID) first, limited to 2 → 5, 3.
        XCTAssertEqual(msgs.map { $0.messageId }, ["5", "3"])
        XCTAssertTrue(msgs[0].unread)
    }

    func testImapSearchFiltersBySubjectOrBody() async throws {
        let client = InMemoryImapClient(messages: [
            summary(uid: 1, subject: "Invoice March", body: "pay", seen: false),
            summary(uid: 2, subject: "Hello", body: "contains invoice here", seen: false),
            summary(uid: 3, subject: "Unrelated", body: "nope", seen: false),
        ])
        let c = ImapEmailConnector(
            opts: ImapOptions(host: "imap.x", port: 993, useSsl: true, username: "u", password: "p"),
            client: client)
        let msgs = try await c.search(query: "invoice", max: 10)
        XCTAssertEqual(Set(msgs.map { $0.messageId }), ["1", "2"]) // both match, order desc handled separately
    }

    func testImapMarkReadFlipsSeenFlag() async throws {
        let client = InMemoryImapClient(messages: [summary(uid: 7, subject: "s", body: "b", seen: false)])
        let c = ImapEmailConnector(
            opts: ImapOptions(host: "imap.x", port: 993, useSsl: true, username: "u", password: "p"),
            client: client)
        XCTAssertEqual(client.message(uid: 7)?.seen, false)
        try await c.markRead(messageId: "7")
        XCTAssertEqual(client.message(uid: 7)?.seen, true)
        XCTAssertTrue(client.message(uid: 7)?.flags.contains("Seen") ?? false)
    }

    func testImapMarkReadRejectsNonUid() async {
        let c = ImapEmailConnector(
            opts: ImapOptions(host: "imap.x", port: 993, useSsl: true, username: "u", password: "p"),
            client: InMemoryImapClient())
        do { try await c.markRead(messageId: "not-a-number"); XCTFail() }
        catch IntegrationError.argument {} catch { XCTFail("wrong \(error)") }
    }
}

/// Test helper: base64url-encode a UTF-8 string the way Gmail returns bodies
/// (unpadded, `-`/`_` alphabet).
enum GmailEmailConnectorTestSupport {
    static func base64Url(_ s: String) -> String {
        var b = Data(s.utf8).base64EncodedString()
        b = b.replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
        b = b.replacingOccurrences(of: "=", with: "")
        return b
    }
}
