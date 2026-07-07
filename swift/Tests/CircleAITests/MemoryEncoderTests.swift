// MemoryEncoderTests.swift
// Verifies CompanionMemoryEncoder end-to-end: a turn fills the graph so recall
// can reach the episode; attributed beliefs form off the hot path (a third
// party's fact never becomes the user's); the queue drops rather than blocks
// when full; an extractor failure is captured, not fatal.

import XCTest
@testable import CircleAI

final class MemoryEncoderTests: XCTestCase {

    struct ThrowingExtractor: IKnowledgeGraphExtractor {
        func extractFromTurn(userText: String, assistantText: String, sourceEpisodeId: String?) async throws -> [KnowledgeTriple] {
            throw BrainError.invalidArgument("boom")
        }
    }

    func testEndToEndRecall() async throws {
        let graph = InMemoryKnowledgeGraph()
        let enc = CompanionMemoryEncoder(extractor: HeuristicKnowledgeGraphExtractor(), graph: graph)
        enc.enqueue(userText: "I love hiking in Drakensberg", assistantText: "Sounds wonderful", episodeId: "ep-hike")
        await enc.close()

        XCTAssertGreaterThan(graph.allTriples().count, 0)
        let hippo = InMemoryHippoRagStore(graph)
        let hits = try await hippo.multiHopRecall(query: "drakensberg", topK: 5)
        let episode = hits.first { $0.item.id == "ep-hike" }
        XCTAssertNotNil(episode)
        XCTAssertEqual(episode?.item.text, "I love hiking in Drakensberg")
    }

    func testIntegrityThroughEncoder() async throws {
        let graph = InMemoryKnowledgeGraph()
        let beliefs = SelfBeliefStore()
        let enc = CompanionMemoryEncoder(extractor: HeuristicKnowledgeGraphExtractor(), graph: graph,
                                         beliefExtractor: HeuristicBeliefExtractor(), beliefs: beliefs)
        enc.enqueue(userText: "my mother is diabetic", assistantText: "Noted", episodeId: "ep1")
        enc.enqueue(userText: "i am vegetarian", assistantText: "Got it", episodeId: "ep2")
        await enc.close()

        let facts = beliefs.selfFacts()
        XCTAssertFalse(facts.contains { $0.object.contains("diabetic") })
        XCTAssertTrue(facts.contains { $0.object == "vegetarian" })
        XCTAssertTrue(beliefs.nonSelf().contains { $0.object == "diabetic" })
    }

    func testDropWrite() async throws {
        let graph = InMemoryKnowledgeGraph()
        let enc = CompanionMemoryEncoder(extractor: HeuristicKnowledgeGraphExtractor(), graph: graph, capacity: 2)
        enc.enqueue(userText: "alpha", assistantText: "", episodeId: "e1")
        enc.enqueue(userText: "bravo", assistantText: "", episodeId: "e2")
        enc.enqueue(userText: "charlie", assistantText: "", episodeId: "e3") // overflow → dropped
        await enc.close()

        XCTAssertNotNil(graph.getNode("e1"))
        XCTAssertNotNil(graph.getNode("e2"))
        XCTAssertNil(graph.getNode("e3"))
    }

    func testIgnoresBlankId() async throws {
        let graph = InMemoryKnowledgeGraph()
        let enc = CompanionMemoryEncoder(extractor: HeuristicKnowledgeGraphExtractor(), graph: graph)
        enc.enqueue(userText: "hello", assistantText: "", episodeId: "")
        enc.enqueue(userText: "hello", assistantText: "", episodeId: "   ")
        await enc.close()
        XCTAssertEqual(graph.allTriples().count, 0)
    }

    func testCapturesError() async throws {
        let graph = InMemoryKnowledgeGraph()
        let enc = CompanionMemoryEncoder(extractor: ThrowingExtractor(), graph: graph)
        enc.enqueue(userText: "x", assistantText: "", episodeId: "e1")
        await enc.close()
        XCTAssertNotNil(enc.lastError)
        XCTAssertNotNil(graph.getNode("e1")) // node upserted before the extractor ran
    }
}
