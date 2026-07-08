// Multimodal.swift
// Compressed semantic memory for media artefacts (image / audio / video /
// document). Ported from CircleAI.Memory.Multimodal (C#), mirroring the verified
// TypeScript port (memory/multimodal.ts):
//   • MediaModality, MultimodalMemoryEntry (+ makeMultimodalMemoryEntry)
//   • IMultimodalCaptioner + CaptionResult + HeuristicMultimodalCaptioner
//   • IMultimodalMemoryStore + InMemoryMultimodalMemoryStore
//   • MultimodalMemoryIngester (+ IngestionResult)
//
// The whole point: we DO NOT store the pixels / audio samples / video frames —
// we store the caption, the embedding, and a SHA-256 of the original so the host
// can reference it back if it kept the file elsewhere. Raw bytes never leave the
// captioner; the store only ever holds the semantic record.

import Foundation
import CryptoKit

// MARK: - MediaModality

/// Modality of a multimodal memory entry. Drives how the ingester routes the raw
/// bytes to the captioner and which side-channel metadata is captured.
public enum MediaModality: String, Sendable, Equatable {
    /// Still image — JPEG, PNG, HEIC, WebP, AVIF.
    case image = "Image"
    /// Audio clip — Opus, WAV, MP3, M4A.
    case audio = "Audio"
    /// Video — MP4, MOV, WebM. Captioned via key-frame extraction by the host.
    case video = "Video"
    /// Text document — PDF, DOCX, plain text snippet larger than a single message.
    case textDocument = "TextDocument"
}

// MARK: - MultimodalMemoryEntry

/// One semantically-compressed media memory. The caption + embedding capture the
/// meaning; raw bytes are never retained by the memory layer.
///
/// `referenceCount` is mutable (incremented on dedup hits); everything else is
/// effectively write-once, matching the C# `init`/`set` split.
public struct MultimodalMemoryEntry: Sendable {
    /// Stable identifier.
    public var id: UUID
    /// UTC timestamp the memory was recorded.
    public var recordedAt: Date
    /// Which kind of media this came from.
    public var modality: MediaModality
    /// Caption — the semantic content.
    public var caption: String
    /// Embedding of the caption (and, for richer captioners, the joint embedding).
    public var embedding: [Float]?
    /// SHA-256 of the original bytes, hex-lower. Lets the host dedupe, reference a
    /// kept file, and verify a re-uploaded file matches what was remembered.
    public var sourceSha256: String
    /// Original MIME type (e.g. image/jpeg). Captured for diagnostics.
    public var sourceMimeType: String?
    /// Size in bytes of the original artefact.
    public var sourceByteCount: Int
    /// Optional URI of the original artefact if the host retained it elsewhere.
    public var sourceUri: String?
    /// Image / video width in pixels, when applicable.
    public var widthPx: Int?
    /// Image / video height in pixels, when applicable.
    public var heightPx: Int?
    /// Audio / video duration in milliseconds, when applicable.
    public var durationMs: Int?
    /// How many times this artefact has been re-presented to the ingester.
    /// Incremented on every dedup hit instead of creating a new entry.
    public var referenceCount: Int
    /// Optional tags (e.g. location, person, topic).
    public var tags: [String: String]?

    public init(
        id: UUID = UUID(),
        recordedAt: Date = Date(),
        modality: MediaModality = .image,
        caption: String = "",
        embedding: [Float]? = nil,
        sourceSha256: String = "",
        sourceMimeType: String? = nil,
        sourceByteCount: Int = 0,
        sourceUri: String? = nil,
        widthPx: Int? = nil,
        heightPx: Int? = nil,
        durationMs: Int? = nil,
        referenceCount: Int = 1,
        tags: [String: String]? = nil
    ) {
        self.id = id
        self.recordedAt = recordedAt
        self.modality = modality
        self.caption = caption
        self.embedding = embedding
        self.sourceSha256 = sourceSha256
        self.sourceMimeType = sourceMimeType
        self.sourceByteCount = sourceByteCount
        self.sourceUri = sourceUri
        self.widthPx = widthPx
        self.heightPx = heightPx
        self.durationMs = durationMs
        self.referenceCount = referenceCount
        self.tags = tags
    }
}

