// IntegrationEmail.swift
//
// Port of the CircleAI.Integration.Email vertical (collapsing the C# folder's
// three files into one):
//   • GmailEmailConnector.cs   → GmailOptions + GmailEmailConnector
//   • MsGraphEmailConnector.cs → MsGraphEmailOptions + MsGraphEmailConnector
//   • ImapEmailConnector.cs    → ImapOptions + ImapEmailConnector
//
// Gmail + MsGraph talk HTTP → the injected `IIntegrationHttpTransport`; every
// URL, JSON body, auth header, and parse step is ported verbatim. IMAP in C#
// uses MailKit (a socket protocol, not HTTP); per the "inject external/socket
// dependency behind an interface" rule, the MailKit surface the connector uses
// is abstracted behind `IImapClient`, with a deterministic in-memory
// `InMemoryImapClient` for tests. The connector logic (search → order-desc →
// take → fetch, flag mapping, mark-read) is ported faithfully.

import Foundation

// MARK: - Gmail

/// Gmail connector config. Port of the C# `GmailOptions`.
public struct GmailOptions: Sendable {
    /// Async callback returning a fresh Bearer token.
    public let accessTokenProvider: @Sendable () async throws -> String?
    public init(accessTokenProvider: @escaping @Sendable () async throws -> String?) {
        self.accessTokenProvider = accessTokenProvider
    }
}

/// Gmail API v1 `IEmailConnector`. Port of the C# `GmailEmailConnector`.
public final class GmailEmailConnector: IEmailConnector, @unchecked Sendable {
    static let baseUri = "https://gmail.googleapis.com/gmail/v1/users/me/"
    private let http: IIntegrationHttpTransport
    private let opts: GmailOptions

    public init(opts: GmailOptions, http: IIntegrationHttpTransport) {
        self.opts = opts
        self.http = http
        if http.baseAddress == nil { http.baseAddress = URL(string: Self.baseUri) }
    }

    public var providerId: String { "gmail" }
    public var isConfigured: Bool { true }

    public func listUnread(max: Int) async throws -> [EmailMessage] {
        try await search(query: "is:unread", max: max)
    }

    public func search(query: String, max: Int) async throws -> [EmailMessage] {
        if query.isBlank { throw IntegrationError.argument("query required") }
        if max <= 0 { throw IntegrationError.argumentOutOfRange("max") }
        try await ensureAuth()

        let listUrl = Self.baseUri + "messages?q=\(IntegrationUri.escapeDataString(query))&maxResults=\(min(max, 100))"
        let listResp = try await http.send(IntegrationHttpRequest(method: .get, url: listUrl))
        try listResp.ensureSuccess()
        let listDoc = try IntegrationJson.parseObject(listResp.body)

        var ids: [String] = []
        if let msgs = IntegrationJson.array(listDoc, "messages") {
            for case let m as [String: Any] in msgs {
                if let id = IntegrationJson.string(m, "id") { ids.append(id) }
            }
        }

        var result: [EmailMessage] = []
        for id in ids {
            let getResp = try await http.send(IntegrationHttpRequest(
                method: .get, url: Self.baseUri + "messages/\(IntegrationUri.escapeDataString(id))?format=full"))
            if !getResp.isSuccessStatusCode { continue }
            let doc = try IntegrationJson.parseObject(getResp.body)
            result.append(Self.parseGmailMessage(doc))
        }
        return result
    }

    public func markRead(messageId: String) async throws {
        if messageId.isBlank { throw IntegrationError.argument("messageId required") }
        try await ensureAuth()
        let body: [String: Any] = ["removeLabelIds": ["UNREAD"]]
        let resp = try await http.send(IntegrationHttpRequest(
            method: .post,
            url: Self.baseUri + "messages/\(IntegrationUri.escapeDataString(messageId))/modify",
            body: try IntegrationJson.encode(body), contentType: .json))
        try resp.ensureSuccess()
    }

    private func ensureAuth() async throws {
        let token = try await opts.accessTokenProvider()
        guard let token, !token.isBlank else {
            throw IntegrationError.invalidOperation("Gmail access token unavailable; refresh OAuth.")
        }
        var headers = http.defaultHeaders
        headers["Authorization"] = "Bearer \(token)"
        http.defaultHeaders = headers
    }

