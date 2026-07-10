// EmbeddingStore.swift
//
// Port of:
//   • CircleAI.Embeddings.Local.ICircleEmbeddingStore (+ EmbeddingDocument,
//     EmbeddingSearchHit, IEmbeddingEncoder)
//   • CircleAI.Embeddings.Local.IEmbeddingIndex (+ EmbeddingIndexHit)
//   • CircleAI.Embeddings.Local.InMemoryEmbeddingStore
//   • InMemoryEmbeddingIndex — the deterministic brute-force IEmbeddingIndex
//     (the C# default index that HnswEmbeddingStore lifts behind the interface)
//
// The store persists via a custom binary format that is byte-identical to the
// C# InMemoryEmbeddingStore (magic "CELQ", version 1, 7-bit-length strings,
// little-endian primitives). Vectors are TurboQuant-compressed at 4-bits-per-dim
// before storage, reusing the byte-exact `TurboQuantCodec` already ported in
// Compression.swift.

import Foundation

// MARK: - EmbeddingDocument — CircleAI.Embeddings.Local.EmbeddingDocument

/// One document in the store. `id` is caller-chosen and uniquely identifies the
/// document for delete / update.
public struct EmbeddingDocument: Sendable, Equatable {
    public let id: String
    public let text: String
    public let metadata: [String: String]?

    public init(id: String, text: String, metadata: [String: String]? = nil) {
        self.id = id
        self.text = text
        self.metadata = metadata
    }
}

// MARK: - EmbeddingSearchHit — CircleAI.Embeddings.Local.EmbeddingSearchHit

/// One hit from `ICircleEmbeddingStore.search`. Higher `score` = closer. Cosine
/// similarity: 1.0 = identical, -1.0 = opposite, 0.0 = orthogonal.
public struct EmbeddingSearchHit: Sendable, Equatable {
    public let document: EmbeddingDocument
    public let score: Float

    public init(document: EmbeddingDocument, score: Float) {
        self.document = document
        self.score = score
    }
}

// MARK: - EmbeddingIndexHit — CircleAI.Embeddings.Local.EmbeddingIndexHit

/// One hit returned by `IEmbeddingIndex.search`. `internalId` is the
/// insertion-order id assigned by `IEmbeddingIndex.add`. Higher `score` = closer.
public struct EmbeddingIndexHit: Sendable, Equatable {
    public let internalId: Int64
    public let score: Float

    public init(internalId: Int64, score: Float) {
        self.internalId = internalId
        self.score = score
    }
}

// MARK: - IEmbeddingEncoder — CircleAI.Embeddings.Local.IEmbeddingEncoder

/// Translates text into a dense vector. Bring your own — sentence-transformers
/// via ONNX, a small MNN encoder, or a cloud API.
public protocol IEmbeddingEncoder: Sendable {
    /// Vector dimension this encoder produces. All vectors fed to the store from
    /// the same encoder must agree.
    var dimension: Int { get }

    /// Encode one text into a dense vector.
    func encode(_ text: String) async throws -> [Float]
}

// MARK: - ICircleEmbeddingStore — CircleAI.Embeddings.Local.ICircleEmbeddingStore

/// On-device embedding store with a built-in RAG primitive. Add documents once,
/// search by text or vector. Vectors are TurboQuant-compressed so the store fits
/// far more documents in the same RAM/disk footprint as raw FP32.
public protocol ICircleEmbeddingStore: AnyObject, Sendable {
    /// Vector dimension this store was created with.
    var dimension: Int { get }

    /// How many documents are currently in the store. Synchronous, lock-guarded
    /// read (mirrors the C# `int Count { get; }`).
    var count: Int { get }

    /// Add (or replace) one document. The encoder produces the vector; the store
    /// quantises and indexes it.
    func add(document: EmbeddingDocument) async throws

    /// Add a document with a caller-supplied vector. Vector length must equal
    /// `dimension`.
    func add(document: EmbeddingDocument, vector: [Float]) async throws

    /// Remove a document by id. Returns true if a document was removed.
    func remove(id: String) async throws -> Bool

