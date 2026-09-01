// MemoryJsonStores.swift
//
// Affect and persona on the local filesystem, and the VAD projection.
//
// WRITE TO A TEMPORARY NAME, THEN RENAME. A rename within one filesystem is
// atomic, so a reader never sees a partial file and a kill mid-write costs the
// update rather than the record. The temporary name is unique per save, so two
// saves for one user cannot contend on one path.
//
// A CORRUPT FILE READS AS A FRESH STATE rather than throwing. Affect is a
// running estimate of how a conversation is going; refusing to start because
// one file is unreadable trades a lost estimate for a dead app, and the next
// save overwrites it anyway.
//
// Ported from JsonAffectStore.cs, JsonPersonaStore.cs and AffectVad.cs.

import Foundation

// NOTE: the C# AffectStateVadExtensions is a static class holding one extension
// method, and Swift already carries it as `AffectState.toVad()` in Memory.swift.
// A Swift extension has no type NAME, so there is nothing to declare here and
// nothing for a name-based parity measure to find. It is in the exclusions file
// for that reason rather than because it is absent.

/// A user id becomes part of a FILE NAME, so anything that is not a letter, a
/// digit, a hyphen or an underscore becomes a hyphen. An id containing a slash
/// would otherwise write outside the folder it was given.
func memoryStoreSafeName(_ userId: String) -> String {
    let cleaned = String(userId.map {
        $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" ? $0 : "-"
    }).trimmingCharacters(in: CharacterSet(charactersIn: "-"))
    return cleaned.isEmpty ? "default" : cleaned
}

func memoryStoreWriteAtomically(_ text: String, to target: String) throws {
    let dir = (target as NSString).deletingLastPathComponent
    try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
    let tmp = target + "." + String(UUID().uuidString.prefix(8)) + ".tmp"
    try text.write(toFile: tmp, atomically: false, encoding: .utf8)
    // Replacing an existing file: remove then move, because a plain move onto
    // an existing path is refused on some filesystems.
    if FileManager.default.fileExists(atPath: target) {
        try? FileManager.default.removeItem(atPath: target)
    }
    do {
        try FileManager.default.moveItem(atPath: tmp, toPath: target)
    } catch {
        try? FileManager.default.removeItem(atPath: tmp)
        throw error
    }
}

public enum MemoryStoreError: Error, Equatable, CustomStringConvertible {
    case directoryRequired
    case userIdRequired
    public var description: String {
        switch self {
        case .directoryRequired: return "Directory is required."
        case .userIdRequired: return "userId is required."
        }
    }
}

/// Affect on the local filesystem.
public struct JsonAffectStore: IAffectStore {

    private let directory: String

    public init(directory: String) throws {
        guard !directory.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw MemoryStoreError.directoryRequired
        }
        self.directory = directory
        try FileManager.default.createDirectory(
            atPath: directory, withIntermediateDirectories: true)
    }

    public func path(for userId: String) -> String {
        (directory as NSString)
            .appendingPathComponent("affect-\(memoryStoreSafeName(userId)).json")
    }

    public func load(userId: String) async throws -> AffectState {
        guard !userId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw MemoryStoreError.userIdRequired
        }
        let file = path(for: userId)
        guard FileManager.default.fileExists(atPath: file) else {
            return AffectState(userId: userId)
        }
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: file)),
              let row = try? JSONDecoder().decode(AffectRow.self, from: data) else {
            // Corrupt: a fresh state, and the next save overwrites it.
            return AffectState(userId: userId)
        }
        return row.toState()
    }

    public func save(_ state: AffectState) async throws {
        state.lastUpdatedAt = Date()
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(AffectRow.of(state))
        try memoryStoreWriteAtomically(String(decoding: data, as: UTF8.self),
                                       to: path(for: state.userId))
    }

    struct AffectRow: Codable {
        var userId: String
        var lastUpdatedAt: Date
        var curiosity: Float
        var engagement: Float
        var uncertainty: Float
        var rapport: Float
        var energy: Float

        func toState() -> AffectState {
            let s = AffectState(userId: userId)
            s.lastUpdatedAt = lastUpdatedAt
            s.curiosity = curiosity
            s.engagement = engagement
            s.uncertainty = uncertainty
            s.rapport = rapport
            s.energy = energy
            return s
        }

        static func of(_ s: AffectState) -> AffectRow {
            AffectRow(userId: s.userId, lastUpdatedAt: s.lastUpdatedAt,
                      curiosity: s.curiosity, engagement: s.engagement,
                      uncertainty: s.uncertainty, rapport: s.rapport, energy: s.energy)
        }
    }
}

/// Persona on the local filesystem, on the same write-then-rename pattern.
public struct JsonPersonaStore: IPersonaStore {

    private let directory: String

    public init(directory: String) throws {
        guard !directory.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw MemoryStoreError.directoryRequired
        }
        self.directory = directory
        try FileManager.default.createDirectory(
            atPath: directory, withIntermediateDirectories: true)
    }

    public func path(for userId: String) -> String {
        (directory as NSString)
            .appendingPathComponent("persona-\(memoryStoreSafeName(userId)).json")
    }

    public func load(userId: String) async throws -> PersonaState {
        guard !userId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw MemoryStoreError.userIdRequired
        }
        let file = path(for: userId)
        guard FileManager.default.fileExists(atPath: file) else {
            return PersonaState(userId: userId)
        }
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: file)),
              let row = try? JSONDecoder().decode(PersonaRow.self, from: data) else {
            return PersonaState(userId: userId)
        }
        return row.toState()
    }

    public func save(_ persona: PersonaState) async throws {
        persona.lastUpdatedAt = Date()
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(PersonaRow.of(persona))
        try memoryStoreWriteAtomically(String(decoding: data, as: UTF8.self),
                                       to: path(for: persona.userId))
    }

    struct PersonaRow: Codable {
        var userId: String
        var lastUpdatedAt: Date
        var verbosity: String
        var formality: String
        var preferredLocale: String?
        var topicWeights: [String: Float]
        var disfavouredTopics: [String]
        var totalInteractions: Int
        var positiveSignals: Int
        var negativeSignals: Int

        func toState() -> PersonaState {
            let p = PersonaState(userId: userId)
            p.lastUpdatedAt = lastUpdatedAt
            p.verbosity = verbosity
            p.formality = formality
            p.preferredLocale = preferredLocale
            p.topicWeights = topicWeights
            p.disfavouredTopics = Set(disfavouredTopics)
            p.totalInteractions = totalInteractions
            p.positiveSignals = positiveSignals
            p.negativeSignals = negativeSignals
            return p
        }

        static func of(_ p: PersonaState) -> PersonaRow {
            PersonaRow(userId: p.userId, lastUpdatedAt: p.lastUpdatedAt,
                       verbosity: p.verbosity, formality: p.formality,
                       preferredLocale: p.preferredLocale,
                       topicWeights: p.topicWeights,
                       disfavouredTopics: Array(p.disfavouredTopics).sorted(),
                       totalInteractions: p.totalInteractions,
                       positiveSignals: p.positiveSignals,
                       negativeSignals: p.negativeSignals)
        }
    }
}
