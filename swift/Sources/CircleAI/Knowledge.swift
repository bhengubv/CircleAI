// Knowledge.swift
//
// Port of src/CircleAI.Knowledge/ (markdown-on-disk knowledge notes):
//   • YamlFrontmatter.cs           — flat YAML frontmatter parser/writer
//                                     (reproduced char-for-char, incl. the
//                                     encode/decode escape set and the strict
//                                     rejects for nesting / lists / flow-style)
//   • KnowledgeNote.cs             — KnowledgeNote record + ToFileText / ParseFile
//   • IKnowledgeStore.cs           — the store contract
//   • FileSystemKnowledgeStore.cs  — one .md file per note, atomic write-then-rename,
//                                     per-id lock, tag/all streaming
//
// Porting notes:
//   • `record` → `struct: Sendable`. `Guid` → `UUID`. `DateTimeOffset` → `Date`.
//   • `Guid.ToString("N")` → 32 hex lowercase; `"D")` → dashed lowercase.
//   • `DateTimeOffset.ToString("O")` (round-trip) → an ISO-8601 formatter with
//     fractional seconds; parsing accepts both fractional and non-fractional.
//   • `IAsyncEnumerable<KnowledgeNote>` → `AsyncThrowingStream`.
//   • `FormatException` → `KnowledgeFormatError`; the C# `CircleAIComponentBase`
//     telemetry wrapper has no Swift equivalent, so ops run directly (behaviour
//     identical — only the telemetry shim is dropped, matching Federation.swift).

import Foundation

// MARK: - Errors

public enum KnowledgeFormatError: Error, Equatable, CustomStringConvertible {
    case message(String)
    public var description: String {
        switch self { case .message(let m): return m }
    }
}

public enum KnowledgeError: Error, Equatable, CustomStringConvertible {
    case rootDirectoryRequired
    case tagRequired
    public var description: String {
        switch self {
        case .rootDirectoryRequired: return "rootDirectory required"
        case .tagRequired: return "tag required"
        }
    }
}

// MARK: - GUID / timestamp helpers

enum KnowledgeFormat {
    /// `Guid.ToString("N")` — 32 lowercase hex digits, no dashes.
    static func guidN(_ id: UUID) -> String {
        id.uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    }

    /// `Guid.ToString("D")` — dashed lowercase.
    static func guidD(_ id: UUID) -> String {
        id.uuidString.lowercased()
    }

    /// Parses a "N" (no-dash) or "D" (dashed) GUID string into a UUID.
    static func parseGuid(_ s: String) -> UUID? {
        if let u = UUID(uuidString: s) { return u }
        // Accept the dash-less "N" form by re-inserting dashes.
        let hex = s.replacingOccurrences(of: "-", with: "")
        guard hex.count == 32, hex.allSatisfy({ $0.isHexDigit }) else { return nil }
        let a = hex.prefix(8)
        let b = hex.dropFirst(8).prefix(4)
        let c = hex.dropFirst(12).prefix(4)
        let d = hex.dropFirst(16).prefix(4)
        let e = hex.dropFirst(20)
        return UUID(uuidString: "\(a)-\(b)-\(c)-\(d)-\(e)")
    }

    private static let iso8601: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
    private static let iso8601NoFraction: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    /// `DateTimeOffset.ToString("O")` — round-trip ISO-8601 with fractional seconds.
    static func roundtrip(_ date: Date) -> String {
        iso8601.string(from: date)
    }

    /// Parses a round-trip timestamp; falls back to `Date()` when unparseable
    /// (mirrors the C# `TryParse ? dto : DateTimeOffset.UtcNow`).
    static func parseTimestamp(_ raw: String?) -> Date {
        guard let raw, !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return Date() }
        if let d = iso8601.date(from: raw) { return d }
        if let d = iso8601NoFraction.date(from: raw) { return d }
        return Date()
    }
}

// MARK: - YamlFrontmatter

/// Parses and writes minimal flat YAML frontmatter blocks. Nested keys, lists,
/// flow-style structures, anchors, and single-quoted scalars are rejected.
enum YamlFrontmatter {
    private static let delimiter = "---"

    /// Renders `frontmatter` into a YAML block followed by `body`.
    static func write(_ frontmatter: [String: String], _ body: String) throws -> String {
        var sb = ""
        sb += delimiter + "\n"
        // C# enumerates the dictionary in its internal order; to keep output
        // deterministic across platforms we emit keys sorted ascending. Values
        // round-trip identically; ordering within the block is the only freedom.
        for key in frontmatter.keys.sorted() {
            try validateKey(key)
            sb += key
            sb += ": "
            sb += encodeValue(frontmatter[key]!)
            sb += "\n"
        }
        sb += delimiter + "\n"
        sb += body
        return sb
    }

