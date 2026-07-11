// Personality.swift
//
// Port of src/CircleAI.Personality/ — the user-DECLARED persona artefact
// (distinct from CircleAI.Memory.PersonaState, the AI's LEARNED model):
//   • Persona.cs                    — Persona record + Persona.Create,
//                                      FormalityRange record, PrivacyLevel enum
//   • IPersonaProvider.cs           — storage contract (get/save/exists/exportAll)
//   • IPersonaConflictResolver.cs   — resolver contract + DeclaredWinsResolver
//                                      (clamp learned formality into declared
//                                      range) + LearnedWinsResolver
//   • JsonPersonaProvider.cs        — file-system provider ({userId}.persona.json,
//                                      atomic write-then-rename, per-userId lock)
//   • PersonaPromptBuilder.cs       — renders a Persona into a system-prompt hint,
//                                      JSON-quoting every user string (injection
//                                      defence)
//
// Porting notes:
//   • `record` → `struct: Sendable, Equatable, Codable`. `Guid` → `UUID`;
//     `DateTimeOffset` → `Date`.
//   • The resolver reads `PersonaState.formality` (the existing Swift class in
//     Memory.swift). `Persona with { Formality = ... }` → a `withFormality` copy.
//   • `IAsyncEnumerable<Persona>` → `AsyncThrowingStream<Persona, Error>`.
//   • JSON string-quoting uses `JSONSerialization` with `.fragmentsAllowed`, which
//     escapes quotes/newlines exactly like System.Text.Json — the injection-hardening
//     property is preserved.
//   • `Path.GetInvalidFileNameChars` → a conservative sanitiser.
//   • Guards → `PersonalityError`.

import Foundation

// MARK: - PrivacyLevel

/// Declared privacy posture.
public enum PrivacyLevel: String, Sendable, Equatable, Codable {
    /// Minimum retention, no proactive surfacing, no third-party calls without prompt.
    case strict = "Strict"
    /// Default. Reasonable retention, helpful proactive prompts.
    case balanced = "Balanced"
    /// Maximum retention, willing to share personal context across surfaces.
    case open = "Open"
}

// MARK: - FormalityRange

/// Declared bounds on conversational formality. Allowed values: "casual",
/// "neutral", "formal".
public struct FormalityRange: Sendable, Equatable, Codable {
    public let floor: String
    public let ceiling: String
    public init(floor: String, ceiling: String) {
        self.floor = floor
        self.ceiling = ceiling
    }
}

// MARK: - Persona

/// User-declared persona artefact — the structured identity the user chose to
/// share, distinct from the AI's learned `PersonaState`.
public struct Persona: Sendable, Equatable, Codable {
    public let id: UUID
    public let displayName: String
    public let pronouns: String?
    public let identityTags: [String]
    public let values: [String]
    public let taboos: [String]
    public let preferredLocale: String
    public let voicePreference: String?
    public let formality: FormalityRange
    public let privacy: PrivacyLevel
    public let createdAt: Date
    public let updatedAt: Date

    public init(
        id: UUID,
        displayName: String,
        pronouns: String?,
        identityTags: [String],
        values: [String],
        taboos: [String],
        preferredLocale: String,
        voicePreference: String?,
        formality: FormalityRange,
        privacy: PrivacyLevel,
        createdAt: Date,
        updatedAt: Date
    ) {
        self.id = id
        self.displayName = displayName
        self.pronouns = pronouns
        self.identityTags = identityTags
        self.values = values
        self.taboos = taboos
        self.preferredLocale = preferredLocale
        self.voicePreference = voicePreference
        self.formality = formality
        self.privacy = privacy
        self.createdAt = createdAt
        self.updatedAt = updatedAt
    }

