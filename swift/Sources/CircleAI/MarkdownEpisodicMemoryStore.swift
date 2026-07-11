// MarkdownEpisodicMemoryStore.swift
//
// Port of src/CircleAI.Knowledge/MarkdownEpisodicMemoryStore.cs — an
// IEpisodicMemoryStore (from CircleAI.Memory) backed by an IKnowledgeStore.
// Each EpisodicMemoryEntry round-trips to one KnowledgeNote with structured
// frontmatter and a "## User\n\n... ## Assistant\n\n..." body.
//
// Porting notes:
//   • Conforms to the existing Swift `IEpisodicMemoryStore` (Memory.swift):
//     add / search(queryEmbedding:topK:) / getRecent(count:) / count() /
//     pruneOlderThan(cutoff:).
//   • The embedding is encoded as base64 of the raw little-endian float bytes
//     (C# `Buffer.BlockCopy` + `Convert.ToBase64String`). The Swift port writes
//     each float as 4 little-endian bytes explicitly so the encoding is
//     deterministic across platforms and matches the C# byte layout.
//   • `CosineSimilarity` here is a plain dot product (embeddings are assumed
//     pre-normalised), matching the C# helper.
//   • `Guid.Empty` new-id substitution → the all-zeros UUID check.
//   • `DateTimeOffset.ToString("O")` → the shared `KnowledgeFormat.roundtrip`.

import Foundation

/// Markdown-on-disk `IEpisodicMemoryStore`, backed by an `IKnowledgeStore`.
public final class MarkdownEpisodicMemoryStore: IEpisodicMemoryStore, @unchecked Sendable {
    // Frontmatter keys used to round-trip an EpisodicMemoryEntry.
    private static let episodeIdKey = "episode_id"
    private static let recordedAtKey = "recorded_at"
    private static let appContextKey = "app_context"
    private static let embeddingKey = "embedding"
    private static let embeddingDimsKey = "embedding_dims"
    private static let tagPrefix = "tag_"

    private static let emptyUUID = UUID(uuidString: "00000000-0000-0000-0000-000000000000")!

    private let store: any IKnowledgeStore

    public init(store: any IKnowledgeStore) {
        self.store = store
    }

    // MARK: - IEpisodicMemoryStore

    public func add(_ entry: EpisodicMemoryEntry) async throws {
        let note = MarkdownEpisodicMemoryStore.toNote(entry)
        _ = try await store.save(note)
    }

    public func search(queryEmbedding: [Float]?, topK: Int = 5) async throws -> [EpisodicMemoryEntry] {
        var snapshot: [EpisodicMemoryEntry] = []
        for try await note in store.enumerateAll() {
            snapshot.append(MarkdownEpisodicMemoryStore.fromNote(note))
        }

        guard let queryEmbedding, !queryEmbedding.isEmpty else {
            return Array(snapshot.sorted { $0.recordedAt > $1.recordedAt }.prefix(topK))
        }

        let scored = snapshot
            .filter { $0.embedding != nil && $0.embedding!.count == queryEmbedding.count }
            .map { (entry: $0, score: MarkdownEpisodicMemoryStore.cosineSimilarity(queryEmbedding, $0.embedding!)) }
            .sorted { $0.score > $1.score }
            .prefix(topK)
            .map { $0.entry }
        return Array(scored)
    }

    public func getRecent(count: Int = 10) async throws -> [EpisodicMemoryEntry] {
        var snapshot: [EpisodicMemoryEntry] = []
        for try await note in store.enumerateAll() {
            snapshot.append(MarkdownEpisodicMemoryStore.fromNote(note))
        }
        return Array(snapshot.sorted { $0.recordedAt > $1.recordedAt }.prefix(count))
    }

    public func count() async throws -> Int {
        var n = 0
        for try await _ in store.enumerateAll() { n += 1 }
        return n
    }

    public func pruneOlderThan(cutoff: Date) async throws -> Int {
        var doomed: [UUID] = []
        for try await note in store.enumerateAll() {
            let entry = MarkdownEpisodicMemoryStore.fromNote(note)
            if entry.recordedAt < cutoff { doomed.append(note.id) }
        }
        for id in doomed {
            try await store.delete(id: id)
        }
        return doomed.count
    }

    // MARK: - EpisodicMemoryEntry <-> KnowledgeNote

    static func toNote(_ entry: EpisodicMemoryEntry) -> KnowledgeNote {
        var frontmatter: [String: String] = [
            episodeIdKey: KnowledgeFormat.guidD(entry.id),
            recordedAtKey: KnowledgeFormat.roundtrip(entry.recordedAt),
        ]
        if let ctx = entry.appContext, !ctx.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            frontmatter[appContextKey] = ctx
        }

        if let emb = entry.embedding, !emb.isEmpty {
            frontmatter[embeddingKey] = encodeEmbedding(emb)
            frontmatter[embeddingDimsKey] = String(emb.count)
        }

