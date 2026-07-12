// HnswEmbeddingStore.swift
//
// Port of src/CircleAI.Embeddings.Local/HnswEmbeddingStore.cs — an
// `ICircleEmbeddingStore` backed by a SIMD-blocked quantised search index.
//
// Same public contract as `InMemoryEmbeddingStore`; the only difference is the
// search path is O(N / SIMD-block) instead of O(N · D). The C# original wraps
// a concrete `TurboVecEmbeddingIndex`; per the port brief the SIMD index is
// injected behind the existing `IEmbeddingIndex` protocol seam (EmbeddingStore.swift),
// so this store owns ONLY the doc-map + `.docs` disk format + save/load logic.
//
// On-disk format: a sidecar `<path>.docs` file holds the id→document map
// (ordinal-keyed, .NET `BinaryWriter` layout via `DotNetBinaryWriter`); the
// injected index persists itself to `<path>`. Save writes both; Load reads both.
//
// Porting notes:
//   • `SemaphoreSlim(1,1)` async gate → an `NSLock` confined to the synchronous
//     critical sections (mirrors the InMemoryEmbeddingStore port), plus the
//     awaited index calls made OUTSIDE the lock to avoid blocking the executor.
//   • `ConcurrentDictionary<string,long> _idLookup` + `List<...> _byId` →
//     `[String: Int64]` + `[EmbeddingDocument]` guarded by the same lock.
//   • The `.docs` `bool live` flag → a byte (0/1) written via `writeBool`
//     (BinaryWriter.Write(bool) is a single byte), byte-identical to C#.
//   • `TurboVecEmbeddingIndex(dimension, bitWidth)` construction is replaced by
//     an injected `IEmbeddingIndex`; `bitWidth` is retained ONLY as a
//     construction-time contract check (it never enters the `.docs` sidecar in
//     C# either).

import Foundation

/// (RT-09b) Embedding store backed by an injected `IEmbeddingIndex` (a
/// SIMD-blocked / quantised index in production). Vectors are quantised by the
/// index; this store layers the document map, metadata, and `.docs` persistence.
///
/// (C# `HnswEmbeddingStore`.)
public final class HnswEmbeddingStore: ICircleEmbeddingStore, @unchecked Sendable {
    private static let docsMagic: Int32 = 0x53434847   // "HGCS" — Hnsw Generic Circle Store
    private static let docsVersion: UInt16 = 1
    public static let defaultBitWidth = 4

    private let encoder: any IEmbeddingEncoder
    private let index: any IEmbeddingIndex
    private let lock = NSLock()

    /// Ordinal internal-id → EmbeddingDocument. Index aligns with the injected
    /// index's slot ids.
    private var byId: [EmbeddingDocument] = []

    /// External-document-id → internal-id, for O(1) Remove.
    private var idLookup: [String: Int64] = [:]

    private var disposed = false

    public var dimension: Int { encoder.dimension }

    public var count: Int {
        lock.lock(); defer { lock.unlock() }
        return byId.count
    }

    /// Construct with an encoder and the index seam. The encoder's `dimension`
    /// must be > 0 and a multiple of 8 (SIMD alignment, matching the C#
    /// turbovec contract). `bitWidth` must be 2, 3, or 4.
    ///
    /// The `index` is injected (C# constructs a concrete `TurboVecEmbeddingIndex`
    /// internally). Its `dimension` must match the encoder's.
    public init(
        encoder: any IEmbeddingEncoder,
        index: any IEmbeddingIndex,
        bitWidth: Int = defaultBitWidth
    ) {
        precondition(encoder.dimension > 0 && encoder.dimension % 8 == 0,
                     "Encoder dimension \(encoder.dimension) must be > 0 and a multiple of 8 for turbovec.")
        precondition(bitWidth >= 2 && bitWidth <= 4, "bitWidth must be 2, 3, or 4.")
        precondition(index.dimension == encoder.dimension,
                     "Index dimension \(index.dimension) != encoder dimension \(encoder.dimension).")
        self.encoder = encoder
        self.index = index
    }

