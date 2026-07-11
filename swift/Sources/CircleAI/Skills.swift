// Skills.swift
//
// Port of the named CircleAI.Skills/ types — the B! skill store + skill-pack
// source declarations + a deterministic pack downloader.
//   • SkillSource.cs             — SkillSource
//   • SkillSummary.cs            — SkillSummary
//   • SkillDetail.cs             — SkillDetail
//   • SkillDraft.cs              — SkillDraft
//   • ISkillStore.cs             — ISkillStore
//   • InMemorySkillStore.cs      — InMemorySkillStore (+ GenerateSlug)
//   • SkillPackSource.cs         — SkillPackSource, KnownSkillPacks
//   • SkillPackAutoImporter.cs   — IPackDownloader (+ in-memory impl),
//                                  SkillPackSourcesOptions
//
// Porting notes:
//   • The C# `HttpPackDownloader` fetches GitHub tarballs (network + tar I/O);
//     the Swift port provides a deterministic `InMemoryPackDownloader` seeded
//     with source-name → local-path mappings, matching the "inject external"
//     rule. `SkillPackAutoImporter` and `SkillPackLoader` walk the extracted
//     SKILL.md tree on disk — file-system-bound and out of the named scope, so
//     they are not ported; the store + source + downloader surface is complete
//     and deterministic on its own.
//   • `GenerateSlug` ports the C# regex slug pipeline (lowercase → spaces to
//     dashes → strip non-[a-z0-9-] → collapse dashes → trim).
//   • Slug fallback for an empty name uses a 32-char lower-hex UUID
//     (C# `Guid.NewGuid().ToString("N")`).

import Foundation

// MARK: - Enum + records

/// Where a `SkillDetail` originated. (C# `SkillSource`.)
public enum SkillSource: Int, Sendable, Codable, CaseIterable {
    /// Loaded from a SKILL.md file on disk.
    case file = 0
    /// Created programmatically and held in memory.
    case inMemory = 1
    /// Fetched from a remote skill registry.
    case remote = 2
}

/// Lightweight projection used in list + search results. (C# `SkillSummary`.)
public struct SkillSummary: Sendable, Equatable, Codable {
    /// Unique slug identifier.
    public let id: String
    /// Display name.
    public let name: String
    /// One-line summary.
    public let description: String
    /// Free-form tags.
    public let tags: [String]
    /// Where the record was loaded from.
    public let source: SkillSource

    public init(id: String, name: String, description: String, tags: [String], source: SkillSource) {
        self.id = id
        self.name = name
        self.description = description
        self.tags = tags
        self.source = source
    }
}

/// Full skill record. (C# `SkillDetail`.)
public struct SkillDetail: Sendable, Equatable, Codable {
    /// Unique slug identifier.
    public let id: String
    /// Display name.
    public let name: String
    /// One-line summary.
    public let description: String
    /// Detailed instructions injected into the system prompt.
    public let instructions: String
    /// Free-form tags.
    public let tags: [String]
    /// Where the record was loaded from.
    public let source: SkillSource
    /// UTC last-modified timestamp.
    public let lastModified: Date

    public init(id: String, name: String, description: String, instructions: String,
                tags: [String], source: SkillSource, lastModified: Date) {
        self.id = id
        self.name = name
        self.description = description
        self.instructions = instructions
        self.tags = tags
        self.source = source
        self.lastModified = lastModified
    }
}

/// Input model for creating/updating a skill. (C# `SkillDraft`.)
public struct SkillDraft: Sendable, Equatable, Codable {
    /// Display name (drives slug generation when no id is provided).
    public let name: String
    /// One-line summary.
    public let description: String
    /// Detailed instructions.
    public let instructions: String
    /// Free-form tags.
    public let tags: [String]

    public init(name: String, description: String, instructions: String, tags: [String]) {
        self.name = name
        self.description = description
        self.instructions = instructions
        self.tags = tags
    }
}

// MARK: - ISkillStore

/// Persistent store for B! skills. (C# `ISkillStore`.)
public protocol ISkillStore: Sendable {
    /// All skills as lightweight summaries (ordered by name).
    func list() async -> [SkillSummary]
    /// Full detail for a skill by id, or `nil`.
    func get(_ id: String) async -> SkillDetail?
    /// Skills whose name/description/tags contain `query` (case-insensitive).
    /// Empty when `query` is empty.
    func search(_ query: String) async -> [SkillSummary]
    /// Creates or replaces a skill. A `nil`/empty `id` auto-generates a slug
    /// from the draft name.
    func upsert(_ id: String?, draft: SkillDraft) async -> SkillDetail
    /// Removes the skill with `id`. No-op if absent.
    func delete(_ id: String) async
}

/// Thread-safe in-memory `ISkillStore`. (C# `InMemorySkillStore`.)
public final class InMemorySkillStore: ISkillStore, @unchecked Sendable {
    private let lock = NSLock()
    private var skills: [String: SkillDetail] = [:]

    public init() {}

    public func list() async -> [SkillSummary] {
        lock.lock(); let snap = Array(skills.values); lock.unlock()
        return snap.map(Self.toSummary)
            .sorted { $0.name.lowercased() < $1.name.lowercased() }
    }

