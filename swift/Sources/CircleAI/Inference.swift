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

    public init(
        maxTokens: Int = 512,
        temperature: Float = 0.7,
        topP: Float = 0.9,
        topK: Int = 40,
        seed: Int? = nil,
        stopSequences: [String]? = nil
    ) {
        self.maxTokens = maxTokens
        self.temperature = temperature
        self.topP = topP
        self.topK = topK
        self.seed = seed
        self.stopSequences = stopSequences
    }
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
    func stream(
        messages: [ChatMessage],
        options: GenerationOptions?
    ) -> AsyncStream<String>
}
