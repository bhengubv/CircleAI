// RagTests.swift
// Exercises RagContextBuilder + RagPipelineBuilder. Mirrors the C#
// RagContextBuilderTests plus the fluent-builder surface and the embedder
// ranking path (verified TS rag.test.ts).
//
// Note: the TS null-store / null-signals / below-range guards are enforced by
// Swift's type system + preconditions (which trap rather than throw), so those
// negative cases are covered structurally and not re-asserted here.

import XCTest
@testable import CircleAI

// A trivial embedder that returns a fixed vector.
private struct FixedEmbedder: ITextEmbedder {
    let vector: [Float]
    func generate(_ text: String) async throws -> [Float] { vector }
}

// An embedder that always throws — exercises the recency fallback.
private struct ThrowingEmbedder: ITextEmbedder {
    struct Offline: Error {}
    func generate(_ text: String) async throws -> [Float] { throw Offline() }
}

// A store that always throws — exercises RAG best-effort resilience.
private struct ThrowingEpisodicStore: IEpisodicMemoryStore {
    struct Failure: Error {}
    func add(_ entry: EpisodicMemoryEntry) async throws { throw Failure() }
    func search(queryEmbedding: [Float]?, topK: Int) async throws -> [EpisodicMemoryEntry] { throw Failure() }
    func getRecent(count: Int) async throws -> [EpisodicMemoryEntry] { throw Failure() }
    func count() async throws -> Int { throw Failure() }
    func pruneOlderThan(cutoff: Date) async throws -> Int { throw Failure() }
}

final class RagTests: XCTestCase {

    private func episodic(
        userText: String = "u",
        assistantText: String = "a",
        at: Date = Date(timeIntervalSince1970: 1_780_000_440), // 2026-06-01T12:34:00Z-ish
        appContext: String? = nil,
        embedding: [Float]? = nil
    ) -> EpisodicMemoryEntry {
        EpisodicMemoryEntry(
            recordedAt: at, userText: userText, assistantText: assistantText,
            appContext: appContext, embedding: embedding)
    }

    private func countOccurrences(_ text: String, _ token: String) -> Int {
        guard !token.isEmpty else { return 0 }
        var count = 0
        var range = text.startIndex..<text.endIndex
        while let found = text.range(of: token, range: range) {
            count += 1
            range = found.upperBound..<text.endIndex
        }
        return count
    }

    // ── Empty / missing query ─────────────────────────────────────────────

    func testEmptyQueryReturnsEmpty() async {
        let b = RagContextBuilder(store: InMemoryEpisodicStore())
        let r = await b.buildContext("")
        XCTAssertEqual(r, "")
    }

    func testWhitespaceQueryReturnsEmpty() async {
        let b = RagContextBuilder(store: InMemoryEpisodicStore())
        let r = await b.buildContext("   ")
        XCTAssertEqual(r, "")
    }

    // ── Empty store ───────────────────────────────────────────────────────

    func testEmptyStoreReturnsEmpty() async {
        let b = RagContextBuilder(store: InMemoryEpisodicStore())
        let r = await b.buildContext("hello")
        XCTAssertEqual(r, "")
    }

    // ── Formatting (recency fallback, no embedder) ────────────────────────

    func testFormattedBlockWithHeaderAndBothTexts() async throws {
        let store = InMemoryEpisodicStore()
        try await store.add(episodic(
            userText: "What is SDPKT?",
            assistantText: "SDPKT is the TGN wallet.",
            at: isoDate("2026-06-01T11:00:00Z")))

        let b = RagContextBuilder(store: store, embedder: nil, topK: 3)
        let result = await b.buildContext("tell me about the wallet")

        XCTAssertNotEqual(result, "")
        XCTAssertTrue(result.contains("What is SDPKT?"))
        XCTAssertTrue(result.contains("SDPKT is the TGN wallet."))
        XCTAssertTrue(result.contains("[Relevant past exchanges"))
    }

    func testFormatsUtcTimestampAndLabels() async throws {
        let store = InMemoryEpisodicStore()
        try await store.add(episodic(userText: "q", assistantText: "r", at: isoDate("2026-06-01T09:05:00Z")))
        let b = RagContextBuilder(store: store, embedder: nil, topK: 1)
        let result = await b.buildContext("anything")
        XCTAssertTrue(result.contains("[2026-06-01 09:05 UTC]"))
        XCTAssertTrue(result.contains("User: q"))
        XCTAssertTrue(result.contains("B!: r"))
    }