    /// Parses `text` into a frontmatter dictionary + body string.
    static func read(_ text: String) throws -> (frontmatter: [String: String], body: String) {
        // Normalise line endings.
        var normal = text.replacingOccurrences(of: "\r\n", with: "\n")
        normal = normal.replacingOccurrences(of: "\r", with: "\n")

        guard normal.hasPrefix(delimiter + "\n") else {
            throw KnowledgeFormatError.message("Frontmatter must start with '---' on its own line.")
        }

        let searchStart = delimiter.count + 1
        let closingMarker = "\n" + delimiter + "\n"
        let chars = Array(normal)

        // Find closing "\n---\n" at or after searchStart.
        guard let closingIdx = indexOf(chars, closingMarker, from: searchStart) else {
            throw KnowledgeFormatError.message("Missing closing '---' line for frontmatter block.")
        }

        let yaml = String(chars[searchStart..<closingIdx])
        let bodyStart = closingIdx + Array(closingMarker).count
        let body = String(chars[bodyStart...])

        var dict: [String: String] = [:]
        for rawLine in yaml.components(separatedBy: "\n") {
            if rawLine.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { continue }
            let firstChar = rawLine.first!
            if firstChar == " " || firstChar == "\t" {
                throw KnowledgeFormatError.message("Nested YAML is not supported.")
            }
            if rawLine.hasPrefix("- ") {
                throw KnowledgeFormatError.message("YAML lists are not supported.")
            }
            let lineChars = Array(rawLine)
            guard let colon = lineChars.firstIndex(of: ":"), colon > 0 else {
                throw KnowledgeFormatError.message("Malformed YAML line: '\(rawLine)'.")
            }
            let key = String(lineChars[0..<colon]).trimmingCharacters(in: .whitespaces)
            var rest = ""
            if colon + 1 < lineChars.count {
                rest = String(lineChars[(colon + 1)...])
                // TrimStart only.
                while let f = rest.first, f == " " || f == "\t" { rest.removeFirst() }
            }
            try validateKey(key)
            if rest.hasPrefix("{") || rest.hasPrefix("[") {
                throw KnowledgeFormatError.message("Flow-style YAML structures are not supported.")
            }
            dict[key] = try decodeValue(rest)
        }
        return (dict, body)
    }

    // MARK: Helpers

    private static func indexOf(_ chars: [Character], _ needle: String, from: Int) -> Int? {
        let nchars = Array(needle)
        if nchars.isEmpty { return from }
        var i = from
        while i + nchars.count <= chars.count {
            var match = true
            for j in 0..<nchars.count where chars[i + j] != nchars[j] { match = false; break }
            if match { return i }
            i += 1
        }
        return nil
    }

    private static func validateKey(_ key: String) throws {
        if key.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw KnowledgeFormatError.message("YAML key cannot be empty.")
        }
        for ch in key {
            if !(ch.isLetter || ch.isNumber || ch == "_" || ch == "-" || ch == ".") {
                throw KnowledgeFormatError.message("Invalid character '\(ch)' in YAML key '\(key)'.")
            }
        }
    }

    private static func encodeValue(_ value: String) -> String {
        if value.isEmpty { return "\"\"" }

        var needsQuoting = false
        for ch in value {
            if ch == ":" || ch == "#" || ch == "\n" || ch == "\r" || ch == "\t" || ch == "\"" || ch == "\\" || ch == "'" || ch == "{" || ch == "[" {
                needsQuoting = true
                break
            }
        }
        if !needsQuoting, let first = value.first, let last = value.last, first == " " || last == " " {
            needsQuoting = true
        }
        if !needsQuoting { return value }

        var sb = "\""
        for ch in value {
            switch ch {
            case "\\": sb += "\\\\"
            case "\"": sb += "\\\""
            case "\n": sb += "\\n"
            case "\r": sb += "\\r"
            case "\t": sb += "\\t"
            default: sb.append(ch)
            }
        }
        sb += "\""
        return sb
    }

    private static func decodeValue(_ raw: String) throws -> String {
        if raw.isEmpty { return "" }
        let chars = Array(raw)

        if chars[0] != "\"" && chars[0] != "'" {
            // Strip a single trailing inline comment ('  # comment') on bare values.
            if let hashRange = raw.range(of: " #") {
                return String(raw[raw.startIndex..<hashRange.lowerBound]).replacingOccurrences(of: "\\s+$", with: "", options: .regularExpression)
            }
            return raw
        }
        if chars[0] == "'" {
            throw KnowledgeFormatError.message("Single-quoted YAML scalars are not supported.")
        }
        if chars.count < 2 || chars[chars.count - 1] != "\"" {
            throw KnowledgeFormatError.message("Unterminated double-quoted YAML scalar.")
        }

        let inner = Array(chars[1..<(chars.count - 1)])
        var sb = ""
        var i = 0
        while i < inner.count {
            let ch = inner[i]
            if ch != "\\" { sb.append(ch); i += 1; continue }
            if i + 1 >= inner.count {
                throw KnowledgeFormatError.message("Trailing backslash in YAML scalar.")
            }
            i += 1
            let next = inner[i]
            switch next {
            case "\\": sb.append("\\")
            case "\"": sb.append("\"")
            case "n": sb.append("\n")
            case "r": sb.append("\r")
            case "t": sb.append("\t")
            default:
                throw KnowledgeFormatError.message("Unsupported YAML escape '\\\(next)'.")
            }
            i += 1
        }
        return sb
    }
}