    /// Search by text. The encoder produces a query vector; returns the `topK`
    /// closest documents by cosine similarity.
    func search(queryText: String, topK: Int) async throws -> [EmbeddingSearchHit]

    /// Search by a pre-computed query vector. Vector length must equal `dimension`.
    func search(queryVector: [Float], topK: Int) async throws -> [EmbeddingSearchHit]

    /// Persist the entire store to `path`. Atomic via write-tmp-then-rename.
    func save(path: String) async throws

    /// Load a previously-saved store from `path`. Replaces all in-memory state.
    func load(path: String) async throws

    /// Release resources.
    func dispose() async
}

// MARK: - IEmbeddingIndex — CircleAI.Embeddings.Local.IEmbeddingIndex

/// Vector index contract. The store layers documents + metadata + persistence
/// on top; the index is the search primitive.
public protocol IEmbeddingIndex: AnyObject, Sendable {
    /// Vector dimensionality. Locked at construction.
    var dimension: Int { get }

    /// How many vectors are currently in the index. Synchronous, lock-guarded
    /// read (mirrors the C# `long Count { get; }`).
    var count: Int64 { get }

    /// Append one vector. Returns the internal id the index assigned.
    func add(vector: [Float]) async throws -> Int64

    /// Search for the top-`topK` nearest neighbours.
    func search(queryVector: [Float], topK: Int) async throws -> [EmbeddingIndexHit]

    /// Persist the index to `path`.
    func save(path: String) async throws

    /// Reload from `path`, replacing the in-memory state.
    func load(path: String) async throws
}

// MARK: - InMemoryEmbeddingStore — CircleAI.Embeddings.Local.InMemoryEmbeddingStore

/// Default `ICircleEmbeddingStore`: brute-force cosine search over
/// TurboQuant-compressed vectors held in memory. Thread-safety via an `NSLock`
/// confined to the synchronous mutation helpers.
public final class InMemoryEmbeddingStore: ICircleEmbeddingStore, @unchecked Sendable {
    private static let fileMagic: Int32 = 0x4C455143 // "CELQ" little-endian
    private static let fileVersion: UInt16 = 1
    public static let defaultBitsPerDim = 4

    private struct Entry {
        let document: EmbeddingDocument
        let payload: TurboQuantPayload
    }

    private let lock = NSLock()
    private let encoder: any IEmbeddingEncoder
    private let bitsPerDim: Int
    private var entries: [String: Entry] = [:]  // insertion-preserving via `order`
    private var order: [String] = []            // preserves first-seen order for stable save/search
    private var disposed = false

    public var dimension: Int { encoder.dimension }

    public var count: Int {
        lock.lock(); defer { lock.unlock() }
        return entries.count
    }

    /// Construct with a caller-supplied encoder. `bitsPerDim` controls the
    /// TurboQuant quantisation depth — 4 bits/dim is the v1 default. Valid
    /// range: 1–8.
    public init(encoder: any IEmbeddingEncoder, bitsPerDim: Int = defaultBitsPerDim) {
        precondition(bitsPerDim >= 1 && bitsPerDim <= 8, "Valid range: 1–8.")
        self.encoder = encoder
        self.bitsPerDim = bitsPerDim
    }

    public func add(document: EmbeddingDocument) async throws {
        let vector = try await encoder.encode(document.text)
        try await add(document: document, vector: vector)
    }

    public func add(document: EmbeddingDocument, vector: [Float]) async throws {
        try throwIfDisposed()
        if vector.count != dimension {
            throw ModelRuntimeError.argument("Vector length \(vector.count) != store dimension \(dimension).")
        }
        let payload = TurboQuantCodec.encode(vector, bitsPerDim: bitsPerDim)
        lock.lock()
        if entries[document.id] == nil { order.append(document.id) }
        entries[document.id] = Entry(document: document, payload: payload)
        lock.unlock()
    }

