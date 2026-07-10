// HostingToolCatalogTests.swift
//
// Verifies InMemoryToolCatalog upsert/get/remove/list/search/listByProvider +
// importFrom, plus JsonRenderParser strict/lenient parsing and the observers
// (PushAIObserver, AetherAIObserver) + MockInferenceBridge.

import XCTest
@testable import CircleAI

final class HostingToolCatalogTests: XCTestCase {

    private func desc(_ name: String, provider: String = "local", tags: [String]? = nil, description: String = "") -> ToolDescriptor {
        ToolDescriptor(name: name, description: description, provider: provider, tags: tags)
    }

    // ── InMemoryToolCatalog ─────────────────────────────────────────────────

    func testUpsertGetRemove() async {
        let cat = InMemoryToolCatalog()
        await cat.upsert(desc("gmail.send"))
        XCTAssertEqual(cat.count, 1)
        let got = await cat.get(name: "GMAIL.SEND") // case-insensitive
        XCTAssertEqual(got?.name, "gmail.send")
        let removed = await cat.remove(name: "gmail.send")
        XCTAssertTrue(removed)
        XCTAssertEqual(cat.count, 0)
    }

    func testUpsertIsIdempotentBySameName() async {
        let cat = InMemoryToolCatalog()
        await cat.upsert(desc("t", description: "v1"))
        await cat.upsert(desc("t", description: "v2"))
        XCTAssertEqual(cat.count, 1)
        let got = await cat.get(name: "t")
        XCTAssertEqual(got?.description, "v2")
    }

    func testListSortedByName() async {
        let cat = InMemoryToolCatalog()
        await cat.upsert(desc("zebra"))
        await cat.upsert(desc("alpha"))
        await cat.upsert(desc("mid"))
        XCTAssertEqual(cat.list().map { $0.name }, ["alpha", "mid", "zebra"])
    }

    func testSearchScoresNameHigherThanDescription() async {
        let cat = InMemoryToolCatalog()
        await cat.upsert(desc("email.send", tags: ["communication"], description: "send an email"))
        await cat.upsert(desc("note.create", description: "email is mentioned here"))
        let results = cat.search("email", topK: 10)
        XCTAssertEqual(results.first?.name, "email.send", "name match outscores description match")
        XCTAssertEqual(results.count, 2)
    }

    func testSearchEmptyQueryReturnsNothing() async {
        let cat = InMemoryToolCatalog()
        await cat.upsert(desc("t"))
        XCTAssertTrue(cat.search("", topK: 10).isEmpty)
        XCTAssertTrue(cat.search("t", topK: 0).isEmpty)
    }

    func testListByProvider() async {
        let cat = InMemoryToolCatalog()
        await cat.upsert(desc("a", provider: "gmail"))
        await cat.upsert(desc("b", provider: "github"))
        await cat.upsert(desc("c", provider: "GMAIL"))
        let gmail = cat.listByProvider("gmail")
        XCTAssertEqual(gmail.count, 2)
    }

    func testImportFromProvider() async throws {
        let cat = InMemoryToolCatalog()
        let provider = FakeToolProvider(tools: [desc("x", provider: "fake"), desc("y", provider: "fake")])
        let n = try await cat.importFrom(provider)
        XCTAssertEqual(n, 2)
        XCTAssertEqual(cat.count, 2)
    }

    // ── JsonRenderParser ────────────────────────────────────────────────────

    func testParseValidCard() throws {
        let json = "{\"kind\":\"card\",\"properties\":{\"title\":\"Hello\"}}"
        let comp = try JsonRenderParser.parse(json, catalog: UiCatalogs.default)
        XCTAssertEqual(comp.kind, "card")
        XCTAssertEqual(comp.properties["title"] as? String, "Hello")
    }

    func testParseWithChildren() throws {
        let json = """
        {"kind":"list","properties":{"ordered":true},"children":[
          {"kind":"textBlock","properties":{"text":"item"}}
        ]}
        """
        let comp = try JsonRenderParser.parse(json, catalog: UiCatalogs.default)
        XCTAssertEqual(comp.children?.count, 1)
        XCTAssertEqual(comp.children?.first?.kind, "textBlock")
    }

    func testStrictRejectsUnknownKind() {
        let json = "{\"kind\":\"gizmo\",\"properties\":{}}"
        XCTAssertThrowsError(try JsonRenderParser.parse(json, catalog: UiCatalogs.default, strict: true))
    }

    func testLenientMapsUnknownKindToTextBlock() throws {
        let json = "{\"kind\":\"gizmo\",\"properties\":{}}"
        let comp = try JsonRenderParser.parse(json, catalog: UiCatalogs.default, strict: false)
        XCTAssertEqual(comp.kind, "textBlock")
        XCTAssertTrue((comp.properties["text"] as? String ?? "").contains("gizmo"))
    }

    func testStrictRejectsUndeclaredProperty() {
        let json = "{\"kind\":\"button\",\"properties\":{\"bogus\":\"x\"}}"
        XCTAssertThrowsError(try JsonRenderParser.parse(json, catalog: UiCatalogs.default, strict: true))
    }