    func testRespectsTopK() async throws {
        let store = InMemoryEpisodicStore()
        for i in 0..<10 {
            try await store.add(episodic(
                userText: "question \(i)", assistantText: "answer \(i)",
                at: Date(timeIntervalSince1970: 1_000_000 + Double(i))))
        }
        let b = RagContextBuilder(store: store, embedder: nil, topK: 2)
        let result = await b.buildContext("any question")
        XCTAssertEqual(countOccurrences(result, "• ["), 2)
    }

    func testIncludesAppContextWhenSet() async throws {
        let store = InMemoryEpisodicStore()
        try await store.add(episodic(userText: "bid query", assistantText: "bid answer", appContext: "tgn.bidbaas"))
        let b = RagContextBuilder(store: store, embedder: nil, topK: 3)
        let result = await b.buildContext("bidding")
        XCTAssertTrue(result.contains("tgn.bidbaas"))
    }

    func testTruncatesLongTexts() async throws {
        let store = InMemoryEpisodicStore()
        let longText = String(repeating: "x", count: 500)
        try await store.add(episodic(userText: longText, assistantText: "a"))
        // maxCharsPerEntry 100 → half 50 → truncate to 49 chars + "…"
        let b = RagContextBuilder(store: store, embedder: nil, topK: 1, maxCharsPerEntry: 100)
        let result = await b.buildContext("q")
        XCTAssertTrue(result.contains(String(repeating: "x", count: 49) + "…"))
        XCTAssertFalse(result.contains(String(repeating: "x", count: 51)))
    }

    // ── Embedder ranking path ─────────────────────────────────────────────

    func testRanksByEmbeddingWhenEmbedderSupplied() async throws {
        let store = InMemoryEpisodicStore()
        try await store.add(episodic(userText: "near", assistantText: "n", embedding: [1, 0]))
        try await store.add(episodic(userText: "far", assistantText: "f", embedding: [0, 1]))

        // Embedder maps any query to the x-axis, so "near" should rank first.
        let b = RagContextBuilder(store: store, embedder: FixedEmbedder(vector: [1, 0]), topK: 1)
        let result = await b.buildContext("anything")
        XCTAssertTrue(result.contains("near"))
        XCTAssertFalse(result.contains("far"))
    }

    func testFallsBackToRecencyWhenEmbedderThrows() async throws {
        let store = InMemoryEpisodicStore()
        try await store.add(episodic(userText: "only", assistantText: "entry", at: isoDate("2026-06-01T00:00:00Z")))
        let b = RagContextBuilder(store: store, embedder: ThrowingEmbedder(), topK: 3)
        let result = await b.buildContext("q")
        XCTAssertTrue(result.contains("only"))
    }

    // ── Resilience — store throws ─────────────────────────────────────────

    func testReturnsEmptyWhenStoreThrows() async {
        let b = RagContextBuilder(store: ThrowingEpisodicStore())
        let r = await b.buildContext("query")
        XCTAssertEqual(r, "")
    }

    // ── RagPipelineBuilder ────────────────────────────────────────────────

    func testBuildsFromInMemoryStore() async throws {
        let store = InMemoryEpisodicStore()
        try await store.add(episodic(userText: "hi", assistantText: "hello"))
        let rag = RagPipelineBuilder.create().withStore(store).withTopK(2).withMaxCharsPerEntry(500).build()
        let ctx = await rag.buildContext("greeting")
        XCTAssertTrue(ctx.contains("hi"))
    }

    func testWithInMemoryStoreWiresFreshStore() async {
        let rag = RagPipelineBuilder.create().withInMemoryStore().build()
        let r = await rag.buildContext("nothing stored")
        XCTAssertEqual(r, "")
    }

    func testWithEmbedderWiresSemanticRanking() async throws {
        let store = InMemoryEpisodicStore()
        try await store.add(episodic(userText: "near", assistantText: "n", embedding: [1, 0]))
        try await store.add(episodic(userText: "far", assistantText: "f", embedding: [0, 1]))
        let rag = RagPipelineBuilder.create()
            .withStore(store)
            .withEmbedder(FixedEmbedder(vector: [1, 0]))
            .withTopK(1)
            .build()
        let ctx = await rag.buildContext("q")
        XCTAssertTrue(ctx.contains("near"))
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private func isoDate(_ s: String) -> Date {
        let fmt = ISO8601DateFormatter()
        fmt.formatOptions = [.withInternetDateTime]
        return fmt.date(from: s)!
    }
}