    /// Creates a fresh persona with sensible defaults: balanced privacy, no
    /// taboos/values, "casual".."formal" formality, timestamps stamped to now.
    public static func create(displayName: String, locale: String) -> Persona {
        precondition(!displayName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        precondition(!locale.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        let now = Date()
        return Persona(
            id: UUID(),
            displayName: displayName,
            pronouns: nil,
            identityTags: [],
            values: [],
            taboos: [],
            preferredLocale: locale,
            voicePreference: nil,
            formality: FormalityRange(floor: "casual", ceiling: "formal"),
            privacy: .balanced,
            createdAt: now,
            updatedAt: now)
    }

    /// Returns a copy with a replaced formality range (mirrors C# `with { Formality = ... }`).
    public func withFormality(_ range: FormalityRange) -> Persona {
        Persona(
            id: id, displayName: displayName, pronouns: pronouns, identityTags: identityTags,
            values: values, taboos: taboos, preferredLocale: preferredLocale,
            voicePreference: voicePreference, formality: range, privacy: privacy,
            createdAt: createdAt, updatedAt: updatedAt)
    }

    /// Returns a copy with a refreshed `updatedAt` (used by providers on save).
    public func withUpdatedAt(_ when: Date) -> Persona {
        Persona(
            id: id, displayName: displayName, pronouns: pronouns, identityTags: identityTags,
            values: values, taboos: taboos, preferredLocale: preferredLocale,
            voicePreference: voicePreference, formality: formality, privacy: privacy,
            createdAt: createdAt, updatedAt: when)
    }
}

// MARK: - Errors

public enum PersonalityError: Error, Equatable, CustomStringConvertible {
    case userIdRequired
    case rootDirectoryRequired

    public var description: String {
        switch self {
        case .userIdRequired: return "userId required"
        case .rootDirectoryRequired: return "rootDirectory required"
        }
    }
}

// MARK: - IPersonaProvider

/// Persists and retrieves user-declared `Persona` documents.
public protocol IPersonaProvider: Sendable {
    /// Loads the persona for `userId`, or nil when none is saved.
    func get(userId: String) async throws -> Persona?

    /// Persists `persona` for `userId`, refreshing `updatedAt`, and returns the saved record.
    func save(userId: String, persona: Persona) async throws -> Persona

    /// Whether a persona is stored for `userId`.
    func exists(userId: String) async throws -> Bool

    /// Streams every stored persona (for user-driven export).
    func exportAll() -> AsyncThrowingStream<Persona, Error>
}

// MARK: - IPersonaConflictResolver

/// Reconciles a declared `Persona` with the AI's learned `PersonaState`.
/// Implementations must be deterministic and must never mutate either input.
public protocol IPersonaConflictResolver: Sendable {
    func resolve(declared: Persona, learned: PersonaState) -> Persona
}

/// Default resolver: the declared persona's bounds are hard limits; the learned
/// formality is clamped to the declared range. Everything else passes through.
public struct DeclaredWinsResolver: IPersonaConflictResolver {
    public init() {}

    public func resolve(declared: Persona, learned: PersonaState) -> Persona {
        let clamped = DeclaredWinsResolver.clampFormality(learned.formality, declared.formality)
        if clamped == learned.formality {
            // Learned was within bounds — no adjustment to surface.
            return declared
        }
        // Learned drifted outside declared bounds — surface the clamped value.
        let range: FormalityRange
        switch clamped {
        case "casual": range = FormalityRange(floor: "casual", ceiling: declared.formality.ceiling)
        case "formal": range = FormalityRange(floor: declared.formality.floor, ceiling: "formal")
        default: range = declared.formality
        }
        return declared.withFormality(range)
    }

    private static func clampFormality(_ learned: String, _ range: FormalityRange) -> String {
        let learnedRank = rank(learned)
        let floorRank = rank(range.floor)
        let ceilingRank = rank(range.ceiling)
        if floorRank > ceilingRank { return range.floor } // inverted range → fixed at floor
        if learnedRank < floorRank { return range.floor }
        if learnedRank > ceilingRank { return range.ceiling }
        return learned
    }

    private static func rank(_ formality: String) -> Int {
        switch formality {
        case "casual": return 0
        case "neutral": return 1
        case "formal": return 2
        default: return 1 // unknown → neutral
        }
    }
}

/// Alternative resolver: the learned state overrides the declared persona
/// ("privacy mode off"). Still returns the declared persona so identity, taboos,
/// and values stay intact; the learned formality/locale is applied elsewhere.
public struct LearnedWinsResolver: IPersonaConflictResolver {
    public init() {}
    public func resolve(declared: Persona, learned: PersonaState) -> Persona {
        declared
    }
}

// MARK: - JsonPersonaProvider

/// File-system `IPersonaProvider` that stores each persona as
/// `{rootDirectory}/{userId}.persona.json`. Atomic write-then-rename, per-userId lock.
public final class JsonPersonaProvider: IPersonaProvider, @unchecked Sendable {
    private let rootDirectory: String
    private let lock = NSLock()
    private var gates: [String: NSLock] = [:]

    private static let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        e.dateEncodingStrategy = .iso8601
        return e
    }()
    private static let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()

    public init(rootDirectory: String) {
        precondition(!rootDirectory.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        self.rootDirectory = rootDirectory
        try? FileManager.default.createDirectory(atPath: rootDirectory, withIntermediateDirectories: true)
    }

    public func get(userId: String) async throws -> Persona? {
        if userId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw PersonalityError.userIdRequired }
        let path = personaPath(userId)
        guard FileManager.default.fileExists(atPath: path) else { return nil }
        let gate = gateFor(userId)
        gate.lock(); defer { gate.unlock() }
        guard let data = FileManager.default.contents(atPath: path) else { return nil }
        return try JsonPersonaProvider.decoder.decode(Persona.self, from: data)
    }

    public func save(userId: String, persona: Persona) async throws -> Persona {
        if userId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw PersonalityError.userIdRequired }
        let refreshed = persona.withUpdatedAt(Date())
        let target = personaPath(userId)
        let tmp = target + "." + UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased() + ".tmp"

        let gate = gateFor(userId)
        gate.lock(); defer { gate.unlock() }
        do {
            let data = try JsonPersonaProvider.encoder.encode(refreshed)
            try data.write(to: URL(fileURLWithPath: tmp))
            if FileManager.default.fileExists(atPath: target) {
                try FileManager.default.removeItem(atPath: target)
            }
            try FileManager.default.moveItem(atPath: tmp, toPath: target)
            return refreshed
        } catch {
            try? FileManager.default.removeItem(atPath: tmp)
            throw error
        }
    }

