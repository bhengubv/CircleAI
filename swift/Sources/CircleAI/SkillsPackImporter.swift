// SkillsPackImporter.swift
//
// Fetches each enabled skill pack, caches it, and feeds its SKILL.md files
// through the loader.
//
// THE DOWNLOAD IS BEHIND A SEAM so a test can substitute one that copies
// pre-staged content out of a temp directory. Without that, every test of the
// import logic is a test of the network.
//
// Ported from src/CircleAI.Skills/SkillPackAutoImporter.cs.

import Foundation

/// Ensures a pack is on disk and returns its directory.
///
/// THE FIRST ARGUMENT IS UNLABELLED. `ensure(pack, cacheRoot:, cacheTtl:)`
/// reads as a sentence at the call site, which is the Swift convention for a
/// method whose first argument is its subject.
public protocol IPackDownloader: Sendable {
    func ensure(_ source: SkillPackSource, cacheRoot: String,
                cacheTtl: TimeInterval) async throws -> String
}

/// Everything that can stop a pack reaching disk.
///
/// `Equatable` because tests compare the case they expected against the one
/// thrown; without it they can only check that *something* was thrown, which
/// passes just as happily when the wrong thing goes wrong.
public enum SkillPackError: Error, Equatable, CustomStringConvertible {
    case emptyCacheRoot
    case fetchFailed(String, Int)
    case extractionUnavailable
    /// Nothing is registered for this source. Distinct from `fetchFailed`: the
    /// fetch never happened, so reporting an HTTP status would invent one.
    case unavailable(String)

    public var description: String {
        switch self {
        case .emptyCacheRoot:
            return "A cache directory is required."
        case .fetchFailed(let name, let status):
            return "Skill pack '\(name)' could not be fetched (HTTP \(status))."
        case .extractionUnavailable:
            return "This host has no archive extractor wired. Supply one to "
                 + "HttpPackDownloader, or use a downloader that stages files directly."
        case .unavailable(let name):
            return "Pack '\(name)' is not available."
        }
    }
}

/// Fetches a pack over HTTP as a tarball and extracts it.
///
/// BOTH THE TRANSPORT AND THE EXTRACTOR ARE CLOSURES. A host already owns an
/// HTTP client configured with its own timeouts and pinning, and Foundation has
/// no tar/gzip reader on the platforms this has to run on — so neither is
/// hidden in here where it would silently bypass what the host configured or
/// fail to exist at all.
public struct HttpPackDownloader: IPackDownloader, Sendable {

    public typealias Fetch = @Sendable (URL) async throws -> (data: Data, status: Int)
    /// Extracts `archive` into `directory`. Returns false when it cannot.
    public typealias Extract = @Sendable (_ archive: Data, _ directory: String) throws -> Bool

    private let fetch: Fetch
    private let extract: Extract?
    private let now: @Sendable () -> Date

    public init(fetch: @escaping Fetch,
                extract: Extract? = nil,
                now: @escaping @Sendable () -> Date = { Date() }) {
        self.fetch = fetch
        self.extract = extract
        self.now = now
    }

    public func ensure(_ source: SkillPackSource, cacheRoot: String,
                       cacheTtl: TimeInterval) async throws -> String {
        guard !cacheRoot.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw SkillPackError.emptyCacheRoot
        }

        let fm = FileManager.default
        let packDir = (cacheRoot as NSString).appendingPathComponent(Self.sanitise(source.name))
        let stamp = (packDir as NSString).appendingPathComponent(".stamp")

        // A FRESH CACHE SHORT-CIRCUITS EVERYTHING. Skill packs change rarely and
        // a host that re-fetches them on every launch costs somebody data for
        // bytes they already have.
        if let attrs = try? fm.attributesOfItem(atPath: stamp),
           let written = attrs[.modificationDate] as? Date,
           now().timeIntervalSince(written) <= cacheTtl {
            return packDir
        }

        guard let url = Self.tarballUrl(for: source) else {
            throw SkillPackError.fetchFailed(source.name, 0)
        }

        let (data, status) = try await fetch(url)
        guard (200..<300).contains(status) else {
            throw SkillPackError.fetchFailed(source.name, status)
        }
        guard let extract else { throw SkillPackError.extractionUnavailable }

        // EXTRACTED TO A STAGING DIRECTORY AND MOVED. A failure part-way
        // through leaves the previous pack intact rather than a half-written
        // one that the loader then reads.
        let stage = packDir + ".stage"
        try? fm.removeItem(atPath: stage)
        try fm.createDirectory(atPath: stage, withIntermediateDirectories: true)

