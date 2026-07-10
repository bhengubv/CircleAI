// HostingServiceTests.swift
//
// Verifies AIService (start/chat/stream/agentic/feedback/tool + enrichment),
// FallbackAIService (local-vs-cloud selection), and AIApiClient (cloud proxy
// over the injected transport). Mirrors the C# behaviour.

import XCTest
@testable import CircleAI

final class HostingServiceTests: XCTestCase {

    private func makeService(
        options: AIOptions = AIOptions(),
        generator: IChatGenerator = EchoGenerator(),
        observer: RecordingServiceObserver? = nil
    ) -> AIService {
        AIService(
            options: options,
            generatorFactory: { _ in generator },
            observer: observer)
    }

    // ── start / ready ───────────────────────────────────────────────────────

    func testStartMakesReadyAndFiresObserver() async throws {
        let obs = RecordingServiceObserver()
        let svc = makeService(options: AIOptions(modelPath: pathToDummyModel(), warmOnStart: false), observer: obs)
        try await svc.start()
        XCTAssertTrue(svc.isReady)
        XCTAssertEqual(obs.started, 1)
    }

    func testStartIsIdempotent() async throws {
        let gen = EchoGenerator()
        let svc = makeService(options: AIOptions(modelPath: pathToDummyModel(), warmOnStart: false), generator: gen)
        try await svc.start()
        try await svc.start()
        XCTAssertTrue(svc.isReady)
    }

    // ── ask / chat ──────────────────────────────────────────────────────────

    func testAskInjectsSystemPromptAndEchoes() async throws {
        let gen = EchoGenerator()
        let svc = makeService(options: AIOptions(modelPath: pathToDummyModel(), systemPrompt: "SYS", warmOnStart: false), generator: gen)
        let reply = try await svc.ask("hi there")
        XCTAssertEqual(reply, "echo:hi there")
        // The prepared messages should have a system message prepended.
        let prepared = gen.lastGenerateMessages!
        XCTAssertEqual(prepared.first?.role, "system")
        XCTAssertTrue(prepared.first!.content.contains("SYS"))
        XCTAssertEqual(prepared.last?.role, "user")
    }

    func testChatHonoursCallerSuppliedSystemMessage() async throws {
        let gen = EchoGenerator()
        let svc = makeService(options: AIOptions(modelPath: pathToDummyModel(), systemPrompt: "SYS", warmOnStart: false), generator: gen)
        _ = try await svc.chat([
            ChatMessage(role: "system", content: "CALLER"),
            ChatMessage(role: "user", content: "q"),
        ], options: nil)
        let prepared = gen.lastGenerateMessages!
        // Caller's system message is honoured as-is; no injected SYS prefix.
        XCTAssertEqual(prepared.count, 2)
        XCTAssertEqual(prepared.first?.content, "CALLER")
    }

    func testChatFiresChatCompletedObserver() async throws {
        let obs = RecordingServiceObserver()
        let svc = makeService(options: AIOptions(modelPath: pathToDummyModel(), warmOnStart: false), observer: obs)
        _ = try await svc.chat([ChatMessage(role: "user", content: "hello")], options: nil)
        XCTAssertEqual(obs.chatCount, 1)
        XCTAssertEqual(obs.chatEvents[0].response, "echo:hello")
    }

    // ── stream ──────────────────────────────────────────────────────────────

    func testStreamYieldsChunksAndFiresObservers() async throws {
        let obs = RecordingServiceObserver()
        let svc = makeService(options: AIOptions(modelPath: pathToDummyModel(), warmOnStart: false), observer: obs)
        var chunks: [String] = []
        for try await c in svc.stream([ChatMessage(role: "user", content: "yo")], options: nil) {
            chunks.append(c)
        }
        XCTAssertEqual(chunks, ["echo:yo"])
        XCTAssertEqual(obs.streamStarted, 1)
        XCTAssertEqual(obs.streamCompleted, 1)
    }

    // ── enrichment: episodic RAG + persona ──────────────────────────────────

