// InnerMonologueTests.swift
//
// Verifies TemplateInnerMonologue (summary + direction template fill, keyword
// priority, determinism) and ReasoningLoopInnerMonologue (prefers the reasoning
// stream, falls back to visible content, degrades to a placeholder, and sends
// the verbatim system prompt + framed user message with reasoning enabled).

import XCTest
@testable import CircleAI

final class InnerMonologueTests: XCTestCase {

    // ── TemplateInnerMonologue ──────────────────────────────────────────────────

    private let tmpl = TemplateInnerMonologue()

    func testProducesOneOfTheThreeFilledFrames() async throws {
        let ctx = "{\"user\":\"asked about weather\"}"
        let r = try await tmpl.reflect(contextJson: ctx)
        let summary = TemplateInnerMonologue.summarise(ctx)
        let direction = TemplateInnerMonologue.inferDirection(ctx)
        let candidates = TemplateInnerMonologue.frames.map {
            $0.replacingOccurrences(of: "{summary}", with: summary)
              .replacingOccurrences(of: "{direction}", with: direction)
        }
        XCTAssertTrue(candidates.contains(r.thought), "thought was: \(r.thought)")
    }

    func testDeterministicForSameInput() async throws {
        let ctx = "{\"note\":\"something happened here today\"}"
        let a = try await tmpl.reflect(contextJson: ctx)
        let b = try await tmpl.reflect(contextJson: ctx)
        XCTAssertEqual(a.thought, b.thought)
    }

    func testDirectionErrorTakesPriority() {
        // "error" wins even when "goal" and "user" are also present.
        XCTAssertEqual(
            TemplateInnerMonologue.inferDirection("{\"error\":\"x\",\"goal\":\"y\",\"user\":\"z\"}"),
            "diagnose the failure first")
    }

    func testDirectionGoalBeforeUser() {
        XCTAssertEqual(
            TemplateInnerMonologue.inferDirection("{\"goal\":\"ship\",\"user\":\"bob\"}"),
            "advance toward the stated goal")
    }

    func testDirectionUser() {
        XCTAssertEqual(
            TemplateInnerMonologue.inferDirection("{\"user\":\"bob\"}"),
            "respond to the user")
    }

    func testDirectionDefault() {
        XCTAssertEqual(
            TemplateInnerMonologue.inferDirection("{\"weather\":\"sunny\"}"),
            "gather more context")
    }

    func testSummariseStripsPunctuationAndCapsAt12Words() {
        let s = TemplateInnerMonologue.summarise("{\"a\":\"one two three four five six seven eight nine ten eleven twelve thirteen\"}")
        let words = s.split(separator: " ")
        XCTAssertEqual(words.count, 12)
        XCTAssertFalse(s.contains("{"))
        XCTAssertFalse(s.contains("\""))
        // First token is the key "a" (punctuation removed), then the value words.
        XCTAssertEqual(words.first, "a")
    }

    // ── ReasoningLoopInnerMonologue ─────────────────────────────────────────────

    /// Fake generator that emits a scripted mix of reasoning + content fragments
    /// via streamFragments, and records the messages/options it was called with.
    final class FragmentGenerator: IChatGenerator, @unchecked Sendable {
        var lastMessages: [ChatMessage] = []
        var lastOptions: GenerationOptions?
        private let frags: [ChatFragment]
        init(_ frags: [ChatFragment]) { self.frags = frags }

        func generate(messages: [ChatMessage], options: GenerationOptions?) async throws -> String {
            frags.filter { $0.kind == .content }.map { $0.text }.joined()
        }
        func stream(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<String> {
            let f = frags
            return AsyncStream { cont in
                for frag in f where frag.kind == .content { cont.yield(frag.text) }
                cont.finish()
            }
        }
        func streamFragments(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<ChatFragment> {
            lastMessages = messages
            lastOptions = options
            let f = frags
            return AsyncStream { cont in
                for frag in f { cont.yield(frag) }
                cont.finish()
            }
        }
    }

    /// Generator whose streamFragments finishes without yielding anything —
    /// exercises the "(no inner state)" degradation.
    final class SilentGenerator: IChatGenerator, @unchecked Sendable {
        func generate(messages: [ChatMessage], options: GenerationOptions?) async throws -> String { "" }
        func stream(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<String> {
            AsyncStream { $0.finish() }
        }
        func streamFragments(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<ChatFragment> {
            AsyncStream { $0.finish() }
        }
    }

    func testPrefersReasoningTrace() async throws {
        let gen = FragmentGenerator([
            ChatFragment(kind: .reasoning, text: "Let me think. "),
            ChatFragment(kind: .reasoning, text: "The user seems calm."),
            ChatFragment(kind: .content, text: "You seem calm."),
        ])
        let m = ReasoningLoopInnerMonologue(gen)
        let r = try await m.reflect(contextJson: "{\"mood\":\"calm\"}")
        XCTAssertEqual(r.thought, "Let me think. The user seems calm.")
    }

    func testFallsBackToContentWhenNoReasoning() async throws {
        let gen = FragmentGenerator([
            ChatFragment(kind: .content, text: "  A short reflection.  "),
        ])
        let m = ReasoningLoopInnerMonologue(gen)
        let r = try await m.reflect(contextJson: "{}")
        XCTAssertEqual(r.thought, "A short reflection.")
    }

    func testDegradesToPlaceholderWhenNothingEmitted() async throws {
        let m = ReasoningLoopInnerMonologue(SilentGenerator())
        let r = try await m.reflect(contextJson: "{}")
        XCTAssertEqual(r.thought, "(no inner state)")
    }

    func testSendsSystemPromptAndReasoningOptions() async throws {
        let gen = FragmentGenerator([ChatFragment(kind: .reasoning, text: "x")])
        let m = ReasoningLoopInnerMonologue(gen)
        _ = try await m.reflect(contextJson: "{\"k\":1}")

        XCTAssertEqual(gen.lastMessages.count, 2)
        XCTAssertEqual(gen.lastMessages[0].role, "system")
        XCTAssertTrue(gen.lastMessages[0].content.hasPrefix("You are this user's inner monologue."))
        XCTAssertEqual(gen.lastMessages[1].role, "user")
        XCTAssertTrue(gen.lastMessages[1].content.contains("{\"k\":1}"))
        XCTAssertTrue(gen.lastMessages[1].content.contains("Reflect on this in 2-3 sentences."))
        XCTAssertEqual(gen.lastOptions?.maxTokens, 256)
        XCTAssertEqual(gen.lastOptions?.temperature ?? .nan, 0.5, accuracy: 1e-6)
        XCTAssertEqual(gen.lastOptions?.includeReasoning, true)
    }
}