        guard try extract(data, stage) else {
            try? fm.removeItem(atPath: stage)
            throw SkillPackError.extractionUnavailable
        }

        // A GitHub tarball nests its content under <repo>-<ref>/. Flattened,
        // because the loader looks for SKILL.md at the top and would otherwise
        // find an empty pack and report nothing wrong.
        var staged = stage
        let entries = (try? fm.contentsOfDirectory(atPath: stage)) ?? []
        let files = entries.filter { entry in
            var isDir: ObjCBool = false
            let p = (stage as NSString).appendingPathComponent(entry)
            _ = fm.fileExists(atPath: p, isDirectory: &isDir)
            return !isDir.boolValue
        }
        if files.isEmpty, let inner = entries.first {
            staged = (stage as NSString).appendingPathComponent(inner)
        }

        try? fm.removeItem(atPath: packDir)
        try fm.moveItem(atPath: staged, toPath: packDir)
        try? fm.removeItem(atPath: stage)

        try? Data(ISO8601DateFormatter().string(from: now()).utf8)
            .write(to: URL(fileURLWithPath: (packDir as NSString).appendingPathComponent(".stamp")))

        return packDir
    }

    /// `https://github.com/<owner>/<repo>/archive/<ref>.tar.gz`
    static func tarballUrl(for source: SkillPackSource) -> URL? {
        var base = source.repoUrl
        while base.hasSuffix("/") { base.removeLast() }
        return URL(string: "\(base)/archive/\(source.gitRef).tar.gz")
    }

    /// A directory name from a pack name. Anything that is not a letter, digit,
    /// dash or underscore becomes a dash — a pack called "SA / Public Sector"
    /// must not create nested directories or escape the cache root.
    static func sanitise(_ name: String) -> String {
        let cleaned = String(name.map { ch in
            ch.isLetter || ch.isNumber || ch == "-" || ch == "_" ? ch : "-"
        })
        let trimmed = cleaned.trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        return trimmed.isEmpty ? "pack" : trimmed.lowercased()
    }
}

/// Imports every enabled pack, once, when the host says so.
public struct SkillPackAutoImporter: Sendable {

    /// Reads a fetched pack directory into the store. The loader is a closure
    /// so this file owns the WHICH and the WHEN, and not the parsing.
    public typealias Import = @Sendable (_ packDirectory: String,
                                         _ source: SkillPackSource) async throws -> Int

    private let downloader: any IPackDownloader
    private let options: SkillPackSourcesOptions
    private let importPack: Import
    private let log: (@Sendable (String) -> Void)?

    public init(downloader: any IPackDownloader,
                options: SkillPackSourcesOptions,
                importPack: @escaping Import,
                log: (@Sendable (String) -> Void)? = nil) {
        self.downloader = downloader
        self.options = options
        self.importPack = importPack
        self.log = log
    }

    public struct Result: Sendable, Equatable {
        public let imported: [String: Int]
        public let failed: [String: String]

        public var totalSkills: Int { imported.values.reduce(0, +) }
    }

    /// Which packs actually run: the ones enabled by default when that is
    /// allowed, plus anything explicitly turned on.
    ///
    /// Explicit enablement wins over the default flag, so a person can switch
    /// on a pack that does not ship enabled without editing anything else.
    public func enabledSources() -> [SkillPackSource] {
        let explicit = Set(options.explicitlyEnabled.map { $0.lowercased() })
        return options.sources.filter { source in
            if explicit.contains(source.name.lowercased()) { return true }
            return options.importDefaultEnabledPacks && source.isDefaultEnabled
        }
    }

    public func run() async -> Result {
        var imported: [String: Int] = [:]
        var failed: [String: String] = [:]

        for source in enabledSources() {
            do {
                let dir = try await downloader.ensure(source,
                                                      cacheRoot: options.cacheDirectory,
                                                      cacheTtl: options.cacheTtl)
                imported[source.name] = try await importPack(dir, source)
            } catch {
                // ONE PACK FAILING MUST NOT LOSE THE OTHERS. A repository that
                // moved is ordinary, and it should cost that pack's skills, not
                // every pack's.
                failed[source.name] = "\(error)"
                log?("skill pack '\(source.name)' failed: \(error)")
            }
        }
        return Result(imported: imported, failed: failed)
    }
}