    func testEpisodicStoreReceivesExchange() async throws {
        let episodic = InMemoryEpisodicStore()
        let opts = AIOptions(modelPath: pathToDummyModel(), warmOnStart: false, episodicMemory: episodic)
        let svc = makeService(options: opts)
        _ = try await svc.ask("remember this")
        // Give the fire-and-forget store task a moment.
        try await Task.sleep(nanoseconds: 50_000_000)
        let count = try await episodic.count()
        XCTAssertEqual(count, 1)
    }

    func testDeviceContextInjectedIntoSystemPrompt() async throws {
        let gen = EchoGenerator()
        let ctx = TestDeviceContext(activeAppId: "tgn.bidbaas", networkType: "wifi")
        let opts = AIOptions(modelPath: pathToDummyModel(), warmOnStart: false, deviceContext: ctx)
        let svc = makeService(options: opts, generator: gen)
        _ = try await svc.ask("q")
        let sys = gen.lastGenerateMessages!.first!.content
        XCTAssertTrue(sys.contains("[Device context]"))
        XCTAssertTrue(sys.contains("Active app: tgn.bidbaas"))
        XCTAssertTrue(sys.contains("Network: wifi"))
    }

    // ── agentic loop ────────────────────────────────────────────────────────

    func testAgenticLoopExecutesToolThenReturns() async throws {
        let bridge = EchoToolBridge()
        let gen = ToolCallGenerator(toolName: "search", finalReply: "final-answer")
        let opts = AIOptions(modelPath: pathToDummyModel(), warmOnStart: false, toolBridge: bridge)
        let svc = makeService(options: opts, generator: gen)
        let result = try await svc.agenticChat("do a search")
        XCTAssertEqual(result, "final-answer")
        XCTAssertEqual(bridge.invokeCount, 1)
        XCTAssertEqual(bridge.invoked, ["search"])
        XCTAssertGreaterThanOrEqual(gen.callCount, 2)
    }

    func testParseToolCallRecognisesBothSpellings() {
        let a = AIService.parseToolCall("<tool_call>{\"name\":\"x\",\"arguments\":{}}</tool_call>")
        XCTAssertEqual(a?.toolName, "x")
        let b = AIService.parseToolCall("<tool_call>{\"tool_name\":\"y\"}</tool_call>")
        XCTAssertEqual(b?.toolName, "y")
        XCTAssertNil(AIService.parseToolCall("plain text, no call"))
    }

    // ── tool invoke without bridge ──────────────────────────────────────────

    func testInvokeToolWithoutBridgeReturnsFailure() async throws {
        let svc = makeService(options: AIOptions(modelPath: pathToDummyModel(), warmOnStart: false))
        let result = try await svc.invokeTool(ToolInvocation(toolName: "t", arguments: [:]))
        XCTAssertFalse(result.success)
        XCTAssertEqual(result.error, "No tool bridge configured.")
    }

    // ── feedback drives persona ─────────────────────────────────────────────

    func testSubmitFeedbackUpdatesPersonaCounters() async throws {
        let persona = InMemoryPersonaStore()
        let feedback = InMemoryFeedbackStore()
        let opts = AIOptions(modelPath: pathToDummyModel(), warmOnStart: false,
                             personaStore: persona, feedbackStore: feedback)
        let svc = makeService(options: opts)
        try await svc.start()
        try await svc.submitFeedback(FeedbackSignal(userText: "u", assistantText: "a", polarity: .positive))
        let stored = try await feedback.count()
        XCTAssertEqual(stored, 1)
        let p = try await persona.load(userId: "default")
        XCTAssertEqual(p.positiveSignals, 1)
        XCTAssertEqual(p.totalInteractions, 1)
    }

    // ── FallbackAIService ───────────────────────────────────────────────────

