// HostingEndpointsTests.swift
//
// Verifies InProcessEndpoint accessor, LoopbackRouter auth/routing/SSE framing +
// constant-time token comparison, GenerationOptionsPayload conversion, and
// AIHttpClient over the injected transport.

import XCTest
@testable import CircleAI

final class HostingEndpointsTests: XCTestCase {

    private func startedService() async throws -> AIService {
        let dir = NSTemporaryDirectory()
        let path = (dir as NSString).appendingPathComponent("ep-model-\(UUID().uuidString).gguf")
        FileManager.default.createFile(atPath: path, contents: Data([0]))
        let svc = AIService(options: AIOptions(modelPath: path, warmOnStart: false),
                            generatorFactory: { _ in EchoGenerator() })
        try await svc.start()
        return svc
    }

    // ── InProcessEndpoint ───────────────────────────────────────────────────

    func testInProcessEndpointExposesService() async throws {
        let svc = try await startedService()
        let ep = InProcessEndpoint()
        XCTAssertNil(ep.serviceAccessor)
        try await ep.start(svc)
        XCTAssertNotNil(ep.serviceAccessor)
        try await ep.stop()
        XCTAssertNil(ep.serviceAccessor)
    }

    // ── LoopbackRouter token ────────────────────────────────────────────────

    func testLoopbackGeneratesTokenWhenUnset() async throws {
        let router = LoopbackRouter(options: AIOptions())
        XCTAssertNil(router.token)
        try await router.start(try await startedService())
        XCTAssertNotNil(router.token)
        XCTAssertFalse(router.token!.isEmpty)
    }

    func testLoopbackUsesConfiguredToken() async throws {
        let router = LoopbackRouter(options: AIOptions(loopbackToken: "secret-123"))
        try await router.start(try await startedService())
        XCTAssertEqual(router.token, "secret-123")
    }

    func testCryptographicEqualsMatchesAndRejects() {
        XCTAssertTrue(LoopbackRouter.cryptographicEquals("abc", "abc"))
        XCTAssertFalse(LoopbackRouter.cryptographicEquals("abc", "abd"))
        XCTAssertFalse(LoopbackRouter.cryptographicEquals("abc", "abcd"))
    }

    // ── LoopbackRouter routing ──────────────────────────────────────────────

    func testUnauthorisedWithoutToken() async throws {
        let router = LoopbackRouter(options: AIOptions(loopbackToken: "t"))
        try await router.start(try await startedService())
        let resp = await router.handle(LoopbackRequest(method: "POST", path: "/butler/ask", token: nil, body: "{\"question\":\"hi\"}"))
        XCTAssertEqual(resp.statusCode, 401)
    }

    func testMethodNotAllowedForGet() async throws {
        let router = LoopbackRouter(options: AIOptions(loopbackToken: "t"))
        try await router.start(try await startedService())
        let resp = await router.handle(LoopbackRequest(method: "GET", path: "/butler/ask", token: "t", body: ""))
        XCTAssertEqual(resp.statusCode, 405)
    }

    func testAskRoute() async throws {
        let router = LoopbackRouter(options: AIOptions(loopbackToken: "t"))
        try await router.start(try await startedService())
        let resp = await router.handle(LoopbackRequest(method: "POST", path: "/butler/ask", token: "t", body: "{\"question\":\"hello\"}"))
        XCTAssertEqual(resp.statusCode, 200)
        XCTAssertEqual(resp.body, "echo:hello")
    }

    func testAskMissingQuestion() async throws {
        let router = LoopbackRouter(options: AIOptions(loopbackToken: "t"))
        try await router.start(try await startedService())
        let resp = await router.handle(LoopbackRequest(method: "POST", path: "/butler/ask", token: "t", body: "{}"))
        XCTAssertEqual(resp.statusCode, 400)
    }

