// EmbeddingStoreTests.swift
//
// Exercises the ported CircleAI.Embeddings.Local surface:
//   InMemoryEmbeddingStore (add/search/remove/save/load, TurboQuant-compressed),
//   InMemoryEmbeddingIndex (brute-force cosine + persistence),
//   EmbeddingDocument / EmbeddingSearchHit / EmbeddingIndexHit DTOs,
//   and the DotNetBinaryWriter/Reader (7-bit-length strings, LE primitives,
//   "CELQ" magic) that make the store file byte-identical to C#.

import XCTest
@testable import CircleAI

// A deterministic 3-d encoder: maps the first three chars to their code points.
// L2-magnitude is irrelevant to the store (it re-normalises for cosine).
private struct CharCodeEncoder: IEmbeddingEncoder {
    let dimension = 3
    func encode(_ text: String) async throws -> [Float] {
        let scalars = Array(text.unicodeScalars.prefix(3)).map { Float($0.value % 97) }
        var v = [Float](repeating: 0.001, count: 3)  // avoid a zero vector
        for (i, s) in scalars.enumerated() { v[i] = s + 0.001 }
        return v
    }
}

final class EmbeddingStoreTests: XCTestCase {

    private func tempPath(_ ext: String) -> String {
        let dir = NSTemporaryDirectory()
        return (dir as NSString).appendingPathComponent("cai-emb-\(UUID().uuidString).\(ext)")
    }

    // ── DotNetBinaryWriter / Reader wire format ───────────────────────────

    func testBinaryWriter7BitStringAndPrimitives() throws {
        let w = DotNetBinaryWriter()
        w.writeInt32(0x4C455143)      // "CELQ"
        w.writeUInt16(1)
        w.writeString("hi")           // len prefix (0x02) + 'h','i'
        w.writeFloat(1.5)
        w.writeInt64(-1)

        let bytes = w.buffer
        // Magic LE = 43 51 45 4C.
        XCTAssertEqual(Array(bytes[0..<4]), [0x43, 0x51, 0x45, 0x4C])
        // Version LE = 01 00.
        XCTAssertEqual(Array(bytes[4..<6]), [0x01, 0x00])
        // String: 0x02, 'h'(0x68), 'i'(0x69).
        XCTAssertEqual(Array(bytes[6..<9]), [0x02, 0x68, 0x69])
        // Float 1.5 = 0x3FC00000 LE = 00 00 C0 3F.
        XCTAssertEqual(Array(bytes[9..<13]), [0x00, 0x00, 0xC0, 0x3F])
        // Int64 -1 = 8x 0xFF.
        XCTAssertEqual(Array(bytes[13..<21]), [UInt8](repeating: 0xFF, count: 8))

        // Round-trip through the reader.
        let r = DotNetBinaryReader(bytes)
        XCTAssertEqual(try r.readInt32(), 0x4C455143)
        XCTAssertEqual(try r.readUInt16(), 1)
        XCTAssertEqual(try r.readString(), "hi")
        XCTAssertEqual(try r.readFloat(), 1.5)
        XCTAssertEqual(try r.readInt64(), -1)
    }

    func testBinaryWriter7BitLongString() throws {
        // A string ≥128 bytes forces a 2-byte 7-bit length prefix.
        let s = String(repeating: "a", count: 200)
        let w = DotNetBinaryWriter()
        w.writeString(s)
        // 200 = 0xC8 → 7-bit: 0xC8, 0x01.
        XCTAssertEqual(w.buffer[0], 0xC8)
        XCTAssertEqual(w.buffer[1], 0x01)
        let r = DotNetBinaryReader(w.buffer)
        XCTAssertEqual(try r.readString(), s)
    }

    // ── InMemoryEmbeddingStore ────────────────────────────────────────────

