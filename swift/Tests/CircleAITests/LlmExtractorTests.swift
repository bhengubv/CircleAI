// LlmExtractorTests.swift
//
// Verifies LlmKnowledgeGraphExtractor: parses a clean JSON array of triples,
// tolerates prose/markdown-fence-wrapped JSON, defaults confidence when "c" is
// missing/invalid, clamps out-of-range confidence, skips objects with blank
// s/p/o, and returns [] on garbage / on an empty turn / on a failing generator.
// Mirrors the verified TS suite (llm_extractor.test.ts).

import XCTest
@testable import CircleAI

final class LlmExtractorTests: XCTestCase {

    /// Minimal fake IChatGenerator that returns a canned reply and records the messages.
    final class FakeChatGenerator: IChatGenerator, @unchecked Sendable {
        var lastMessages: [ChatMessage] = []
        private let reply: String
        init(_ reply: String) { self.reply = reply }

        func generate(messages: [ChatMessage], options: GenerationOptions?) async throws -> String {
            lastMessages = messages
            return reply
        }
        func stream(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<String> {
            lastMessages = messages
            let r = reply
            return AsyncStream { cont in cont.yield(r); cont.finish() }
        }
    }

    /// A generator that always throws — exercises the graceful-degradation path.
    final class ThrowingChatGenerator: IChatGenerator, @unchecked Sendable {
        struct Offline: Error {}
        func generate(messages: [ChatMessage], options: GenerationOptions?) async throws -> String {
            throw Offline()
        }
        func stream(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<String> {
            AsyncStream { $0.finish() }
        }
    }

    // ── clean JSON ────────────────────────────────────────────────────────────

    func testParsesPlainJsonArray() async throws {
        let gen = FakeChatGenerator(
            "[{\"s\":\"Tony\",\"p\":\"has_daughter\",\"o\":\"Alex\",\"c\":0.9}," +
            "{\"s\":\"Alex\",\"p\":\"lives_in\",\"o\":\"Durban\",\"c\":0.5}]")
        let ex = LlmKnowledgeGraphExtractor(gen)
        let triples = try await ex.extractFromTurn(userText: "hi", assistantText: "ok", sourceEpisodeId: "ep1")

        XCTAssertEqual(triples.count, 2)
        XCTAssertEqual(triples[0].subject, "Tony")
        XCTAssertEqual(triples[0].predicate, "has_daughter")
        XCTAssertEqual(triples[0].object, "Alex")
        XCTAssertEqual(triples[0].confidence, 0.9, accuracy: 1e-6)
        XCTAssertEqual(triples[0].source, "ep1")
        XCTAssertEqual(triples[1].object, "Durban")
        XCTAssertEqual(triples[1].confidence, 0.5, accuracy: 1e-6)
    }

    func testSendsVerbatimSystemPromptAndFramedUserMessage() async throws {
        let gen = FakeChatGenerator("[]")
        let ex = LlmKnowledgeGraphExtractor(gen)
        _ = try await ex.extractFromTurn(userText: "the weather", assistantText: "is sunny", sourceEpisodeId: "ep1")

        XCTAssertEqual(gen.lastMessages.count, 2)
        XCTAssertEqual(gen.lastMessages[0].role, "system")
        XCTAssertTrue(gen.lastMessages[0].content.hasPrefix("You are a knowledge-graph extractor."))
        XCTAssertEqual(gen.lastMessages[1].role, "user")
        XCTAssertEqual(gen.lastMessages[1].content, "USER:\nthe weather\nASSISTANT:\nis sunny\n")
    }

    // ── defensive parsing ───────────────────────────────────────────────────────

    func testExtractsJsonEmbeddedInProse() async throws {
        let gen = FakeChatGenerator(
            "Sure! Here are the triples:\n```json\n[{\"s\":\"Paris\",\"p\":\"capital_of\",\"o\":\"France\",\"c\":0.95}]\n```\nHope that helps.")
        let ex = LlmKnowledgeGraphExtractor(gen)
        let triples = try await ex.extractFromTurn(userText: "u", assistantText: "a", sourceEpisodeId: "ep2")

        XCTAssertEqual(triples.count, 1)
        XCTAssertEqual(triples[0].subject, "Paris")
        XCTAssertEqual(triples[0].predicate, "capital_of")
        XCTAssertEqual(triples[0].object, "France")
        XCTAssertEqual(triples[0].confidence, 0.95, accuracy: 1e-6)
    }

    func testDefaultsConfidenceWhenMissing() async throws {
        let gen = FakeChatGenerator("[{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\"}]")
        let ex = LlmKnowledgeGraphExtractor(gen)
        let triples = try await ex.extractFromTurn(userText: "u", assistantText: "a", sourceEpisodeId: "ep3")
        XCTAssertEqual(triples.count, 1)
        XCTAssertEqual(triples[0].confidence, 0.75, accuracy: 1e-6)
    }

