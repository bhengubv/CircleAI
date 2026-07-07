// EpisodicStoreTests.swift
// Verifies InMemoryEpisodicStore: cosine search, recency fallback, FIFO cap, prune.

import XCTest
@testable import CircleAI

final class EpisodicStoreTests: XCTestCase {

    private func mk(_ userText: String, embedding: [Float]? = nil,
                    at: Date = Date(timeIntervalSince1970: 1_767_225_600)) -> EpisodicMemoryEntry {
        EpisodicMemoryEntry(recordedAt: at, userText: userText, assistantText: "a", embedding: embedding)
    }

    func testCosineNearestFirst() async throws {
        let store = InMemoryEpisodicStore()
        try await store.add(mk("x-axis", embedding: [1, 0]))
        try await store.add(mk("y-axis", embedding: [0, 1]))
        let hits = try await store.search(queryEmbedding: [1, 0], topK: 2)
        XCTAssertEqual(hits.count, 2)
        XCTAssertEqual(hits[0].userText, "x-axis")
        XCTAssertEqual(hits[1].userText, "y-axis")
    }

    func testTopK() async throws {
        let store = InMemoryEpisodicStore()
        try await store.add(mk("a", embedding: [1, 0]))
        try await store.add(mk("b", embedding: [0.9, 0.1]))
        try await store.add(mk("c", embedding: [0, 1]))
        let hits = try await store.search(queryEmbedding: [1, 0], topK: 1)
        XCTAssertEqual(hits.count, 1)
        XCTAssertEqual(hits[0].userText, "a")
    }

    func testIgnoresWrongDimension() async throws {
        let store = InMemoryEpisodicStore()
        try await store.add(mk("ok", embedding: [1, 0]))
        try await store.add(mk("wrongdim", embedding: [1, 0, 0]))
        let hits = try await store.search(queryEmbedding: [1, 0], topK: 5)
        XCTAssertEqual(hits.count, 1)
        XCTAssertEqual(hits[0].userText, "ok")
    }

    func testRecencyFallback() async throws {
        let store = InMemoryEpisodicStore()
        try await store.add(mk("old", at: Date(timeIntervalSince1970: 1_000_000)))
        try await store.add(mk("new", at: Date(timeIntervalSince1970: 2_000_000)))
        let hits = try await store.search(queryEmbedding: nil, topK: 5)
        XCTAssertEqual(hits[0].userText, "new")
        XCTAssertEqual(hits[1].userText, "old")
    }

    func testEmptyEmbeddingIsRecency() async throws {
        let store = InMemoryEpisodicStore()
        try await store.add(mk("old", at: Date(timeIntervalSince1970: 1_000_000)))
        try await store.add(mk("new", at: Date(timeIntervalSince1970: 2_000_000)))
        let hits = try await store.search(queryEmbedding: [], topK: 1)
        XCTAssertEqual(hits[0].userText, "new")
    }

    func testFifoEviction() async throws {
        let store = InMemoryEpisodicStore(maxEntries: 2)
        try await store.add(mk("a"))
        try await store.add(mk("b"))
        try await store.add(mk("c"))
        let n = try await store.count()
        XCTAssertEqual(n, 2)
        let recent = try await store.getRecent(count: 10)
        XCTAssertEqual(Set(recent.map { $0.userText }), ["b", "c"]) // 'a' evicted
    }

    func testPrune() async throws {
        let store = InMemoryEpisodicStore()
        try await store.add(mk("old", at: Date(timeIntervalSince1970: 1_000_000)))
        try await store.add(mk("new", at: Date(timeIntervalSince1970: 2_000_000)))
        let removed = try await store.pruneOlderThan(cutoff: Date(timeIntervalSince1970: 1_500_000))
        XCTAssertEqual(removed, 1)
        let n = try await store.count()
        XCTAssertEqual(n, 1)
    }
}
