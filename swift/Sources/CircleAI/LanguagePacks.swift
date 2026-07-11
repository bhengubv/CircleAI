// LanguagePacks.swift
//
// Port of the language-pack base surface from
// src/CircleAI.Languages.Language/:
//   • ILanguagePack.cs            → LanguagePackMetadata, CulturalNote, ILanguagePack
//   • ILanguagePackRegistry.cs    → ILanguagePackRegistry
//   • DefaultLanguagePackRegistry.cs → DefaultLanguagePackRegistry (NSLock-guarded)
//   • LanguagePackHelpers.cs      → LanguagePackRegistry (BCP-47 matching) + LocaleHintMerge
//
// The eight concrete packs (Afrikaans/Amharic/Arabic/Hausa/Portuguese/Sesotho/
// Swahili/isiZulu) live in LanguagePackData.swift.
//
// Porting notes:
//   • C# `Version` → `PackVersion` value type (major/minor) — Foundation has no
//     lightweight semantic-version struct we want to lean on here.
//   • C# `StringComparer.OrdinalIgnoreCase` dictionaries → lookups lowercase the
//     key (idiom/notes/registry tags) so match behaviour is preserved.
//   • `ILanguagePack.getCulturalNotes` returns `[]` when the context is unmapped,
//     matching the C# `? n : []`.
//   • Registries hold shared mutable state → `final class … @unchecked Sendable`
//     with a single `NSLock`; every locked region is a synchronous block.

import Foundation

// MARK: - PackVersion

/// A lightweight semantic version (major.minor) for a language pack. Mirrors the
/// C# `System.Version(major, minor)` used in `LanguagePackMetadata.PackVersion`.
public struct PackVersion: Sendable, Equatable, Comparable, CustomStringConvertible {
    public let major: Int
    public let minor: Int

    public init(_ major: Int, _ minor: Int) {
        self.major = major
        self.minor = minor
    }

    public static func < (lhs: PackVersion, rhs: PackVersion) -> Bool {
        lhs.major != rhs.major ? lhs.major < rhs.major : lhs.minor < rhs.minor
    }

    public var description: String { "\(major).\(minor)" }
}

// MARK: - LanguagePackMetadata

/// Metadata for a language pack. Port of C# record `LanguagePackMetadata`.
public struct LanguagePackMetadata: Sendable, Equatable {
    /// BCP-47 tag, e.g. "zu", "af", "ar".
    public let bcpTag: String
    /// English display name, e.g. "isiZulu".
    public let displayName: String
    /// Name in the language itself, e.g. "Kiswahili".
    public let nativeName: String
    /// ISO 3166-1 alpha-2 primary region, e.g. "ZA".
    public let primaryRegion: String
    /// All ISO regions this language is spoken in.
    public let spokenInRegions: [String]
    /// Pack version.
    public let packVersion: PackVersion

    public init(
        bcpTag: String,
        displayName: String,
        nativeName: String,
        primaryRegion: String,
        spokenInRegions: [String],
        packVersion: PackVersion
    ) {
        self.bcpTag = bcpTag
        self.displayName = displayName
        self.nativeName = nativeName
        self.primaryRegion = primaryRegion
        self.spokenInRegions = spokenInRegions
        self.packVersion = packVersion
    }
}

// MARK: - CulturalNote

/// Cultural/contextual note for a specific topic. Port of C# record `CulturalNote`.
public struct CulturalNote: Sendable, Equatable {
    /// The context this note applies to (e.g. "greeting", "business").
    public let context: String
    /// Human-readable guidance for the model.
    public let guidance: String
    /// Illustrative examples.
    public let examples: [String]

    public init(context: String, guidance: String, examples: [String]) {
        self.context = context
        self.guidance = guidance
        self.examples = examples
    }
}

// MARK: - ILanguagePack

/// A language-specific knowledge pack. Provides idiomatic expressions, cultural
/// context, and prompt tuning for the on-device LLM to reason correctly in this
/// language. Port of C# interface `ILanguagePack`.
public protocol ILanguagePack: AnyObject, Sendable {
    var metadata: LanguagePackMetadata { get }

    /// Returns the idiomatic translation of a common phrase, or nil if not mapped.
    func idiomaticExpression(_ phrase: String) -> String?

    /// Adapts a base system prompt for this language and culture.
    func adaptSystemPrompt(_ basePrompt: String) -> String

    /// Cultural notes for a given context (e.g. "greeting", "business", "medical").
    func culturalNotes(_ context: String) -> [CulturalNote]

    /// Returns a locale-appropriate greeting for the given time of day.
    func greeting(timeOfDay: String) -> String

    /// Returns locale-specific number/date/currency formatting hints.
    func localeHints() -> [String: String]
}

// MARK: - ILanguagePackRegistry