    func testDefaultsConfidenceWhenNonNumeric() async throws {
        let gen = FakeChatGenerator("[{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\",\"c\":\"high\"}]")
        let ex = LlmKnowledgeGraphExtractor(gen)
        let triples = try await ex.extractFromTurn(userText: "u", assistantText: "a", sourceEpisodeId: "ep3")
        XCTAssertEqual(triples[0].confidence, 0.75, accuracy: 1e-6)
    }

    func testDefaultsConfidenceWhenBoolean() async throws {
        // JSON booleans must NOT count as numbers → default confidence applies.
        let gen = FakeChatGenerator("[{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\",\"c\":true}]")
        let ex = LlmKnowledgeGraphExtractor(gen)
        let triples = try await ex.extractFromTurn(userText: "u", assistantText: "a", sourceEpisodeId: "ep3")
        XCTAssertEqual(triples.count, 1)
        XCTAssertEqual(triples[0].confidence, 0.75, accuracy: 1e-6)
    }

    func testClampsConfidence() async throws {
        let gen = FakeChatGenerator(
            "[{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\",\"c\":5},{\"s\":\"d\",\"p\":\"e\",\"o\":\"f\",\"c\":-2}]")
        let ex = LlmKnowledgeGraphExtractor(gen)
        let triples = try await ex.extractFromTurn(userText: "u", assistantText: "a", sourceEpisodeId: "ep3")
        XCTAssertEqual(triples[0].confidence, 1, accuracy: 1e-6)
        XCTAssertEqual(triples[1].confidence, 0, accuracy: 1e-6)
    }

    func testSkipsBlankOrMissingSpo() async throws {
        let gen = FakeChatGenerator(
            "[{\"s\":\"\",\"p\":\"b\",\"o\":\"c\"},{\"s\":\"a\",\"p\":\"  \",\"o\":\"c\"},{\"s\":\"a\",\"p\":\"b\"},{\"s\":\"keep\",\"p\":\"p\",\"o\":\"o\"}]")
        let ex = LlmKnowledgeGraphExtractor(gen)
        let triples = try await ex.extractFromTurn(userText: "u", assistantText: "a", sourceEpisodeId: "ep3")
        XCTAssertEqual(triples.count, 1)
        XCTAssertEqual(triples[0].subject, "keep")
    }

    func testSkipsNonObjectArrayEntries() async throws {
        let gen = FakeChatGenerator("[1, \"two\", null, {\"s\":\"a\",\"p\":\"b\",\"o\":\"c\"}]")
        let ex = LlmKnowledgeGraphExtractor(gen)
        let triples = try await ex.extractFromTurn(userText: "u", assistantText: "a", sourceEpisodeId: "ep3")
        XCTAssertEqual(triples.count, 1)
        XCTAssertEqual(triples[0].subject, "a")
    }

    // ── empty results ───────────────────────────────────────────────────────────

    func testReturnsEmptyOnPureGarbage() async throws {
        let gen = FakeChatGenerator("I could not find any facts, sorry.")
        let ex = LlmKnowledgeGraphExtractor(gen)
        let triples = try await ex.extractFromTurn(userText: "u", assistantText: "a", sourceEpisodeId: "ep4")
        XCTAssertEqual(triples.count, 0)
    }

    func testReturnsEmptyOnMalformedJson() async throws {
        let gen = FakeChatGenerator("[{\"s\":\"a\", \"p\": }]")
        let ex = LlmKnowledgeGraphExtractor(gen)
        let triples = try await ex.extractFromTurn(userText: "u", assistantText: "a", sourceEpisodeId: "ep4")
        XCTAssertEqual(triples.count, 0)
    }

    func testReturnsEmptyWhenJsonIsObjectNotArray() async throws {
        let gen = FakeChatGenerator("{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\"}")
        let ex = LlmKnowledgeGraphExtractor(gen)
        // No '[' before ']' — object braces only, so no valid slice.
        let triples = try await ex.extractFromTurn(userText: "u", assistantText: "a", sourceEpisodeId: "ep4")
        XCTAssertEqual(triples.count, 0)
    }

    func testReturnsEmptyWhenBothTextsBlankNoLlmCall() async throws {
        let gen = FakeChatGenerator("[{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\"}]")
        let ex = LlmKnowledgeGraphExtractor(gen)
        let triples = try await ex.extractFromTurn(userText: "   ", assistantText: "", sourceEpisodeId: nil)
        XCTAssertEqual(triples.count, 0)
        // Generator was never invoked.
        XCTAssertEqual(gen.lastMessages.count, 0)
    }

    func testReturnsEmptyWhenGeneratorThrows() async throws {
        let ex = LlmKnowledgeGraphExtractor(ThrowingChatGenerator())
        let triples = try await ex.extractFromTurn(userText: "u", assistantText: "a", sourceEpisodeId: "ep5")
        XCTAssertEqual(triples.count, 0)
    }
}
