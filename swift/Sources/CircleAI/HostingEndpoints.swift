// HostingEndpoints.swift
//
// Port of the CircleAI.Hosting transport surface:
//   - IAIEndpoint.cs                → IAIEndpoint (transport-agnostic endpoint)
//   - Endpoints/InProcessEndpoint.cs→ InProcessEndpoint (no transport; direct ref)
//   - Endpoints/HttpLoopbackEndpoint.cs → LoopbackRouter (auth + routing + SSE framing)
//                                     behind an injected listener seam (HttpListener
//                                     has no portable Swift analogue), plus
//                                     GenerationOptionsPayload conversion + the
//                                     constant-time token comparison.
//   - Endpoints/AIHttpClient.cs     → AIHttpClient (mirrors IAIService over the
//                                     injected IButlerHttpTransport)
//
// The wire contracts (routes, X-Butler-Token header, SSE JSON framing, 401/405/
// 404/400/502 status semantics) are ported exactly; only the socket layer is
// swapped for an injected request/response model.

import Foundation

// =====================================================================
// IAIEndpoint + InProcessEndpoint
// =====================================================================

/// Transport-agnostic endpoint that exposes an `IAIService`. Ported from
/// `IAIEndpoint`.
public protocol IAIEndpoint: AnyObject, Sendable {
    /// Begins serving requests against `service`. Idempotent after first success.
    func start(_ service: IAIService) async throws
    /// Stops accepting new requests and drains in-flight ones.
    func stop() async throws
    /// Release resources.
    func dispose() async
}

/// In-process endpoint. No transport — exposes the underlying `IAIService`
/// directly so callers can invoke it as a regular object. Ported from
/// `InProcessEndpoint`.
public final class InProcessEndpoint: IAIEndpoint, @unchecked Sendable {
    private let lock = NSLock()
    private var service: IAIService?
    private var started = false
    private var disposed = false

    public init() {}

    /// The wrapped service. nil until `start(_:)` has run.
    public var serviceAccessor: IAIService? {
        lock.lock(); defer { lock.unlock() }; return service
    }

    public func start(_ service: IAIService) async throws {
        lock.lock(); defer { lock.unlock() }
        if disposed { throw AIServiceError.disposed }
        if started { return }
        self.service = service
        started = true
    }

    public func stop() async throws {
        lock.lock(); started = false; service = nil; lock.unlock()
    }

    public func dispose() async {
        lock.lock(); disposed = true; started = false; service = nil; lock.unlock()
    }
}

// =====================================================================
// Loopback wire payloads + router
// =====================================================================

/// Sampling knobs carried over the wire. Mirrors the C#
/// `HttpLoopbackEndpoint.GenerationOptionsPayload` / `AIHttpClient` payload.
public struct GenerationOptionsPayload: Codable, Sendable {
    public var maxTokens: Int?
    public var temperature: Float?
    public var topP: Float?
    public var topK: Int?
    public var seed: Int?
    public var stopSequences: [String]?

    public init(maxTokens: Int? = nil, temperature: Float? = nil, topP: Float? = nil,
                topK: Int? = nil, seed: Int? = nil, stopSequences: [String]? = nil) {
        self.maxTokens = maxTokens
        self.temperature = temperature
        self.topP = topP
        self.topK = topK
        self.seed = seed
        self.stopSequences = stopSequences
    }

    public static func from(_ o: GenerationOptions) -> GenerationOptionsPayload {
        GenerationOptionsPayload(
            maxTokens: o.maxTokens, temperature: o.temperature, topP: o.topP,
            topK: o.topK, seed: o.seed, stopSequences: o.stopSequences)
    }

    public func toGenerationOptions() -> GenerationOptions {
        let d = GenerationOptions()
        return GenerationOptions(
            maxTokens: maxTokens ?? d.maxTokens,
            temperature: temperature ?? d.temperature,
            topP: topP ?? d.topP,
            topK: topK ?? d.topK,
            seed: seed,
            stopSequences: stopSequences)
    }
}

/// A parsed loopback HTTP request handed to `LoopbackRouter`. Models the subset
/// of `HttpListenerContext` the router reads.
public struct LoopbackRequest: Sendable {
    public let method: String
    public let path: String
    public let token: String?          // value of the X-Butler-Token header
    public let body: String

    public init(method: String, path: String, token: String?, body: String) {
        self.method = method
        self.path = path
        self.token = token
        self.body = body
    }
}

