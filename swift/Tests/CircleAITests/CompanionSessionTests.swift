// CompanionSessionTests.swift
// Verifies the concrete CompanionSession: a turn recalls fused memory + the
// user's own facts into the system prompt, calls the generator, persists the
// exchange, hands it to the encoder, recalls it on a later turn, and streams.

import XCTest
@testable import CircleAI

final class CompanionSessionTests: XCTestCase {

    final class CapturingGenerator: IChatGenerator, @unchecked Sendable {
        var lastMessages: [ChatMessage] = []
        let reply: String
        let chunks: [String]?
        init(_ reply: String, chunks: [String]? = nil) { self.reply = reply; self.chunks = chunks }

        func generate(messages: [ChatMessage], options: GenerationOptions?) async throws -> String {
            lastMessages = messages
            return reply
        }
        func stream(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<String> {
            lastMessages = messages
            let parts = chunks ?? [reply]
            return AsyncStream { cont in
                for p in parts { cont.yield(p) }
                cont.finish()
            }
        }
    }

    private func recordSelfFact(_ store: SelfBeliefStore, _ text: String) async throws {
        let bx = HeuristicBeliefExtractor()
        for b in try await bx.extract(text: text, source: "t0") { store.record(b) }
    }

    private func makeSession(_ gen: IChatGenerator, _ episodic: InMemoryEpisodicStore,
                             beliefs: SelfBeliefStore? = nil, encoder: CompanionMemoryEncoder? = nil) -> CompanionSession {
        let recall = FusedRecall(episodic: episodic, graph: nil)
        return CompanionSession(generator: gen, episodic: episodic, recall: recall,
                                options: CompanionSessionOptions(sessionId: "s1", identityId: "u1", interface: .mobile),
                                encoder: encoder, beliefs: beliefs)
    }

    func testInjectsMemoriesAndFacts() async throws {
        let episodic = InMemoryEpisodicStore()
        try await episodic.add(EpisodicMemoryEntry(userText: "I have a peanut allergy", assistantText: "Noted"))
        let beliefs = SelfBeliefStore()
        try await recordSelfFact(beliefs, "i am vegetarian")

        let gen = CapturingGenerator("Here are some options")
        let session = makeSession(gen, episodic, beliefs: beliefs)

        let reply = try await session.send("what can I eat?")
        XCTAssertEqual(reply, "Here are some options")

        let system = gen.lastMessages[0]
        XCTAssertEqual(system.role, "system")
        XCTAssertTrue(system.content.contains("peanut allergy"), "recalled memory should be in the prompt")
        XCTAssertTrue(system.content.contains("vegetarian"), "user fact should be in the prompt")
        XCTAssertEqual(gen.lastMessages.last?.content, "what can I eat?")
    }

    func testPersistsAndHistory() async throws {
        let episodic = InMemoryEpisodicStore()
        let session = makeSession(CapturingGenerator("ok"), episodic)
        _ = try await session.send("hello")
        let n = try await episodic.count()
        XCTAssertEqual(n, 1)
        XCTAssertEqual(session.history.count, 2)
        XCTAssertEqual(session.history[0].role, "user")
        XCTAssertEqual(session.history[1].role, "assistant")
    }

    func testRecallsPriorTurn() async throws {
        let episodic = InMemoryEpisodicStore()
        let gen = CapturingGenerator("noted")
        let session = makeSession(gen, episodic)
        _ = try await session.send("my favourite colour is blue")
        _ = try await session.send("what's my favourite colour?")
        XCTAssertTrue(gen.lastMessages[0].content.contains("favourite colour is blue"))
    }

    func testHandsToEncoder() async throws {
        let episodic = InMemoryEpisodicStore()
        let graph = InMemoryKnowledgeGraph()
        let encoder = CompanionMemoryEncoder(extractor: HeuristicKnowledgeGraphExtractor(), graph: graph)
        let session = makeSession(CapturingGenerator("ok"), episodic, encoder: encoder)
        _ = try await session.send("remember my dentist appointment")
        await encoder.close()
        XCTAssertTrue(graph.allTriples().contains { $0.object == "dentist" })
    }

    func testStreams() async throws {
        let episodic = InMemoryEpisodicStore()
        let gen = CapturingGenerator("unused", chunks: ["Hel", "lo"])
        let session = makeSession(gen, episodic)
        var chunks: [String] = []
        for await c in session.stream("hi") { chunks.append(c) }
        XCTAssertEqual(chunks, ["Hel", "lo"])
        let n = try await episodic.count()
        XCTAssertEqual(n, 1)
        XCTAssertEqual(session.history[1].content, "Hello")
    }

    func testContextReflectsRecall() async throws {
        let episodic = InMemoryEpisodicStore()
        try await episodic.add(EpisodicMemoryEntry(userText: "I live in Durban", assistantText: "Nice"))
        let session = makeSession(CapturingGenerator("ok"), episodic)
        _ = try await session.send("where do I live?")
        XCTAssertTrue(session.getContext().recentMemorySnippets.contains("I live in Durban"))
    }

    func testAgentPersists() async throws {
        let episodic = InMemoryEpisodicStore()
        let session = makeSession(CapturingGenerator("done"), episodic)
        let reply = try await session.agent("do the thing")
        XCTAssertEqual(reply, "done")
        let n = try await episodic.count()
        XCTAssertEqual(n, 1)
    }
}