    public func add(document: EmbeddingDocument) async throws {
        let vector = try await encoder.encode(document.text)
        try await add(document: document, vector: vector)
    }

    public func add(document: EmbeddingDocument, vector: [Float]) async throws {
        try throwIfDisposed()
        if vector.count != dimension {
            throw ModelRuntimeError.argument(
                "Vector length \(vector.count) != store dimension \(dimension).")
        }

        // Replace-by-id is not supported (v1 "add only"); callers Remove first.
        // Snapshot the guard under the lock, do the (awaited) index add outside
        // it, then commit under the lock — matching the C# gate ordering.
        lock.lock()
        if idLookup[document.id] != nil {
            lock.unlock()
            throw ModelRuntimeError.invalidOperation(
                "Document id '\(document.id)' already exists. Call remove first.")
        }
        lock.unlock()

        let internalId = try await index.add(vector: vector)

        lock.lock()
        byId.append(document)
        idLookup[document.id] = internalId
        lock.unlock()
    }

    public func remove(id: String) async throws -> Bool {
        if id.isBlank { throw ModelRuntimeError.argument("id required") }
        try throwIfDisposed()
        // v1 semantics: mark deleted in the lookup so subsequent searches skip
        // the slot. Compaction is a follow-up (mirrors the C# note).
        lock.lock(); defer { lock.unlock() }
        if idLookup.removeValue(forKey: id) != nil { return true }
        return false
    }

    public func search(queryText: String, topK: Int = 5) async throws -> [EmbeddingSearchHit] {
        if queryText.isEmpty { throw ModelRuntimeError.argument("queryText required") }
        let vector = try await encoder.encode(queryText)
        return try await search(queryVector: vector, topK: topK)
    }

    public func search(queryVector: [Float], topK: Int = 5) async throws -> [EmbeddingSearchHit] {
        try throwIfDisposed()
        if queryVector.count != dimension {
            throw ModelRuntimeError.argument(
                "Query length \(queryVector.count) != store dimension \(dimension).")
        }
        if topK <= 0 { throw ModelRuntimeError.argument("topK") }

        // Over-fetch to compensate for removed slots; cap to current count.
        let indexCount = Int(index.count)
        let overFetch = min(indexCount, max(topK * 2, topK + 10))
        if overFetch == 0 { return [] }

        let rawHits = try await index.search(queryVector: queryVector, topK: overFetch)
        if rawHits.isEmpty { return [] }

        lock.lock(); defer { lock.unlock() }
        var results: [EmbeddingSearchHit] = []
        results.reserveCapacity(topK)
        for hit in rawHits {
            if hit.internalId < 0 || hit.internalId >= Int64(byId.count) { continue }
            let doc = byId[Int(hit.internalId)]
            if idLookup[doc.id] == nil { continue }   // removed
            results.append(EmbeddingSearchHit(document: doc, score: hit.score))
            if results.count == topK { break }
        }
        return results
    }

    public func save(path: String) async throws {
        if path.isBlank { throw ModelRuntimeError.argument("path required") }
        try throwIfDisposed()

        let dir = (path as NSString).deletingLastPathComponent
        if !dir.isEmpty {
            try? FileManager.default.createDirectory(
                atPath: dir, withIntermediateDirectories: true)
        }

        // Persist the injected index (awaited, outside the lock).
        try await index.save(path: path)

        // Snapshot the doc map under the lock, then serialise + write.
        lock.lock()
        let docsSnapshot = byId
        let liveSnapshot = Set(idLookup.keys)
        let dim = dimension
        lock.unlock()

        let w = DotNetBinaryWriter()
        w.writeInt32(Self.docsMagic)
        w.writeUInt16(Self.docsVersion)
        w.writeInt32(Int32(dim))
        w.writeInt32(Int32(docsSnapshot.count))
        for doc in docsSnapshot {
            w.writeString(doc.id)
            w.writeString(doc.text)
            w.writeBool(liveSnapshot.contains(doc.id))   // live flag
            let meta = doc.metadata
            w.writeInt32(Int32(meta?.count ?? 0))
            if let meta = meta {
                // C# enumerates the dictionary in its internal order; emit sorted
                // by key so the sidecar is deterministic across platforms.
                for k in meta.keys.sorted() {
                    w.writeString(k)
                    w.writeString(meta[k]!)
                }
            }
        }

        let docsPath = path + ".docs"
        let tmp = docsPath + ".tmp"
        try Data(w.buffer).write(to: URL(fileURLWithPath: tmp))
        if FileManager.default.fileExists(atPath: docsPath) {
            try FileManager.default.removeItem(atPath: docsPath)
        }
        try FileManager.default.moveItem(atPath: tmp, toPath: docsPath)
    }

