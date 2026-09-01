// SkillsPacks.swift
//
// Port of the remaining CircleAI.Skills/ types that Skills.swift left out:
//   • SkillPackLoader.cs    — SkillPackManifest, ParsedSkill, SkillPackLoader
//   • SkillContextBuilder.cs — SkillContextBuilder
//   • FileSkillStore.cs      — FileSkillStore
//
// Porting notes:
//   • These conform to the ALREADY-PORTED `ISkillStore` protocol in Skills.swift,
//     whose methods are `list()` / `get(_:)` / `search(_:)` / `upsert(_:draft:)`
//     / `delete(_:)` (no `Async` suffix, no cancellation token). The C#
//     `UpsertAsync(id, draft)` maps to `upsert(id, draft:)`, etc.
//   • `FileSkillStore` and `SkillPackLoader.import` walk / read / write SKILL.md
//     files on disk. Direct `FileManager` I/O is well-precedented in this Swift
//     port (Catalog, Knowledge, ModelDownloadService, …), so — unlike the pure
//     compute types that inject a seam — a *file* store legitimately touches the
//     file system directly; that is its whole identity, and it matches the C#
//     behaviour byte-for-byte (front-matter parse, tag list, last-write mtime).
//   • The C# `IAsyncEnumerable<ParsedSkill> LoadAsync` (streaming) becomes a
//     synchronous `load(root:) -> [ParsedSkill]` that materialises the walk —
//     Swift has no first-class async-sequence-from-disk idiom in this module,
//     and every caller here folds straight into an array anyway. An `onWarning`
//     callback preserves the per-file failure-reporting contract.
//   • Slug generation reuses `InMemorySkillStore.generateSlug` where the C#
//     did; `SkillPackLoader`'s own `Slugify` (letters/digits → dashes) is a
//     distinct algorithm and is ported verbatim as `slugify`.

import Foundation

// MARK: - Records

/// Description of a skill pack — name, version, provenance. (C#
/// `SkillPackManifest`.)
public struct SkillPackManifest: Sendable, Equatable, Codable {
    /// e.g. "Claude-BugHunter".
    public let name: String
    /// Pack version or short commit; "unknown" when unset.
    public let version: String
    /// Canonical repo URL.
    public let sourceUrl: String
    /// SPDX identifier, e.g. "MIT" or "Apache-2.0".
    public let license: String
    /// How many skills loaded from this pack.
    public let skillCount: Int

    public init(name: String, version: String, sourceUrl: String, license: String, skillCount: Int) {
        self.name = name
        self.version = version
        self.sourceUrl = sourceUrl
        self.license = license
        self.skillCount = skillCount
    }
}

/// One parsed skill straight from a SKILL.md file. (C# `ParsedSkill`.)
public struct ParsedSkill: Sendable, Equatable, Codable {
    public let id: String
    public let name: String
    public let description: String
    public let instructions: String
    public let tags: [String]
    public let sourceFilePath: String

    public init(id: String, name: String, description: String, instructions: String,
                tags: [String], sourceFilePath: String) {
        self.id = id
        self.name = name
        self.description = description
        self.instructions = instructions
        self.tags = tags
        self.sourceFilePath = sourceFilePath
    }
}

// MARK: - SkillPackLoader

/// Errors raised by the skill-pack loader. (C# throws `ArgumentException` /
/// `DirectoryNotFoundException`.)
public enum SkillPackLoaderError: Error, Equatable, CustomStringConvertible {
    case argument(String)
    case directoryNotFound(String)

    public var description: String {
        switch self {
        case .argument(let m): return m
        case .directoryNotFound(let p): return "Skill pack root not found: \(p)"
        }
    }
}

/// Walks a skill-pack directory, reads each SKILL.md, parses YAML front-matter +
/// markdown body, and returns / imports the loaded skills. (C#
/// `SkillPackLoader`.)
public enum SkillPackLoader {
    /// Default file name the loader searches for.
    public static let defaultSkillFile = "SKILL.md"