    func testStoreAddSearchRemove() async throws {
        let store = InMemoryEmbeddingStore(encoder: CharCodeEncoder(), bitsPerDim: 4)
        try await store.add(document: EmbeddingDocument(id: "a", text: "apple"))
        try await store.add(document: EmbeddingDocument(id: "b", text: "banana"))
        try await store.add(document: EmbeddingDocument(id: "c", text: "cherry"))
        let n = store.count
        XCTAssertEqual(n, 3)

        // Querying "apple" should rank "a" first (identical vector).
        let hits = try await store.search(queryText: "apple", topK: 3)
        XCTAssertEqual(hits.first?.document.id, "a")
        XCTAssert(hits.first!.score > 0.99)

        // Remove and confirm.
        let removed = try await store.remove(id: "b")
        XCTAssertTrue(removed)
        let removedAgain = try await store.remove(id: "b")
        XCTAssertFalse(removedAgain)
        let n2 = store.count
        XCTAssertEqual(n2, 2)
    }

    func testStoreAddWithExplicitVectorAndSearchByVector() async throws {
        let store = InMemoryEmbeddingStore(encoder: CharCodeEncoder(), bitsPerDim: 4)
        try await store.add(document: EmbeddingDocument(id: "x", text: "ignored"), vector: [1, 0, 0])
        try await store.add(document: EmbeddingDocument(id: "y", text: "ignored"), vector: [0, 1, 0])
        let hits = try await store.search(queryVector: [1, 0, 0], topK: 2)
        XCTAssertEqual(hits.first?.document.id, "x")
    }

    func testStoreRejectsWrongDimension() async {
        let store = InMemoryEmbeddingStore(encoder: CharCodeEncoder())
        do {
            try await store.add(document: EmbeddingDocument(id: "z", text: "t"), vector: [1, 2])
            XCTFail("expected dimension error")
        } catch {
            guard case .argument = (error as? ModelRuntimeError) else { return XCTFail("wrong error") }
        }
    }

    func testStoreSaveLoadRoundTrip() async throws {
        let path = tempPath("celq")
        defer { try? FileManager.default.removeItem(atPath: path) }

        let store = InMemoryEmbeddingStore(encoder: CharCodeEncoder(), bitsPerDim: 4)
        try await store.add(document: EmbeddingDocument(
            id: "doc-1", text: "hello world",
            metadata: ["lang": "en", "src": "test"]))
        try await store.add(document: EmbeddingDocument(id: "doc-2", text: "second"))
        try await store.save(path: path)
        XCTAssertTrue(FileManager.default.fileExists(atPath: path))

        // Fresh store loads the file and reproduces documents + metadata.
        let restored = InMemoryEmbeddingStore(encoder: CharCodeEncoder(), bitsPerDim: 4)
        try await restored.load(path: path)
        let n = restored.count
        XCTAssertEqual(n, 2)
        let hits = try await restored.search(queryText: "hello world", topK: 1)
        XCTAssertEqual(hits.first?.document.id, "doc-1")
        XCTAssertEqual(hits.first?.document.metadata?["lang"], "en")
        XCTAssertEqual(hits.first?.document.metadata?["src"], "test")
    }

    func testStoreFileBeginsWithMagic() async throws {
        let path = tempPath("celq")
        defer { try? FileManager.default.removeItem(atPath: path) }
        let store = InMemoryEmbeddingStore(encoder: CharCodeEncoder(), bitsPerDim: 4)
        try await store.add(document: EmbeddingDocument(id: "a", text: "x"))
        try await store.save(path: path)
        let data = Array(FileManager.default.contents(atPath: path)!)
        // "CELQ" little-endian = 43 51 45 4C, then version 01 00, then bits 04 00.
        XCTAssertEqual(Array(data[0..<4]), [0x43, 0x51, 0x45, 0x4C])
        XCTAssertEqual(Array(data[4..<6]), [0x01, 0x00])
        XCTAssertEqual(Array(data[6..<8]), [0x04, 0x00])
    }

    func testStoreLoadRejectsBitMismatch() async throws {
        let path = tempPath("celq")
        defer { try? FileManager.default.removeItem(atPath: path) }
        let store = InMemoryEmbeddingStore(encoder: CharCodeEncoder(), bitsPerDim: 4)
        try await store.add(document: EmbeddingDocument(id: "a", text: "x"))
        try await store.save(path: path)

        let mismatched = InMemoryEmbeddingStore(encoder: CharCodeEncoder(), bitsPerDim: 2)
        do {
            try await mismatched.load(path: path)
            XCTFail("expected invalidData")
        } catch {
            guard case .invalidData = (error as? ModelRuntimeError) else { return XCTFail("wrong error") }
        }
    }