        var tags: [String] = []
        if let entryTags = entry.tags {
            // Sort keys for deterministic frontmatter/tag ordering.
            for key in entryTags.keys.sorted() {
                frontmatter[tagPrefix + key] = entryTags[key]!
                tags.append(key)
            }
        }

        let body = "## User\n\n" + entry.userText + "\n\n" + "## Assistant\n\n" + entry.assistantText

        let id = entry.id == emptyUUID ? UUID() : entry.id
        return KnowledgeNote(
            id: id,
            title: truncateForTitle(entry.userText),
            bodyMarkdown: body,
            frontmatter: frontmatter,
            tags: tags,
            createdAt: entry.recordedAt,
            updatedAt: entry.recordedAt)
    }

    static func fromNote(_ note: KnowledgeNote) -> EpisodicMemoryEntry {
        var episodeId = note.id
        if let raw = note.frontmatter[episodeIdKey], let parsed = KnowledgeFormat.parseGuid(raw) {
            episodeId = parsed
        }

        var recordedAt = note.createdAt
        if let rawWhen = note.frontmatter[recordedAtKey] {
            let parsed = KnowledgeFormat.parseTimestamp(rawWhen)
            // parseTimestamp falls back to now on failure; only override when the
            // raw value was present and non-empty (matches the C# TryParse guard).
            if !rawWhen.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                recordedAt = parsed
            }
        }

        let appContext = note.frontmatter[appContextKey]

        var embedding: [Float]? = nil
        if let b64 = note.frontmatter[embeddingKey], !b64.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            embedding = decodeEmbedding(b64)
        }

        let (userText, assistantText) = splitBody(note.bodyMarkdown)

        var tagsOut: [String: String]? = nil
        for (k, v) in note.frontmatter where k.hasPrefix(tagPrefix) {
            if tagsOut == nil { tagsOut = [:] }
            tagsOut![String(k.dropFirst(tagPrefix.count))] = v
        }

        return EpisodicMemoryEntry(
            id: episodeId,
            recordedAt: recordedAt,
            userText: userText,
            assistantText: assistantText,
            appContext: appContext,
            embedding: embedding,
            tags: tagsOut)
    }

    // MARK: - Helpers

    private static func splitBody(_ body: String) -> (user: String, assistant: String) {
        if body.isEmpty { return ("", "") }
        let normal = body.replacingOccurrences(of: "\r\n", with: "\n")
        let userMarker = "## User\n\n"
        let assistantMarker = "\n\n## Assistant\n\n"

        let chars = Array(normal)
        guard let userIdx = range(chars, userMarker, from: 0) else { return (normal, "") }
        guard let assistantIdx = range(chars, assistantMarker, from: 0), assistantIdx > userIdx else {
            return (normal, "")
        }

        let userStart = userIdx + Array(userMarker).count
        let userText = String(chars[userStart..<assistantIdx])
        let assistantStart = assistantIdx + Array(assistantMarker).count
        let assistantText = String(chars[assistantStart...])
        return (userText, assistantText)
    }

    private static func range(_ chars: [Character], _ needle: String, from: Int) -> Int? {
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

    private static func truncateForTitle(_ source: String) -> String {
        if source.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return "(untitled)" }
        let single = source
            .replacingOccurrences(of: "\n", with: " ")
            .replacingOccurrences(of: "\r", with: " ")
            .trimmingCharacters(in: .whitespaces)
        if single.count <= 64 { return single }
        return String(single.prefix(64))
    }

    private static func cosineSimilarity(_ a: [Float], _ b: [Float]) -> Float {
        var dot: Float = 0
        for i in 0..<a.count { dot += a[i] * b[i] }
        return dot
    }

    /// Encodes floats as base64 of their little-endian 4-byte representations
    /// (byte-identical to the C# `Buffer.BlockCopy` layout on little-endian hosts).
    private static func encodeEmbedding(_ v: [Float]) -> String {
        var bytes = [UInt8]()
        bytes.reserveCapacity(v.count * 4)
        for f in v {
            let bits = f.bitPattern
            bytes.append(UInt8(bits & 0xFF))
            bytes.append(UInt8((bits >> 8) & 0xFF))
            bytes.append(UInt8((bits >> 16) & 0xFF))
            bytes.append(UInt8((bits >> 24) & 0xFF))
        }
        return Data(bytes).base64EncodedString()
    }

    /// Inverse of `encodeEmbedding`. Returns nil on malformed base64 (mirrors the
    /// C# `catch { embedding = null; }`).
    private static func decodeEmbedding(_ b64: String) -> [Float]? {
        guard let data = Data(base64Encoded: b64) else { return nil }
        let bytes = [UInt8](data)
        let n = bytes.count / 4
        var out = [Float](repeating: 0, count: n)
        for i in 0..<n {
            let base = i * 4
            let bits = UInt32(bytes[base])
                | (UInt32(bytes[base + 1]) << 8)
                | (UInt32(bytes[base + 2]) << 16)
                | (UInt32(bytes[base + 3]) << 24)
            out[i] = Float(bitPattern: bits)
        }
        return out
    }
}