    static func parseGmailMessage(_ msg: [String: Any]) -> EmailMessage {
        let id = IntegrationJson.string(msg, "id") ?? ""
        var labels: [String] = []
        if let labs = IntegrationJson.array(msg, "labelIds") {
            for case let l as String in labs { labels.append(l) }
        }
        let unread = labels.contains { $0.caseInsensitiveCompare("UNREAD") == .orderedSame }

        // Headers (case-insensitive lookup, as the C# uses OrdinalIgnoreCase).
        var headers: [String: String] = [:]
        if let payload = IntegrationJson.object(msg, "payload"),
           let hs = IntegrationJson.array(payload, "headers") {
            for case let h as [String: Any] in hs {
                if let name = IntegrationJson.string(h, "name"), let val = IntegrationJson.string(h, "value") {
                    headers[name.lowercased()] = val
                }
            }
        }
        func header(_ name: String) -> String? { headers[name.lowercased()] }

        let bodyText = Self.extractBody(IntegrationJson.object(msg, "payload"))
        var receivedMs: Int64 = 0
        if let dateStr = IntegrationJson.string(msg, "internalDate"), let ms = Int64(dateStr) { receivedMs = ms }

        let toList: [String]
        if let t = header("To") {
            toList = t.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        } else {
            toList = []
        }

        return EmailMessage(
            messageId: id,
            from: header("From") ?? "",
            to: toList,
            subject: header("Subject") ?? "",
            bodyText: bodyText,
            receivedUtc: Date(timeIntervalSince1970: Double(receivedMs) / 1000.0),
            unread: unread,
            labels: labels)
    }

    static func extractBody(_ payload: [String: Any]?) -> String {
        guard let payload else { return "" }
        if let body = IntegrationJson.object(payload, "body"), let data = IntegrationJson.string(body, "data") {
            return decodeBase64Url(data)
        }
        if let parts = IntegrationJson.array(payload, "parts") {
            // First pass: prefer text/plain.
            for case let part as [String: Any] in parts {
                if let mime = IntegrationJson.string(part, "mimeType"), mime.caseInsensitiveCompare("text/plain") == .orderedSame {
                    return extractBody(part)
                }
            }
            // Second pass: first non-empty.
            for case let part as [String: Any] in parts {
                let content = extractBody(part)
                if !content.isEmpty { return content }
            }
        }
        return ""
    }

    static func decodeBase64Url(_ s: String) -> String {
        if s.isEmpty { return "" }
        var t = s.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        let padding = t.count % 4
        if padding > 0 { t += String(repeating: "=", count: 4 - padding) }
        guard let data = Data(base64Encoded: t), let str = String(data: data, encoding: .utf8) else { return "" }
        return str
    }
}

// MARK: - Microsoft Graph Email

/// MS Graph email connector config. Port of the C# `MsGraphEmailOptions`.
public struct MsGraphEmailOptions: Sendable {
    /// Async callback returning a fresh Bearer token.
    public let accessTokenProvider: @Sendable () async throws -> String?
    public init(accessTokenProvider: @escaping @Sendable () async throws -> String?) {
        self.accessTokenProvider = accessTokenProvider
    }
}

/// Microsoft Graph v1.0 `IEmailConnector` for Outlook / Microsoft 365 mail. Port
/// of the C# `MsGraphEmailConnector`.
public final class MsGraphEmailConnector: IEmailConnector, @unchecked Sendable {
    static let baseUri = "https://graph.microsoft.com/v1.0/"
    private let http: IIntegrationHttpTransport
    private let opts: MsGraphEmailOptions

    public init(opts: MsGraphEmailOptions, http: IIntegrationHttpTransport) {
        self.opts = opts
        self.http = http
        if http.baseAddress == nil { http.baseAddress = URL(string: Self.baseUri) }
    }

    public var providerId: String { "ms-graph-mail" }
    public var isConfigured: Bool { true }

    public func listUnread(max: Int) async throws -> [EmailMessage] {
        try await ensureAuth()
        let path = "me/mailFolders('Inbox')/messages?$filter=isRead+eq+false&$top=\(min(max, 50))&$orderby=receivedDateTime+desc"
        let resp = try await http.send(IntegrationHttpRequest(method: .get, url: Self.baseUri + path))
        try resp.ensureSuccess()
        let doc = try IntegrationJson.parseObject(resp.body)
        return Self.readMessages(doc)
    }

    public func search(query: String, max: Int) async throws -> [EmailMessage] {
        if query.isBlank { throw IntegrationError.argument("query required") }
        try await ensureAuth()
        let path = "me/messages?$search=\(IntegrationUri.escapeDataString(query))&$top=\(min(max, 50))"
        let resp = try await http.send(IntegrationHttpRequest(method: .get, url: Self.baseUri + path))
        try resp.ensureSuccess()
        let doc = try IntegrationJson.parseObject(resp.body)
        return Self.readMessages(doc)
    }

    public func markRead(messageId: String) async throws {
        if messageId.isBlank { throw IntegrationError.argument("messageId required") }
        try await ensureAuth()
        let body: [String: Any] = ["isRead": true]
        let resp = try await http.send(IntegrationHttpRequest(
            method: .patch,
            url: Self.baseUri + "me/messages/\(IntegrationUri.escapeDataString(messageId))",
            body: try IntegrationJson.encode(body), contentType: .json))
        try resp.ensureSuccess()
    }