    public func load(path: String) async throws {
        if path.isBlank { throw ModelRuntimeError.argument("path required") }
        try throwIfDisposed()
        let docsPath = path + ".docs"
        guard FileManager.default.fileExists(atPath: path) else {
            throw ModelRuntimeError.fileNotFound("Index file not found: \(path)")
        }
        guard FileManager.default.fileExists(atPath: docsPath) else {
            throw ModelRuntimeError.fileNotFound("Docs sidecar not found: \(docsPath)")
        }

        // Reload the injected index (awaited, outside the lock).
        try await index.load(path: path)

        guard let data = FileManager.default.contents(atPath: docsPath) else {
            throw ModelRuntimeError.fileNotFound(docsPath)
        }
        let r = DotNetBinaryReader(Array(data))
        let magic = try r.readInt32()
        if magic != Self.docsMagic {
            throw ModelRuntimeError.invalidData("Not an HnswEmbeddingStore docs sidecar.")
        }
        let version = try r.readUInt16()
        if version != Self.docsVersion {
            throw ModelRuntimeError.invalidData("Unsupported docs version \(version).")
        }
        let fileDim = try r.readInt32()
        if Int(fileDim) != dimension {
            throw ModelRuntimeError.invalidData(
                "Dimension mismatch: store=\(dimension), file=\(fileDim).")
        }
        let count = try r.readInt32()

        var newById: [EmbeddingDocument] = []
        var newLookup: [String: Int64] = [:]
        newById.reserveCapacity(Int(count))
        var i: Int32 = 0
        while i < count {
            let id = try r.readString()
            let text = try r.readString()
            let live = try r.readBool()
            let metaCount = try r.readInt32()
            var metadata: [String: String]? = nil
            if metaCount > 0 {
                var m: [String: String] = [:]
                var k: Int32 = 0
                while k < metaCount {
                    let key = try r.readString()
                    let value = try r.readString()
                    m[key] = value
                    k += 1
                }
                metadata = m
            }
            let doc = EmbeddingDocument(id: id, text: text, metadata: metadata)
            newById.append(doc)
            if live { newLookup[id] = Int64(i) }
            i += 1
        }

        lock.lock()
        byId = newById
        idLookup = newLookup
        lock.unlock()
    }

    public func dispose() async {
        lock.lock()
        if !disposed {
            disposed = true
            byId.removeAll()
            idLookup.removeAll()
        }
        lock.unlock()
    }

    private func throwIfDisposed() throws {
        lock.lock(); defer { lock.unlock() }
        if disposed { throw ModelRuntimeError.objectDisposed("HnswEmbeddingStore") }
    }
}

// MARK: - .NET BinaryWriter/Reader bool support
//
// `DotNetBinaryWriter` / `DotNetBinaryReader` live in EmbeddingStore.swift and
// cover Int32/Int64/UInt16/Float/String/Byte[]. The `.docs` sidecar needs a
// single-byte bool (BinaryWriter.Write(bool) writes one byte: 1 for true, 0 for
// false). These extensions add that, reusing the existing byte primitives so the
// output stays byte-identical to C#.

extension DotNetBinaryWriter {
    /// Mirrors `BinaryWriter.Write(bool)`: one byte, 1 = true, 0 = false.
    func writeBool(_ v: Bool) {
        writeBytes([v ? 1 : 0])
    }
}

extension DotNetBinaryReader {
    /// Mirrors `BinaryReader.ReadBoolean`: reads one byte, non-zero = true.
    func readBool() throws -> Bool {
        let b = try readBytes(1)
        return b[0] != 0
    }
}