/// Builds a `MultimodalMemoryEntry` filling the same defaults the C# record's
/// initialisers do: fresh UUID id, `recordedAt = now`, `referenceCount = 1`.
/// A convenience mirror of the TS `makeMultimodalMemoryEntry` factory.
public func makeMultimodalMemoryEntry(
    id: UUID = UUID(),
    recordedAt: Date = Date(),
    modality: MediaModality = .image,
    caption: String = "",
    embedding: [Float]? = nil,
    sourceSha256: String = "",
    sourceMimeType: String? = nil,
    sourceByteCount: Int = 0,
    sourceUri: String? = nil,
    widthPx: Int? = nil,
    heightPx: Int? = nil,
    durationMs: Int? = nil,
    referenceCount: Int = 1,
    tags: [String: String]? = nil
) -> MultimodalMemoryEntry {
    MultimodalMemoryEntry(
        id: id, recordedAt: recordedAt, modality: modality, caption: caption,
        embedding: embedding, sourceSha256: sourceSha256, sourceMimeType: sourceMimeType,
        sourceByteCount: sourceByteCount, sourceUri: sourceUri, widthPx: widthPx,
        heightPx: heightPx, durationMs: durationMs, referenceCount: referenceCount, tags: tags)
}

// MARK: - CaptionResult + IMultimodalCaptioner

/// Output of a single captioning call.
public struct CaptionResult: Sendable {
    /// Human-readable semantic description of the artefact. Must not be empty.
    public let caption: String
    /// Embedding of the artefact. nil when the captioner has no embedding backend.
    public let embedding: [Float]?
    /// Image / video width when known.
    public let widthPx: Int?
    /// Image / video height when known.
    public let heightPx: Int?
    /// Audio / video duration when known.
    public let durationMs: Int?

    public init(
        caption: String,
        embedding: [Float]? = nil,
        widthPx: Int? = nil,
        heightPx: Int? = nil,
        durationMs: Int? = nil
    ) {
        self.caption = caption
        self.embedding = embedding
        self.widthPx = widthPx
        self.heightPx = heightPx
        self.durationMs = durationMs
    }
}

/// Converts raw media bytes into a semantic representation.
public protocol IMultimodalCaptioner: Sendable {
    /// True when this captioner can handle the given modality + mime. The ingester
    /// picks among multiple captioners using this predicate.
    func canCaption(modality: MediaModality, mimeType: String?) -> Bool

    /// Produces a `CaptionResult` for the given source bytes. Implementations must
    /// not retain the bytes after the call returns.
    func caption(modality: MediaModality, sourceBytes: [UInt8], mimeType: String?) async throws -> CaptionResult
}

/// Default `IMultimodalCaptioner`. Returns a descriptive shell caption — never
/// fabricates semantic content. Always available, zero model dependency, zero
/// token cost.
public struct HeuristicMultimodalCaptioner: IMultimodalCaptioner {
    public init() {}

    public func canCaption(modality: MediaModality, mimeType: String?) -> Bool { true }

    public func caption(modality: MediaModality, sourceBytes: [UInt8], mimeType: String?) async throws -> CaptionResult {
        let detected = HeuristicMultimodalCaptioner.detectMime(sourceBytes, declared: mimeType)
        let len = sourceBytes.count
        let caption: String
        switch modality {
        case .image:        caption = "[Image — no captioner wired. \(detected), \(len) bytes.]"
        case .audio:        caption = "[Audio — no captioner wired. \(detected), \(len) bytes.]"
        case .video:        caption = "[Video — no captioner wired. \(detected), \(len) bytes.]"
        case .textDocument: caption = "[Document — no captioner wired. \(detected), \(len) bytes.]"
        }
        return CaptionResult(caption: caption, embedding: nil)
    }

    static func detectMime(_ bytes: [UInt8], declared: String?) -> String {
        if let d = declared, !d.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return d }
        if bytes.count >= 4 {
            if bytes[0] == 0xFF && bytes[1] == 0xD8 { return "image/jpeg" }
            if bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 { return "image/png" }
            if bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 { return "image/gif" }
            if bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 { return "audio/wav" }
            if bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46 { return "application/pdf" }
        }
        return "application/octet-stream"
    }
}

// MARK: - IMultimodalMemoryStore

