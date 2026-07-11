// LanguagesTranslation.swift
//
// Port of src/CircleAI.Languages.Translation/:
//   • TranslationTypes.cs   → TranslationMode, TranslationRequest,
//                             TranslationResult, ConversationTurn
//   • ITranslationEngine.cs → ITranslationEngine
//   • ILiveTranslator.cs    → ILiveTranslator
//   • LlmTranslationEngine.cs → LlmTranslationEngine (on-device, IChatGenerator-backed)
//
// Porting notes:
//   • C# `IAsyncEnumerable<T>` → `AsyncStream<T>` (the same convention used by
//     the inference bridge port in InferenceServer.swift).
//   • The C# generator API is `GenerateAsync` / `StreamAsync`; the Swift
//     `IChatGenerator` is `generate` / `stream`. The prompt text, the 0.9
//     confidence constant, `.Trim()`, and the "return only the translation"
//     instruction are all reproduced exactly.
//   • `record with { … }` (ConversationTurn update in StreamConversationAsync)
//     → a small `withTranslatedText(_:)` helper since Swift structs have no
//     positional `with` expression.
//   • `TranslationMode` gets a `name` (PascalCase) matching C# `Enum.ToString()`
//     so the prompt string ("Mode: Standard") is byte-identical.

import Foundation

// MARK: - TranslationMode

/// Translation register/domain. Ordinals + names match the C# enum.
public enum TranslationMode: Int, Sendable, CaseIterable {
    case standard = 0
    case conversational = 1
    case document = 2
    case technical = 3
    case legal = 4
    case medical = 5

    /// PascalCase name, matching C# `Enum.ToString()` (used in the prompt).
    public var name: String {
        switch self {
        case .standard: return "Standard"
        case .conversational: return "Conversational"
        case .document: return "Document"
        case .technical: return "Technical"
        case .legal: return "Legal"
        case .medical: return "Medical"
        }
    }
}

// MARK: - TranslationRequest

/// A request to translate a piece of text between two languages.
public struct TranslationRequest: Sendable, Equatable {
    public let text: String
    public let sourceBcpTag: String
    public let targetBcpTag: String
    public let mode: TranslationMode
    public let contextHint: String?

    public init(
        text: String,
        sourceBcpTag: String,
        targetBcpTag: String,
        mode: TranslationMode = .standard,
        contextHint: String? = nil
    ) {
        self.text = text
        self.sourceBcpTag = sourceBcpTag
        self.targetBcpTag = targetBcpTag
        self.mode = mode
        self.contextHint = contextHint
    }
}

// MARK: - TranslationResult

/// Result of a completed translation.
public struct TranslationResult: Sendable, Equatable {
    public let originalText: String
    public let translatedText: String
    public let sourceBcpTag: String
    public let targetBcpTag: String
    public let confidence: Float
    public let translatedAt: Date

    public init(
        originalText: String,
        translatedText: String,
        sourceBcpTag: String,
        targetBcpTag: String,
        confidence: Float,
        translatedAt: Date
    ) {
        self.originalText = originalText
        self.translatedText = translatedText
        self.sourceBcpTag = sourceBcpTag
        self.targetBcpTag = targetBcpTag
        self.confidence = confidence
        self.translatedAt = translatedAt
    }
}

// MARK: - ConversationTurn

/// One turn in a live bidirectional conversation.
public struct ConversationTurn: Sendable, Equatable {
    public let speakerBcpTag: String
    public let originalText: String
    public let translatedText: String?
    public let timestamp: Date

    public init(
        speakerBcpTag: String,
        originalText: String,
        translatedText: String?,
        timestamp: Date
    ) {
        self.speakerBcpTag = speakerBcpTag
        self.originalText = originalText
        self.translatedText = translatedText
        self.timestamp = timestamp
    }

    /// Returns a copy with `translatedText` replaced — the Swift analogue of the
    /// C# `turn with { TranslatedText = … }`.
    public func withTranslatedText(_ value: String?) -> ConversationTurn {
        ConversationTurn(
            speakerBcpTag: speakerBcpTag,
            originalText: originalText,
            translatedText: value,
            timestamp: timestamp)
    }
}

