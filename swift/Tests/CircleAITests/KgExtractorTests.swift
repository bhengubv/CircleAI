// KgExtractorTests.swift
// Verifies HeuristicKnowledgeGraphExtractor: bidirectional mentions/seenin
// triples, stop/short-word filtering, dedup, memory-id fallback.

import XCTest
@testable import CircleAI

final class KgExtractorTests: XCTestCase {
    private let ex = HeuristicKnowledgeGraphExtractor()

    func testTwoWayLinks() async throws {
        let triples = try await ex.extractFromTurn(userText: "Durban weather is sunny", assistantText: "", sourceEpisodeId: "ep1")
        XCTAssertEqual(triples.count, 6) // durban, weather, sunny × 2
        func has(_ s: String, _ p: String, _ o: String) -> Bool {
            triples.contains { $0.subject == s && $0.predicate == p && $0.object == o }
        }
        XCTAssertTrue(has("ep1", "mentions", "durban"))
        XCTAssertTrue(has("durban", "seenin", "ep1"))
        XCTAssertTrue(has("ep1", "mentions", "weather"))
        XCTAssertTrue(has("ep1", "mentions", "sunny"))
    }

    func testDropsStopAndShort() async throws {
        let triples = try await ex.extractFromTurn(userText: "I am at the shop", assistantText: "", sourceEpisodeId: "ep2")
        let objects = triples.filter { $0.predicate == "mentions" }.map { $0.object }
        XCTAssertEqual(objects, ["shop"])
    }

    func testDedup() async throws {
        let triples = try await ex.extractFromTurn(userText: "test test test", assistantText: "", sourceEpisodeId: "ep3")
        XCTAssertEqual(triples.count, 2)
    }

    func testAssistantWords() async throws {
        let triples = try await ex.extractFromTurn(userText: "tell me about", assistantText: "Johannesburg traffic", sourceEpisodeId: "ep4")
        let objects = triples.filter { $0.predicate == "mentions" }.map { $0.object }.sorted()
        XCTAssertEqual(objects, ["johannesburg", "tell", "traffic"])
    }

    func testMemoryIdFallback() async throws {
        let triples = try await ex.extractFromTurn(userText: "hello world", assistantText: "", sourceEpisodeId: nil)
        XCTAssertTrue(triples.contains { $0.subject == "hello world" && $0.predicate == "mentions" })
    }

    func testEmptyTurn() async throws {
        let triples = try await ex.extractFromTurn(userText: "", assistantText: "", sourceEpisodeId: nil)
        XCTAssertEqual(triples.count, 0)
    }

    func testSourceAndConfidence() async throws {
        let triples = try await ex.extractFromTurn(userText: "coffee", assistantText: "", sourceEpisodeId: "ep5")
        XCTAssertGreaterThan(triples.count, 0)
        for t in triples {
            XCTAssertEqual(t.source, "ep5")
            XCTAssertEqual(t.confidence, 0.6, accuracy: 1e-6)
        }
    }
}