/// Registry of all installed language packs. Port of C# interface
/// `ILanguagePackRegistry`.
public protocol ILanguagePackRegistry: AnyObject, Sendable {
    func register(_ pack: ILanguagePack)
    func byBcpTag(_ bcpTag: String) -> ILanguagePack?
    func availablePacks() -> [LanguagePackMetadata]
    func hasPack(_ bcpTag: String) -> Bool
}

// MARK: - DefaultLanguagePackRegistry

/// Thread-safe in-memory `ILanguagePackRegistry`. Port of C#
/// `DefaultLanguagePackRegistry` (Dictionary + Lock). Keys are matched
/// case-insensitively (the C# uses the raw `BcpTag` as the dictionary key; tags
/// are canonically lower-case, so we lower-case on both store and lookup to be
/// robust).
public final class DefaultLanguagePackRegistry: ILanguagePackRegistry, @unchecked Sendable {
    private let lock = NSLock()
    private var packs: [String: ILanguagePack] = [:]

    public init() {}

    public func register(_ pack: ILanguagePack) {
        lock.lock(); defer { lock.unlock() }
        packs[pack.metadata.bcpTag.lowercased()] = pack
    }

    public func byBcpTag(_ bcpTag: String) -> ILanguagePack? {
        lock.lock(); defer { lock.unlock() }
        return packs[bcpTag.lowercased()]
    }

    public func availablePacks() -> [LanguagePackMetadata] {
        lock.lock(); defer { lock.unlock() }
        return packs.values.map { $0.metadata }
    }

    public func hasPack(_ bcpTag: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return packs[bcpTag.lowercased()] != nil
    }
}

// MARK: - LanguagePackRegistry (BCP-47 matching helper)

/// Registry with richer BCP-47 matching. Port of the C#
/// `LanguagePackHelpers.LanguagePackRegistry` (ConcurrentDictionary). Supports
/// exact-tag, language-prefix, and region lookups.
public final class LanguagePackRegistry: @unchecked Sendable {
    private let lock = NSLock()
    private var byTag: [String: ILanguagePack] = [:]

    public init() {}

    public func register(_ pack: ILanguagePack) {
        lock.lock(); defer { lock.unlock() }
        byTag[pack.metadata.bcpTag.lowercased()] = pack
    }

    /// Exact-tag lookup. Returns nil for blank input.
    public func byExactTag(_ bcpTag: String) -> ILanguagePack? {
        if bcpTag.trimmingCharacters(in: .whitespaces).isEmpty { return nil }
        lock.lock(); defer { lock.unlock() }
        return byTag[bcpTag.lowercased()]
    }

    /// Language-prefix lookup: "pt-BR" matches a pack whose tag starts with "pt".
    /// Returns the first match in an unspecified but stable order.
    public func byLanguage(_ langPrefix: String) -> ILanguagePack? {
        if langPrefix.trimmingCharacters(in: .whitespaces).isEmpty { return nil }
        let prefix = langPrefix.split(separator: "-").first.map(String.init)?.lowercased()
            ?? langPrefix.lowercased()
        lock.lock(); defer { lock.unlock() }
        for pack in byTag.values where pack.metadata.bcpTag.lowercased().hasPrefix(prefix) {
            return pack
        }
        return nil
    }

    /// All packs spoken in `region` (case-insensitive). Traps on blank region,
    /// mirroring the C# `ArgumentException`.
    public func forRegion(_ region: String) -> [ILanguagePack] {
        precondition(!region.trimmingCharacters(in: .whitespaces).isEmpty, "region required")
        let needle = region.lowercased()
        lock.lock(); defer { lock.unlock() }
        return byTag.values.filter { pack in
            pack.metadata.spokenInRegions.contains { $0.lowercased() == needle }
        }
    }

    /// All registered tags, sorted ascending (mirrors `AllTags()` `OrderBy`).
    public func allTags() -> [String] {
        lock.lock(); defer { lock.unlock() }
        return byTag.keys.sorted()
    }
}

// MARK: - LocaleHintMerge

/// Merges two locale-hint dictionaries. Port of C# static `LocaleHintMerge`.
/// `primary` wins on key collision. Matching is case-insensitive on keys.
public enum LocaleHintMerge {
    public static func merge(
        primary: [String: String],
        secondary: [String: String]
    ) -> [String: String] {
        // Build case-insensitively: start from secondary, then overlay primary.
        var merged: [String: String] = [:]
        var canonical: [String: String] = [:]  // lowercased key → original key

        func put(_ key: String, _ value: String) {
            let lower = key.lowercased()
            if let existing = canonical[lower] {
                merged[existing] = value
            } else {
                canonical[lower] = key
                merged[key] = value
            }
        }

        for (k, v) in secondary { put(k, v) }
        for (k, v) in primary { put(k, v) }
        return merged
    }
}
