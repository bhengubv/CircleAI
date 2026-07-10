// LocalChatGeneratorTests.swift

import XCTest
@testable import CircleAI

final class LocalChatGeneratorTests: XCTestCase {

    private func msgs(_ pairs: [(String, String)]) -> [ChatMessage] {
        pairs.map { ChatMessage(role: $0.0, content: $0.1) }
    }

    func testBuildQwenChatPromptShape() {
        let prompt = LocalChatGenerator.buildQwenChatPrompt(msgs([
            ("system", "You are B!"),
            ("user", "hi"),
        ]))
        XCTAssertTrue(prompt.contains("<|im_start|>system\nYou are B!\n<|im_end|>\n"))
        XCTAssertTrue(prompt.contains("<|im_start|>user\nhi\n<|im_end|>\n"))
        XCTAssertTrue(prompt.hasSuffix("<|im_start|>assistant\n"))
    }

    func testBuildQwenChatPromptDefaultsEmptyRoleToUser() {
        let prompt = LocalChatGenerator.buildQwenChatPrompt([ChatMessage(role: "  ", content: "x")])
        XCTAssertTrue(prompt.contains("<|im_start|>user\nx\n<|im_end|>\n"))
    }

    func testGenerateIsDeterministicForSameSeed() async throws {
        let gen = LocalChatGenerator()
        let opts = GenerationOptions(maxTokens: 20, seed: 42)
        let a = try await gen.generate(messages: msgs([("user", "hello")]), options: opts)
        let b = try await gen.generate(messages: msgs([("user", "hello")]), options: opts)
        XCTAssertEqual(a, b)
        XCTAssertFalse(a.isEmpty)
    }

    func testDifferentPromptsProduceDifferentOutput() async throws {
        let gen = LocalChatGenerator()
        let opts = GenerationOptions(maxTokens: 30, seed: 1)
        let a = try await gen.generate(messages: msgs([("user", "alpha")]), options: opts)
        let b = try await gen.generate(messages: msgs([("user", "beta")]), options: opts)
        XCTAssertNotEqual(a, b)
    }

    func testStreamConcatenatesToGenerate() async throws {
        let gen = LocalChatGenerator()
        let opts = GenerationOptions(maxTokens: 15, seed: 7)
        var streamed = ""
        for await chunk in gen.stream(messages: msgs([("user", "q")]), options: opts) {
            streamed += chunk
        }
        let full = try await gen.generate(messages: msgs([("user", "q")]), options: opts)
        XCTAssertEqual(streamed, full)
    }

    func testStreamFragmentsSplitsReasoningWhenIncluded() async throws {
        let gen = LocalChatGenerator()
        let opts = GenerationOptions(maxTokens: 10, seed: 3, includeReasoning: true)
        var reasoning = ""
        var content = ""
        for await f in gen.streamFragments(messages: msgs([("user", "think")]), options: opts) {
            if f.kind == .reasoning { reasoning += f.text } else { content += f.text }
        }
        XCTAssertFalse(reasoning.isEmpty, "reasoning trace should be surfaced when includeReasoning=true")
        XCTAssertFalse(content.isEmpty)
    }

    func testStreamFragmentsDropsReasoningWhenExcluded() async throws {
        let gen = LocalChatGenerator()
        let opts = GenerationOptions(maxTokens: 10, seed: 3, includeReasoning: false)
        var sawReasoning = false
        for await f in gen.streamFragments(messages: msgs([("user", "think")]), options: opts) {
            if f.kind == .reasoning { sawReasoning = true }
        }
        XCTAssertFalse(sawReasoning)
    }

    func testGenerateResponseReportsCountsAndReasoning() async throws {
        let gen = LocalChatGenerator()
        let opts = GenerationOptions(maxTokens: 12, seed: 9, includeReasoning: true)
        let resp = try await gen.generateResponse(messages: msgs([("user", "hello world")]), options: opts)
        XCTAssertFalse(resp.text.isEmpty)
        XCTAssertGreaterThan(resp.tokensOut, 0)
        XCTAssertGreaterThan(resp.tokensIn, 0)
        XCTAssertNotNil(resp.reasoningContent)
        XCTAssertTrue(resp.latencyMs >= 0)
    }

