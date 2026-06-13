// Inference.swift
//
// ChatMessage, GenerationOptions, and the IChatGenerator protocol.
// Contract for an on-device chat-style text generator.

import Foundation

// NOTE: ChatMessage is declared in Models.swift and re-used here.

// MARK: - GenerationOptions

/// Knobs for a single generation call.
public struct GenerationOptions: Sendable {
    /// Maximum number of new tokens to produce.
    public var maxTokens: Int

    /// Sampling temperature. 0 = greedy; higher = more random.
    public var temperature: Float

    /// Nucleus sampling cutoff (top-p). 1.0 disables.
    public var topP: Float

    /// Top-k cutoff. 0 disables.
    public var topK: Int

    /// Optional RNG seed. nil means non-deterministic.
    public var seed: Int?

    /// Optional substrings that will end generation when matched in emitted output.
    public var stopSequences: [String]?

    /// Whether to surface the model's reasoning trace (Qwen3
    /// `<think>…</think>`) on the call. Default `true`.
    public var includeReasoning: Bool

    /// (RT-11) Declarative power budget for this call. Default
    /// `PowerBudget.normal` auto-downgrades to `.low` below 15% battery.
    /// Pass `.none` to opt out.
    public var budget: PowerBudget

    /// (RT-06) Whether the runtime should consult the cross-session prefix
    /// cache for a warm (modelId, systemPrompt) snapshot. Default `false`.
    public var usePrefixCache: Bool

    public init(
        maxTokens: Int = 512,
        temperature: Float = 0.7,
        topP: Float = 0.9,
        topK: Int = 40,
        seed: Int? = nil,
        stopSequences: [String]? = nil,
        includeReasoning: Bool = true,
        budget: PowerBudget = .normal,
        usePrefixCache: Bool = false
    ) {
        self.maxTokens = maxTokens
        self.temperature = temperature
        self.topP = topP
        self.topK = topK
        self.seed = seed
        self.stopSequences = stopSequences
        self.includeReasoning = includeReasoning
        self.budget = budget
        self.usePrefixCache = usePrefixCache
    }
}

/// Per-call power budget. Mirrors CircleAI.Inference.PowerBudget.
public enum PowerBudget: Int, Sendable {
    /// Opt out — honour maxTokens literally.
    case none = 0
    /// ~64 token cap; prefers TQ4 KV; smaller model in chain when configured.
    case low = 1
    /// Default. ~512 token cap. Auto-downgrades to .low below 15% battery.
    case normal = 2
    /// ~2048 token cap; full FP16 KV. Auto-throttles on thermal warnings.
    case high = 3
}

// MARK: - IChatGenerator

/// Contract for an on-device chat-style text generator.
/// Implementations own native model state.
public protocol IChatGenerator: AnyObject {
    /// Generates a complete assistant reply for the given conversation.
    func generate(
        messages: [ChatMessage],
        options: GenerationOptions?
    ) async throws -> String

    /// Streams the assistant reply token-by-token (or piece-by-piece) as it is
    /// decoded. Each yielded string is the next chunk — callers concatenate in order.
    /// Content only — any reasoning inside `<think>…</think>` is filtered out.
    /// Use `streamFragments` when you also need the reasoning stream.
    func stream(
        messages: [ChatMessage],
        options: GenerationOptions?
    ) -> AsyncStream<String>

    /// Fragment-aware streaming variant. Yields each piece tagged as either
    /// `.content` or `.reasoning` so the caller can route the model's
    /// `<think>` block into a separate `reasoning_content` field (o1 /
    /// DeepSeek style).
    ///
    /// Default implementation wraps `stream` and tags every chunk as
    /// `.content`; generators that surface reasoning override this method.
    func streamFragments(
        messages: [ChatMessage],
        options: GenerationOptions?
    ) -> AsyncStream<ChatFragment>

    /// (RT-02) Save the current model session to `path`. Returns `true` on
    /// success. Default implementation returns `false`; native generators
    /// override.
    func saveSession(path: String) async throws -> Bool

    /// (RT-02) Load a previously-saved session from `path`. Returns `true`
    /// on success. Default implementation returns `false`.
    func loadSession(path: String) async throws -> Bool
}

extension IChatGenerator {
    public func streamFragments(
        messages: [ChatMessage],
        options: GenerationOptions?
    ) -> AsyncStream<ChatFragment> {
        let inner = self.stream(messages: messages, options: options)
        return AsyncStream { continuation in
            let task = Task {
                for await chunk in inner {
                    if Task.isCancelled { break }
                    continuation.yield(ChatFragment(kind: .content, text: chunk))
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    public func saveSession(path: String) async throws -> Bool { false }
    public func loadSession(path: String) async throws -> Bool { false }
}
