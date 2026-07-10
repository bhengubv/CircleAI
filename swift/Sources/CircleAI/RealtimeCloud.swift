// RealtimeCloud.swift
//
// Port of the CircleAI.Realtime.Cloud module:
//   • IRealtimeTransport.cs         — IRealtimeTransport, IRealtimeTransportFactory,
//                                      NullRealtimeTransportFactory.
//   • Options.cs                    — OpenAiRealtimeOptions, GeminiLiveOptions,
//                                      NovaSonicOptions, ElevenLabsConvOptions,
//                                      UltravoxOptions.
//   • RealtimeWebSocketSession.cs    — the transport-backed IRealtimeSession +
//                                      the lenient cross-vendor JSON event parser.
//   • OpenAiRealtimeService.cs       — OpenAI Realtime connector.
//   • GeminiLiveService.cs           — Gemini Live connector.
//   • NovaSonicService.cs            — AWS Nova Sonic connector.
//   • ElevenLabsConvService.cs       — ElevenLabs Conversational connector.
//   • UltravoxService.cs             — Ultravox connector (two-step: POST → WS).
//
// Host-supplied WebSocket transport. Connectors are framework-free; the host
// wires the actual socket against `IRealtimeTransport`. This mirrors how the
// codebase already injects the one HTTP/socket leaf for every cloud provider
// (see VisionCloud.swift's `IImageHttpTransport`, NetworkingWebSocket.swift's
// `IWebSocketSocket`) so the SDK bakes in no client / provider key.
//
// C# `Uri` maps to `String` (the endpoint text), matching VisionCloud's
// `baseAddress: String`. `Uri.EscapeDataString` maps to `escapeDataString(_:)`
// below (RFC-3986 unreserved set). `ILogger` has no SwiftPM analogue here and is
// dropped: the only use in the reference is a Debug-level "could not parse
// frame" log, which this port swallows (the parser already returns nil and
// continues), consistent with VoiceListener.swift's fail-soft handling.

import Foundation

// =====================================================================
// IRealtimeTransport.cs
// =====================================================================

/// WebSocket-style transport for a realtime session. Port of
/// `CircleAI.Realtime.Cloud.IRealtimeTransport` (`IAsyncDisposable` →
/// `dispose() async`).
public protocol IRealtimeTransport: AnyObject, Sendable {
    /// Send one JSON text frame.
    func sendText(_ text: String) async throws

    /// Send one binary frame.
    func sendBinary(_ bytes: Data) async throws

    /// Stream incoming text frames.
    func receiveText() -> AsyncStream<String>

    /// Stream incoming binary frames.
    func receiveBinary() -> AsyncStream<Data>

    /// Close the connection cleanly.
    func close() async throws

    /// True while the underlying socket is open.
    var isOpen: Bool { get }

    /// Tear down the transport (mirrors `IAsyncDisposable.DisposeAsync`).
    func dispose() async
}

/// Factory that produces transports for a given endpoint. Port of
/// `CircleAI.Realtime.Cloud.IRealtimeTransportFactory`. `Uri` maps to the
/// endpoint text (`String`); the header map is optional exactly as in C#.
public protocol IRealtimeTransportFactory: AnyObject, Sendable {
    /// Connect to `endpoint` with the given headers.
    func connect(endpoint: String, headers: [String: String]?) async throws -> IRealtimeTransport
}

/// Default transport factory that throws on connect — the host wires the real
/// one. Port of `CircleAI.Realtime.Cloud.NullRealtimeTransportFactory`.
public final class NullRealtimeTransportFactory: IRealtimeTransportFactory, @unchecked Sendable {
    public static let instance = NullRealtimeTransportFactory()
    public init() {}

    public func connect(endpoint: String, headers: [String: String]?) async throws -> IRealtimeTransport {
        throw RealtimeError.noVendorRegistered(
            "No IRealtimeTransportFactory is registered. Add the host package that provides a real ClientWebSocket-based factory.")
    }
}

// =====================================================================
// Options.cs
// =====================================================================

/// OpenAI Realtime options. Bearer auth + WSS endpoint. Port of
/// `CircleAI.Realtime.Cloud.OpenAiRealtimeOptions`.
public struct OpenAiRealtimeOptions: Sendable, Equatable {
    public let webSocketEndpoint: String
    public let apiKey: String?
    public let defaultModel: String
    /// Beta header value required by OpenAI Realtime.
    public let betaHeader: String