    public func get(_ id: String) async -> SkillDetail? {
        precondition(!id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "id required")
        lock.lock(); defer { lock.unlock() }
        return skills[id]
    }

    public func search(_ query: String) async -> [SkillSummary] {
        if query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return [] }
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        lock.lock(); let snap = Array(skills.values); lock.unlock()
        return snap.filter { Self.matches($0, query: q) }
            .map(Self.toSummary)
            .sorted { $0.name.lowercased() < $1.name.lowercased() }
    }

    public func upsert(_ id: String?, draft: SkillDraft) async -> SkillDetail {
        let effectiveId: String
        if let id = id, !id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            effectiveId = id.trimmingCharacters(in: .whitespacesAndNewlines)
        } else {
            effectiveId = Self.generateSlug(draft.name)
        }
        let detail = SkillDetail(id: effectiveId, name: draft.name, description: draft.description,
                                 instructions: draft.instructions, tags: draft.tags,
                                 source: .inMemory, lastModified: Date())
        lock.lock(); skills[effectiveId] = detail; lock.unlock()
        return detail
    }

    public func delete(_ id: String) async {
        precondition(!id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "id required")
        lock.lock(); skills[id] = nil; lock.unlock()
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static func toSummary(_ d: SkillDetail) -> SkillSummary {
        SkillSummary(id: d.id, name: d.name, description: d.description, tags: d.tags, source: d.source)
    }

    private static func matches(_ s: SkillDetail, query: String) -> Bool {
        let q = query.lowercased()
        return s.name.lowercased().contains(q)
            || s.description.lowercased().contains(q)
            || s.tags.contains { $0.lowercased().contains(q) }
    }

    /// Converts a display name to a URL-safe lowercase slug. "My Skill" →
    /// "my-skill". (C# `InMemorySkillStore.GenerateSlug`.)
    public static func generateSlug(_ name: String) -> String {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty { return randomHex32() }
        var slug = trimmed.lowercased()
        // Collapse whitespace runs to single dashes.
        slug = slug.replacingOccurrences(of: "\\s+", with: "-", options: .regularExpression)
        // Strip anything not a-z, 0-9, or dash.
        slug = slug.replacingOccurrences(of: "[^a-z0-9\\-]", with: "", options: .regularExpression)
        // Collapse repeated dashes.
        slug = slug.replacingOccurrences(of: "-{2,}", with: "-", options: .regularExpression)
        // Trim leading/trailing dashes.
        slug = slug.trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        return slug.isEmpty ? randomHex32() : slug
    }

    private static func randomHex32() -> String {
        UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    }
}

// MARK: - Skill packs

/// Source declaration for a single remote skill pack. (C# `SkillPackSource`.)
public struct SkillPackSource: Sendable, Equatable, Codable {
    /// Display name and tag prefix.
    public let name: String
    /// Canonical repo URL.
    public let repoUrl: String
    /// Branch / tag / commit.
    public let gitRef: String
    /// SPDX identifier or descriptive string.
    public let license: String
    /// Optional subdir where SKILL.md files live ("" walks the whole tree).
    public let skillSubdir: String
    /// Cardinality hint (not enforced).
    public let estimatedSkillCount: Int
    /// Whether the auto-importer imports this pack by default.
    public let isDefaultEnabled: Bool
    /// Extra tags merged into every imported skill.
    public let defaultTags: [String]?

    public init(name: String, repoUrl: String, gitRef: String = "main", license: String = "unknown",
                skillSubdir: String = "", estimatedSkillCount: Int = 0,
                isDefaultEnabled: Bool = true, defaultTags: [String]? = nil) {
        self.name = name
        self.repoUrl = repoUrl
        self.gitRef = gitRef
        self.license = license
        self.skillSubdir = skillSubdir
        self.estimatedSkillCount = estimatedSkillCount
        self.isDefaultEnabled = isDefaultEnabled
        self.defaultTags = defaultTags
    }
}

/// Default catalogue of skill packs. (C# `KnownSkillPacks`.)
public enum KnownSkillPacks {
    public static let awesomeAgentSkills = SkillPackSource(
        name: "awesome-agent-skills",
        repoUrl: "https://github.com/bhengubv/awesome-agent-skills",
        license: "Apache-2.0", skillSubdir: "skills",
        estimatedSkillCount: 1000, defaultTags: ["community"])

    public static let anthropicCybersecurity = SkillPackSource(
        name: "Anthropic-Cybersecurity-Skills",
        repoUrl: "https://github.com/mukul975/Anthropic-Cybersecurity-Skills",
        license: "Apache-2.0", skillSubdir: "skills",
        estimatedSkillCount: 754, defaultTags: ["security", "mitre"])

    public static let privacyDataProtection = SkillPackSource(
        name: "Privacy-Data-Protection-Skills",
        repoUrl: "https://github.com/mukul975/Privacy-Data-Protection-Skills",
        license: "Apache-2.0", skillSubdir: "skills",
        estimatedSkillCount: 282, defaultTags: ["privacy", "compliance"])

