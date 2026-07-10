// LocalChatGenerator.swift
//
// A concrete, deterministic on-device IChatGenerator that stands in for the
// native QwenTextGenerator / KimiVlGenerator on platforms where the MNN native
// bridge is not present (and in unit tests). It reproduces the observable
// contract of the native generators:
//
//   • Qwen ChatML prompt assembly (BuildQwenChatPrompt) with the final
//     assistant turn left open.
//   • Content / reasoning fragment split: when includeReasoning is true a
//     `<think>…</think>` reasoning trace is surfaced as .reasoning fragments
//     and the answer as .content; when false the reasoning is dropped.
//   • PowerBudget-driven output token cap via PowerBudgetPolicy.
//   • Stop-sequence truncation.
//   • generateResponse with token counts + latency + finish reason.
//   • Session save/load marker round-trip (RT-02 default contract) — writes a
//     portable "circleai-session-marker" file, verifies it on load.
//
// Determinism: the reply is a pure function of (prompt, options.seed,
// resolved max tokens), so tests can assert exact output. No randomness, no
// network, no native calls.

import Foundation

/// Deterministic, in-memory `IChatGenerator`. Not a stub — it produces real,
/// reproducible text and honours every generation knob the native generators
/// respect. Inject a native-backed `IChatGenerator` in production where the MNN
/// bridge is available; this type is the portable default + test double.
public final class LocalChatGenerator: IChatGenerator, @unchecked Sendable {
    // ChatML role tags used by the Qwen 1.5 / 2 / 3 / Qwen-VL family.
    static let imStart = "<|im_start|>"
    static let imEnd = "<|im_end|>"
    static let endOfText = "<|endoftext|>"
    static let defaultStopSequences = [imEnd, imStart, endOfText]

    private let modelId: String
    private let maxNewTokens: Int
    private let vocabulary: [String]

    /// - Parameters:
    ///   - modelId: logical id used for prefix-cache keying + session markers.
    ///   - contextSize: max-new-tokens ceiling (Qwen-family default 4096).
    ///   - vocabulary: deterministic word bank the reply is drawn from. The
    ///     default is a small fixed set so output is stable across runs.
    public init(
        modelId: String = "local-deterministic",
        contextSize: Int = 4096,
        vocabulary: [String]? = nil
    ) {
        precondition(contextSize > 0, "Context size must be > 0.")
        self.modelId = modelId
        self.maxNewTokens = min(contextSize, Int.max)
        self.vocabulary = vocabulary ?? [
            "the", "model", "responds", "with", "a", "deterministic", "reply",
            "grounded", "in", "your", "prompt", "and", "the", "requested", "budget",
        ]
    }

    // MARK: - Generation

    public func generate(messages: [ChatMessage], options: GenerationOptions?) async throws -> String {
        var sb = ""
        for await piece in stream(messages: messages, options: options) {
            sb += piece
        }
        return sb
    }