    func testGenerateResponseFinishLengthAtBudget() async throws {
        // A tiny budget forces the reply to fill exactly maxTokens → .length.
        let gen = LocalChatGenerator()
        let opts = GenerationOptions(maxTokens: 3, seed: 5, budget: .none)
        let resp = try await gen.generateResponse(messages: msgs([("user", "hi")]), options: opts)
        XCTAssertEqual(resp.tokensOut, 3)
        XCTAssertEqual(resp.finishReason, .length)
    }

    func testPowerBudgetLowCapsOutputTokens() async throws {
        let gen = LocalChatGenerator(contextSize: 4096)
        // Low budget caps at 64 regardless of the requested 1000.
        let opts = GenerationOptions(maxTokens: 1000, seed: 2, budget: .low)
        let resp = try await gen.generateResponse(messages: msgs([("user", "long")]), options: opts)
        XCTAssertLessThanOrEqual(resp.tokensOut, 64)
    }

    func testStopSequenceTruncatesOutput() {
        let text = "one two STOP three four"
        XCTAssertEqual(LocalChatGenerator.applyStops(text, stops: ["STOP"]), "one two ")
        XCTAssertEqual(LocalChatGenerator.applyStops(text, stops: ["nope"]), text)
    }

    func testSessionSaveLoadRoundTrip() async throws {
        let gen = LocalChatGenerator(modelId: "m-rt")
        let path = (NSTemporaryDirectory() as NSString).appendingPathComponent("sess-\(UUID().uuidString).bin")
        defer { try? FileManager.default.removeItem(atPath: path) }
        let saved = try await gen.saveSession(path: path)
        XCTAssertTrue(saved)
        let loaded = try await gen.loadSession(path: path)
        XCTAssertTrue(loaded)
    }

    func testLoadSessionMissingFileReturnsFalse() async throws {
        let gen = LocalChatGenerator()
        let loaded = try await gen.loadSession(path: (NSTemporaryDirectory() as NSString).appendingPathComponent("nope-\(UUID().uuidString)"))
        XCTAssertFalse(loaded)
    }

    func testSaveSessionEmptyPathThrows() async {
        let gen = LocalChatGenerator()
        do {
            _ = try await gen.saveSession(path: "  ")
            XCTFail("expected throw")
        } catch { XCTAssertEqual(error as? LocalChatGeneratorError, .pathRequired) }
    }

    func testApproximateTokensRule() {
        XCTAssertEqual(LocalChatGenerator.approximateTokens("abcdefgh"), 2) // 8/4
        XCTAssertEqual(LocalChatGenerator.approximateTokens("a"), 1)        // max(1, ...)
        XCTAssertEqual(LocalChatGenerator.approximateTokens(""), 0)
    }

    func testDefaultGenerateResponseOnCustomGenerator() async throws {
        // A generator that only implements generate() gets the extension default.
        final class PlainGen: IChatGenerator, @unchecked Sendable {
            func generate(messages: [ChatMessage], options: GenerationOptions?) async throws -> String { "fixed reply here" }
            func stream(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<String> {
                AsyncStream { c in c.yield("fixed reply here"); c.finish() }
            }
        }
        let g = PlainGen()
        let resp = try await g.generateResponse(messages: [ChatMessage(role: "user", content: "hi there")], options: nil)
        XCTAssertEqual(resp.text, "fixed reply here")
        XCTAssertEqual(resp.finishReason, .stop)
        XCTAssertEqual(resp.tokensOut, LocalChatGenerator.approximateTokens("fixed reply here"))
        XCTAssertNil(resp.reasoningContent)
    }
}