    func testStrictRejectsChildrenWhenNotAllowed() {
        let json = "{\"kind\":\"button\",\"properties\":{\"label\":\"L\",\"action\":\"a\"},\"children\":[{\"kind\":\"textBlock\",\"properties\":{\"text\":\"t\"}}]}"
        XCTAssertThrowsError(try JsonRenderParser.parse(json, catalog: UiCatalogs.default, strict: true))
    }

    func testMissingKindThrows() {
        XCTAssertThrowsError(try JsonRenderParser.parse("{\"properties\":{}}", catalog: UiCatalogs.default))
    }

    func testDescribeCatalogForPromptListsKinds() {
        let prompt = JsonRenderParser.describeCatalogForPrompt(UiCatalogs.default)
        XCTAssertTrue(prompt.contains("- card"))
        XCTAssertTrue(prompt.contains("- button"))
        XCTAssertTrue(prompt.contains("children: array of components"))
    }

    // ── RecordingGenerativeUIRenderer ───────────────────────────────────────

    func testRecordingRendererCapturesLast() async {
        let renderer = RecordingGenerativeUIRenderer()
        let comp = UiComponent(kind: "card", properties: ["title": "T"])
        await renderer.render(comp)
        XCTAssertEqual(renderer.renderCount, 1)
        XCTAssertEqual(renderer.lastRendered?.kind, "card")
    }

    // ── Observers ───────────────────────────────────────────────────────────

    func testPushObserverSendsTruncatedResponse() async {
        let sender = RecordingPushSender()
        let obs = PushAIObserver(sender: sender, deviceToken: "dev-token")
        let long = String(repeating: "x", count: 250)
        await obs.onChatCompleted(AIChatEvent(correlationId: UUID(), messages: [], response: long, elapsed: 0, timestamp: Date()))
        // Fire-and-forget send task.
        try? await Task.sleep(nanoseconds: 50_000_000)
        let sent = sender.snapshot()
        XCTAssertEqual(sent.count, 1)
        XCTAssertEqual(sent[0].title, "B!")
        XCTAssertTrue(sent[0].body.hasSuffix("…"))
        XCTAssertEqual(sent[0].body.count, 101) // 100 chars + ellipsis
    }

    func testAetherObserverPublishesResponse() async {
        let transport = RecordingAetherTransport()
        let obs = AetherAIObserver(transport: transport)
        await obs.onChatCompleted(AIChatEvent(correlationId: UUID(), messages: [], response: "hi", elapsed: 0, timestamp: Date()))
        try? await Task.sleep(nanoseconds: 50_000_000)
        let pubs = transport.snapshot()
        XCTAssertEqual(pubs.count, 1)
        XCTAssertEqual(pubs[0].topic, "butler/response")
        let s = String(data: pubs[0].payload, encoding: .utf8) ?? ""
        XCTAssertTrue(s.contains("hi"))
    }

    // ── MockInferenceBridge ─────────────────────────────────────────────────

    func testMockBridgeCompletesWithCannedOutput() async throws {
        let bridge = MockInferenceBridge(cannedOutput: "canned", modelId: "m1")
        let loadedM1 = try await bridge.isModelLoaded("m1")
        XCTAssertTrue(loadedM1)
        let loadedOther = try await bridge.isModelLoaded("other")
        XCTAssertFalse(loadedOther)
        let req = InferenceRequest.create(modelId: "m1", prompt: "hi")
        let resp = try await bridge.complete(req)
        XCTAssertEqual(resp.outputText, "canned")
        XCTAssertEqual(resp.status, .completed)
        XCTAssertEqual(resp.requestId, req.id)
    }

    func testMockBridgeStreamsCannedOutput() async throws {
        let bridge = MockInferenceBridge(cannedOutput: "canned")
        var chunks: [String] = []
        for await c in bridge.streamCompletion(InferenceRequest.create(modelId: "mock-model", prompt: "x")) {
            chunks.append(c)
        }
        XCTAssertEqual(chunks, ["canned"])
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    struct FakeToolProvider: IToolProvider {
        let tools: [ToolDescriptor]
        var providerId: String { "fake" }
        func discover() async throws -> [ToolDescriptor] { tools }
        func isAvailable() async throws -> Bool { true }
    }

    final class RecordingPushSender: IPushNotificationSender, @unchecked Sendable {
        private let lock = NSLock()
        private var sent: [(token: String, title: String, body: String)] = []
        func send(deviceToken: String, title: String, body: String) async throws {
            lock.lock(); sent.append((deviceToken, title, body)); lock.unlock()
        }
        func snapshot() -> [(token: String, title: String, body: String)] { lock.lock(); defer { lock.unlock() }; return sent }
    }

    final class RecordingAetherTransport: ICircleAetherTransport, @unchecked Sendable {
        private let lock = NSLock()
        private var pubs: [(topic: String, payload: Data)] = []
        func publish(topic: String, payload: Data) async throws {
            lock.lock(); pubs.append((topic, payload)); lock.unlock()
        }
        func snapshot() -> [(topic: String, payload: Data)] { lock.lock(); defer { lock.unlock() }; return pubs }
    }
}