/// A non-streaming loopback response. `contentType` mirrors the C# writer
/// (text/plain or application/json).
public struct LoopbackResponse: Sendable, Equatable {
    public let statusCode: Int
    public let contentType: String
    public let body: String

    public init(statusCode: Int, contentType: String, body: String) {
        self.statusCode = statusCode
        self.contentType = contentType
        self.body = body
    }
}

/// Routes loopback requests to an `IAIService` with the exact C#
/// `HttpLoopbackEndpoint` semantics — shared-secret auth, POST-only, the four
/// `/butler/*` routes, and SSE framing — behind an injected model so no
/// `HttpListener` is required. Ported from `HttpLoopbackEndpoint`.
///
/// The bound token is generated at construction when `AIOptions.loopbackToken`
/// is nil, exactly as the C# endpoint does at start; read it via `token`.
public final class LoopbackRouter: IAIEndpoint, @unchecked Sendable {
    private let options: AIOptions
    private let lock = NSLock()
    private var service: IAIService?
    private var tokenValue: String?
    private var started = false
    private var disposed = false

    public init(options: AIOptions) {
        self.options = options
    }

    /// Effective shared-secret token. nil until started.
    public var token: String? { lock.lock(); defer { lock.unlock() }; return tokenValue }

    public func start(_ service: IAIService) async throws {
        lock.lock()
        if disposed { lock.unlock(); throw AIServiceError.disposed }
        if started { lock.unlock(); return }
        self.service = service
        self.tokenValue = (options.loopbackToken?.isEmpty == false)
            ? options.loopbackToken
            : AIOptions.generateRandomToken()
        started = true
        lock.unlock()
    }

    public func stop() async throws {
        lock.lock(); started = false; service = nil; lock.unlock()
    }

    public func dispose() async {
        lock.lock(); disposed = true; started = false; service = nil; lock.unlock()
    }

    // ------------------------------------------------------------------
    // Non-streaming routes: ask / chat / tool
    // ------------------------------------------------------------------

    /// Handle a non-streaming loopback request. Streaming (`/butler/stream`) uses
    /// `handleStream` instead. Mirrors `HandleRequestAsync` + route handlers.
    public func handle(_ request: LoopbackRequest) async -> LoopbackResponse {
        if !authorise(request.token) {
            return plain(401, "unauthorised")
        }
        if request.method.caseInsensitiveCompare("POST") != .orderedSame {
            return plain(405, "method not allowed")
        }
        guard let service = currentService() else {
            return plain(500, "internal error")
        }

        switch request.path {
        case "/butler/ask":
            guard let q = Self.decode(AskPayload.self, request.body)?.question,
                  !q.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                return plain(400, "missing 'question'")
            }
            let answer = (try? await service.ask(q)) ?? ""
            return plain(200, answer)

        case "/butler/chat":
            guard let payload = Self.decode(ChatPayload.self, request.body),
                  let messages = payload.messages, !messages.isEmpty else {
                return plain(400, "missing 'messages'")
            }
            let msgs = messages.map { ChatMessage(role: $0.role ?? "user", content: $0.content ?? "") }
            let opts = payload.options?.toGenerationOptions()
            let content = (try? await service.chat(msgs, options: opts)) ?? ""
            return json(200, ["content": content])

        case "/butler/tool":
            guard let payload = Self.decode(ToolPayload.self, request.body),
                  let toolName = payload.toolName,
                  !toolName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                return plain(400, "missing 'toolName'")
            }
            var args: [String: Any?] = [:]
            for (k, v) in (payload.arguments ?? [:]) { args[k] = v.value }
            let invocation = ToolInvocation(toolName: toolName, arguments: args)
            let result = (try? await service.invokeTool(invocation))
                ?? ToolResult.failure(toolName: toolName, error: "invoke failed")
            return json(result.success ? 200 : 502, [
                "toolName": result.toolName,
                "success": result.success,
                "error": result.error ?? NSNull(),
            ])

        case "/butler/stream":
            // Streaming path — callers should invoke handleStream. Signal misuse
            // with a plain error so behaviour is deterministic.
            return plain(400, "use handleStream for /butler/stream")