    public func exists(userId: String) async throws -> Bool {
        if userId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw PersonalityError.userIdRequired }
        return FileManager.default.fileExists(atPath: personaPath(userId))
    }

    public func exportAll() -> AsyncThrowingStream<Persona, Error> {
        let dir = rootDirectory
        return AsyncThrowingStream { continuation in
            guard let files = try? FileManager.default.contentsOfDirectory(atPath: dir) else {
                continuation.finish()
                return
            }
            for file in files where file.hasSuffix(".persona.json") {
                let full = (dir as NSString).appendingPathComponent(file)
                guard let data = FileManager.default.contents(atPath: full) else { continue }
                // Skip corrupted records during export rather than failing the whole stream.
                if let persona = try? JsonPersonaProvider.decoder.decode(Persona.self, from: data) {
                    continuation.yield(persona)
                }
            }
            continuation.finish()
        }
    }

    // MARK: Helpers

    private func gateFor(_ userId: String) -> NSLock {
        lock.lock(); defer { lock.unlock() }
        if let g = gates[userId] { return g }
        let g = NSLock()
        gates[userId] = g
        return g
    }

    private func personaPath(_ userId: String) -> String {
        let invalid = CharacterSet(charactersIn: "/\\:*?\"<>|\0")
        let safeScalars = userId.unicodeScalars.map { invalid.contains($0) ? "_" : Character($0) }
        var safe = String(safeScalars)
        if safe.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { safe = "default" }
        return (rootDirectory as NSString).appendingPathComponent(safe + ".persona.json")
    }
}

// MARK: - PersonaPromptBuilder

/// Renders a `Persona` into a compact natural-language system-prompt hint,
/// JSON-quoting every user-controlled string (prompt-injection defence).
public enum PersonaPromptBuilder {
    /// Renders `persona` into a hint, or "" when it is effectively default.
    public static func buildSystemHint(_ persona: Persona) -> String {
        if isEffectivelyDefault(persona) { return "" }

        var sb = "[Persona]"
        sb += "\nYou are speaking with " + quote(persona.displayName) + "."

        if let pronouns = persona.pronouns, !pronouns.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            sb += " They identify as " + quote(pronouns) + "."
        }

        sb += "\nThey prefer responses in " + quote(persona.preferredLocale)
        sb += ", tone between " + quote(persona.formality.floor) + " and " + quote(persona.formality.ceiling) + "."

        if !persona.identityTags.isEmpty {
            sb += "\nIdentity tags: " + quoteList(persona.identityTags) + "."
        }
        if !persona.values.isEmpty {
            sb += "\nTheir declared values: " + quoteList(persona.values) + "."
        }
        if !persona.taboos.isEmpty {
            sb += "\nAvoid: " + quoteList(persona.taboos) + "."
        }
        if let voice = persona.voicePreference, !voice.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            sb += "\nPreferred voice tag: " + quote(voice) + "."
        }

        if persona.privacy == .strict {
            sb += "\nPrivacy: strict — minimize stored signals, do not surface personal context proactively, and never share personal context across surfaces without explicit prompt."
        } else if persona.privacy == .open {
            sb += "\nPrivacy: open — the user has authorised broader retention and proactive surfacing."
        }

        return sb
    }

    /// True when the persona carries no information beyond `Persona.create` defaults.
    private static func isEffectivelyDefault(_ p: Persona) -> Bool {
        (p.pronouns?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true)
            && p.identityTags.isEmpty
            && p.values.isEmpty
            && p.taboos.isEmpty
            && (p.voicePreference?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true)
            && p.privacy == .balanced
            && p.formality.floor == "casual"
            && p.formality.ceiling == "formal"
    }

    /// JSON-encodes `value` into a quoted literal — any embedded quote/newline/
    /// directive becomes inert text inside a quoted string.
    private static func quote(_ value: String) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: value, options: [.fragmentsAllowed]) else {
            return "\"\""
        }
        return String(decoding: data, as: UTF8.self)
    }

    private static func quoteList(_ items: [String]) -> String {
        items.map { quote($0) }.joined(separator: ", ")
    }
}
