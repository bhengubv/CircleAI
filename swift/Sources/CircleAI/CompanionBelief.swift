// CompanionBelief.swift
// Memory integrity: attribution + belief revision. Ported from
// Circle.AI.Companion (PersonalBelief, HeuristicBeliefExtractor, SelfBeliefStore).
//
// Every belief carries WHOSE fact it is — the user's own (self), someone else's
// (other), or a general fact (world). The highest-harm rule: a fact about a third
// party ("my mother is diabetic") must never be recorded as a fact about the user.

import Foundation

/// Whose fact a belief is about.
public enum Attribution: Sendable {
    case selfBelief
    case other
    case world
}

/// A single attributed belief, with provenance and confidence.
public struct PersonalBelief: Sendable {
    public let attribution: Attribution
    public let subject: String
    public let predicate: String
    public let object: String
    public let confidence: Float
    public let source: String?
    public let recordedAt: Date
    public init(attribution: Attribution, subject: String, predicate: String, object: String,
                confidence: Float, source: String?, recordedAt: Date) {
        self.attribution = attribution
        self.subject = subject
        self.predicate = predicate
        self.object = object
        self.confidence = confidence
        self.source = source
        self.recordedAt = recordedAt
    }
}

/// Turns a sentence into attributed beliefs.
public protocol IBeliefExtractor {
    func extract(text: String, source: String?) async throws -> [PersonalBelief]
}

/// Model-free belief extractor with attribution discipline. Coarse by design, but
/// it never collapses "my mother" into "me". Attribution is decided by the
/// sentence's leading subject.
public struct HeuristicBeliefExtractor: IBeliefExtractor {
    private static let separators: Set<Character> =
        [" ", "\t", "\n", "\r", ".", ",", "?", "!", ";", ":", "\"", "(", ")"]

    private static let relations: Set<String> = [
        "mother", "father", "mom", "mum", "dad", "sister", "brother", "wife", "husband", "son", "daughter",
        "aunt", "uncle", "grandmother", "grandfather", "granny", "grandpa", "gran", "nan", "friend",
        "colleague", "boss", "neighbour", "neighbor", "cousin", "partner", "girlfriend", "boyfriend",
    ]
    private static let possessive: Set<String> = ["my", "her", "his", "their", "our"]
    private static let stop: Set<String> = [
        "the", "a", "an", "is", "are", "was", "were", "be", "been", "am", "to", "of", "in", "on", "at", "and", "or",
        "but", "with", "has", "have", "had", "that", "this", "it", "as", "for", "really", "very", "just", "now",
    ]

    public init() {}

    public func extract(text: String, source: String?) async throws -> [PersonalBelief] {
        if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return [] }

        let tokens = text.lowercased().split(whereSeparator: { Self.separators.contains($0) }).map(String.init)
        if tokens.isEmpty { return [] }

        var attribution: Attribution
        var subject: String
        var skip = Set<Int>()

        if tokens.count >= 2 && Self.possessive.contains(tokens[0]) && Self.relations.contains(tokens[1]) {
            // "my mother ..." → someone else
            attribution = .other; subject = tokens[1]; skip.insert(0); skip.insert(1)
        } else if Self.relations.contains(tokens[0]) {
            attribution = .other; subject = tokens[0]; skip.insert(0)
        } else if tokens[0] == "i" || tokens[0] == "i'm" || tokens[0] == "im" || tokens[0] == "me" || tokens[0] == "my" {
            // "I ..." or "my <non-relation> ..." → the user
            attribution = .selfBelief; subject = "user"; skip.insert(0)
        } else {
            attribution = .world; subject = tokens[0]
        }

        let objTokens = tokens.enumerated().filter { pair in
            !skip.contains(pair.offset) && pair.element.count >= 3
                && !Self.stop.contains(pair.element) && !Self.relations.contains(pair.element)
        }.map { $0.element }
        let obj = objTokens.joined(separator: " ")
        if obj.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return [] }

        return [PersonalBelief(attribution: attribution, subject: subject, predicate: "isAbout",
                               object: obj, confidence: 0.6, source: source, recordedAt: Date())]
    }
}

/// The user's own facts, with attribution filtering, revision, and correction.
public final class SelfBeliefStore: @unchecked Sendable {
    private let lock = NSLock()
    private var selfFactsList: [PersonalBelief] = []
    private var auditList: [PersonalBelief] = [] // other/world — remembered, never a user fact

    public init() {}

    /// Record a belief. Only self beliefs become user facts; the rest are audited.
    public func record(_ belief: PersonalBelief) {
        lock.lock(); defer { lock.unlock() }
        if belief.attribution != .selfBelief {
            auditList.append(belief)
            return
        }
        // Supersede an existing self-belief on the same (subject, predicate).
        selfFactsList.removeAll { eqCi($0.subject, belief.subject) && eqCi($0.predicate, belief.predicate) }
        selfFactsList.append(belief)
    }

    public func selfFacts() -> [PersonalBelief] {
        lock.lock(); defer { lock.unlock() }
        return selfFactsList
    }

    public func nonSelf() -> [PersonalBelief] {
        lock.lock(); defer { lock.unlock() }
        return auditList
    }

    /// Correction ("no, that's my mother"): drop any user fact mentioning the text.
    public func retract(objectContains: String) -> Int {
        let needle = objectContains.trimmingCharacters(in: .whitespacesAndNewlines)
        if needle.isEmpty { return 0 }
        let lowerNeedle = needle.lowercased()
        lock.lock(); defer { lock.unlock() }
        let before = selfFactsList.count
        selfFactsList.removeAll { $0.object.lowercased().contains(lowerNeedle) }
        return before - selfFactsList.count
    }

    /// Introspection: the distinct source turns behind the user's facts.
    public func provenance() -> [String] {
        lock.lock(); defer { lock.unlock() }
        var seen = Set<String>()
        var out: [String] = []
        for b in selfFactsList {
            if let s = b.source, !seen.contains(s) {
                seen.insert(s)
                out.append(s)
            }
        }
        return out
    }
}

private func eqCi(_ a: String, _ b: String) -> Bool { a.lowercased() == b.lowercased() }