    public static let claudeBugHunter = SkillPackSource(
        name: "Claude-BugHunter",
        repoUrl: "https://github.com/bhengubv/Claude-BugHunter",
        license: "Apache-2.0", skillSubdir: "skills",
        estimatedSkillCount: 51, defaultTags: ["security", "bug-bounty"])

    public static let last30Days = SkillPackSource(
        name: "last30days-skill",
        repoUrl: "https://github.com/bhengubv/last30days-skill",
        license: "MIT", estimatedSkillCount: 1, defaultTags: ["research"])

    public static let edubaBrand = SkillPackSource(
        name: "eduba-brand",
        repoUrl: "https://github.com/bhengubv/eduba-brand",
        license: "n/a (pattern-port)", skillSubdir: ".agents/skills/eduba-brand",
        estimatedSkillCount: 1, defaultTags: ["branding", "eduba"])

    public static let careerOps = SkillPackSource(
        name: "career-ops",
        repoUrl: "https://github.com/bhengubv/career-ops",
        license: "MIT", estimatedSkillCount: 14, isDefaultEnabled: false,
        defaultTags: ["job-search", "career", "thejobcenter"])

    public static let buildYourOwnX = SkillPackSource(
        name: "build-your-own-x",
        repoUrl: "https://github.com/bhengubv/build-your-own-x",
        license: "MIT", estimatedSkillCount: 0, isDefaultEnabled: false,
        defaultTags: ["education", "tutorial"])

    /// Every known pack.
    public static let all: [SkillPackSource] = [
        awesomeAgentSkills, anthropicCybersecurity, privacyDataProtection,
        claudeBugHunter, last30Days, edubaBrand, careerOps, buildYourOwnX,
    ]
}

/// Settings for skill-pack auto-import. (C# `SkillPackSourcesOptions`.) The C#
/// default cache directory + TTL are dropped from this portable value type;
/// hosts wire their own materialisation via `IPackDownloader`.
public struct SkillPackSourcesOptions: Sendable {
    /// All packs the host knows about.
    public var sources: [SkillPackSource]
    /// When true, import every default-enabled source.
    public var importDefaultEnabledPacks: Bool
    /// Pack names opted in beyond the default-enabled set.
    public var explicitlyEnabled: [String]

    public init(sources: [SkillPackSource] = KnownSkillPacks.all,
                importDefaultEnabledPacks: Bool = true,
                explicitlyEnabled: [String] = []) {
        self.sources = sources
        self.importDefaultEnabledPacks = importDefaultEnabledPacks
        self.explicitlyEnabled = explicitlyEnabled
    }

    /// The enabled subset, matching the C# `EnumerateEnabled` (default-enabled
    /// first, then explicitly-enabled, de-duplicated by name).
    public func enumerateEnabled() -> [SkillPackSource] {
        var seen = Set<String>()
        var result: [SkillPackSource] = []
        if importDefaultEnabledPacks {
            for s in sources where s.isDefaultEnabled {
                if seen.insert(s.name.lowercased()).inserted { result.append(s) }
            }
        }
        let byName = Dictionary(sources.map { ($0.name.lowercased(), $0) }, uniquingKeysWith: { a, _ in a })
        for name in explicitlyEnabled {
            if let src = byName[name.lowercased()], seen.insert(src.name.lowercased()).inserted {
                result.append(src)
            }
        }
        return result
    }
}

/// Errors raised by a pack downloader.
public enum SkillPackError: Error, Equatable, CustomStringConvertible {
    case unavailable(String)
    public var description: String {
        switch self {
        case .unavailable(let name): return "Pack '\(name)' is not available."
        }
    }
}

/// Strategy for materialising a remote pack into a local directory. (C#
/// `IPackDownloader`.) Returns the local path containing the extracted repo.
public protocol IPackDownloader: Sendable {
    /// Ensure `source` is materialised under `cacheRoot`. Returns the local
    /// path containing the extracted repo.
    func ensure(_ source: SkillPackSource, cacheRoot: String, cacheTtl: TimeInterval) async throws -> String
}

/// Deterministic in-memory downloader — seeded with source-name → local-path
/// mappings. Replaces the C# `HttpPackDownloader` (GitHub tarball fetch) so the
/// import flow is testable without the network. Throws `SkillPackError.unavailable`
/// when a source has no seeded path.
public final class InMemoryPackDownloader: IPackDownloader, @unchecked Sendable {
    private let lock = NSLock()
    private var paths: [String: String] = [:]

    public init() {}

    /// Register the local path a source materialises to.
    public func add(sourceName: String, localPath: String) {
        lock.lock(); paths[sourceName] = localPath; lock.unlock()
    }

    public func ensure(_ source: SkillPackSource, cacheRoot: String, cacheTtl: TimeInterval) async throws -> String {
        lock.lock(); let path = paths[source.name]; lock.unlock()
        guard let path = path else { throw SkillPackError.unavailable(source.name) }
        // Match the C# contract: return the on-disk pack directory. When the
        // caller registered an absolute path, honour it; otherwise root it.
        if path.hasPrefix("/") || path.contains(":") { return path }
        return "\(cacheRoot)/\(path)"
    }
}
