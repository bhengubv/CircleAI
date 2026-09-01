// MemoryAtoms.swift
//
// The atom: one thing worth remembering, and the situation that finds it.
//
// Ported from src/CircleAI.Memory: MemoryAtom.cs, Situation.cs, IAtomStore.cs,
// AtomCandidate.cs, IAtomExtractor.cs, CueExtractor.cs.

import Foundation

public enum AtomKind: Int, Sendable, Equatable, CaseIterable, Codable {
    case decision = 0
    case ruling
    case fact
    case preference
    case relationship

    /// The name the shared log writes, which the C# also writes.
    public var wireName: String {
        switch self {
        case .decision: return "Decision"
        case .ruling: return "Ruling"
        case .fact: return "Fact"
        case .preference: return "Preference"
        case .relationship: return "Relationship"
        }
    }

    public static func fromWire(_ raw: String?) -> AtomKind? {
        guard let raw else { return nil }
        return allCases.first { $0.wireName.lowercased() == raw.lowercased() }
    }
}

public enum DecisionOutcome: Int, Sendable, Equatable, CaseIterable, Codable {
    case open = 0
    case resolved
    case failed

    public var wireName: String {
        switch self {
        case .open: return "Open"
        case .resolved: return "Resolved"
        case .failed: return "Failed"
        }
    }

    public static func fromWire(_ raw: String?) -> DecisionOutcome? {
        guard let raw else { return nil }
        return allCases.first { $0.wireName.lowercased() == raw.lowercased() }
    }
}

public struct MemoryAtom: Sendable, Equatable {
    public var id: UUID
    public var kind: AtomKind
    public var text: String
    public var subject: String?
    public var sourceEpisode: UUID?
    public var recordedAtUtc: Date
    public var machine: String?
    public var corrections: Int
    public var lastCorrectedUtc: Date?
    public var supersededBy: UUID?
    public var challenge: String?
    public var outcome: DecisionOutcome?
    public var verify: String?
    public var verifiedAtUtc: Date?
    public var verifiedOk: Bool?

    public init(
        id: UUID = UUID(),
        kind: AtomKind = .decision,
        text: String = "",
        subject: String? = nil,
        sourceEpisode: UUID? = nil,
        recordedAtUtc: Date = Date(timeIntervalSince1970: 0),
        machine: String? = nil,
        corrections: Int = 0,
        lastCorrectedUtc: Date? = nil,
        supersededBy: UUID? = nil,
        challenge: String? = nil,
        outcome: DecisionOutcome? = nil,
        verify: String? = nil,
        verifiedAtUtc: Date? = nil,
        verifiedOk: Bool? = nil
    ) {
        self.id = id
        self.kind = kind
        self.text = text
        self.subject = subject
        self.sourceEpisode = sourceEpisode
        self.recordedAtUtc = recordedAtUtc
        self.machine = machine
        self.corrections = corrections
        self.lastCorrectedUtc = lastCorrectedUtc
        self.supersededBy = supersededBy
        self.challenge = challenge
        self.outcome = outcome
        self.verify = verify
        self.verifiedAtUtc = verifiedAtUtc
        self.verifiedOk = verifiedOk
    }

    public var isCurrent: Bool { supersededBy == nil }

    /// A FACT that failed its own check. Still readable, no longer an answer.
    public var isStale: Bool { kind == .fact && verifiedOk == false }

    public var failed: Bool { outcome == .failed }
}

/// What is about to happen, described well enough to look it up.
///
/// THIS IS THE WHOLE DIFFERENCE between a memory that helps and one that does
/// not. Loading everything at the start of a conversation puts the rules
/// furthest from the moment they apply; an hour and forty tool calls later,
/// nothing read at the greeting is meaningfully present, and no amount of
/// emphasis in the file changes that.
///
/// So recall is keyed on the ACTION rather than the session. Before a deploy,
/// ask what is known about deploying — the subject of the action matched
/// against the subject of the atom, which is a lookup rather than a guess.
public struct Situation: Sendable, Equatable {
    public var verb: String?
    public var target: String?
    public var tool: String?
    public var text: String?

    public init(verb: String? = nil, target: String? = nil, tool: String? = nil, text: String? = nil) {
        self.verb = verb
        self.target = target
        self.tool = tool
        self.text = text
    }