    public init(
        webSocketEndpoint: String = "wss://api.openai.com/v1/realtime",
        apiKey: String? = nil,
        defaultModel: String = "gpt-4o-realtime-preview-2024-12-17",
        betaHeader: String = "realtime=v1"
    ) {
        self.webSocketEndpoint = webSocketEndpoint
        self.apiKey = apiKey
        self.defaultModel = defaultModel
        self.betaHeader = betaHeader
    }
}

/// Google Gemini Live options. Port of `CircleAI.Realtime.Cloud.GeminiLiveOptions`.
public struct GeminiLiveOptions: Sendable, Equatable {
    public let webSocketEndpoint: String
    public let apiKey: String?
    public let defaultModel: String

    public init(
        webSocketEndpoint: String = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent",
        apiKey: String? = nil,
        defaultModel: String = "models/gemini-2.0-flash-exp"
    ) {
        self.webSocketEndpoint = webSocketEndpoint
        self.apiKey = apiKey
        self.defaultModel = defaultModel
    }
}

/// AWS Nova Sonic options. Uses SigV4 auth on the WS handshake. Port of
/// `CircleAI.Realtime.Cloud.NovaSonicOptions`.
public struct NovaSonicOptions: Sendable, Equatable {
    /// AWS region (e.g. `us-east-1`).
    public let region: String
    public let accessKeyId: String?
    public let secretAccessKey: String?
    public let sessionToken: String?
    public let defaultModel: String

    public init(
        region: String = "us-east-1",
        accessKeyId: String? = nil,
        secretAccessKey: String? = nil,
        sessionToken: String? = nil,
        defaultModel: String = "amazon.nova-sonic-v1:0"
    ) {
        self.region = region
        self.accessKeyId = accessKeyId
        self.secretAccessKey = secretAccessKey
        self.sessionToken = sessionToken
        self.defaultModel = defaultModel
    }
}

/// ElevenLabs Conversational AI options. Port of
/// `CircleAI.Realtime.Cloud.ElevenLabsConvOptions`.
public struct ElevenLabsConvOptions: Sendable, Equatable {
    public let webSocketEndpoint: String
    public let apiKey: String?
    /// ElevenLabs Agent id created in their dashboard.
    public let agentId: String?

    public init(
        webSocketEndpoint: String = "wss://api.elevenlabs.io/v1/convai/conversation",
        apiKey: String? = nil,
        agentId: String? = nil
    ) {
        self.webSocketEndpoint = webSocketEndpoint
        self.apiKey = apiKey
        self.agentId = agentId
    }
}

/// Ultravox options. Port of `CircleAI.Realtime.Cloud.UltravoxOptions`.
public struct UltravoxOptions: Sendable, Equatable {
    /// Ultravox HTTP API endpoint (for session creation).
    public let apiEndpoint: String
    public let apiKey: String?
    public let defaultModel: String
    public let defaultVoice: String

    public init(
        apiEndpoint: String = "https://api.ultravox.ai",
        apiKey: String? = nil,
        defaultModel: String = "fixie-ai/ultravox-70B",
        defaultVoice: String = "Mark"
    ) {
        self.apiEndpoint = apiEndpoint
        self.apiKey = apiKey
        self.defaultModel = defaultModel
        self.defaultVoice = defaultVoice
    }
}

// =====================================================================
// URL escaping — Uri.EscapeDataString analogue
// =====================================================================

/// Percent-encode per `Uri.EscapeDataString`: every byte except the RFC-3986
/// unreserved set (`A–Z a–z 0–9 - _ . ~`) is %-encoded from its UTF-8 bytes with
/// uppercase hex. Used to build vendor query strings exactly as the C# services
/// do. (Byte-level, matching the tree's existing `NetworkingHttp` escaper rather
/// than `addingPercentEncoding`, which would spare all Unicode alphanumerics.)
internal func escapeDataString(_ s: String) -> String {
    let unreserved = Set("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~".utf8)
    func hex(_ v: UInt8) -> Character {
        let digits = Array("0123456789ABCDEF")
        return digits[Int(v)]
    }
    var out = ""
    out.reserveCapacity(s.count)
    for byte in s.utf8 {
        if unreserved.contains(byte) {
            out.append(Character(UnicodeScalar(byte)))
        } else {
            out.append("%")
            out.append(hex((byte >> 4) & 0xF))
            out.append(hex(byte & 0xF))
        }
    }
    return out
}