    private func ensureAuth() async throws {
        let token = try await opts.accessTokenProvider()
        guard let token, !token.isBlank else {
            throw IntegrationError.invalidOperation("Microsoft Graph access token unavailable; refresh OAuth.")
        }
        var headers = http.defaultHeaders
        headers["Authorization"] = "Bearer \(token)"
        http.defaultHeaders = headers
    }

    static func readMessages(_ root: [String: Any]) -> [EmailMessage] {
        var list: [EmailMessage] = []
        guard let arr = IntegrationJson.array(root, "value") else { return list }
        for case let m as [String: Any] in arr {
            var to: [String] = []
            if let rcpts = IntegrationJson.array(m, "toRecipients") {
                for case let r as [String: Any] in rcpts {
                    if let ea = IntegrationJson.object(r, "emailAddress"), let addr = IntegrationJson.string(ea, "address") {
                        to.append(addr)
                    }
                }
            }
            var fromAddr = ""
            if let fr = IntegrationJson.object(m, "from"), let fea = IntegrationJson.object(fr, "emailAddress"),
               let addr = IntegrationJson.string(fea, "address") { fromAddr = addr }

            var received = IntegrationDates.minValue
            if let rd = IntegrationJson.string(m, "receivedDateTime") {
                let d = IntegrationDates.parseUtc(rd)
                if d != IntegrationDates.minValue { received = d }
            }

            var labels: [String] = []
            if let cats = IntegrationJson.array(m, "categories") {
                for case let c as String in cats { labels.append(c) }
            }

            var body = ""
            if let b = IntegrationJson.object(m, "body"), let bc = IntegrationJson.string(b, "content") {
                body = bc
            } else if let bp = IntegrationJson.string(m, "bodyPreview") {
                body = bp
            }

            // C#: Unread = isRead property present AND == JSON false.
            let isReadVal = IntegrationJson.bool(m, "isRead")
            let unread = (isReadVal == false)

            list.append(EmailMessage(
                messageId: IntegrationJson.string(m, "id") ?? "",
                from: fromAddr,
                to: to,
                subject: IntegrationJson.string(m, "subject") ?? "",
                bodyText: body,
                receivedUtc: received,
                unread: unread,
                labels: labels))
        }
        return list
    }
}

// MARK: - IMAP

/// IMAP connector config. Port of the C# `ImapOptions`.
public struct ImapOptions: Sendable, Equatable {
    /// IMAP host (e.g. "imap.fastmail.com").
    public let host: String
    /// Default 993 for IMAPS.
    public let port: Int
    /// Use SSL/TLS. Default true.
    public let useSsl: Bool
    /// IMAP username.
    public let username: String
    /// IMAP password (app-specific recommended).
    public let password: String
    /// Folder to read. Default INBOX.
    public let folder: String

    public init(host: String, port: Int, useSsl: Bool, username: String, password: String, folder: String = "INBOX") {
        self.host = host
        self.port = port
        self.useSsl = useSsl
        self.username = username
        self.password = password
        self.folder = folder
    }
}

/// One IMAP message summary — the subset of the MailKit `Envelope`/`Flags` the
/// connector reads (the injected-protocol analogue of a MailKit fetch result).
public struct ImapMessageSummary: Sendable, Equatable {
    /// The IMAP UID.
    public let uid: UInt32
    /// Sender address (first From mailbox).
    public let from: String
    /// Recipient addresses (To mailboxes).
    public let to: [String]
    /// Subject line.
    public let subject: String
    /// Envelope date (UTC), or nil (→ "now" at read time, per C#).
    public let dateUtc: Date?
    /// Whether the message has the \Seen flag.
    public let seen: Bool
    /// The named flags set on the message (e.g. "Seen", "Flagged").
    public let flags: [String]
    /// The plain-text (or HTML fallback) body.
    public let bodyText: String

    public init(uid: UInt32, from: String, to: [String], subject: String, dateUtc: Date?,
                seen: Bool, flags: [String], bodyText: String) {
        self.uid = uid
        self.from = from
        self.to = to
        self.subject = subject
        self.dateUtc = dateUtc
        self.seen = seen
        self.flags = flags
        self.bodyText = bodyText
    }
}