// MARK: - KnowledgeNote

/// A markdown knowledge note: YAML frontmatter + markdown body.
public struct KnowledgeNote: Sendable, Equatable {
    public let id: UUID
    public let title: String
    public let bodyMarkdown: String
    public let frontmatter: [String: String]
    public let tags: [String]
    public let createdAt: Date
    public let updatedAt: Date

    public init(id: UUID, title: String, bodyMarkdown: String, frontmatter: [String: String], tags: [String], createdAt: Date, updatedAt: Date) {
        self.id = id
        self.title = title
        self.bodyMarkdown = bodyMarkdown
        self.frontmatter = frontmatter
        self.tags = tags
        self.createdAt = createdAt
        self.updatedAt = updatedAt
    }

    private static let titleKey = "title"
    private static let createdKey = "created_at"
    private static let updatedKey = "updated_at"
    private static let idKey = "id"
    private static let tagsKey = "tags"

    /// Serialises this note to its on-disk text form.
    public func toFileText() -> String {
        var merged = frontmatter
        merged[KnowledgeNote.idKey] = KnowledgeFormat.guidD(id)
        merged[KnowledgeNote.titleKey] = title
        merged[KnowledgeNote.createdKey] = KnowledgeFormat.roundtrip(createdAt)
        merged[KnowledgeNote.updatedKey] = KnowledgeFormat.roundtrip(updatedAt)
        merged[KnowledgeNote.tagsKey] = tags.joined(separator: ",")
        // write() cannot realistically throw for these validated keys.
        return (try? YamlFrontmatter.write(merged, bodyMarkdown)) ?? ""
    }

    /// Parses the on-disk text form back into a `KnowledgeNote`.
    public static func parseFile(_ text: String) throws -> KnowledgeNote {
        let (frontmatter, body) = try YamlFrontmatter.read(text)

        guard let idRaw = frontmatter[idKey], let id = KnowledgeFormat.parseGuid(idRaw) else {
            throw KnowledgeFormatError.message("Knowledge note frontmatter missing or invalid 'id'.")
        }

        let title = frontmatter[titleKey] ?? ""
        let created = KnowledgeFormat.parseTimestamp(frontmatter[createdKey])
        let updated = KnowledgeFormat.parseTimestamp(frontmatter[updatedKey])

        let tags: [String]
        if let rawTags = frontmatter[tagsKey], !rawTags.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            tags = rawTags.components(separatedBy: ",")
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .filter { !$0.isEmpty }
        } else {
            tags = []
        }

        var userFrontmatter: [String: String] = [:]
        for (k, v) in frontmatter {
            if k == idKey || k == titleKey || k == createdKey || k == updatedKey || k == tagsKey { continue }
            userFrontmatter[k] = v
        }

        return KnowledgeNote(id: id, title: title, bodyMarkdown: body, frontmatter: userFrontmatter, tags: tags, createdAt: created, updatedAt: updated)
    }
}

// MARK: - IKnowledgeStore

/// Persistent store for `KnowledgeNote` documents.
public protocol IKnowledgeStore: Sendable {
    /// Loads the note with `id`, or nil when none exists.
    func get(id: UUID) async throws -> KnowledgeNote?

    /// Persists `note` (the returned record may differ, e.g. refreshed `updatedAt`).
    func save(_ note: KnowledgeNote) async throws -> KnowledgeNote

    /// Deletes the note with `id`. No-op if it does not exist.
    func delete(id: UUID) async throws