    /// Scan `root` recursively for files named `skillFile`, parse each, and
    /// return the resulting `ParsedSkill` records. Files that fail to parse are
    /// skipped, with the failure raised on the optional `onWarning` callback.
    /// (C# `LoadAsync`, materialised.)
    public static func load(
        root: String,
        skillFile: String = defaultSkillFile,
        onWarning: ((String, Error) -> Void)? = nil
    ) throws -> [ParsedSkill] {
        guard !root.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw SkillPackLoaderError.argument("root required")
        }
        let fm = FileManager.default
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: root, isDirectory: &isDir), isDir.boolValue else {
            throw SkillPackLoaderError.directoryNotFound(root)
        }

        var results: [ParsedSkill] = []
        let rootUrl = URL(fileURLWithPath: root, isDirectory: true)
        guard let enumerator = fm.enumerator(at: rootUrl, includingPropertiesForKeys: nil) else {
            return results
        }
        for case let url as URL in enumerator where url.lastPathComponent == skillFile {
            let file = url.path
            do {
                let text = try String(contentsOf: url, encoding: .utf8)
                if let skill = try? parse(content: text, sourceFilePath: file) {
                    results.append(skill)
                } else {
                    onWarning?(file, SkillPackLoaderError.argument("empty or unparsable content"))
                }
            } catch {
                onWarning?(file, error)
            }
        }
        return results
    }

    /// Import every parsed skill into `store`, returning a manifest with the
    /// count imported. (C# `ImportAsync`.)
    @discardableResult
    public static func `import`(
        into store: any ISkillStore,
        root: String,
        packName: String,
        packVersion: String = "unknown",
        sourceUrl: String = "",
        license: String = "unknown",
        skillFile: String = defaultSkillFile,
        onWarning: ((String, Error) -> Void)? = nil
    ) async throws -> SkillPackManifest {
        guard !packName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw SkillPackLoaderError.argument("packName required")
        }

        var count = 0
        let parsedSkills = try load(root: root, skillFile: skillFile, onWarning: onWarning)
        let packTag = "pack:\(packName.lowercased())"
        for parsed in parsedSkills {
            // Concat + distinct (case-insensitive), matching the C# tag merge.
            var mergedTags = parsed.tags
            if !mergedTags.contains(where: { $0.caseInsensitiveCompare(packTag) == .orderedSame }) {
                mergedTags.append(packTag)
            }
            let draft = SkillDraft(
                name: parsed.name,
                description: parsed.description,
                instructions: parsed.instructions,
                tags: mergedTags)
            // PROPAGATES rather than warning. A store that refuses a write
            // refuses every write - importing into the read-only capability
            // manifest cannot partly succeed - so swallowing this would
            // return skillCount: 0 as though the pack were empty.
            _ = try await store.upsert(parsed.id, draft: draft)
            count += 1
        }
        return SkillPackManifest(name: packName, version: packVersion, sourceUrl: sourceUrl, license: license, skillCount: count)
    }

    /// Parse a single SKILL.md file's text. `sourceFilePath` is informational —
    /// used as a fallback when no name/heading can be extracted. Throws when
    /// `content` is empty. (C# `Parse`.)
    public static func parse(content: String, sourceFilePath: String) throws -> ParsedSkill {
        guard !content.isEmpty else { throw SkillPackLoaderError.argument("content required") }

        let (fmBody, mdBody) = splitFrontmatter(content)

        let name = extractField(fmBody, "name")
            ?? extractFirstHeading(mdBody)
            ?? (sourceFilePath as NSString).lastPathComponent.replacingOccurrences(of: ".md", with: "")
        let description = extractField(fmBody, "description") ?? truncate(mdBody, 280)
        let tags = extractTags(fmBody)
        let id = slugify(name)

        return ParsedSkill(
            id: id,
            name: name,
            description: description,
            instructions: mdBody.trimmingCharacters(in: .whitespacesAndNewlines),
            tags: tags,
            sourceFilePath: sourceFilePath)
    }

    // MARK: front-matter parsing

    /// Returns (frontmatterBody, markdownBody). Mirrors the C#
    /// `FrontmatterRegex` (`^---\n … \n---\n`).
    private static func splitFrontmatter(_ content: String) -> (String, String) {
        let pattern = "^\\s*---\\s*\\r?\\n([\\s\\S]*?)\\r?\\n---\\s*\\r?\\n"
        guard
            let regex = try? NSRegularExpression(pattern: pattern),
            let match = regex.firstMatch(in: content, range: NSRange(content.startIndex..., in: content)),
            match.range.location == 0,
            let bodyRange = Range(match.range(at: 1), in: content),
            let fullRange = Range(match.range, in: content)
        else {
            return ("", content)
        }
        let fmBody = String(content[bodyRange])
        var mdBody = String(content[fullRange.upperBound...])
        // TrimStart('\r', '\n').
        while let first = mdBody.first, first == "\r" || first == "\n" {
            mdBody.removeFirst()
        }
        return (fmBody, mdBody)
    }

    private static func extractField(_ fmBody: String, _ field: String) -> String? {
        if fmBody.isEmpty { return nil }
        let escaped = NSRegularExpression.escapedPattern(for: field)
        let pattern = "^\\s*\(escaped)\\s*:\\s*(.*)$"
        guard
            let regex = try? NSRegularExpression(pattern: pattern, options: [.anchorsMatchLines]),
            let match = regex.firstMatch(in: fmBody, range: NSRange(fmBody.startIndex..., in: fmBody)),
            let vRange = Range(match.range(at: 1), in: fmBody)
        else { return nil }
        var value = String(fmBody[vRange]).trimmingCharacters(in: .whitespaces)
        // Trim outer quotes.
        if value.count >= 2 {
            let f = value.first!, l = value.last!
            if (f == "\"" && l == "\"") || (f == "'" && l == "'") {
                value = String(value.dropFirst().dropLast())
            }
        }
        return value.isEmpty ? nil : value
    }

    private static func extractTags(_ fmBody: String) -> [String] {
        if fmBody.isEmpty { return [] }
        // Inline: tags: [a, b, c]
        if let regex = try? NSRegularExpression(pattern: "^\\s*tags\\s*:\\s*\\[([^\\]]*)\\]", options: [.anchorsMatchLines]),
           let match = regex.firstMatch(in: fmBody, range: NSRange(fmBody.startIndex..., in: fmBody)),
           let vRange = Range(match.range(at: 1), in: fmBody) {
            return String(fmBody[vRange])
                .split(separator: ",")
                .map { $0.trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "'\"")) }
                .filter { !$0.isEmpty }
        }
        // Block: tags:\n  - a\n  - b
        if let regex = try? NSRegularExpression(pattern: "^\\s*tags\\s*:\\s*\\r?\\n((?:\\s+-\\s+\\S+\\s*\\r?\\n?)+)", options: [.anchorsMatchLines]),
           let match = regex.firstMatch(in: fmBody, range: NSRange(fmBody.startIndex..., in: fmBody)),
           let vRange = Range(match.range(at: 1), in: fmBody) {
            return String(fmBody[vRange])
                .split(separator: "\n")
                .map {
                    var s = $0.trimmingCharacters(in: .whitespaces)
                    if s.hasPrefix("-") { s.removeFirst() }
                    return s.trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "'\""))
                }
                .filter { !$0.isEmpty }
        }
        return []
    }

    private static func extractFirstHeading(_ mdBody: String) -> String? {
        guard
            let regex = try? NSRegularExpression(pattern: "^#\\s+(.+)$", options: [.anchorsMatchLines]),
            let match = regex.firstMatch(in: mdBody, range: NSRange(mdBody.startIndex..., in: mdBody)),
            let vRange = Range(match.range(at: 1), in: mdBody)
        else { return nil }
        return String(mdBody[vRange]).trimmingCharacters(in: .whitespaces)
    }

    private static func truncate(_ s: String, _ max: Int) -> String {
        let flat = s.replacingOccurrences(of: "\r", with: " ")
            .replacingOccurrences(of: "\n", with: " ")
            .trimmingCharacters(in: .whitespaces)
        if flat.count <= max { return flat }
        let end = flat.index(flat.startIndex, offsetBy: max - 1)
        return String(flat[flat.startIndex..<end]) + "\u{2026}"
    }

    /// Letters/digits kept (lowercased); other runs become single dashes; a
    /// trailing dash is trimmed. (C# `Slugify`.)
    private static func slugify(_ name: String) -> String {
        var sb = ""
        var prevDash = false
        for ch in name {
            if ch.isLetter || ch.isNumber {
                sb.append(Character(ch.lowercased()))
                prevDash = false
            } else if !prevDash && !sb.isEmpty {
                sb.append("-")
                prevDash = true
            }
        }
        while sb.hasSuffix("-") { sb.removeLast() }
        return sb.isEmpty ? "unnamed" : sb
    }
}