    public func remove(id: String) async throws -> Bool {
        precondition(!id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        try throwIfDisposed()
        lock.lock(); defer { lock.unlock() }
        if entries.removeValue(forKey: id) != nil {
            order.removeAll { $0 == id }
            return true
        }
        return false
    }

    public func search(queryText: String, topK: Int = 5) async throws -> [EmbeddingSearchHit] {
        precondition(!queryText.isEmpty)
        let vector = try await encoder.encode(queryText)
        return try await search(queryVector: vector, topK: topK)
    }

    public func search(queryVector: [Float], topK: Int = 5) async throws -> [EmbeddingSearchHit] {
        try throwIfDisposed()
        if queryVector.count != dimension {
            throw ModelRuntimeError.argument("Vector length \(queryVector.count) != store dimension \(dimension).")
        }
        if topK <= 0 { throw ModelRuntimeError.argument("topK") }

        let qNorm = InMemoryEmbeddingStore.normSafe(queryVector)
        var q = queryVector
        if qNorm > 0 { for i in 0..<q.count { q[i] /= qNorm } }

        // Snapshot under lock, score outside.
        lock.lock()
        let snapshot: [(id: String, entry: Entry)] = order.compactMap { id in
            entries[id].map { (id, $0) }
        }
        lock.unlock()

        var scored: [(score: Float, id: String)] = []
        scored.reserveCapacity(snapshot.count)
        for (id, entry) in snapshot {
            let decoded = TurboQuantCodec.decode(entry.payload, dim: dimension, bitsPerDim: bitsPerDim)
            let entryNorm = InMemoryEmbeddingStore.normSafe(decoded)
            if entryNorm <= 0 { continue }
            var dot: Float = 0
            for i in 0..<dimension { dot += q[i] * (decoded[i] / entryNorm) }
            scored.append((dot, id))
        }

        // Top-K by descending score; ties broken by ordinal id, matching C#'s
        // ScoreComparer (score asc, then ordinal id) then OrderByDescending.
        scored.sort { a, b in
            if a.score != b.score { return a.score > b.score }
            return a.id < b.id
        }

        let take = min(topK, scored.count)
        var out: [EmbeddingSearchHit] = []
        out.reserveCapacity(take)
        lock.lock()
        for i in 0..<take {
            if let e = entries[scored[i].id] {
                out.append(EmbeddingSearchHit(document: e.document, score: scored[i].score))
            }
        }
        lock.unlock()
        return out
    }

    public func save(path: String) async throws {
        precondition(!path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        try throwIfDisposed()

        lock.lock()
        let snapshot: [(String, Entry)] = order.compactMap { id in entries[id].map { (id, $0) } }
        let dim = dimension
        let bits = bitsPerDim
        lock.unlock()

        let dir = (path as NSString).deletingLastPathComponent
        if !dir.isEmpty {
            try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        }
        let tmp = path + ".tmp"

        let w = DotNetBinaryWriter()
        w.writeInt32(InMemoryEmbeddingStore.fileMagic)
        w.writeUInt16(InMemoryEmbeddingStore.fileVersion)
        w.writeUInt16(UInt16(bits))
        w.writeInt32(Int32(dim))
        w.writeInt32(Int32(snapshot.count))
        for (id, entry) in snapshot {
            w.writeString(id)
            w.writeString(entry.document.text)
            let meta = entry.document.metadata
            w.writeInt32(Int32(meta?.count ?? 0))
            if let meta = meta {
                // C# enumerates the dictionary in its internal order. To keep the
                // file deterministic across platforms we emit metadata sorted by
                // key (round-trips identically; order within the file is the only
                // freedom and this makes it stable).
                for k in meta.keys.sorted() {
                    w.writeString(k)
                    w.writeString(meta[k]!)
                }
            }
            w.writeFloat(entry.payload.norm)
            w.writeInt32(Int32(entry.payload.packedIndices.count))
            w.writeBytes(entry.payload.packedIndices)
        }

        try Data(w.buffer).write(to: URL(fileURLWithPath: tmp))
        if FileManager.default.fileExists(atPath: path) {
            try FileManager.default.removeItem(atPath: path)
        }
        try FileManager.default.moveItem(atPath: tmp, toPath: path)
    }

    public func load(path: String) async throws {
        precondition(!path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        try throwIfDisposed()
        guard FileManager.default.fileExists(atPath: path) else {
            throw ModelRuntimeError.fileNotFound("Embedding store file not found: \(path)")
        }
        guard let data = FileManager.default.contents(atPath: path) else {
            throw ModelRuntimeError.fileNotFound(path)
        }

        let r = DotNetBinaryReader(Array(data))
        let magic = try r.readInt32()
        if magic != InMemoryEmbeddingStore.fileMagic {
            throw ModelRuntimeError.invalidData("Not a CircleAI embedding store file.")
        }
        let version = try r.readUInt16()
        if version != InMemoryEmbeddingStore.fileVersion {
            throw ModelRuntimeError.invalidData("Unsupported file version \(version).")
        }
        let fileBits = try r.readUInt16()
        if Int(fileBits) != bitsPerDim {
            throw ModelRuntimeError.invalidData("Bits-per-dim mismatch: store=\(bitsPerDim), file=\(fileBits).")
        }
        let fileDim = try r.readInt32()
        if Int(fileDim) != dimension {
            throw ModelRuntimeError.invalidData("Dimension mismatch: store=\(dimension), file=\(fileDim).")
        }

        let count = try r.readInt32()
        var newEntries: [String: Entry] = [:]
        var newOrder: [String] = []
        for _ in 0..<count {
            let id = try r.readString()
            let text = try r.readString()
            let metaCount = try r.readInt32()
            var metadata: [String: String]? = nil
            if metaCount > 0 {
                var m: [String: String] = [:]
                for _ in 0..<metaCount {
                    let k = try r.readString()
                    let v = try r.readString()
                    m[k] = v
                }
                metadata = m
            }
            let norm = try r.readFloat()
            let packedLen = try r.readInt32()
            let packed = try r.readBytes(Int(packedLen))
            let doc = EmbeddingDocument(id: id, text: text, metadata: metadata)
            newEntries[id] = Entry(document: doc, payload: TurboQuantPayload(norm: norm, packedIndices: packed))
            newOrder.append(id)
        }

        lock.lock()
        entries = newEntries
        order = newOrder
        lock.unlock()
    }

    public func dispose() async {
        lock.lock()
        if !disposed {
            disposed = true
            entries.removeAll()
            order.removeAll()
        }
        lock.unlock()
    }

    private func throwIfDisposed() throws {
        lock.lock(); defer { lock.unlock() }
        if disposed { throw ModelRuntimeError.objectDisposed("InMemoryEmbeddingStore") }
    }

    private static func normSafe(_ v: [Float]) -> Float {
        var sum = 0.0
        for x in v { sum += Double(x) * Double(x) }
        return Float(sum.squareRoot())
    }
}

// MARK: - InMemoryEmbeddingIndex — the deterministic IEmbeddingIndex default

/// Brute-force `IEmbeddingIndex`: cosine over raw FP32 vectors held in memory.
/// This is the search primitive the C# `InMemoryEmbeddingStore` inlines and that
/// `HnswEmbeddingStore` swaps behind the interface. Internal ids are assigned in
/// insertion order (starting at 0), matching the C# `Count`-as-next-id scheme.
///
/// Persistence uses a compact deterministic binary format (magic "CEIX",
/// version 1): int32 magic, uint16 version, int32 dimension, int64 count, then
/// per-vector `dimension` float32s little-endian.
public final class InMemoryEmbeddingIndex: IEmbeddingIndex, @unchecked Sendable {
    private static let fileMagic: Int32 = 0x58494543 // "CEIX" little-endian
    private static let fileVersion: UInt16 = 1

    private let lock = NSLock()
    private let dim: Int
    private var vectors: [[Float]] = []

    public var dimension: Int { dim }

    public var count: Int64 {
        lock.lock(); defer { lock.unlock() }
        return Int64(vectors.count)
    }

    public init(dimension: Int) {
        precondition(dimension > 0, "dimension must be > 0")
        self.dim = dimension
    }

    public func add(vector: [Float]) async throws -> Int64 {
        if vector.count != dim {
            throw ModelRuntimeError.argument("Vector length \(vector.count) != index dimension \(dim).")
        }
        lock.lock(); defer { lock.unlock() }
        let id = Int64(vectors.count)
        vectors.append(vector)
        return id
    }

    public func search(queryVector: [Float], topK: Int) async throws -> [EmbeddingIndexHit] {
        if queryVector.count != dim {
            throw ModelRuntimeError.argument("Vector length \(queryVector.count) != index dimension \(dim).")
        }
        if topK <= 0 { throw ModelRuntimeError.argument("topK") }

        let qNorm = InMemoryEmbeddingIndex.normSafe(queryVector)
        var q = queryVector
        if qNorm > 0 { for i in 0..<q.count { q[i] /= qNorm } }

        lock.lock()
        let snapshot = vectors
        lock.unlock()

        var scored: [(score: Float, id: Int64)] = []
        scored.reserveCapacity(snapshot.count)
        for (i, vec) in snapshot.enumerated() {
            let n = InMemoryEmbeddingIndex.normSafe(vec)
            if n <= 0 { continue }
            var dot: Float = 0
            for k in 0..<dim { dot += q[k] * (vec[k] / n) }
            scored.append((dot, Int64(i)))
        }
        scored.sort { a, b in
            if a.score != b.score { return a.score > b.score }
            return a.id < b.id
        }
        let take = min(topK, scored.count)
        return (0..<take).map { EmbeddingIndexHit(internalId: scored[$0].id, score: scored[$0].score) }
    }

    public func save(path: String) async throws {
        precondition(!path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        lock.lock()
        let snapshot = vectors
        lock.unlock()

        let dir = (path as NSString).deletingLastPathComponent
        if !dir.isEmpty {
            try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        }

        let w = DotNetBinaryWriter()
        w.writeInt32(InMemoryEmbeddingIndex.fileMagic)
        w.writeUInt16(InMemoryEmbeddingIndex.fileVersion)
        w.writeInt32(Int32(dim))
        w.writeInt64(Int64(snapshot.count))
        for vec in snapshot {
            for f in vec { w.writeFloat(f) }
        }
        let tmp = path + ".tmp"
        try Data(w.buffer).write(to: URL(fileURLWithPath: tmp))
        if FileManager.default.fileExists(atPath: path) {
            try FileManager.default.removeItem(atPath: path)
        }
        try FileManager.default.moveItem(atPath: tmp, toPath: path)
    }

    public func load(path: String) async throws {
        precondition(!path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        guard let data = FileManager.default.contents(atPath: path) else {
            throw ModelRuntimeError.fileNotFound("Embedding index file not found: \(path)")
        }
        let r = DotNetBinaryReader(Array(data))
        let magic = try r.readInt32()
        if magic != InMemoryEmbeddingIndex.fileMagic {
            throw ModelRuntimeError.invalidData("Not a CircleAI embedding index file.")
        }
        let version = try r.readUInt16()
        if version != InMemoryEmbeddingIndex.fileVersion {
            throw ModelRuntimeError.invalidData("Unsupported file version \(version).")
        }
        let fileDim = try r.readInt32()
        if Int(fileDim) != dim {
            throw ModelRuntimeError.invalidData("Dimension mismatch: index=\(dim), file=\(fileDim).")
        }
        let n = try r.readInt64()
        var loaded: [[Float]] = []
        loaded.reserveCapacity(Int(n))
        for _ in 0..<n {
            var vec = [Float](repeating: 0, count: dim)
            for k in 0..<dim { vec[k] = try r.readFloat() }
            loaded.append(vec)
        }
        lock.lock()
        vectors = loaded
        lock.unlock()
    }

    private static func normSafe(_ v: [Float]) -> Float {
        var sum = 0.0
        for x in v { sum += Double(x) * Double(x) }
        return Float(sum.squareRoot())
    }
}

// MARK: - DotNetBinaryWriter / DotNetBinaryReader
//
// Faithful reproduction of System.IO.BinaryWriter / BinaryReader semantics:
//   • Int32 / Int64 / UInt16 / Float — little-endian.
//   • String — a 7-bit-encoded-int length prefix (bytes) followed by UTF-8.
//   • Byte[] — raw bytes.
// This makes the InMemoryEmbeddingStore file byte-identical to the C# writer.

final class DotNetBinaryWriter {
    private(set) var buffer: [UInt8] = []

    func writeInt32(_ v: Int32) {
        let u = UInt32(bitPattern: v)
        buffer.append(UInt8(u & 0xFF))
        buffer.append(UInt8((u >> 8) & 0xFF))
        buffer.append(UInt8((u >> 16) & 0xFF))
        buffer.append(UInt8((u >> 24) & 0xFF))
    }

    func writeInt64(_ v: Int64) {
        let u = UInt64(bitPattern: v)
        for i in 0..<8 { buffer.append(UInt8((u >> (8 * i)) & 0xFF)) }
    }

    func writeUInt16(_ v: UInt16) {
        buffer.append(UInt8(v & 0xFF))
        buffer.append(UInt8((v >> 8) & 0xFF))
    }

    func writeFloat(_ v: Float) {
        let u = v.bitPattern
        buffer.append(UInt8(u & 0xFF))
        buffer.append(UInt8((u >> 8) & 0xFF))
        buffer.append(UInt8((u >> 16) & 0xFF))
        buffer.append(UInt8((u >> 24) & 0xFF))
    }

    func writeBytes(_ bytes: [UInt8]) {
        buffer.append(contentsOf: bytes)
    }

    /// Mirrors BinaryWriter.Write(string): 7-bit-encoded-int byte-length prefix
    /// then the UTF-8 bytes.
    func writeString(_ s: String) {
        let utf8 = Array(s.utf8)
        write7BitEncodedInt(utf8.count)
        buffer.append(contentsOf: utf8)
    }

    private func write7BitEncodedInt(_ value: Int) {
        var v = UInt32(truncatingIfNeeded: value)
        while v >= 0x80 {
            buffer.append(UInt8((v & 0x7F) | 0x80))
            v >>= 7
        }
        buffer.append(UInt8(v))
    }
}

final class DotNetBinaryReader {
    enum ReadError: Error, Sendable { case endOfStream }

    private let data: [UInt8]
    private var pos: Int = 0

    init(_ data: [UInt8]) { self.data = data }

    private func need(_ n: Int) throws {
        if pos + n > data.count { throw ReadError.endOfStream }
    }

    func readInt32() throws -> Int32 {
        try need(4)
        let u = UInt32(data[pos])
            | (UInt32(data[pos + 1]) << 8)
            | (UInt32(data[pos + 2]) << 16)
            | (UInt32(data[pos + 3]) << 24)
        pos += 4
        return Int32(bitPattern: u)
    }

    func readInt64() throws -> Int64 {
        try need(8)
        var u: UInt64 = 0
        for i in 0..<8 { u |= UInt64(data[pos + i]) << (8 * i) }
        pos += 8
        return Int64(bitPattern: u)
    }

    func readUInt16() throws -> UInt16 {
        try need(2)
        let u = UInt16(data[pos]) | (UInt16(data[pos + 1]) << 8)
        pos += 2
        return u
    }

    func readFloat() throws -> Float {
        try need(4)
        let u = UInt32(data[pos])
            | (UInt32(data[pos + 1]) << 8)
            | (UInt32(data[pos + 2]) << 16)
            | (UInt32(data[pos + 3]) << 24)
        pos += 4
        return Float(bitPattern: u)
    }

    func readBytes(_ n: Int) throws -> [UInt8] {
        try need(n)
        let slice = Array(data[pos..<(pos + n)])
        pos += n
        return slice
    }

    func readString() throws -> String {
        let len = try read7BitEncodedInt()
        let bytes = try readBytes(len)
        return String(decoding: bytes, as: UTF8.self)
    }

    private func read7BitEncodedInt() throws -> Int {
        var result: UInt32 = 0
        var shift: UInt32 = 0
        while true {
            try need(1)
            let b = data[pos]; pos += 1
            result |= UInt32(b & 0x7F) << shift
            if (b & 0x80) == 0 { break }
            shift += 7
            if shift > 35 { throw ReadError.endOfStream }
        }
        return Int(result)
    }
}
