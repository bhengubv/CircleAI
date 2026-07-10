// InnerMonologue.swift
//
// Port of CircleAI.Companion inner-monologue layer — the C# reference:
//   - IInnerMonologue + SelfReflection   (HerJarvisContracts.cs)
//   - TemplateInnerMonologue             (HerJarvisRealImplementations.cs)
//   - ReasoningLoopInnerMonologue        (ReasoningLoopInnerMonologue.cs)
//
// Contract #13: self-reflection / inner monologue.
// TemplateInnerMonologue fills a narrative template from a context summary and
// an inferred direction. ReasoningLoopInnerMonologue drives a reasoning-capable
// LLM (o1 / DeepSeek-R1 style) and captures the <think> stream as the thought.
//
// In-memory + deterministic (the template path). The reasoning path defers to
// the injected IChatGenerator.

import Foundation

// MARK: - SelfReflection

/// A single reflective thought stamped with the time it was produced.
public struct SelfReflection: Sendable, Equatable {
    public let thought: String
    public let at: Date

    public init(thought: String, at: Date) {
        self.thought = thought
        self.at = at
    }
}

// MARK: - IInnerMonologue

/// Contract #13 — self-reflection / inner monologue.
public protocol IInnerMonologue: AnyObject {
    /// Produce a reflective thought about the supplied context JSON.
    func reflect(contextJson: String) async throws -> SelfReflection
}

// MARK: - TemplateInnerMonologue

/// Narrative-template reflection over context. Summarises the raw JSON into a
/// short phrase, infers a "direction" from salient keywords, and folds both into
/// one of three template frames. Ported from `TemplateInnerMonologue`
/// (HerJarvisRealImplementations.cs).
///
/// Note on frame selection: the C# reference seeds frame choice with
/// `contextJson.GetHashCode()`, which .NET randomises per process — so the exact
/// frame is not reproducible across runs (in C# either). This port uses a
/// deterministic FNV-1a hash so the choice is stable for a given input; the
/// meaningful, testable behaviour (summary + direction substitution, and that
/// the output is one of the three frames) is preserved exactly.
public final class TemplateInnerMonologue: IInnerMonologue, @unchecked Sendable {
    static let frames: [String] = [
        "Observation: {summary}. Implication: this likely means {direction}.",
        "Looking at {summary}, the salient pattern is {direction}.",
        "Given {summary}, my next step is to {direction}.",
    ]

    public init() {}

    public func reflect(contextJson: String) async throws -> SelfReflection {
        let summary = Self.summarise(contextJson)
        let direction = Self.inferDirection(contextJson)
        let seed = Self.stableHash(contextJson) & Int(Int32.max)
        let frame = Self.frames[seed % Self.frames.count]
        let thought = frame
            .replacingOccurrences(of: "{summary}", with: summary)
            .replacingOccurrences(of: "{direction}", with: direction)
        return SelfReflection(thought: thought, at: Date())
    }

    /// Strips JSON punctuation, keeps the first 12 whitespace-separated words.
    static func summarise(_ json: String) -> String {
        var clean = json
        for ch in ["{", "}", "[", "]", "\""] {
            clean = clean.replacingOccurrences(of: ch, with: " ")
        }
        let words = clean.split(separator: " ", omittingEmptySubsequences: true).prefix(12)
        return words.joined(separator: " ")
    }

    /// Salient-keyword direction, checked in the reference's order:
    /// error → goal → user → default.
    static func inferDirection(_ json: String) -> String {
        if json.range(of: "error", options: .caseInsensitive) != nil { return "diagnose the failure first" }
        if json.range(of: "goal", options: .caseInsensitive) != nil { return "advance toward the stated goal" }
        if json.range(of: "user", options: .caseInsensitive) != nil { return "respond to the user" }
        return "gather more context"
    }

    /// Deterministic 32-bit FNV-1a hash over the UTF-8 bytes, returned as a
    /// non-negative Int. Replaces .NET's per-process-randomised String.GetHashCode
    /// with a stable value so frame selection is reproducible.
    static func stableHash(_ s: String) -> Int {
        var hash: UInt32 = 2166136261
        for byte in s.utf8 {
            hash ^= UInt32(byte)
            hash = hash &* 16777619
        }
        return Int(hash & 0x7FFF_FFFF)
    }
}

// MARK: - ReasoningLoopInnerMonologue

/// Inner-monologue powered by a reasoning-capable LLM. Streams fragments from
/// the injected `IChatGenerator`, accumulating the `.reasoning` fragments as the
/// inner thought and `.content` fragments as the visible conclusion; prefers the
/// reasoning trace, falls back to content, and degrades to "(no inner state)".
/// Ported from `ReasoningLoopInnerMonologue` (ReasoningLoopInnerMonologue.cs).
public final class ReasoningLoopInnerMonologue: IInnerMonologue, @unchecked Sendable {
    private static let reasoningSystemPrompt =
        "You are this user's inner monologue. Reason carefully before responding. " +
        "Use <think>...</think> blocks for chain-of-thought. The visible answer " +
        "afterwards should be short and reflective — not a solution, an observation."

    private let llm: IChatGenerator

    public init(_ llm: IChatGenerator) {
        self.llm = llm
    }

    public func reflect(contextJson: String) async throws -> SelfReflection {
        let messages = [
            ChatMessage(role: "system", content: Self.reasoningSystemPrompt),
            ChatMessage(role: "user",
                        content: "Context (raw JSON):\n\(contextJson)\n\nReflect on this in 2-3 sentences."),
        ]
        let options = GenerationOptions(maxTokens: 256, temperature: 0.5, includeReasoning: true)

        var reasoning = ""
        var content = ""
        // The reference wraps the stream in try/catch and swallows failures,
        // degrading to whatever was captured (or the placeholder below).
        for await frag in llm.streamFragments(messages: messages, options: options) {
            if frag.kind == .reasoning {
                reasoning += frag.text
            } else {
                content += frag.text
            }
        }

        // Prefer the reasoning trace as the "thought"; fall back to visible content.
        var thought = !reasoning.isEmpty
            ? reasoning.trimmingCharacters(in: .whitespacesAndNewlines)
            : content.trimmingCharacters(in: .whitespacesAndNewlines)
        if thought.isEmpty { thought = "(no inner state)" }
        return SelfReflection(thought: thought, at: Date())
    }
}
