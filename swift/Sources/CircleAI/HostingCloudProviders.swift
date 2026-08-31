// HostingCloudProviders.swift
//
// The per-provider half of the cloud fallback: what each vendor request looks
// like, how their streams are framed, and how the text is read back out.
//
// Ported from src/CircleAI.Hosting.CloudFallback. The chain, the orchestrator
// and IConfigurableChatGenerator already live in HostingCloudFallback.swift;
// this file adds the providers themselves.
//
// SCOPE: request shaping, SSE framing and delta extraction are ported in full
// and tested. The HTTP call sits behind ICloudChatTransport, so all of the
// above is testable without a network or an API key.

import Foundation

// MARK: - Provider options

/// Everything a provider needs, with the real endpoints and model defaults.
public struct CloudChatOptions: Sendable, Equatable {
    public var baseAddress: String
    public var apiKey: String?
    public var model: String
    public var temperature: Float
    public var maxTokens: Int
    /// Anthropic only. Ignored by the others.
    public var anthropicVersion: String

    public init(baseAddress: String, apiKey: String? = nil, model: String,
                temperature: Float = 0.7, maxTokens: Int = 1024,
                anthropicVersion: String = "2023-06-01") {
        self.baseAddress = baseAddress
        self.apiKey = apiKey
        self.model = model
        self.temperature = temperature
        self.maxTokens = maxTokens
        self.anthropicVersion = anthropicVersion
    }

    public static func openAi(apiKey: String? = nil) -> CloudChatOptions {
        CloudChatOptions(baseAddress: "https://api.openai.com", apiKey: apiKey, model: "gpt-4o-mini")
    }
    public static func anthropic(apiKey: String? = nil) -> CloudChatOptions {
        CloudChatOptions(baseAddress: "https://api.anthropic.com", apiKey: apiKey,
                         model: "claude-3-5-sonnet-latest")
    }
    public static func gemini(apiKey: String? = nil) -> CloudChatOptions {
        CloudChatOptions(baseAddress: "https://generativelanguage.googleapis.com",
                         apiKey: apiKey, model: "gemini-2.0-flash")
    }
    public static func groq(apiKey: String? = nil) -> CloudChatOptions {
        CloudChatOptions(baseAddress: "https://api.groq.com", apiKey: apiKey,
                         model: "llama-3.3-70b-versatile")
    }
    public static func cerebras(apiKey: String? = nil) -> CloudChatOptions {
        CloudChatOptions(baseAddress: "https://api.cerebras.ai", apiKey: apiKey, model: "llama3.3-70b")
    }
    public static func together(apiKey: String? = nil) -> CloudChatOptions {
        CloudChatOptions(baseAddress: "https://api.together.xyz", apiKey: apiKey,
                         model: "meta-llama/Llama-3.3-70B-Instruct-Turbo")
    }
    public static func deepSeek(apiKey: String? = nil) -> CloudChatOptions {
        CloudChatOptions(baseAddress: "https://api.deepseek.com", apiKey: apiKey, model: "deepseek-chat")
    }
}

/// The providers this module knows how to shape a request for.
public enum CloudProvider: String, Sendable, Equatable, CaseIterable {
    case openAi = "openai"
    case anthropic
    case gemini
    case groq
    case cerebras
    case together
    case deepSeek = "deepseek"

    /// Groq serves the OpenAI-compatible API under a different path - the one
    /// detail that is easy to get wrong and produces a 404, not an error message.
    public var chatCompletionsPath: String {
        switch self {
        case .groq: return "/openai/v1/chat/completions"
        case .anthropic: return "/v1/messages"
        case .gemini: return ""   // built per-request; the model is in the path
        default: return "/v1/chat/completions"
        }
    }

    /// Anthropic and Gemini have their own request and event shapes; everything
    /// else speaks the OpenAI one.
    public var wireShape: CloudWireShape {
        switch self {
        case .anthropic: return .anthropic
        case .gemini: return .gemini
        default: return .openAiCompatible
        }
    }

    public var displayName: String {
        switch self {
        case .openAi: return "OpenAI"
        case .anthropic: return "Anthropic"
        case .gemini: return "Gemini"
        case .groq: return "Groq"
        case .cerebras: return "Cerebras"
        case .together: return "Together"
        case .deepSeek: return "DeepSeek"
        }
    }
}

public enum CloudWireShape: Sendable, Equatable {
    case openAiCompatible
    case anthropic
    case gemini
}

// MARK: - Server-sent events

/// Pulls the payloads out of an SSE stream.
public enum ServerSentEventsReader {

    /// Lines in, frames out. Anything that is not a `data:` line is skipped -
    /// comments, `event:` lines and the blank separators all are - and the
    /// stream ends at `[DONE]` rather than waiting for the socket to close.
    public static func frames(fromLines lines: [String]) -> [String] {
        var out: [String] = []
        for line in lines {
            guard line.hasPrefix("data:") else { continue }
            var payload = String(line.dropFirst(5))
            while let f = payload.first, f == " " || f == "\t" { payload.removeFirst() }
            if payload == "[DONE]" { return out }
            out.append(payload)
        }
        return out
    }