/// The injected IMAP client — the socket seam standing in for MailKit's
/// `ImapClient`. Real deployments back this with MailKit (or a bridged native
/// client); tests back it with `InMemoryImapClient`.
public protocol IImapClient: AnyObject, Sendable {
    /// Connect + authenticate + open the folder (read-only). Returns the UNSEEN
    /// message summaries (the connector orders/limits them).
    func fetchUnseen(_ opts: ImapOptions) async throws -> [ImapMessageSummary]
    /// Connect + authenticate + open the folder (read-only). Returns summaries
    /// whose body or subject contains `query`.
    func search(_ opts: ImapOptions, query: String) async throws -> [ImapMessageSummary]
    /// Connect + authenticate + open the folder (read-write). Set \Seen on `uid`.
    func markSeen(_ opts: ImapOptions, uid: UInt32) async throws
}

/// Generic IMAP `IEmailConnector` (backed by MailKit in production). Port of the
/// C# `ImapEmailConnector` — the search → order-desc-by-UID → take → fetch
/// pipeline and flag mapping are preserved; the socket work is delegated to the
/// injected `IImapClient`.
public final class ImapEmailConnector: IEmailConnector, @unchecked Sendable {
    private let opts: ImapOptions
    private let client: IImapClient

    public init(opts: ImapOptions, client: IImapClient) {
        self.opts = opts
        self.client = client
    }

    public var providerId: String { "imap" }
    public var isConfigured: Bool { !opts.host.isBlank && !opts.username.isBlank && !opts.password.isBlank }

    public func listUnread(max: Int) async throws -> [EmailMessage] {
        if max <= 0 { throw IntegrationError.argumentOutOfRange("max") }
        let uids = try await client.fetchUnseen(opts)
        let slice = Array(uids.sorted { $0.uid > $1.uid }.prefix(max))
        return slice.map(Self.toMessage)
    }

    public func search(query: String, max: Int) async throws -> [EmailMessage] {
        if query.isBlank { throw IntegrationError.argument("query required") }
        if max <= 0 { throw IntegrationError.argumentOutOfRange("max") }
        let uids = try await client.search(opts, query: query)
        let slice = Array(uids.sorted { $0.uid > $1.uid }.prefix(max))
        return slice.map(Self.toMessage)
    }

    public func markRead(messageId: String) async throws {
        if messageId.isBlank { throw IntegrationError.argument("messageId required") }
        guard let raw = UInt32(messageId) else { throw IntegrationError.argument("Expected an IMAP UID") }
        try await client.markSeen(opts, uid: raw)
    }

    /// Map a MailKit summary to `EmailMessage`, matching the C# `FetchAsync`:
    /// From = first From mailbox; To = To mailboxes; ReceivedUtc = envelope date
    /// or now; Unread = \Seen absent.
    static func toMessage(_ s: ImapMessageSummary) -> EmailMessage {
        EmailMessage(
            messageId: String(s.uid),
            from: s.from,
            to: s.to,
            subject: s.subject,
            bodyText: s.bodyText,
            receivedUtc: s.dateUtc ?? Date(),
            unread: !s.seen,
            labels: s.flags)
    }
}

/// Deterministic in-memory `IImapClient` for tests — a scripted mailbox with no
/// socket. Holds a fixed set of summaries; `search` filters by
/// subject-or-body-contains; `markSeen` flips the \Seen flag.
public final class InMemoryImapClient: IImapClient, @unchecked Sendable {
    private let lock = NSLock()
    private var messages: [UInt32: ImapMessageSummary]

    public init(messages: [ImapMessageSummary] = []) {
        var map: [UInt32: ImapMessageSummary] = [:]
        for m in messages { map[m.uid] = m }
        self.messages = map
    }

    /// Seed / replace a message.
    public func put(_ m: ImapMessageSummary) {
        lock.lock(); messages[m.uid] = m; lock.unlock()
    }

    /// Current view of a message (for assertions).
    public func message(uid: UInt32) -> ImapMessageSummary? {
        lock.lock(); defer { lock.unlock() }
        return messages[uid]
    }

    public func fetchUnseen(_ opts: ImapOptions) async throws -> [ImapMessageSummary] {
        lock.lock(); defer { lock.unlock() }
        return messages.values.filter { !$0.seen }
    }

    public func search(_ opts: ImapOptions, query: String) async throws -> [ImapMessageSummary] {
        lock.lock(); defer { lock.unlock() }
        return messages.values.filter {
            $0.subject.localizedCaseInsensitiveContains(query) || $0.bodyText.localizedCaseInsensitiveContains(query)
        }
    }

    public func markSeen(_ opts: ImapOptions, uid: UInt32) async throws {
        lock.lock(); defer { lock.unlock() }
        guard let m = messages[uid] else { return }
        var flags = m.flags
        if !flags.contains("Seen") { flags.append("Seen") }
        messages[uid] = ImapMessageSummary(
            uid: m.uid, from: m.from, to: m.to, subject: m.subject, dateUtc: m.dateUtc,
            seen: true, flags: flags, bodyText: m.bodyText)
    }
}