/// Persistent store of compressed multimodal memories.
public protocol IMultimodalMemoryStore {
    /// Adds an entry. Duplicate SHA-256 hits should be handled via getByHash.
    func add(_ entry: MultimodalMemoryEntry) async throws
    /// Returns the entry with the given hash, or nil if unknown.
    func getByHash(_ sourceSha256: String) async throws -> MultimodalMemoryEntry?
    /// Increments referenceCount for the entry whose hash matches. No-op when unknown.
    func reinforce(_ sourceSha256: String) async throws
    /// Returns the top-topK entries whose embedding is most similar (cosine) to
    /// queryEmbedding. When the query is nil, falls back to most-recent.
    func search(queryEmbedding: [Float]?, topK: Int) async throws -> [MultimodalMemoryEntry]
    /// Returns the most recent count entries.
    func getRecent(count: Int) async throws -> [MultimodalMemoryEntry]
    /// Removes entries older than cutoff. Returns count removed.
    func pruneOlderThan(cutoff: Date) async throws -> Int
    /// Total entries currently stored.
    func count() async throws -> Int
}

/// In-memory `IMultimodalMemoryStore`. Keyed by SHA-256 (case-insensitive to
/// reproduce the C# OrdinalIgnoreCase dictionary).
public final class InMemoryMultimodalMemoryStore: IMultimodalMemoryStore, @unchecked Sendable {
    private let lock = NSLock()
    private var byHash: [String: MultimodalMemoryEntry] = [:]

    public init() {}

    // Synchronous, lock-guarded helpers — safe to call from async contexts
    // (the lock is never held across an await).

    private func put(_ entry: MultimodalMemoryEntry) {
        lock.lock(); defer { lock.unlock() }
        byHash[InMemoryMultimodalMemoryStore.keyOf(entry.sourceSha256)] = entry
    }

    private func fetch(_ sha: String) -> MultimodalMemoryEntry? {
        lock.lock(); defer { lock.unlock() }
        return byHash[InMemoryMultimodalMemoryStore.keyOf(sha)]
    }

    private func incrementRef(_ sha: String) {
        lock.lock(); defer { lock.unlock() }
        let key = InMemoryMultimodalMemoryStore.keyOf(sha)
        if var e = byHash[key] {
            e.referenceCount += 1
            byHash[key] = e
        }
    }

    private func snapshot() -> [MultimodalMemoryEntry] {
        lock.lock(); defer { lock.unlock() }
        return Array(byHash.values)
    }

    private func removeOlder(_ cutoff: Date) -> Int {
        lock.lock(); defer { lock.unlock() }
        let doomed = byHash.filter { $0.value.recordedAt < cutoff }.map { $0.key }
        for k in doomed { byHash.removeValue(forKey: k) }
        return doomed.count
    }

    public func add(_ entry: MultimodalMemoryEntry) async throws {
        if entry.sourceSha256.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw MultimodalError.sourceHashRequired
        }
        put(entry)
    }

    public func getByHash(_ sourceSha256: String) async throws -> MultimodalMemoryEntry? {
        fetch(sourceSha256)
    }

    public func reinforce(_ sourceSha256: String) async throws {
        incrementRef(sourceSha256)
    }

    public func search(queryEmbedding: [Float]?, topK: Int) async throws -> [MultimodalMemoryEntry] {
        let all = snapshot()
        if let qe = queryEmbedding {
            let ranked = all
                .filter { ($0.embedding?.isEmpty == false) }
                .map { (entry: $0, score: cosineFull(qe, $0.embedding!)) }
                .sorted { $0.score > $1.score }
            return Array(ranked.prefix(topK).map { $0.entry })
        } else {
            let sorted = all.sorted { $0.recordedAt > $1.recordedAt }
            return Array(sorted.prefix(topK))
        }
    }

    public func getRecent(count: Int) async throws -> [MultimodalMemoryEntry] {
        let sorted = snapshot().sorted { $0.recordedAt > $1.recordedAt }
        return Array(sorted.prefix(count))
    }

    public func pruneOlderThan(cutoff: Date) async throws -> Int {
        removeOlder(cutoff)
    }

    public func count() async throws -> Int {
        lock.lock(); defer { lock.unlock() }
        return byHash.count
    }

    static func keyOf(_ sha: String) -> String { sha.lowercased() }
}

