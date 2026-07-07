// MemoryExtractor.swift
// Knowledge-graph extraction: turn → (subject, predicate, object) triples.
// Ported from Circle.AI.Companion (IKnowledgeGraphExtractor,
// HeuristicKnowledgeGraphExtractor) — the C# reference. The heuristic extractor
// is model-free: it links a turn's content words to the memory they came from,
// two-way, so a later question can reach an older memory across turns.

import Foundation

/// Turns a conversation turn into knowledge-graph triples.
public protocol IKnowledgeGraphExtractor {
    func extractFromTurn(userText: String, assistantText: String,
                         sourceEpisodeId: String?) async throws -> [KnowledgeTriple]
}

/// Model-free extractor: links a turn's content words to their memory, two-way.
public struct HeuristicKnowledgeGraphExtractor: IKnowledgeGraphExtractor {
    private static let defaultConfidence: Float = 0.6

    private static let separators: Set<Character> =
        [" ", "\t", "\n", "\r", ".", ",", "?", "!", ";", ":", "'", "\"", "(", ")", "/", "-"]

    private static let stop: Set<String> = [
        "the", "a", "an", "and", "or", "but", "if", "is", "are", "was", "were", "be", "been", "being",
        "to", "of", "in", "on", "at", "for", "with", "from", "by", "as", "into", "about", "over", "under",
        "my", "your", "our", "their", "his", "her", "its", "this", "that", "these", "those",
        "i", "you", "he", "she", "it", "we", "they", "me", "him", "them", "us",
        "do", "does", "did", "done", "have", "has", "had", "will", "would", "can", "could", "should",
        "shall", "may", "might", "must", "not", "no", "yes", "so", "than", "then", "there", "here",
        "how", "why", "what", "when", "where", "who", "which", "whom",
        "am", "get", "got", "really", "just", "very", "much", "many", "some", "any", "all",
    ]

    public init() {}

    public func extractFromTurn(userText: String, assistantText: String,
                                sourceEpisodeId: String?) async throws -> [KnowledgeTriple] {
        // The memory node is identified by the source id when given, else the
        // user's words — so recall can hand back the memory it came from.
        let memory: String
        if let s = sourceEpisodeId, !s.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            memory = s
        } else {
            memory = userText
        }
        if memory.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return [] }

        let words = Self.contentWords(userText + " " + assistantText)
        let now = Date()
        var triples: [KnowledgeTriple] = []
        for w in words {
            triples.append(KnowledgeTriple(subject: memory, predicate: "mentions", object: w,
                                           source: sourceEpisodeId, confidence: Self.defaultConfidence, recordedAt: now))
            triples.append(KnowledgeTriple(subject: w, predicate: "seenin", object: memory,
                                           source: sourceEpisodeId, confidence: Self.defaultConfidence, recordedAt: now))
        }
        return triples
    }

    private static func contentWords(_ text: String) -> [String] {
        var seen = Set<String>()
        var result: [String] = []
        for raw in text.lowercased().split(whereSeparator: { separators.contains($0) }) {
            let word = String(raw)
            if word.count < 3 || stop.contains(word) { continue }
            if seen.insert(word).inserted { result.append(word) }
        }
        return result
    }
}