        default:
            return plain(404, "not found")
        }
    }

    /// Handle `/butler/stream`, yielding SSE-framed lines exactly as the C#
    /// endpoint writes them: `data: {json}\n\n` per chunk, then an
    /// `event: done\ndata: {}\n\n` terminator. Auth failures yield a single
    /// plain frame.
    public func handleStream(_ request: LoopbackRequest) -> AsyncThrowingStream<String, Error> {
        AsyncThrowingStream { continuation in
            let task = Task {
                if !self.authorise(request.token) {
                    continuation.yield("unauthorised"); continuation.finish(); return
                }
                guard request.method.caseInsensitiveCompare("POST") == .orderedSame else {
                    continuation.yield("method not allowed"); continuation.finish(); return
                }
                guard let service = self.currentService(),
                      let payload = Self.decode(ChatPayload.self, request.body),
                      let messages = payload.messages, !messages.isEmpty else {
                    continuation.yield("missing 'messages'"); continuation.finish(); return
                }
                let msgs = messages.map { ChatMessage(role: $0.role ?? "user", content: $0.content ?? "") }
                let opts = payload.options?.toGenerationOptions()
                do {
                    for try await piece in service.stream(msgs, options: opts) {
                        if Task.isCancelled { break }
                        let encoded = Self.jsonString(piece)
                        continuation.yield("data: \(encoded)\n\n")
                    }
                    // Closing event.
                    continuation.yield("event: done\ndata: {}\n\n")
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private func currentService() -> IAIService? { lock.lock(); defer { lock.unlock() }; return service }

    private func authorise(_ supplied: String?) -> Bool {
        lock.lock(); let tok = tokenValue; lock.unlock()
        guard let tok = tok, !tok.isEmpty else { return false }
        guard let supplied = supplied, !supplied.isEmpty else { return false }
        return Self.cryptographicEquals(supplied, tok)
    }

    /// Length-then-constant-time comparison. Mirrors the C# `CryptographicEquals`.
    static func cryptographicEquals(_ a: String, _ b: String) -> Bool {
        let ab = Array(a.unicodeScalars), bb = Array(b.unicodeScalars)
        if ab.count != bb.count { return false }
        var diff: UInt32 = 0
        for i in 0..<ab.count { diff |= ab[i].value ^ bb[i].value }
        return diff == 0
    }

    private func plain(_ status: Int, _ text: String) -> LoopbackResponse {
        LoopbackResponse(statusCode: status, contentType: "text/plain; charset=utf-8", body: text)
    }

    private func json(_ status: Int, _ dict: [String: Any]) -> LoopbackResponse {
        let body = (try? JSONSerialization.data(withJSONObject: dict, options: [.sortedKeys]))
            .flatMap { String(data: $0, encoding: .utf8) } ?? "{}"
        return LoopbackResponse(statusCode: status, contentType: "application/json; charset=utf-8", body: body)
    }

    private static func jsonString(_ s: String) -> String {
        let data = (try? JSONSerialization.data(withJSONObject: [s], options: [])) ?? Data()
        if let arr = String(data: data, encoding: .utf8), arr.count >= 2 {
            return String(arr.dropFirst().dropLast())
        }
        return "\"\(s)\""
    }

    private static func decode<T: Decodable>(_ type: T.Type, _ body: String) -> T? {
        guard !body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              let data = body.data(using: .utf8) else { return nil }
        let dec = JSONDecoder()
        return try? dec.decode(T.self, from: data)
    }

    // ---- wire payloads (mirror HttpLoopbackEndpoint) ----

    struct AskPayload: Codable { var question: String? }
    struct ChatMessagePayload: Codable { var role: String?; var content: String? }
    struct ChatPayload: Codable { var messages: [ChatMessagePayload]?; var options: GenerationOptionsPayload? }
    struct ToolPayload: Codable { var toolName: String?; var arguments: [String: AnyCodable]? }
}

// =====================================================================
// AIHttpClient (out-of-process client for the loopback endpoint)
// =====================================================================

/// HTTP client mirroring `IAIService` over the injected transport, for callers
/// that talk to a `LoopbackRouter`/HttpLoopbackEndpoint out-of-process. Routes
/// are `butler/{ask,chat,stream,tool}`. Ported from `AIHttpClient`.
public final class AIHttpClient: @unchecked Sendable {
    private let transport: any IButlerHttpTransport

    public init(transport: any IButlerHttpTransport) {
        self.transport = transport
    }

    public func ask(_ question: String) async throws -> String {
        precondition(!question.isEmpty, "question required")
        let body = Self.jsonObject(["question": question])
        return try await transport.post(path: "butler/ask", bodyJson: body)
    }

    public func chat(_ messages: [ChatMessage], options: GenerationOptions? = nil) async throws -> String {
        let body = Self.chatBody(messages, options)
        let resp = try await transport.post(path: "butler/chat", bodyJson: body)
        guard let data = resp.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return "" }
        return (obj["content"] as? String) ?? (obj["Content"] as? String) ?? ""
    }

    public func stream(_ messages: [ChatMessage], options: GenerationOptions? = nil) -> AsyncThrowingStream<String, Error> {
        let body = Self.chatBody(messages, options)
        let raw = transport.postStream(path: "butler/stream", bodyJson: body)
        return AsyncThrowingStream { continuation in
            let task = Task {
                do {
                    for try await line in raw {
                        if Task.isCancelled { break }
                        if line.isEmpty { continue } // SSE event separator
                        if line.hasPrefix("event:") {
                            let ev = String(line.dropFirst("event:".count)).trimmingCharacters(in: .whitespaces)
                            if ev == "done" { break }
                            continue
                        }
                        guard line.hasPrefix("data:") else { continue }
                        let dataPart = String(line.dropFirst("data:".count)).trimmingCharacters(in: .whitespaces)
                        if dataPart.isEmpty { continue }
                        // Server sends JSON-encoded strings; tolerate plain text.
                        let piece: String
                        if let d = dataPart.data(using: .utf8),
                           let decoded = try? JSONDecoder().decode(String.self, from: d) {
                            piece = decoded
                        } else {
                            piece = dataPart
                        }
                        if !piece.isEmpty { continuation.yield(piece) }
                    }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    public func invokeTool(_ invocation: ToolInvocation) async throws -> ToolResult {
        var argDict: [String: Any] = [:]
        for (k, v) in invocation.arguments { argDict[k] = v ?? NSNull() }
        let body = Self.jsonObject(["toolName": invocation.toolName, "arguments": argDict])
        let resp = try await transport.post(path: "butler/tool", bodyJson: body)
        guard let data = resp.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return ToolResult(toolName: invocation.toolName, success: false,
                              error: "Empty response from Butler endpoint.")
        }
        let success = (obj["success"] as? Bool) ?? (obj["Success"] as? Bool) ?? false
        let error = (obj["error"] as? String) ?? (obj["Error"] as? String)
        let result = obj["result"] ?? obj["Result"]
        return ToolResult(toolName: invocation.toolName, success: success,
                          result: result, error: error)
    }

    private static func chatBody(_ messages: [ChatMessage], _ options: GenerationOptions?) -> String {
        var dict: [String: Any] = [
            "messages": messages.map { ["role": $0.role, "content": $0.content] },
        ]
        if let o = options {
            var od: [String: Any] = [
                "maxTokens": o.maxTokens, "temperature": o.temperature,
                "topP": o.topP, "topK": o.topK,
            ]
            if let seed = o.seed { od["seed"] = seed }
            if let stops = o.stopSequences { od["stopSequences"] = stops }
            dict["options"] = od
        }
        return jsonObject(dict)
    }

    private static func jsonObject(_ dict: [String: Any]) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: dict, options: [.sortedKeys]),
              let s = String(data: data, encoding: .utf8) else { return "{}" }
        return s
    }
}

// =====================================================================
// AnyCodable — minimal JSON value box for tool-argument decoding
// =====================================================================

/// Minimal type-erased JSON value used to decode `arguments` bags whose value
/// types are not known ahead of time.
public struct AnyCodable: Codable, @unchecked Sendable {
    public let value: Any?

    public init(_ value: Any?) { self.value = value }

    public init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() { value = nil }
        else if let b = try? c.decode(Bool.self) { value = b }
        else if let i = try? c.decode(Int.self) { value = i }
        else if let d = try? c.decode(Double.self) { value = d }
        else if let s = try? c.decode(String.self) { value = s }
        else if let a = try? c.decode([AnyCodable].self) { value = a.map { $0.value } }
        else if let o = try? c.decode([String: AnyCodable].self) {
            value = o.mapValues { $0.value }
        } else { value = nil }
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch value {
        case nil: try c.encodeNil()
        case let b as Bool: try c.encode(b)
        case let i as Int: try c.encode(i)
        case let d as Double: try c.encode(d)
        case let s as String: try c.encode(s)
        default: try c.encodeNil()
        }
    }
}
