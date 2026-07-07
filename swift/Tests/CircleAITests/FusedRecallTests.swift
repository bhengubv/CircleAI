// FusedRecallTests.swift
// Verifies FusedRecall: RRF ordering, cross-source reinforcement, cold-start,
// confidence gate, empty-query skip, graceful degradation, and dedup.

import XCTest
@testable import CircleAI

final class FusedRecallTests: XCTestCase {

    final class FakeEpisodic: IEpisodicMemoryStore, @unchecked Sendable {
        let hits: [EpisodicMemoryEntry]
        init(_ hits: [EpisodicMemoryEntry]) { self.hits = hits }
        func add(_ entry: EpisodicMemoryEntry) async throws {}
        func search(queryEmbedding: [Float]?, topK: Int) async throws -> [EpisodicMemoryEntry] { Array(hits.prefix(topK)) }
        func getRecent(count: Int) async throws -> [EpisodicMemoryEntry] { Array(hits.prefix(count)) }
        func count() async throws -> Int { hits.count }
        func pruneOlderThan(cutoff: Date) async throws -> Int { 0 }
    }

    final class FakeHippo: IHippoRagStore, @unchecked Sendable {
        let backendId = "fake"
        let hits: [MemoryHit]
        init(_ hits: [MemoryHit]) { self.hits = hits }
        func index(_ item: MemoryItem) async throws {}
        func multiHopRecall(query: String, topK: Int) async throws -> [MemoryHit] { Array(hits.prefix(topK)) }
    }

    final class ThrowingHippo: IHippoRagStore, @unchecked Sendable {
        let backendId = "boom"
        func index(_ item: MemoryItem) async throws {}
        func multiHopRecall(query: String, topK: Int) async throws -> [MemoryHit] {
            throw BrainError.invalidArgument("graph unavailable")
        }
    }

    private func makeEp(_ text: String) -> EpisodicMemoryEntry {
        EpisodicMemoryEntry(userText: text, assistantText: "")
    }
    private func graphHit(_ text: String, confidence: String? = nil) -> MemoryHit {
        let md: [String: String]? = confidence.map { ["confidence": $0] }
        return MemoryHit(item: MemoryItem(id: text, text: text, metadata: md), score: 1)
    }

    func testReinforcement() async throws {
        let episodic = FakeEpisodic([makeEp("A"), makeEp("B"), makeEp("C")])
        let graph = FakeHippo([graphHit("B")])
        let recall = FusedRecall(episodic: episodic, graph: graph)
        let hits = try await recall.recall(query: "q", queryEmbedding: nil, topK: 5)
        XCTAssertEqual(hits.map { $0.item.text }, ["B", "A", "C"])
    }

    func testColdStart() async throws {
        let episodic = FakeEpisodic([makeEp("A"), makeEp("B"), makeEp("C")])
        let recall = FusedRecall(episodic: episodic, graph: nil)
        let hits = try await recall.recall(query: "q", queryEmbedding: nil, topK: 5)
        XCTAssertEqual(hits.map { $0.item.text }, ["A", "B", "C"])
    }

    func testTopK() async throws {
        let episodic = FakeEpisodic([makeEp("A"), makeEp("B"), makeEp("C")])
        let recall = FusedRecall(episodic: episodic, graph: nil)
        let hits = try await recall.recall(query: "q", queryEmbedding: nil, topK: 2)
        XCTAssertEqual(hits.map { $0.item.text }, ["A", "B"])
    }

    func testConfidenceGate() async throws {
        let episodic = FakeEpisodic([])
        let graph = FakeHippo([graphHit("LOW", confidence: "0.2"), graphHit("HIGH", confidence: "0.9")])
        let recall = FusedRecall(episodic: episodic, graph: graph)
        let hits = try await recall.recall(query: "q", queryEmbedding: nil, topK: 5)
        let texts = hits.map { $0.item.text }
        XCTAssertFalse(texts.contains("LOW"))
        XCTAssertTrue(texts.contains("HIGH"))
    }

    func testNoConfidenceKept() async throws {
        let episodic = FakeEpisodic([])
        let graph = FakeHippo([graphHit("NOCONF")])
        let recall = FusedRecall(episodic: episodic, graph: graph)
        let hits = try await recall.recall(query: "q", queryEmbedding: nil, topK: 5)
        XCTAssertEqual(hits.map { $0.item.text }, ["NOCONF"])
    }

    func testEmptyQuerySkipsGraph() async throws {
        let episodic = FakeEpisodic([makeEp("A")])
        let graph = FakeHippo([graphHit("GRAPH")])
        let recall = FusedRecall(episodic: episodic, graph: graph)
        let hits = try await recall.recall(query: "   ", queryEmbedding: nil, topK: 5)
        let texts = hits.map { $0.item.text }
        XCTAssertEqual(texts, ["A"])
        XCTAssertFalse(texts.contains("GRAPH"))
    }

    func testGraphThrowsDegrades() async throws {
        let episodic = FakeEpisodic([makeEp("A")])
        let recall = FusedRecall(episodic: episodic, graph: ThrowingHippo())
        let hits = try await recall.recall(query: "q", queryEmbedding: nil, topK: 5)
        XCTAssertEqual(hits.map { $0.item.text }, ["A"])
    }

    func testDedup() async throws {
        let episodic = FakeEpisodic([makeEp("Durban  Weather")])
        let graph = FakeHippo([graphHit("durban weather")])
        let recall = FusedRecall(episodic: episodic, graph: graph)
        let hits = try await recall.recall(query: "q", queryEmbedding: nil, topK: 5)
        XCTAssertEqual(hits.count, 1)
    }
}