// =====================================================================
// RealtimeWebSocketSession.cs
// =====================================================================

/// Concrete `IRealtimeSession` backed by an `IRealtimeTransport`. Vendor-specific
/// JSON envelope translation lives here; binary frames become
/// `RealtimeAudioFrame` in the config's format. Port of
/// `CircleAI.Realtime.Cloud.RealtimeWebSocketSession`.
public final class RealtimeWebSocketSession: IRealtimeSession, @unchecked Sendable {
    private let transport: IRealtimeTransport
    private let config: RealtimeSessionConfig
    private let providerId: String
    // C# `Guid.NewGuid().ToString("n")` — 32 lowercase hex digits, no dashes.
    private let sessionIdValue: String = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()

    public init(
        transport: IRealtimeTransport,
        config: RealtimeSessionConfig,
        providerId: String
    ) {
        self.transport = transport
        self.config = config
        self.providerId = providerId
    }

    public var sessionId: String { sessionIdValue }

    public func receiveAudio() -> AsyncStream<RealtimeAudioFrame> {
        let source = transport.receiveBinary()
        let fmt = config.audioFormat
        return AsyncStream(bufferingPolicy: .unbounded) { continuation in
            let task = Task {
                for await frame in source {
                    continuation.yield(RealtimeAudioFrame(pcm: frame, format: fmt, offset: 0))
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    public func sendAudio(_ frame: RealtimeAudioFrame) async throws {
        try await transport.sendBinary(frame.pcm)
    }

    public func sendText(_ text: String) async throws {
        // Vendor-neutral envelope. Host-specific shims may translate.
        let json = Self.serialize([
            "type": "user.text",
            "provider": providerId,
            "text": text,
        ])
        try await transport.sendText(json)
    }

    public func sendToolResult(callId: String, resultJson: String) async throws {
        let json = Self.serialize([
            "type": "tool.result",
            "provider": providerId,
            "call_id": callId,
            "result_json": resultJson,
        ])
        try await transport.sendText(json)
    }

    public func cancelResponse() async throws {
        let json = Self.serialize([
            "type": "response.cancel",
            "provider": providerId,
        ])
        try await transport.sendText(json)
    }

    public func receiveEvents() -> AsyncStream<RealtimeEvent> {
        let source = transport.receiveText()
        return AsyncStream(bufferingPolicy: .unbounded) { continuation in
            let task = Task {
                for await text in source {
                    // The parser is total + fail-soft: a frame it cannot map
                    // yields nil and is skipped (mirrors the C# try/catch +
                    // Debug log, minus the logger).
                    if let ev = RealtimeWebSocketSession.parseEvent(text) {
                        continuation.yield(ev)
                    }
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    public func dispose() async {
        do { try await transport.close() } catch { /* swallow, matches C# `catch { }` */ }
        await transport.dispose()
    }

    // ── JSON helpers ────────────────────────────────────────────────────────────

    /// Serialise a `[String: String]` object to compact JSON with stable key
    /// order (the outbound envelopes are small, fixed-shape objects).
    private static func serialize(_ obj: [String: String]) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: obj, options: [.sortedKeys]),
              let s = String(data: data, encoding: .utf8) else {
            return "{}"
        }
        return s
    }

    /// Re-serialise one parsed JSON value back to its raw text (the analogue of
    /// `JsonElement.GetRawText()` used for tool-call `arguments`).
    private static func rawText(_ value: Any) -> String {
        if let s = value as? String { return s }
        if JSONSerialization.isValidJSONObject(value),
           let data = try? JSONSerialization.data(withJSONObject: value, options: [.sortedKeys]),
           let s = String(data: data, encoding: .utf8) {
            return s
        }
        // Scalars (numbers/bools) aren't valid top-level JSON objects; stringify.
        return String(describing: value)
    }

    /// Lenient cross-vendor JSON event parser. Port of the C#
    /// `RealtimeWebSocketSession.ParseEvent`. Returns nil for frames it does not
    /// recognise (and for blank / non-object input).
    public static func parseEvent(_ json: String) -> RealtimeEvent? {
        if json.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return nil }
        guard let data = json.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return nil
        }
        let at = Date()

        // OpenAI Realtime uses "type" = "input_audio_buffer.speech_started" etc.
        if let type = root["type"] as? String {
            func str(_ key: String) -> String { (root[key] as? String) ?? "" }

            switch type {
            case "input_audio_buffer.speech_started", "speech_started":
                return .speechStarted(at: at)

            case "input_audio_buffer.speech_stopped", "speech_stopped":
                return .speechEnded(at: at)

            case "conversation.item.input_audio_transcription.delta", "transcript.delta":
                return .transcriptDelta(at: at, delta: str("delta"), direction: .inbound)

            case "conversation.item.input_audio_transcription.completed", "transcript.final":
                // C#: prefer "transcript", else "text", else "".
                let text: String = {
                    if let t = root["transcript"] as? String { return t }
                    if let x = root["text"] as? String { return x }
                    return ""
                }()
                return .transcriptFinal(at: at, text: text, direction: .inbound)

            case "response.audio_transcript.delta":
                return .transcriptDelta(at: at, delta: str("delta"), direction: .outbound)

            case "response.audio_transcript.done":
                return .transcriptFinal(at: at, text: str("transcript"), direction: .outbound)

            case "response.function_call_arguments.done", "tool.call":
                let args: String = {
                    if let a = root["arguments"] { return rawText(a) }
                    return "{}"
                }()
                return .toolCall(at: at, callId: str("call_id"), toolName: str("name"), argumentsJson: args)

            case "response.done", "turn.complete":
                return .turnComplete(at: at)

            case "error":
                // C#: error.message if present, else the whole json.
                let message: String = {
                    if let err = root["error"] as? [String: Any], let em = err["message"] as? String {
                        return em
                    }
                    return json
                }()
                return .sessionError(at: at, message: message)

            default:
                return nil
            }
        }

        // Gemini Live emits { serverContent: { modelTurn: { parts: [{ text }] } } }.
        if let sc = root["serverContent"] as? [String: Any] {
            if let tc = sc["turnComplete"] as? Bool, tc == true {
                return .turnComplete(at: at)
            }
            if let mt = sc["modelTurn"] as? [String: Any],
               let parts = mt["parts"] as? [[String: Any]] {
                for part in parts {
                    if let text = part["text"] as? String {
                        return .transcriptDelta(at: at, delta: text, direction: .outbound)
                    }
                }
            }
        }

        return nil
    }
}

// =====================================================================
// OpenAiRealtimeService.cs
// =====================================================================

/// `IRealtimeService` backed by OpenAI Realtime. Authenticates with Bearer +
/// `OpenAI-Beta: realtime=v1`. Port of
/// `CircleAI.Realtime.Cloud.OpenAiRealtimeService`.
public final class OpenAiRealtimeService: IRealtimeService, @unchecked Sendable {
    private let options: OpenAiRealtimeOptions
    private let transports: IRealtimeTransportFactory

    public init(
        options: OpenAiRealtimeOptions,
        transports: IRealtimeTransportFactory? = nil
    ) {
        self.options = options
        self.transports = transports ?? NullRealtimeTransportFactory.instance
    }

    public var providerId: String { "openai-realtime" }
    public var isConfigured: Bool { !(options.apiKey ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    public func startSession(_ config: RealtimeSessionConfig) async throws -> IRealtimeSession {
        try ensureConfigured()

        let modelToUse = config.model.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? options.defaultModel : config.model
        let endpoint = "\(options.webSocketEndpoint)?model=\(escapeDataString(modelToUse))"

        let headers = [
            "Authorization": "Bearer \(options.apiKey ?? "")",
            "OpenAI-Beta": options.betaHeader,
        ]

        let transport = try await transports.connect(endpoint: endpoint, headers: headers)
        return RealtimeWebSocketSession(transport: transport, config: config, providerId: providerId)
    }

    private func ensureConfigured() throws {
        if !isConfigured {
            throw RealtimeError.notConfigured(
                "OpenAI Realtime is not configured. Set OpenAiRealtimeOptions.ApiKey before calling StartSessionAsync.")
        }
    }
}

// =====================================================================
// GeminiLiveService.cs
// =====================================================================

/// `IRealtimeService` backed by Gemini Live (BidiGenerateContent). Authenticates
/// with the API key on the query string. Port of
/// `CircleAI.Realtime.Cloud.GeminiLiveService`.
public final class GeminiLiveService: IRealtimeService, @unchecked Sendable {
    private let options: GeminiLiveOptions
    private let transports: IRealtimeTransportFactory

    public init(
        options: GeminiLiveOptions,
        transports: IRealtimeTransportFactory? = nil
    ) {
        self.options = options
        self.transports = transports ?? NullRealtimeTransportFactory.instance
    }

    public var providerId: String { "gemini-live" }
    public var isConfigured: Bool { !(options.apiKey ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    public func startSession(_ config: RealtimeSessionConfig) async throws -> IRealtimeSession {
        try ensureConfigured()

        let endpoint = "\(options.webSocketEndpoint)?key=\(escapeDataString(options.apiKey ?? ""))"
        let transport = try await transports.connect(endpoint: endpoint, headers: nil)
        return RealtimeWebSocketSession(transport: transport, config: config, providerId: providerId)
    }

    private func ensureConfigured() throws {
        if !isConfigured {
            throw RealtimeError.notConfigured(
                "Gemini Live is not configured. Set GeminiLiveOptions.ApiKey before calling StartSessionAsync.")
        }
    }
}

// =====================================================================
// NovaSonicService.cs
// =====================================================================

/// `IRealtimeService` backed by AWS Nova Sonic. Exposes credentials via headers;
/// the host's transport factory performs the SigV4 signing. Port of
/// `CircleAI.Realtime.Cloud.NovaSonicService`.
public final class NovaSonicService: IRealtimeService, @unchecked Sendable {
    private let options: NovaSonicOptions
    private let transports: IRealtimeTransportFactory

    public init(
        options: NovaSonicOptions,
        transports: IRealtimeTransportFactory? = nil
    ) {
        self.options = options
        self.transports = transports ?? NullRealtimeTransportFactory.instance
    }

    public var providerId: String { "aws-nova-sonic" }
    public var isConfigured: Bool {
        !(options.accessKeyId ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        && !(options.secretAccessKey ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    public func startSession(_ config: RealtimeSessionConfig) async throws -> IRealtimeSession {
        try ensureConfigured()

        let endpoint = "wss://bedrock-runtime.\(options.region).amazonaws.com/model/\(escapeDataString(config.model))/invoke-with-bidirectional-stream"

        // Expose credentials via headers; the host's transport factory SigV4-signs.
        var headers: [String: String] = [
            "X-Amz-Access-Key": options.accessKeyId ?? "",
            "X-Amz-Secret-Key": options.secretAccessKey ?? "",
            "X-Amz-Region": options.region,
        ]
        if let token = options.sessionToken, !token.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            headers["X-Amz-Security-Token"] = token
        }

        let transport = try await transports.connect(endpoint: endpoint, headers: headers)
        return RealtimeWebSocketSession(transport: transport, config: config, providerId: providerId)
    }

    private func ensureConfigured() throws {
        if !isConfigured {
            throw RealtimeError.notConfigured(
                "AWS Nova Sonic is not configured. Set NovaSonicOptions.AccessKeyId and SecretAccessKey before calling StartSessionAsync.")
        }
    }
}

// =====================================================================
// ElevenLabsConvService.cs
// =====================================================================

/// `IRealtimeService` backed by ElevenLabs Conversational AI. The endpoint takes
/// `?agent_id={id}`; `xi-api-key` header authenticates. Port of
/// `CircleAI.Realtime.Cloud.ElevenLabsConvService`.
public final class ElevenLabsConvService: IRealtimeService, @unchecked Sendable {
    private let options: ElevenLabsConvOptions
    private let transports: IRealtimeTransportFactory

    public init(
        options: ElevenLabsConvOptions,
        transports: IRealtimeTransportFactory? = nil
    ) {
        self.options = options
        self.transports = transports ?? NullRealtimeTransportFactory.instance
    }

    public var providerId: String { "elevenlabs-conv" }
    public var isConfigured: Bool {
        !(options.apiKey ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        && !(options.agentId ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    public func startSession(_ config: RealtimeSessionConfig) async throws -> IRealtimeSession {
        try ensureConfigured()

        let endpoint = "\(options.webSocketEndpoint)?agent_id=\(escapeDataString(options.agentId ?? ""))"
        let headers = ["xi-api-key": options.apiKey ?? ""]

        let transport = try await transports.connect(endpoint: endpoint, headers: headers)
        return RealtimeWebSocketSession(transport: transport, config: config, providerId: providerId)
    }

    private func ensureConfigured() throws {
        if !isConfigured {
            throw RealtimeError.notConfigured(
                "ElevenLabs Conversational AI is not configured. Set ElevenLabsConvOptions.ApiKey AND AgentId before calling StartSessionAsync.")
        }
    }
}

// =====================================================================
// UltravoxService.cs
// =====================================================================

/// One HTTP response the Ultravox HTTP leaf hands back: status + body bytes.
/// (Ultravox needs an HTTP POST to create a call before the WS opens; the HTTP
/// call is the injected leaf, exactly as VisionCloud injects `IImageHttpTransport`.)
public struct UltravoxHttpResponse: Sendable, Equatable {
    public let statusCode: Int
    public let body: Data

    public init(statusCode: Int, body: Data) {
        self.statusCode = statusCode
        self.body = body
    }

    /// Mirrors `HttpResponseMessage.IsSuccessStatusCode` (2xx).
    public var isSuccess: Bool { (200..<300).contains(statusCode) }
}

/// The single injected HTTP leaf `UltravoxService` uses for `POST /api/calls`.
/// Keeps the SDK free of a baked-in HTTP client, matching the rest of the tree.
public protocol IUltravoxHttpTransport: Sendable {
    /// POST a JSON body to `baseAddress + path` with `headers`. Returns status + body.
    func postJson(
        baseAddress: String,
        path: String,
        headers: [String: String],
        jsonBody: Data
    ) async throws -> UltravoxHttpResponse
}

/// `IRealtimeService` backed by Ultravox. Two-step: `POST /api/calls` to create a
/// call → returns `joinUrl` → open WS to `joinUrl`. Port of
/// `CircleAI.Realtime.Cloud.UltravoxService`. The C# `HttpClient` maps to the
/// injected `IUltravoxHttpTransport`.
public final class UltravoxService: IRealtimeService, @unchecked Sendable {
    private let http: IUltravoxHttpTransport
    private let options: UltravoxOptions
    private let transports: IRealtimeTransportFactory

    public init(
        http: IUltravoxHttpTransport,
        options: UltravoxOptions,
        transports: IRealtimeTransportFactory? = nil
    ) {
        self.http = http
        self.options = options
        self.transports = transports ?? NullRealtimeTransportFactory.instance
    }

    public var providerId: String { "ultravox" }
    public var isConfigured: Bool { !(options.apiKey ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    public func startSession(_ config: RealtimeSessionConfig) async throws -> IRealtimeSession {
        try ensureConfigured()

        let modelToUse = config.model.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? options.defaultModel : config.model
        let voiceToUse = (config.voiceId ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? options.defaultVoice : (config.voiceId ?? "")

        // Body mirrors the C# anonymous object (systemPrompt omitted when nil, as
        // JsonContent would emit null; here we include it only when present so
        // the wire stays clean and stable).
        var body: [String: Any] = [
            "model": modelToUse,
            "voice": voiceToUse,
            "medium": ["serverWebSocket": ["inputSampleRate": 16000, "outputSampleRate": 24000]],
        ]
        if let sp = config.systemPrompt { body["systemPrompt"] = sp }

        let jsonBody = (try? JSONSerialization.data(withJSONObject: body, options: [.sortedKeys])) ?? Data("{}".utf8)
        let headers = ["X-API-Key": options.apiKey ?? ""]

        let resp = try await http.postJson(
            baseAddress: options.apiEndpoint,
            path: "/api/calls",
            headers: headers,
            jsonBody: jsonBody)

        // C#: resp.EnsureSuccessStatusCode() throws on non-2xx.
        if !resp.isSuccess {
            throw RealtimeError.badVendorResponse("Ultravox API returned HTTP \(resp.statusCode).")
        }

        let joinUrl: String? = {
            if let root = try? JSONSerialization.jsonObject(with: resp.body) as? [String: Any],
               let ju = root["joinUrl"] as? String {
                return ju
            }
            return nil
        }()

        guard let joinUrl, !joinUrl.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw RealtimeError.badVendorResponse("Ultravox API did not return a joinUrl.")
        }

        let transport = try await transports.connect(endpoint: joinUrl, headers: nil)
        return RealtimeWebSocketSession(transport: transport, config: config, providerId: providerId)
    }

    private func ensureConfigured() throws {
        if !isConfigured {
            throw RealtimeError.notConfigured(
                "Ultravox is not configured. Set UltravoxOptions.ApiKey before calling StartSessionAsync.")
        }
    }
}