/// Errors raised by the multimodal memory layer.
public enum MultimodalError: Error, Sendable {
    /// A store add was attempted without a source SHA-256.
    case sourceHashRequired
    /// The ingester was constructed with no captioners.
    case noCaptioners
    /// The ingester was handed empty source bytes.
    case emptySourceBytes
}

// MARK: - MultimodalMemoryIngester

/// Outcome of a `MultimodalMemoryIngester.ingest` call.
public struct IngestionResult: Sendable {
    public let entry: MultimodalMemoryEntry
    public let wasDeduplicated: Bool

    public init(entry: MultimodalMemoryEntry, wasDeduplicated: Bool) {
        self.entry = entry
        self.wasDeduplicated = wasDeduplicated
    }
}

/// Ingests raw media bytes into compressed semantic memory.
///
///   1. Hashes the source (SHA-256, hex-lower).
///   2. Dedupes — if the hash is known, reinforces the existing entry and returns
///      it (no re-captioning, no duplicate storage).
///   3. Picks a captioner via canCaption().
///   4. Asks the captioner for a CaptionResult.
///   5. Persists a MultimodalMemoryEntry to the store.
///
/// Raw bytes are never persisted. The hash is the only durable handle the memory
/// layer keeps for the original artefact.
public final class MultimodalMemoryIngester: @unchecked Sendable {
    private let captioners: [IMultimodalCaptioner]
    private let store: IMultimodalMemoryStore

    /// Captioners are tried in order — the first one whose canCaption() returns
    /// true wins. The host typically registers richer captioners first and the
    /// heuristic fallback last.
    public init(captioners: [IMultimodalCaptioner], store: IMultimodalMemoryStore) throws {
        if captioners.isEmpty { throw MultimodalError.noCaptioners }
        self.captioners = captioners
        self.store = store
    }

    /// Ingests an artefact. When the SHA-256 matches an existing entry the stored
    /// record is reinforced rather than re-captioned, and the result's
    /// `wasDeduplicated` is true.
    public func ingest(
        modality: MediaModality,
        sourceBytes: [UInt8],
        mimeType: String? = nil,
        sourceUri: String? = nil,
        tags: [String: String]? = nil
    ) async throws -> IngestionResult {
        if sourceBytes.isEmpty { throw MultimodalError.emptySourceBytes }

        let hash = MultimodalMemoryIngester.computeSha256(sourceBytes)
        if let existing = try await store.getByHash(hash) {
            try await store.reinforce(hash)
            return IngestionResult(entry: existing, wasDeduplicated: true)
        }

        let captioner = pickCaptioner(modality: modality, mime: mimeType)
        let caption = try await captioner.caption(modality: modality, sourceBytes: sourceBytes, mimeType: mimeType)

        let entry = makeMultimodalMemoryEntry(
            modality: modality,
            caption: caption.caption,
            embedding: caption.embedding,
            sourceSha256: hash,
            sourceMimeType: mimeType,
            sourceByteCount: sourceBytes.count,
            sourceUri: sourceUri,
            widthPx: caption.widthPx,
            heightPx: caption.heightPx,
            durationMs: caption.durationMs,
            tags: tags)

        try await store.add(entry)
        return IngestionResult(entry: entry, wasDeduplicated: false)
    }

    /// Convenience overload accepting `Data` source bytes.
    public func ingest(
        modality: MediaModality,
        sourceData: Data,
        mimeType: String? = nil,
        sourceUri: String? = nil,
        tags: [String: String]? = nil
    ) async throws -> IngestionResult {
        try await ingest(
            modality: modality, sourceBytes: [UInt8](sourceData),
            mimeType: mimeType, sourceUri: sourceUri, tags: tags)
    }

    private func pickCaptioner(modality: MediaModality, mime: String?) -> IMultimodalCaptioner {
        for c in captioners where c.canCaption(modality: modality, mimeType: mime) { return c }
        // The last registered captioner should accept everything; if no
        // host-supplied captioner matches, the heuristic fallback wins.
        return captioners[captioners.count - 1]
    }

    /// SHA-256 of `bytes`, hex-lower. `SHA256.hash` is deterministic (unlike
    /// Ed25519 signing), so this is safe and byte-stable across platforms.
    static func computeSha256(_ bytes: [UInt8]) -> String {
        let digest = SHA256.hash(data: Data(bytes))
        return digest.map { String(format: "%02x", $0) }.joined()
    }
}