    /// Splits on either line ending before framing.
    public static func frames(from text: String) -> [String] {
        frames(fromLines: text.replacingOccurrences(of: "\r\n", with: "\n")
            .components(separatedBy: "\n"))
    }
}

// MARK: - Request shaping

/// Builds the request body each provider expects. Separate from sending it, so
/// the shape can be checked without a key or a network.
public enum CloudRequestBuilder {

    /// OpenAI and everything compatible with it: one flat message list, system
    /// messages included as-is.
    public static func openAiBody(messages: [ChatMessage], options: CloudChatOptions,
                                  temperature: Float? = nil, maxTokens: Int? = nil) -> [String: Any] {
        [
            "model": options.model,
            "stream": true,
            "temperature": temperature ?? options.temperature,
            "max_tokens": maxTokens ?? options.maxTokens,
            "messages": messages.map { ["role": $0.role, "content": $0.content] },
        ]
    }

    /// Anthropic lifts system messages OUT of the list into a top-level field,
    /// joined by a blank line. Leaving them in the list is rejected by the API.
    public static func anthropicBody(messages: [ChatMessage], options: CloudChatOptions,
                                     temperature: Float? = nil, maxTokens: Int? = nil) -> [String: Any] {
        let system = messages.filter { $0.role.lowercased() == "system" }
            .map(\.content).joined(separator: "\n\n")
        let chat = messages.filter { $0.role.lowercased() != "system" }
            .map { ["role": $0.role.lowercased(), "content": $0.content] }

        var body: [String: Any] = [
            "model": options.model,
            "max_tokens": maxTokens ?? options.maxTokens,
            "temperature": temperature ?? options.temperature,
            "stream": true,
            "messages": chat,
        ]
        // Omitted entirely when there is none - an empty system field is not
        // the same as no system field.
        if !system.isEmpty { body["system"] = system }
        return body
    }

    /// Gemini renames the role: assistant becomes model, and every message is
    /// wrapped in a parts array. System goes into systemInstruction.
    public static func geminiBody(messages: [ChatMessage], options: CloudChatOptions,
                                  temperature: Float? = nil, maxTokens: Int? = nil) -> [String: Any] {
        let system = messages.filter { $0.role.lowercased() == "system" }
            .map(\.content).joined(separator: "\n\n")

        let contents = messages.filter { $0.role.lowercased() != "system" }.map { m -> [String: Any] in
            let role = m.role.lowercased() == "assistant" ? "model" : m.role.lowercased()
            return ["role": role, "parts": [["text": m.content]]]
        }

        var body: [String: Any] = [
            "contents": contents,
            "generationConfig": [
                "temperature": temperature ?? options.temperature,
                "maxOutputTokens": maxTokens ?? options.maxTokens,
            ],
        ]
        if !system.isEmpty {
            body["systemInstruction"] = ["parts": [["text": system]]]
        }
        return body
    }

    /// Gemini puts the model AND the key in the path, both percent-encoded -
    /// a model id with a slash in it otherwise silently addresses another route.
    public static func geminiPath(options: CloudChatOptions) -> String {
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-._~"))
        let model = options.model.addingPercentEncoding(withAllowedCharacters: allowed) ?? options.model
        let key = (options.apiKey ?? "").addingPercentEncoding(withAllowedCharacters: allowed) ?? ""
        return "/v1beta/models/\(model):streamGenerateContent?alt=sse&key=\(key)"
    }

    public static func body(for provider: CloudProvider, messages: [ChatMessage],
                            options: CloudChatOptions, temperature: Float? = nil,
                            maxTokens: Int? = nil) -> [String: Any] {
        switch provider.wireShape {
        case .openAiCompatible:
            return openAiBody(messages: messages, options: options,
                              temperature: temperature, maxTokens: maxTokens)
        case .anthropic:
            return anthropicBody(messages: messages, options: options,
                                 temperature: temperature, maxTokens: maxTokens)
        case .gemini:
            return geminiBody(messages: messages, options: options,
                              temperature: temperature, maxTokens: maxTokens)
        }
    }

    /// The headers that carry the key. Three providers, three conventions.
    public static func headers(for provider: CloudProvider, options: CloudChatOptions) -> [String: String] {
        switch provider {
        case .anthropic:
            return ["x-api-key": options.apiKey ?? "",
                    "anthropic-version": options.anthropicVersion,
                    "content-type": "application/json"]
        case .gemini:
            // The key is in the query string, not a header.
            return ["content-type": "application/json"]
        default:
            return ["Authorization": "Bearer \(options.apiKey ?? "")",
                    "content-type": "application/json"]
        }
    }
}

// MARK: - Reading the deltas back

/// Pulls the text out of one streamed frame. Every shape returns nil for a
/// frame it does not recognise - a keepalive, a usage report or a role marker
/// is not an error, it just carries no words.
public enum CloudDeltaReader {