    func testChatRouteReturnsJsonContent() async throws {
        let router = LoopbackRouter(options: AIOptions(loopbackToken: "t"))
        try await router.start(try await startedService())
        let body = "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"}]}"
        let resp = await router.handle(LoopbackRequest(method: "POST", path: "/butler/chat", token: "t", body: body))
        XCTAssertEqual(resp.statusCode, 200)
        XCTAssertTrue(resp.body.contains("\"content\""))
        XCTAssertTrue(resp.body.contains("echo:q"))
    }

    func testUnknownRoute404() async throws {
        let router = LoopbackRouter(options: AIOptions(loopbackToken: "t"))
        try await router.start(try await startedService())
        let resp = await router.handle(LoopbackRequest(method: "POST", path: "/nope", token: "t", body: "{}"))
        XCTAssertEqual(resp.statusCode, 404)
    }

    func testStreamFramesSse() async throws {
        let router = LoopbackRouter(options: AIOptions(loopbackToken: "t"))
        try await router.start(try await startedService())
        let body = "{\"messages\":[{\"role\":\"user\",\"content\":\"yo\"}]}"
        var frames: [String] = []
        for try await f in router.handleStream(LoopbackRequest(method: "POST", path: "/butler/stream", token: "t", body: body)) {
            frames.append(f)
        }
        XCTAssertTrue(frames.contains { $0.hasPrefix("data: ") && $0.contains("echo:yo") })
        XCTAssertTrue(frames.contains { $0.hasPrefix("event: done") })
    }

    // ── GenerationOptionsPayload ────────────────────────────────────────────

    func testGenerationOptionsPayloadRoundTrip() {
        let o = GenerationOptions(maxTokens: 128, temperature: 0.3, topP: 0.8, topK: 20, seed: 7, stopSequences: ["X"])
        let payload = GenerationOptionsPayload.from(o)
        let back = payload.toGenerationOptions()
        XCTAssertEqual(back.maxTokens, 128)
        XCTAssertEqual(back.temperature, 0.3)
        XCTAssertEqual(back.topP, 0.8)
        XCTAssertEqual(back.topK, 20)
        XCTAssertEqual(back.seed, 7)
        XCTAssertEqual(back.stopSequences, ["X"])
    }

    func testGenerationOptionsPayloadDefaultsFillFromDefault() {
        let payload = GenerationOptionsPayload()
        let defaults = GenerationOptions()
        let back = payload.toGenerationOptions()
        XCTAssertEqual(back.maxTokens, defaults.maxTokens)
        XCTAssertEqual(back.temperature, defaults.temperature)
        XCTAssertNil(back.seed)
    }

    // ── AIHttpClient ────────────────────────────────────────────────────────

    func testAIHttpClientChatReadsContent() async throws {
        // A transport that returns a chat-shaped JSON with a "content" field.
        let client = AIHttpClient(transport: ContentTransport())
        let reply = try await client.chat([ChatMessage(role: "user", content: "q")], options: nil)
        XCTAssertEqual(reply, "hi-from-endpoint")
    }

    func testAIHttpClientStreamStopsOnDoneEvent() async throws {
        let client = AIHttpClient(transport: EventDoneTransport())
        var chunks: [String] = []
        for try await c in client.stream([ChatMessage(role: "user", content: "q")], options: nil) {
            chunks.append(c)
        }
        XCTAssertEqual(chunks, ["alpha", "beta"])
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    struct ContentTransport: IButlerHttpTransport {
        func health() async throws {}
        func post(path: String, bodyJson: String) async throws -> String {
            "{\"content\":\"hi-from-endpoint\"}"
        }
        func postStream(path: String, bodyJson: String) -> AsyncThrowingStream<String, Error> {
            AsyncThrowingStream { $0.finish() }
        }
    }

    struct EventDoneTransport: IButlerHttpTransport {
        func health() async throws {}
        func post(path: String, bodyJson: String) async throws -> String { "{}" }
        func postStream(path: String, bodyJson: String) -> AsyncThrowingStream<String, Error> {
            AsyncThrowingStream { c in
                // JSON-encoded string frames, then an event:done terminator.
                c.yield("data: \"alpha\"")
                c.yield("")
                c.yield("data: \"beta\"")
                c.yield("event: done")
                c.finish()
            }
        }
    }
}