    func testFallbackUsesLocalWhenRamSufficient() async throws {
        let local = FakeButler(reply: "local")
        let cloud = FakeButler(reply: "cloud")
        let fb = FallbackAIService(local: local, cloud: cloud,
                                   ramThresholdBytes: 1000,
                                   ramSource: StaticRam(availableRamBytes: 5000))
        try await fb.start()
        let ans = try await fb.ask("q")
        XCTAssertEqual(ans, "local")
        XCTAssertTrue(local.isReady)
        XCTAssertFalse(cloud.isReady)
    }

    func testFallbackUsesCloudWhenRamBelowThreshold() async throws {
        let local = FakeButler(reply: "local")
        let cloud = FakeButler(reply: "cloud")
        let fb = FallbackAIService(local: local, cloud: cloud,
                                   ramThresholdBytes: 10_000,
                                   ramSource: StaticRam(availableRamBytes: 500))
        try await fb.start()
        let ans = try await fb.ask("q")
        XCTAssertEqual(ans, "cloud")
        XCTAssertTrue(cloud.isReady)
    }

    func testFallbackFallsBackWhenLocalStartThrows() async throws {
        // Local start throws (no generator factory + no loader/path) → cloud.
        let local = AIService(options: AIOptions()) // start() will throw noResolver
        let cloud = FakeButler(reply: "cloud")
        let fb = FallbackAIService(local: local, cloud: cloud,
                                   ramThresholdBytes: 1,
                                   ramSource: StaticRam(availableRamBytes: 1_000_000))
        try await fb.start()
        let ans = try await fb.ask("q")
        XCTAssertEqual(ans, "cloud")
    }

    // ── AIApiClient (cloud proxy) ───────────────────────────────────────────

    func testApiClientHealthGatesReady() async throws {
        let transport = InMemoryButlerTransport(healthy: false)
        let client = AIApiClient(transport: transport)
        do { try await client.start(); XCTFail("expected health failure") }
        catch { /* expected */ }
        XCTAssertFalse(client.isReady)

        transport.setHealthy(true)
        try await client.start()
        XCTAssertTrue(client.isReady)
    }

    func testApiClientAskReadsTextField() async throws {
        let client = AIApiClient(transport: InMemoryButlerTransport())
        try await client.start()
        let reply = try await client.ask("q")
        XCTAssertEqual(reply, "cloud-reply")
    }

    func testApiClientStreamParsesSseAndStopsOnDone() async throws {
        let client = AIApiClient(transport: InMemoryButlerTransport())
        var chunks: [String] = []
        for try await c in client.stream([ChatMessage(role: "user", content: "q")], options: nil) {
            chunks.append(c)
        }
        XCTAssertEqual(chunks, ["tok1", "tok2"])
    }

    func testApiClientToolParsesResult() async throws {
        let client = AIApiClient(transport: InMemoryButlerTransport())
        let result = try await client.invokeTool(ToolInvocation(toolName: "t", arguments: [:]))
        XCTAssertTrue(result.success)
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// Writes a tiny dummy file the AIService can treat as a pinned model path
    /// (the injected generator factory ignores its contents).
    private func pathToDummyModel() -> String {
        let dir = NSTemporaryDirectory()
        let path = (dir as NSString).appendingPathComponent("circleai-hosting-dummy-\(UUID().uuidString).gguf")
        FileManager.default.createFile(atPath: path, contents: Data([0x00]))
        return path
    }
}

/// Minimal IDeviceContext for enrichment tests.
struct TestDeviceContext: IDeviceContext {
    var activeAppId: String?
    var locale: String? = nil
    var timeZoneId: String? = "UTC"
    var localTime: Date? = nil
    var latitude: Double? = nil
    var longitude: Double? = nil
    var locationHint: String? = nil
    var batteryLevel: Float? = nil
    var isCharging: Bool? = nil
    var networkType: String?
    var cpuUsagePercent: Float? = nil
    var availableMemoryBytes: Int64? = nil
    var thermalState: String? = nil
    var storageFreeBytes: Int64? = nil
    var lastActiveUtc: Date? = nil

    init(activeAppId: String? = nil, networkType: String? = nil) {
        self.activeAppId = activeAppId
        self.networkType = networkType
    }
}
