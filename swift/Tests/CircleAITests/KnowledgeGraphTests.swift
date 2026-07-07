// KnowledgeGraphTests.swift
// Verifies InMemoryKnowledgeGraph + InMemoryHippoRagStore (Personalised PageRank
// multi-hop recall), including the three precision guarantees.

import XCTest
@testable import CircleAI

final class KnowledgeGraphTests: XCTestCase {

    func testStoresTriples() {
        let kg = InMemoryKnowledgeGraph()
        kg.addTriple(subject: "a", predicate: "rel", object: "b", source: "ep1", confidence: 1.0)
        let all = kg.allTriples()
        XCTAssertEqual(all.count, 1)
        XCTAssertEqual(all[0].subject, "a")
        XCTAssertEqual(all[0].object, "b")
    }

    func testReplaceSameTriple() {
        let kg = InMemoryKnowledgeGraph()
        kg.addTriple(subject: "a", predicate: "rel", object: "b", source: "ep1", confidence: 0.5)
        kg.addTriple(subject: "a", predicate: "rel", object: "b", source: "ep2", confidence: 0.9)
        let all = kg.allTriples()
        XCTAssertEqual(all.count, 1)
        XCTAssertEqual(all[0].confidence, 0.9, accuracy: 1e-6)
        XCTAssertEqual(all[0].source, "ep2")
    }

    func testUpsertNode() {
        let kg = InMemoryKnowledgeGraph()
        kg.upsertNode(KnowledgeNode(id: "heart", kind: "organ", name: "the heart"))
        XCTAssertEqual(kg.getNode("heart")?.name, "the heart")
        XCTAssertNil(kg.getNode("missing"))
    }

    func testMultiHopReachesAssociatedExcludesSeed() async throws {
        let kg = InMemoryKnowledgeGraph()
        kg.addTriple(subject: "chest", predicate: "relates", object: "heart", source: "ep1", confidence: 1.0)
        kg.addTriple(subject: "heart", predicate: "relates", object: "father_cardiac_event", source: "ep2", confidence: 1.0)
        let hippo = InMemoryHippoRagStore(kg)
        let hits = try await hippo.multiHopRecall(query: "chest tightness", topK: 5)
        let ids = hits.map { $0.item.id }
        XCTAssertFalse(ids.contains("chest"))
        XCTAssertTrue(ids.contains("heart"))
        XCTAssertTrue(ids.contains("father_cardiac_event"))
        let heart = hits.first { $0.item.id == "heart" }!
        let father = hits.first { $0.item.id == "father_cardiac_event" }!
        XCTAssertGreaterThanOrEqual(heart.score, father.score)
    }

    func testNoSeedReturnsEmpty() async throws {
        let kg = InMemoryKnowledgeGraph()
        kg.addTriple(subject: "chest", predicate: "relates", object: "heart", source: "ep1", confidence: 1.0)
        let hippo = InMemoryHippoRagStore(kg)
        let hits = try await hippo.multiHopRecall(query: "banana apple", topK: 5)
        XCTAssertEqual(hits.count, 0)
    }

    func testEmptyGraph() async throws {
        let hippo = InMemoryHippoRagStore(InMemoryKnowledgeGraph())
        let hits = try await hippo.multiHopRecall(query: "anything", topK: 5)
        XCTAssertEqual(hits.count, 0)
    }

    func testConfidenceWeighting() async throws {
        let kg = InMemoryKnowledgeGraph()
        kg.addTriple(subject: "root", predicate: "r", object: "alpha", source: "ep1", confidence: 1.0)
        kg.addTriple(subject: "root", predicate: "r", object: "beta", source: "ep2", confidence: 0.1)
        let hippo = InMemoryHippoRagStore(kg)
        let hits = try await hippo.multiHopRecall(query: "root", topK: 5)
        XCTAssertFalse(hits.map { $0.item.id }.contains("root"))
        XCTAssertEqual(hits[0].item.id, "alpha")
        XCTAssertEqual(hits[1].item.id, "beta")
        XCTAssertGreaterThan(hits[0].score, hits[1].score)
    }

    func testNodeNameAsText() async throws {
        let kg = InMemoryKnowledgeGraph()
        kg.addTriple(subject: "chest", predicate: "relates", object: "heart", source: "ep1", confidence: 1.0)
        kg.upsertNode(KnowledgeNode(id: "heart", kind: "organ", name: "the heart"))
        let hippo = InMemoryHippoRagStore(kg)
        let hits = try await hippo.multiHopRecall(query: "chest", topK: 5)
        let heart = hits.first { $0.item.id == "heart" }!
        XCTAssertEqual(heart.item.text, "the heart")
    }

    func testIndexRegistersTriples() async throws {
        let kg = InMemoryKnowledgeGraph()
        let hippo = InMemoryHippoRagStore(kg)
        try await hippo.index(MemoryItem(id: "note1", text: "durban weather", metadata: ["topic": "durban"]))
        let preds = Set(kg.readTriples(subject: "note1").map { $0.predicate })
        XCTAssertEqual(preds, ["memory_text", "topic"])
    }

    func testReverseEdgeRecall() async throws {
        let kg = InMemoryKnowledgeGraph()
        kg.addTriple(subject: "durban", predicate: "seenin", object: "note1", source: "ep1", confidence: 1.0)
        kg.upsertNode(KnowledgeNode(id: "note1", kind: "memory", name: "durban weather"))
        let hippo = InMemoryHippoRagStore(kg)
        let hits = try await hippo.multiHopRecall(query: "durban", topK: 5)
        let ids = hits.map { $0.item.id }
        XCTAssertFalse(ids.contains("durban"))
        XCTAssertTrue(ids.contains("note1"))
        XCTAssertEqual(hits.first { $0.item.id == "note1" }?.item.text, "durban weather")
    }
}