    public var key: String {
        [verb, target]
            .compactMap { $0 }
            .filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() }
            .joined(separator: ":")
    }

    /// Most specific first, then broader.
    ///
    /// A slash-delimited target is walked UP — android/p30 also matches android
    /// — because a rule filed against the general case has to be found by the
    /// specific one. Without that, a rule about deploying to Android is
    /// invisible the moment somebody names the phone.
    public var keys: [String] {
        var out: [String] = []
        let v = verb?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        var t = target?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()

        if let v, !v.isEmpty, let target0 = t, !target0.isEmpty {
            out.append("\(v):\(target0)")
            while let current = t, let cut = current.lastIndex(of: "/"), cut != current.startIndex {
                let shorter = String(current[current.startIndex..<cut])
                out.append("\(v):\(shorter)")
                t = shorter
            }
        }

        if let v, !v.isEmpty { out.append(v) }
        return out
    }

    public var query: String {
        [verb, target, tool, text]
            .compactMap { $0 }
            .filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .joined(separator: " ")
    }

    public var isEmpty: Bool {
        keys.isEmpty && (text?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true)
    }
}

public struct RecallResult: Sendable, Equatable {
    public let atoms: [MemoryAtom]
    public let tone: [MemoryAtom]
    public let considered: Int

    public init(atoms: [MemoryAtom], tone: [MemoryAtom], considered: Int) {
        self.atoms = atoms
        self.tone = tone
        self.considered = considered
    }

    public var any: Bool { !atoms.isEmpty }

    public static let empty = RecallResult(atoms: [], tone: [], considered: 0)
}

/// How much recall is allowed to say.
///
/// A budget rather than a limit: what fits is chosen by rank, so the cap costs
/// the least useful atoms rather than truncating the most useful one mid-word.
public struct RecallBudget: Sendable, Equatable {
    public let maxAtoms: Int
    public let maxCharacters: Int

    public init(maxAtoms: Int = 5, maxCharacters: Int = 600) {
        self.maxAtoms = maxAtoms
        self.maxCharacters = maxCharacters
    }

    public static let `default` = RecallBudget()
}

/// Reading and writing the layer between raw turns and a persona.
///
/// NOTHING HERE REQUIRES AN EMBEDDING. Vector search improves recall; it must
/// never be what ENABLES it. A store that stops working without a 100 MB model
/// is a store that does not work on the phone this is for.
public protocol IAtomStore: Sendable {
    func add(_ atom: MemoryAtom) async throws
    func supersede(_ oldAtomId: UUID, with replacement: MemoryAtom) async throws -> MemoryAtom
    func match(_ situation: Situation, limit: Int) async throws -> [MemoryAtom]
    func byKind(_ kind: AtomKind, limit: Int) async throws -> [MemoryAtom]
    func all(includeSuperseded: Bool, limit: Int) async throws -> [MemoryAtom]
    func knows(_ text: String) async throws -> Bool
    func get(_ id: UUID) async throws -> MemoryAtom?
    func markVerified(_ id: UUID, ok: Bool, whenUtc: Date) async throws
    func count() async throws -> Int
}

public extension IAtomStore {
    func match(_ situation: Situation) async throws -> [MemoryAtom] {
        try await match(situation, limit: 20)
    }
    func byKind(_ kind: AtomKind) async throws -> [MemoryAtom] {
        try await byKind(kind, limit: 50)
    }
    func all() async throws -> [MemoryAtom] {
        try await all(includeSuperseded: false, limit: 500)
    }
}

/// Something worth remembering, SPOTTED rather than written.
///
/// Extraction PROPOSES; it does not decide. A candidate carries what was
/// spotted, which words triggered it and how sure that is, because an extractor
/// that silently writes whatever it thinks it saw fills the memory with noise —
/// and noise ranks. Recall then puts a misreading in front of somebody at the
/// exact moment they are about to act on it, which is worse than an empty
/// memory.
public struct AtomCandidate: Sendable, Equatable {
    public let atom: MemoryAtom
    public let confidence: Double
    public let cue: String
    public let quote: String

    public init(atom: MemoryAtom, confidence: Double, cue: String, quote: String) {
        self.atom = atom
        self.confidence = confidence
        self.cue = cue
        self.quote = quote
    }

    /// Above this it is recorded; below it, it is offered. Nothing is
    /// superseded on a guess.
    public static let recordAbove = 0.80

    public var certain: Bool { confidence >= Self.recordAbove }
}

/// The seam between what was said and what is remembered.
///
/// ONE SEAM, TWO MECHANISMS. CueExtractor needs no model and therefore works on
/// a phone with the radios off, which makes it the FLOOR rather than the
/// fallback. A model reads a conversation better than any list of phrases will,
/// and when one is loaded it should do this job — but it must never be what
/// makes the memory work at all.
public protocol IAtomExtractor: Sendable {
    var name: String { get }
    func extract(_ episode: EpisodicMemoryEntry, subject: String?) -> [AtomCandidate]
}

public extension IAtomExtractor {
    func extract(_ episode: EpisodicMemoryEntry) -> [AtomCandidate] {
        extract(episode, subject: nil)
    }
}
