// LlmKnowledgeGraphExtractor.swift
// LLM-backed knowledge-graph extraction: turn → (subject, predicate, object)
// triples. Ported from CircleAI.Companion (LlmKnowledgeGraphExtractor) — the C#
// reference, mirroring the verified TS/Go/Python/Kotlin/Rust ports.
//
// Uses an on-device IChatGenerator to ask an LLM to extract triples from a
// single conversation turn. The extraction prompt asks for strict-JSON output;
// the parser is defensive against the model emitting extra prose or fences.

import Foundation

/// Model-backed `IKnowledgeGraphExtractor`: asks an LLM for triples and parses
/// its JSON reply. Any structural problem — a failed generation, prose around
/// the JSON, or malformed JSON — degrades gracefully to an empty list rather
/// than throwing.
public final class LlmKnowledgeGraphExtractor: IKnowledgeGraphExtractor, @unchecked Sendable {

    /// Confidence used when the model omits (or malforms) the "c" field.
    private static let defaultConfidence: Float = 0.75

    private static let systemPrompt =
        "You are a knowledge-graph extractor. Read the conversation turn between USER and ASSISTANT. " +
        "Identify entities (people, places, things, concepts) and facts. " +
        "Output a single JSON array of triples like [{\"s\":\"Subject\",\"p\":\"predicate\",\"o\":\"object\",\"c\":0.0-1.0}, ...]. " +
        "Only output the JSON — no prose, no markdown fences."

    private let generator: IChatGenerator

    public init(_ generator: IChatGenerator) {
        self.generator = generator
    }

    public func extractFromTurn(userText: String, assistantText: String,
                                sourceEpisodeId: String?) async throws -> [KnowledgeTriple] {
        if Self.isBlank(userText) && Self.isBlank(assistantText) { return [] }

        let userMsg = "USER:\n" + userText + "\nASSISTANT:\n" + assistantText + "\n"

        let reply: String
        do {
            reply = try await generator.generate(
                messages: [
                    ChatMessage(role: "system", content: Self.systemPrompt),
                    ChatMessage(role: "user", content: userMsg),
                ],
                options: nil)
        } catch {
            // LLM call failed — degrade gracefully, no triples this turn.
            return []
        }

        return Self.parseTriples(reply, sourceEpisodeId: sourceEpisodeId)
    }

    /// Parses the model's reply into triples. Finds the first `[` and last `]`,
    /// JSON-parses the slice, and reads s/p/o/c from each object. Any structural
    /// problem yields an empty list rather than throwing.
    static func parseTriples(_ raw: String, sourceEpisodeId: String?) -> [KnowledgeTriple] {
        if isBlank(raw) { return [] }
        guard let firstBracket = raw.firstIndex(of: "["),
              let lastBracket = raw.lastIndex(of: "]"),
              firstBracket < lastBracket else { return [] }

        let jsonSlice = String(raw[firstBracket...lastBracket])
        guard let data = jsonSlice.data(using: .utf8),
              let parsed = try? JSONSerialization.jsonObject(with: data, options: []),
              let array = parsed as? [Any] else { return [] }

        let now = Date()
        var hits: [KnowledgeTriple] = []
        for entry in array {
            guard let obj = entry as? [String: Any] else { continue }
            let s = obj["s"] as? String
            let p = obj["p"] as? String
            let o = obj["o"] as? String
            let c: Float
            if let n = obj["c"], let d = Self.asDouble(n) {
                c = Self.clamp(Float(d), 0, 1)
            } else {
                c = Self.defaultConfidence
            }
            if isBlank(s) || isBlank(p) || isBlank(o) { continue }
            hits.append(KnowledgeTriple(subject: s!, predicate: p!, object: o!,
                                        source: sourceEpisodeId, confidence: c, recordedAt: now))
        }
        return hits
    }

    /// Reads a JSON value as a Double when — and only when — it is a real JSON
    /// number. JSON booleans deserialise to `NSNumber` under JSONSerialization,
    /// so they must be excluded explicitly (they must NOT count as numbers).
    private static func asDouble(_ value: Any) -> Double? {
        guard let number = value as? NSNumber else { return nil }
        if CFGetTypeID(number) == CFBooleanGetTypeID() { return nil }
        return number.doubleValue
    }

    private static func isBlank(_ s: String?) -> Bool {
        guard let s = s else { return true }
        return s.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    private static func clamp(_ x: Float, _ lo: Float, _ hi: Float) -> Float {
        max(lo, min(hi, x))
    }
}