    /// Streams notes carrying `tag`.
    func searchByTag(_ tag: String) -> AsyncThrowingStream<KnowledgeNote, Error>

    /// Streams every note currently stored.
    func enumerateAll() -> AsyncThrowingStream<KnowledgeNote, Error>
}

// MARK: - FileSystemKnowledgeStore

/// File-system `IKnowledgeStore`. Each note is `{rootDirectory}/{id-no-dashes}.md`.
/// Atomic write-then-rename, per-id lock for read/write correctness.
public final class FileSystemKnowledgeStore: IKnowledgeStore, @unchecked Sendable {
    private let rootDirectory: String
    private let lock = NSLock()
    private var gates: [UUID: NSLock] = [:]

    public init(rootDirectory: String) {
        precondition(!rootDirectory.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        self.rootDirectory = rootDirectory
        try? FileManager.default.createDirectory(atPath: rootDirectory, withIntermediateDirectories: true)
    }

    public func get(id: UUID) async throws -> KnowledgeNote? {
        let path = notePath(id)
        guard FileManager.default.fileExists(atPath: path) else { return nil }
        let gate = gateFor(id)
        gate.lock(); defer { gate.unlock() }
        let text = try String(contentsOfFile: path, encoding: .utf8)
        return try KnowledgeNote.parseFile(text)
    }

    public func save(_ note: KnowledgeNote) async throws -> KnowledgeNote {
        let refreshed = KnowledgeNote(
            id: note.id, title: note.title, bodyMarkdown: note.bodyMarkdown,
            frontmatter: note.frontmatter, tags: note.tags,
            createdAt: note.createdAt, updatedAt: Date())
        let target = notePath(refreshed.id)
        let tmp = target + "." + KnowledgeFormat.guidN(UUID()) + ".tmp"

        let gate = gateFor(refreshed.id)
        gate.lock(); defer { gate.unlock() }
        do {
            try refreshed.toFileText().write(toFile: tmp, atomically: false, encoding: .utf8)
            if FileManager.default.fileExists(atPath: target) {
                try FileManager.default.removeItem(atPath: target)
            }
            try FileManager.default.moveItem(atPath: tmp, toPath: target)
            return refreshed
        } catch {
            if FileManager.default.fileExists(atPath: tmp) {
                try? FileManager.default.removeItem(atPath: tmp)
            }
            throw error
        }
    }

    public func delete(id: UUID) async throws {
        let path = notePath(id)
        let gate = gateFor(id)
        gate.lock(); defer { gate.unlock() }
        if FileManager.default.fileExists(atPath: path) {
            try FileManager.default.removeItem(atPath: path)
        }
    }

    public func searchByTag(_ tag: String) -> AsyncThrowingStream<KnowledgeNote, Error> {
        let root = rootDirectory
        return AsyncThrowingStream { continuation in
            if tag.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                continuation.finish(throwing: KnowledgeError.tagRequired)
                return
            }
            for note in FileSystemKnowledgeStore.enumerateNotes(root: root) {
                if note.tags.contains(where: { $0.caseInsensitiveCompare(tag) == .orderedSame }) {
                    continuation.yield(note)
                }
            }
            continuation.finish()
        }
    }

    public func enumerateAll() -> AsyncThrowingStream<KnowledgeNote, Error> {
        let root = rootDirectory
        return AsyncThrowingStream { continuation in
            for note in FileSystemKnowledgeStore.enumerateNotes(root: root) {
                continuation.yield(note)
            }
            continuation.finish()
        }
    }

    // MARK: Helpers

    /// Reads + parses every top-level `.md` file, skipping ones not in our format.
    private static func enumerateNotes(root: String) -> [KnowledgeNote] {
        guard FileManager.default.fileExists(atPath: root),
              let entries = try? FileManager.default.contentsOfDirectory(atPath: root) else { return [] }
        var out: [KnowledgeNote] = []
        for entry in entries where (entry as NSString).pathExtension.lowercased() == "md" {
            let full = (root as NSString).appendingPathComponent(entry)
            guard let text = try? String(contentsOfFile: full, encoding: .utf8),
                  let note = try? KnowledgeNote.parseFile(text) else { continue }
            out.append(note)
        }
        return out
    }

    private func gateFor(_ id: UUID) -> NSLock {
        lock.lock(); defer { lock.unlock() }
        if let g = gates[id] { return g }
        let g = NSLock()
        gates[id] = g
        return g
    }

    private func notePath(_ id: UUID) -> String {
        (rootDirectory as NSString).appendingPathComponent(KnowledgeFormat.guidN(id) + ".md")
    }
}