    public static func delta(from frame: String, shape: CloudWireShape) -> String? {
        guard let data = frame.data(using: .utf8),
              let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
            return nil
        }
        switch shape {
        case .openAiCompatible: return openAi(root)
        case .anthropic: return anthropic(root)
        case .gemini: return gemini(root)
        }
    }

    /// choices[0].delta.content
    static func openAi(_ root: [String: Any]) -> String? {
        guard let choices = root["choices"] as? [Any], let first = choices.first as? [String: Any],
              let delta = first["delta"] as? [String: Any],
              let content = delta["content"] as? String, !content.isEmpty else { return nil }
        return content
    }

    /// Only content_block_delta carries text. message_start, ping and
    /// message_stop all arrive on the same stream and carry none.
    static func anthropic(_ root: [String: Any]) -> String? {
        guard (root["type"] as? String) == "content_block_delta",
              let delta = root["delta"] as? [String: Any],
              let text = delta["text"] as? String, !text.isEmpty else { return nil }
        return text
    }

    /// candidates[0].content.parts[0].text
    static func gemini(_ root: [String: Any]) -> String? {
        guard let candidates = root["candidates"] as? [Any],
              let first = candidates.first as? [String: Any],
              let content = first["content"] as? [String: Any],
              let parts = content["parts"] as? [Any],
              let part = parts.first as? [String: Any],
              let text = part["text"] as? String, !text.isEmpty else { return nil }
        return text
    }

    /// Every delta in an SSE body, in order.
    public static func deltas(fromStream text: String, shape: CloudWireShape) -> [String] {
        ServerSentEventsReader.frames(from: text).compactMap { delta(from: $0, shape: shape) }
    }
}

// MARK: - Generators

/// What a host has to provide: send this body to this path and hand back the
/// SSE text. Everything else in this file works without one.
public protocol ICloudChatTransport: Sendable {
    func post(baseAddress: String, path: String, headers: [String: String],
              body: Data) async throws -> (status: Int, text: String)
}

/// One provider, configured or not. Conforms to the existing
/// `IConfigurableChatGenerator`, so it drops straight into `CloudFallbackChain`.
public final class CloudChatGenerator: IConfigurableChatGenerator, @unchecked Sendable {
    public let provider: CloudProvider
    private let options: CloudChatOptions
    private let transport: (any ICloudChatTransport)?

    public init(provider: CloudProvider, options: CloudChatOptions,
                transport: (any ICloudChatTransport)? = nil) {
        self.provider = provider
        self.options = options
        self.transport = transport
    }

    public var id: String { provider.rawValue }

    /// A key is what makes it configured. Nothing here checks whether the key
    /// WORKS - that is what the first call finds out.
    public var isConfigured: Bool {
        !(options.apiKey ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    public var engineLabel: String { "\(provider.displayName) \u{B7} \(options.model)" }

    public var statusMessage: String {
        isConfigured ? "Ready \u{B7} \(options.model)" : "\(provider.displayName) API key not configured."
    }

    public func generate(messages: [ChatMessage], options genOptions: GenerationOptions?) async throws -> String {
        var out = ""
        for await chunk in stream(messages: messages, options: genOptions) { out += chunk }
        return out
    }

    public func stream(messages: [ChatMessage], options genOptions: GenerationOptions?) -> AsyncStream<String> {
        stream(messages: messages,
               temperature: genOptions?.temperature,
               maxTokens: genOptions?.maxTokens)
    }

    func stream(messages: [ChatMessage], temperature: Float? = nil,
                maxTokens: Int? = nil) -> AsyncStream<String> {
        AsyncStream { continuation in
            Task {
                // FAIL SOFT. An unconfigured provider yields one bracketed
                // status frame and finishes, so a chain can move past it and a
                // UI can show the reason instead of an exception.
                guard isConfigured, let transport else {
                    continuation.yield("[\(statusMessage)]")
                    continuation.finish()
                    return
                }

                let path = provider == .gemini
                    ? CloudRequestBuilder.geminiPath(options: options)
                    : provider.chatCompletionsPath
                let body = CloudRequestBuilder.body(for: provider, messages: messages,
                                                    options: options, temperature: temperature,
                                                    maxTokens: maxTokens)
                guard let data = try? JSONSerialization.data(withJSONObject: body) else {
                    continuation.yield("[\(id) error: could not encode the request]")
                    continuation.finish()
                    return
                }

                do {
                    let (status, text) = try await transport.post(
                        baseAddress: options.baseAddress, path: path,
                        headers: CloudRequestBuilder.headers(for: provider, options: options),
                        body: data)

                    if status < 200 || status > 299 {
                        continuation.yield("[\(id) error \(status): \(Self.truncate(text, 240))]")
                        continuation.finish()
                        return
                    }
                    for delta in CloudDeltaReader.deltas(fromStream: text, shape: provider.wireShape) {
                        continuation.yield(delta)
                    }
                } catch {
                    continuation.yield("[\(id) error: \(error.localizedDescription)]")
                }
                continuation.finish()
            }
        }
    }

    static func truncate(_ value: String, _ max: Int) -> String {
        value.count <= max ? value : String(value.prefix(max)) + "\u{2026}"
    }
}