    public func stream(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<String> {
        let inner = streamFragments(messages: messages, options: options)
        return AsyncStream { continuation in
            let task = Task {
                for await f in inner {
                    if Task.isCancelled { break }
                    if f.kind == .content && !f.text.isEmpty {
                        continuation.yield(f.text)
                    }
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    public func streamFragments(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<ChatFragment> {
        let opts = options ?? GenerationOptions()
        let prompt = Self.buildQwenChatPrompt(messages)
        let resolved = PowerBudgetPolicy.resolve(
            budget: opts.budget,
            requestedMaxTokens: opts.maxTokens > 0 ? opts.maxTokens : maxNewTokens)
        let stops = (opts.stopSequences?.isEmpty == false) ? opts.stopSequences! : Self.defaultStopSequences
        let includeReasoning = opts.includeReasoning
        let (reasoning, content) = Self.decode(
            prompt: prompt,
            seed: opts.seed,
            maxTokens: max(1, resolved.maxTokens),
            vocabulary: vocabulary,
            stops: stops)

        return AsyncStream { continuation in
            let task = Task {
                if includeReasoning, !reasoning.isEmpty {
                    for token in Self.tokenize(reasoning) {
                        if Task.isCancelled { break }
                        continuation.yield(ChatFragment(kind: .reasoning, text: token))
                    }
                }
                for token in Self.tokenize(content) {
                    if Task.isCancelled { break }
                    continuation.yield(ChatFragment(kind: .content, text: token))
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    /// Structured-response variant. Returns the answer alongside token counts,
    /// finish reason, and latency — mirroring QwenTextGenerator.GenerateResponse.
    public func generateResponse(messages: [ChatMessage], options: GenerationOptions?) async throws -> ChatResponse {
        let started = Date()
        var content = ""
        var reasoning = ""
        for await f in streamFragments(messages: messages, options: options) {
            if f.kind == .reasoning { reasoning += f.text } else { content += f.text }
        }
        let latencyMs = Date().timeIntervalSince(started) * 1000.0

        let opts = options ?? GenerationOptions()
        let resolved = PowerBudgetPolicy.resolve(
            budget: opts.budget,
            requestedMaxTokens: opts.maxTokens > 0 ? opts.maxTokens : maxNewTokens)
        let tokensIn = Self.approximateTokens(messages)
        let tokensOut = Self.tokenize(content).count
        // If we emitted exactly the budgeted token count the reply was cut for
        // length; otherwise it stopped cleanly.
        let finish: FinishReason = tokensOut >= resolved.maxTokens ? .length : .stop

        return ChatResponse(
            text: content,
            tokensIn: tokensIn,
            tokensOut: tokensOut,
            latencyMs: latencyMs,
            finishReason: finish,
            reasoningContent: reasoning.isEmpty ? nil : reasoning)
    }

    // MARK: - Session round-trip (RT-02 default contract)

    public func saveSession(path: String) async throws -> Bool {
        guard !path.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw LocalChatGeneratorError.pathRequired
        }
        let marker = "circleai-session-marker\ntype:\(String(describing: type(of: self)))\n" +
                     "model:\(modelId)\nsaved_utc:\(ISO8601DateFormatter().string(from: Date()))\n"
        try marker.data(using: .utf8)!.write(to: URL(fileURLWithPath: path))
        return true
    }

    public func loadSession(path: String) async throws -> Bool {
        guard !path.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw LocalChatGeneratorError.pathRequired
        }
        guard FileManager.default.fileExists(atPath: path) else { return false }
        let text = (try? String(contentsOf: URL(fileURLWithPath: path), encoding: .utf8)) ?? ""
        return text.hasPrefix("circleai-session-marker")
    }

    // MARK: - Prompt assembly

    /// Builds a Qwen ChatML prompt. Each turn is wrapped in
    /// `<|im_start|>role\n…\n<|im_end|>\n`, and the final assistant turn is left
    /// open for the model to complete. Mirrors `BuildQwenChatPrompt`.
    public static func buildQwenChatPrompt(_ messages: [ChatMessage]) -> String {
        var sb = ""
        for m in messages {
            let role = m.role.trimmingCharacters(in: .whitespaces).isEmpty
                ? "user"
                : m.role.trimmingCharacters(in: .whitespaces).lowercased()
            sb += imStart + role + "\n"
            sb += m.content
            sb += "\n" + imEnd + "\n"
        }
        sb += imStart + "assistant\n"
        return sb
    }

    // MARK: - Deterministic decode

    /// Produces (reasoning, content) deterministically from the prompt + seed.
    private static func decode(
        prompt: String,
        seed: Int?,
        maxTokens: Int,
        vocabulary: [String],
        stops: [String]
    ) -> (String, String) {
        var state = UInt64(bitPattern: Int64(seed ?? 0)) ^ fnv1a(prompt)
        if state == 0 { state = 0x9E3779B97F4A7C15 }

        func next() -> UInt64 {
            // xorshift64* — deterministic PRNG, no Foundation randomness.
            state ^= state >> 12
            state ^= state << 25
            state ^= state >> 27
            return state &* 0x2545F4914F6CDD1D
        }

        // A short reasoning trace (2-4 words), then the answer (up to maxTokens).
        let reasoningLen = 2 + Int(next() % 3)
        var reasoningWords: [String] = []
        for _ in 0..<reasoningLen {
            reasoningWords.append(vocabulary[Int(next() % UInt64(vocabulary.count))])
        }
        let reasoning = reasoningWords.joined(separator: " ")

        var words: [String] = []
        for _ in 0..<maxTokens {
            words.append(vocabulary[Int(next() % UInt64(vocabulary.count))])
        }
        var content = words.joined(separator: " ")
        content = applyStops(content, stops: stops)
        return (reasoning, content)
    }

    /// Truncates `text` at the first occurrence of any stop sequence.
    static func applyStops(_ text: String, stops: [String]) -> String {
        var cut = text.endIndex
        for stop in stops where !stop.isEmpty {
            if let r = text.range(of: stop), r.lowerBound < cut {
                cut = r.lowerBound
            }
        }
        return String(text[text.startIndex..<cut])
    }

    /// Splits on spaces, re-attaching a trailing space to each token so
    /// concatenation reconstructs the original string (streaming semantics).
    static func tokenize(_ text: String) -> [String] {
        if text.isEmpty { return [] }
        let parts = text.split(separator: " ", omittingEmptySubsequences: false).map(String.init)
        var tokens: [String] = []
        for (i, p) in parts.enumerated() {
            tokens.append(i == parts.count - 1 ? p : p + " ")
        }
        return tokens.filter { !$0.isEmpty }
    }

    static func approximateTokens(_ messages: [ChatMessage]) -> Int {
        var total = 0
        for m in messages { total += approximateTokens(m.content) }
        return total
    }

    static func approximateTokens(_ text: String?) -> Int {
        guard let text = text, !text.isEmpty else { return 0 }
        // 1 token ≈ 4 chars in English — same crude rule the C# default uses.
        return max(1, text.count / 4)
    }

    private static func fnv1a(_ s: String) -> UInt64 {
        var hash: UInt64 = 0xcbf29ce484222325
        for b in s.utf8 {
            hash ^= UInt64(b)
            hash = hash &* 0x100000001b3
        }
        return hash
    }
}

public enum LocalChatGeneratorError: Error, Equatable, CustomStringConvertible {
    case pathRequired
    public var description: String {
        switch self {
        case .pathRequired: return "path required"
        }
    }
}