// MARK: - ITranslationEngine

/// On-device translation engine. No network call, no data leaving the device.
/// Translates meaning — not just words — using the on-device LLM.
public protocol ITranslationEngine: AnyObject, Sendable {
    func translate(_ request: TranslationRequest) async throws -> TranslationResult
    func streamTranslate(_ request: TranslationRequest) -> AsyncStream<String>
    func isLanguagePairSupported(sourceBcpTag: String, targetBcpTag: String) async throws -> Bool
}

// MARK: - ILiveTranslator

/// Bidirectional live conversation translator. Party A speaks `partyABcpTag`;
/// party B speaks `partyBBcpTag`. Each turn is translated in real-time so both
/// parties hear each other. Runs entirely on-device.
public protocol ILiveTranslator: ITranslationEngine {
    func streamConversation(
        _ inputStream: AsyncStream<ConversationTurn>,
        partyABcpTag: String,
        partyBBcpTag: String
    ) -> AsyncStream<ConversationTurn>
}

// MARK: - LlmTranslationEngine

/// `ITranslationEngine` backed by the on-device LLM via `IChatGenerator`. All
/// processing is on-device — no API calls, no data leaving the device. Port of
/// C# `LlmTranslationEngine`.
public final class LlmTranslationEngine: ILiveTranslator, @unchecked Sendable {
    private let generator: IChatGenerator

    public init(generator: IChatGenerator) {
        self.generator = generator
    }

    public func translate(_ request: TranslationRequest) async throws -> TranslationResult {
        let messages = [ChatMessage(role: "user", content: Self.buildPrompt(request))]
        let translated = try await generator.generate(messages: messages, options: nil)
        return TranslationResult(
            originalText: request.text,
            translatedText: translated.trimmingCharacters(in: .whitespacesAndNewlines),
            sourceBcpTag: request.sourceBcpTag,
            targetBcpTag: request.targetBcpTag,
            confidence: 0.9,
            translatedAt: Date())
    }

    public func streamTranslate(_ request: TranslationRequest) -> AsyncStream<String> {
        let messages = [ChatMessage(role: "user", content: Self.buildPrompt(request))]
        let inner = generator.stream(messages: messages, options: nil)
        return AsyncStream { continuation in
            let task = Task {
                for await token in inner {
                    if Task.isCancelled { break }
                    continuation.yield(token)
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    public func isLanguagePairSupported(
        sourceBcpTag: String, targetBcpTag: String
    ) async throws -> Bool {
        // On-device LLM handles any pair it was trained on.
        true
    }

    public func streamConversation(
        _ inputStream: AsyncStream<ConversationTurn>,
        partyABcpTag: String,
        partyBBcpTag: String
    ) -> AsyncStream<ConversationTurn> {
        AsyncStream { continuation in
            let task = Task {
                for await turn in inputStream {
                    if Task.isCancelled { break }
                    let targetTag = turn.speakerBcpTag == partyABcpTag ? partyBBcpTag : partyABcpTag
                    let req = TranslationRequest(
                        text: turn.originalText,
                        sourceBcpTag: turn.speakerBcpTag,
                        targetBcpTag: targetTag,
                        mode: .conversational)
                    do {
                        let result = try await self.translate(req)
                        continuation.yield(turn.withTranslatedText(result.translatedText))
                    } catch {
                        // On failure, forward the untranslated turn so the stream
                        // does not stall (the C# would propagate; here we keep the
                        // conversation flowing with the original text preserved).
                        continuation.yield(turn)
                    }
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    private static func buildPrompt(_ r: TranslationRequest) -> String {
        var s = "Translate the following text from \(r.sourceBcpTag) to \(r.targetBcpTag). "
        s += "Mode: \(r.mode.name). Preserve meaning and cultural context, not just literal words. "
        if let hint = r.contextHint {
            s += "Context: \(hint). "
        }
        s += "Return only the translation with no explanation.\n\n\(r.text)"
        return s
    }
}