// MARK: - SkillContextBuilder

/// Selects the most relevant skills for a user query and formats them as a
/// system-prompt context block. (C# `SkillContextBuilder`.)
public final class SkillContextBuilder: @unchecked Sendable {
    private let store: any ISkillStore
    private let maxSkills: Int

    /// - Parameters:
    ///   - store: source of available skills.
    ///   - maxSkills: max skills to include (>= 1; default 5).
    public init(store: any ISkillStore, maxSkills: Int = 5) {
        precondition(maxSkills >= 1, "maxSkills must be at least 1.")
        self.store = store
        self.maxSkills = maxSkills
    }

    /// Returns a formatted system-prompt block listing the most relevant skills
    /// for `userQuery`. Empty string when the store is empty or nothing matches
    /// (falls back to the full list, exactly as the C#). (C# `BuildContextAsync`.)
    public func buildContext(userQuery: String) async -> String {
        if userQuery.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return "" }

        let matches = await store.search(userQuery)
        let candidates: [SkillSummary]
        if !matches.isEmpty {
            candidates = Array(matches.prefix(maxSkills))
        } else {
            let all = await store.list()
            if all.isEmpty { return "" }
            candidates = Array(all.prefix(maxSkills))
        }

        var sb = "## Available Skills\n"
        for summary in candidates {
            guard let detail = await store.get(summary.id) else { continue }
            sb += "\n"
            sb += "**\(detail.id)** — \(detail.description)\n"
            if !detail.instructions.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                for line in detail.instructions.split(separator: "\n", omittingEmptySubsequences: false) {
                    sb += "  \(line)\n"
                }
            }
        }

        // TrimEnd.
        while let last = sb.last, last == "\n" || last == "\r" || last == " " || last == "\t" {
            sb.removeLast()
        }
        return sb
    }
}