    func testStoreLoadMissingFileThrows() async {
        let store = InMemoryEmbeddingStore(encoder: CharCodeEncoder())
        do {
            try await store.load(path: tempPath("celq"))
            XCTFail("expected fileNotFound")
        } catch {
            guard case .fileNotFound = (error as? ModelRuntimeError) else { return XCTFail("wrong error") }
        }
    }

    func testStoreDisposedThrows() async {
        let store = InMemoryEmbeddingStore(encoder: CharCodeEncoder())
        await store.dispose()
        do {
            try await store.add(document: EmbeddingDocument(id: "a", text: "x"), vector: [1, 0, 0])
            XCTFail("expected objectDisposed")
        } catch {
            guard case .objectDisposed = (error as? ModelRuntimeError) else { return XCTFail("wrong error") }
        }
    }

    // ── InMemoryEmbeddingIndex ────────────────────────────────────────────

    func testIndexAddAssignsInsertionIds() async throws {
        let index = InMemoryEmbeddingIndex(dimension: 3)
        let id0 = try await index.add(vector: [1, 0, 0])
        let id1 = try await index.add(vector: [0, 1, 0])
        let id2 = try await index.add(vector: [0, 0, 1])
        XCTAssertEqual([id0, id1, id2], [0, 1, 2])
        let c = index.count
        XCTAssertEqual(c, 3)
    }

    func testIndexSearchRanksNearest() async throws {
        let index = InMemoryEmbeddingIndex(dimension: 3)
        _ = try await index.add(vector: [1, 0, 0])   // id 0
        _ = try await index.add(vector: [0, 1, 0])   // id 1
        _ = try await index.add(vector: [0.9, 0.1, 0]) // id 2 (close to query)
        let hits = try await index.search(queryVector: [1, 0, 0], topK: 2)
        XCTAssertEqual(hits.count, 2)
        XCTAssertEqual(hits[0].internalId, 0)  // exact match first
        XCTAssertEqual(hits[1].internalId, 2)  // near match second
        XCTAssert(hits[0].score >= hits[1].score)
    }

    func testIndexRejectsWrongDim() async {
        let index = InMemoryEmbeddingIndex(dimension: 3)
        do {
            _ = try await index.add(vector: [1, 2])
            XCTFail("expected argument error")
        } catch {
            guard case .argument = (error as? ModelRuntimeError) else { return XCTFail("wrong error") }
        }
    }

    func testIndexSaveLoadRoundTrip() async throws {
        let path = tempPath("ceix")
        defer { try? FileManager.default.removeItem(atPath: path) }

        let index = InMemoryEmbeddingIndex(dimension: 3)
        _ = try await index.add(vector: [1, 0, 0])
        _ = try await index.add(vector: [0.25, 0.5, 0.75])
        try await index.save(path: path)

        let restored = InMemoryEmbeddingIndex(dimension: 3)
        try await restored.load(path: path)
        let c = restored.count
        XCTAssertEqual(c, 2)
        let hits = try await restored.search(queryVector: [0.25, 0.5, 0.75], topK: 1)
        XCTAssertEqual(hits.first?.internalId, 1)
    }

    func testIndexLoadRejectsDimMismatch() async throws {
        let path = tempPath("ceix")
        defer { try? FileManager.default.removeItem(atPath: path) }
        let index = InMemoryEmbeddingIndex(dimension: 3)
        _ = try await index.add(vector: [1, 0, 0])
        try await index.save(path: path)

        let wrong = InMemoryEmbeddingIndex(dimension: 4)
        do {
            try await wrong.load(path: path)
            XCTFail("expected invalidData")
        } catch {
            guard case .invalidData = (error as? ModelRuntimeError) else { return XCTFail("wrong error") }
        }
    }
}