// MARK: - FileSkillStore

/// `ISkillStore` backed by SKILL.md files in a directory. Each file uses YAML
/// front-matter for metadata and a Markdown body for the instructions. (C#
/// `FileSkillStore`.)
public final class FileSkillStore: ISkillStore, @unchecked Sendable {
    private let directoryPath: String

    /// Creates the store, creating `directoryPath` if absent.
    public init(directoryPath: String) {
        precondition(!directoryPath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "directoryPath required")
        self.directoryPath = directoryPath
        try? FileManager.default.createDirectory(atPath: directoryPath, withIntermediateDirectories: true)
    }

    public func list() async -> [SkillSummary] {
        var results: [SkillSummary] = []
        for file in skillFiles() {
            if let detail = readSkillFile(file) {
                results.append(Self.toSummary(detail))
            }
        }
        return results.sorted { $0.name.lowercased() < $1.name.lowercased() }
    }

    public func get(_ id: String) async -> SkillDetail? {
        precondition(!id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "id required")
        for file in skillFiles() {
            if let detail = readSkillFile(file), detail.id.caseInsensitiveCompare(id) == .orderedSame {
                return detail
            }
        }
        return nil
    }

    public func search(_ query: String) async -> [SkillSummary] {
        if query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return [] }
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        var results: [SkillSummary] = []
        for file in skillFiles() {
            if let detail = readSkillFile(file), Self.matchesQuery(detail, query: q) {
                results.append(Self.toSummary(detail))
            }
        }
        return results.sorted { $0.name.lowercased() < $1.name.lowercased() }
    }

    @discardableResult
    public func upsert(_ id: String?, draft: SkillDraft) async -> SkillDetail {
        let effectiveId: String
        if let id = id, !id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            effectiveId = id.trimmingCharacters(in: .whitespacesAndNewlines)
        } else {
            effectiveId = InMemorySkillStore.generateSlug(draft.name)
        }

        let filePath = (directoryPath as NSString).appendingPathComponent("\(effectiveId).md")
        let tags = draft.tags.isEmpty ? "[]" : "[\(draft.tags.joined(separator: ", "))]"

        var content = ""
        content += "---\n"
        content += "id: \(effectiveId)\n"
        content += "name: \(draft.name)\n"
        content += "description: \(draft.description)\n"
        content += "tags: \(tags)\n"
        content += "---\n"
        content += "\n"
        content += draft.instructions

        try? content.write(toFile: filePath, atomically: true, encoding: .utf8)

        return SkillDetail(
            id: effectiveId,
            name: draft.name,
            description: draft.description,
            instructions: draft.instructions,
            tags: draft.tags,
            source: .file,
            lastModified: Date())
    }

    public func delete(_ id: String) async {
        precondition(!id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "id required")
        let filePath = (directoryPath as NSString).appendingPathComponent("\(id).md")
        if FileManager.default.fileExists(atPath: filePath) {
            try? FileManager.default.removeItem(atPath: filePath)
        }
    }

    // MARK: parsing

    private func skillFiles() -> [String] {
        let fm = FileManager.default
        guard let entries = try? fm.contentsOfDirectory(atPath: directoryPath) else { return [] }
        return entries
            .filter { $0.hasSuffix(".md") }
            .map { (directoryPath as NSString).appendingPathComponent($0) }
    }

    private func readSkillFile(_ filePath: String) -> SkillDetail? {
        guard let content = try? String(contentsOfFile: filePath, encoding: .utf8) else { return nil }
        let fileNameNoExt = ((filePath as NSString).lastPathComponent as NSString).deletingPathExtension
        return Self.parseSkillFile(content: content, fileNameWithoutExt: fileNameNoExt, filePath: filePath)
    }

    /// Parse a SKILL.md into a `SkillDetail`. Front-matter is the block between
    /// the first two `---` lines. (C# `ParseSkillFile`.)
    public static func parseSkillFile(content: String, fileNameWithoutExt: String, filePath: String) -> SkillDetail? {
        if content.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return nil }

        let lines = content.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
        guard lines.count >= 2, lines[0].trimmingCharacters(in: .whitespaces) == "---" else { return nil }

        var frontMatterEnd = -1
        for i in 1..<lines.count where lines[i].trimmingCharacters(in: .whitespaces) == "---" {
            frontMatterEnd = i
            break
        }
        guard frontMatterEnd >= 0 else { return nil }

        // key: value pairs (case-insensitive keys).
        var meta: [String: String] = [:]
        for i in 1..<frontMatterEnd {
            let line = lines[i]
            guard let colon = line.firstIndex(of: ":") else { continue }
            let key = String(line[line.startIndex..<colon]).trimmingCharacters(in: .whitespaces).lowercased()
            let value = String(line[line.index(after: colon)...]).trimmingCharacters(in: .whitespaces)
            meta[key] = value
        }

        let id = (meta["id"].flatMap { $0.isEmpty ? nil : $0 }) ?? fileNameWithoutExt
        let name = meta["name"] ?? id
        let description = meta["description"] ?? ""
        let tags = parseTagsList(meta["tags"] ?? "")

        let instructionsLines = lines[(frontMatterEnd + 1)...]
        let instructions = instructionsLines.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)

        // Last-write mtime, or now.
        var lastModified = Date()
        if let attrs = try? FileManager.default.attributesOfItem(atPath: filePath),
           let mdate = attrs[.modificationDate] as? Date {
            lastModified = mdate
        }

        return SkillDetail(
            id: id, name: name, description: description, instructions: instructions,
            tags: tags, source: .file, lastModified: lastModified)
    }

    /// Parse a YAML inline list `[a, b, c]` or a bare scalar. (C# `ParseTagsList`.)
    private static func parseTagsList(_ raw0: String) -> [String] {
        var raw = raw0.trimmingCharacters(in: .whitespaces)
        if raw.isEmpty { return [] }
        if raw.hasPrefix("[") && raw.hasSuffix("]") {
            raw = String(raw.dropFirst().dropLast())
        }
        return raw.split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
    }

    private static func toSummary(_ d: SkillDetail) -> SkillSummary {
        SkillSummary(id: d.id, name: d.name, description: d.description, tags: d.tags, source: d.source)
    }

    private static func matchesQuery(_ s: SkillDetail, query: String) -> Bool {
        s.name.range(of: query, options: .caseInsensitive) != nil
            || s.description.range(of: query, options: .caseInsensitive) != nil
            || s.tags.contains { $0.range(of: query, options: .caseInsensitive) != nil }
    }
}
