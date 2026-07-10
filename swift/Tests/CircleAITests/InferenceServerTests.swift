// InferenceServerTests.swift
//
// Exercises the ported CircleAI.Inference.Server surface: bridge, registry,
// lifecycle manager, API-key auth, admission control, native runtime status,
// companion resolver, OpenAI DTOs, and the in-memory endpoint handlers.

import XCTest
@testable import CircleAI

// MARK: - Test doubles

/// Echo embedder — returns a fixed-length vector derived from the text length.
private struct EchoEmbedder: ITextEmbedder {
    let dims: Int
    init(dims: Int = 4) { self.dims = dims }
    func generate(_ text: String) async throws -> [Float] {
        (0..<dims).map { Float(text.count + $0) }
    }
}

/// Companion session double.
private final class StubCompanionSession: ICompanionSession, @unchecked Sendable {
    let sessionId: String
    let identityId: String
    let interface: InterfaceKind = .web
    private let reply: String
    private var turns: [CompanionTurn] = []

    init(sessionId: String, identityId: String, reply: String) {
        self.sessionId = sessionId
        self.identityId = identityId
        self.reply = reply
    }

    func send(_ message: String) async throws -> String {
        turns.append(CompanionTurn(role: "user", content: message))
        turns.append(CompanionTurn(role: "assistant", content: reply))
        return reply
    }
    func stream(_ message: String) -> AsyncStream<String> {
        let r = reply
        return AsyncStream { c in c.yield(r); c.finish() }
    }
    func agent(_ instruction: String) async throws -> String { "agent:" + reply }
    func getContext() -> CompanionContext {
        CompanionContext(identityId: identityId, displayName: "U", interface: interface,
                         personaHints: "", affectSummary: "", recentMemorySnippets: [], activeGoals: [])
    }
    func refreshContext() async throws {}
    var history: [CompanionTurn] { turns }
    func signalFeedback(positive: Bool, note: String?) async throws {}
    var proactiveEvents: AsyncStream<CompanionProactiveEvent> { AsyncStream { $0.finish() } }
}

private func makeBridge(modelId: String) -> LocalProcessInferenceBridge {
    let gen = LocalChatGenerator(modelId: modelId)
    let descriptor = ModelDescriptor(
        modelId: modelId, version: "v1", format: .gguf, contextWindowTokens: 4096,
        vocabSize: 151_936, parameterCount: 0, quantisationLabel: "Q4", approximateMemoryBytes: 1_000_000)
    return LocalProcessInferenceBridge(chatGenerator: gen, descriptor: descriptor)
}

private func decode<T: Decodable>(_ type: T.Type, _ data: Data) throws -> T {
    let dec = JSONDecoder(); dec.dateDecodingStrategy = .iso8601
    return try dec.decode(type, from: data)
}

final class InferenceServerTests: XCTestCase {

    // MARK: - LocalProcessInferenceBridge

    func testBridgeCompleteReturnsReply() async throws {
        let bridge = makeBridge(modelId: "m")
        let req = InferenceRequest.create(modelId: "m", prompt: "hi", maxOutputTokens: 20)
        let resp = try await bridge.complete(req)
        XCTAssertEqual(resp.modelId, "m")
        XCTAssertNotEqual(resp.status, .failed)
        XCTAssertFalse(resp.outputText.isEmpty)
        XCTAssertGreaterThan(resp.promptTokenCount, 0)
    }

    func testBridgeCompleteWrongModelFails() async throws {
        let bridge = makeBridge(modelId: "m")
        let req = InferenceRequest.create(modelId: "other", prompt: "hi")
        let resp = try await bridge.complete(req)
        XCTAssertEqual(resp.status, .failed)
        XCTAssertNotNil(resp.failureMessage)
    }

    func testBridgeIsModelLoaded() async throws {
        let bridge = makeBridge(modelId: "m")
        let loadedM = try await bridge.isModelLoaded("m")
        XCTAssertTrue(loadedM)
        let loadedNope = try await bridge.isModelLoaded("nope")
        XCTAssertFalse(loadedNope)
        let firstModelId = try await bridge.listLoadedModels().first?.modelId
        XCTAssertEqual(firstModelId, "m")
    }

    func testBridgeStreamYieldsAtLeastOneChunk() async throws {
        let bridge = makeBridge(modelId: "m")
        let req = InferenceRequest.create(modelId: "m", prompt: "hi", maxOutputTokens: 10)
        var chunks = 0
        for await c in bridge.streamCompletion(req) where !c.isEmpty { chunks += 1 }
        XCTAssertGreaterThan(chunks, 0)
    }

    func testBridgeDeviceCapabilities() async throws {
        let bridge = makeBridge(modelId: "m")
        let caps = try await bridge.deviceCapabilities()
        XCTAssertTrue(caps.hasTransportLayerEncryption)
    }

    // MARK: - Registry

    func testRegistryRegisterResolveDeregister() {
        let reg = InferenceServerModelRegistry()
        let bridge = makeBridge(modelId: "m")
        reg.register("m", bridge: bridge)
        XCTAssertNotNil(reg.resolve("m"))
        XCTAssertEqual(reg.chatModelIds(), ["m"])
        XCTAssertTrue(reg.deregister("m"))
        XCTAssertNil(reg.resolve("m"))
        XCTAssertFalse(reg.deregister("m"))
    }

    func testRegistryEmbeddersSeparateFromChat() {
        let reg = InferenceServerModelRegistry()
        reg.register("chat", bridge: makeBridge(modelId: "chat"))
        reg.registerEmbedder("emb", embedder: EchoEmbedder())
        XCTAssertNotNil(reg.resolveEmbedder("emb"))
        XCTAssertNil(reg.resolve("emb"))
        XCTAssertEqual(Set(reg.allModelIds()), Set(["chat", "emb"]))
    }

    // MARK: - Lifecycle manager

    func testLifecycleLoadThenAlreadyLoaded() async throws {
        let reg = InferenceServerModelRegistry()
        let mgr = ModelLifecycleManager(registry: reg, probe: StaticHostResourceProbe())
        let desc = ModelLoadDescriptor(modelId: "m", backend: .cpu, requestedTier: .tier1Small,
            vramRequiredBytes: 0, ramRequiredBytes: 1_000_000,
            bridgeFactory: { makeBridge(modelId: "m") })
        let r1 = try await mgr.load(desc)
        XCTAssertEqual(r1.outcome, .loaded)
        XCTAssertNotNil(reg.resolve("m"))
        XCTAssertEqual(mgr.totalAllocatedRamBytes, 1_000_000)

        let r2 = try await mgr.load(desc)
        XCTAssertEqual(r2.outcome, .alreadyLoaded)
    }

    func testLifecycleInsufficientRam() async throws {
        let reg = InferenceServerModelRegistry()
        let mgr = ModelLifecycleManager(registry: reg, probe: StaticHostResourceProbe(totalPhysicalMemoryBytes: 1_000))
        let desc = ModelLoadDescriptor(modelId: "m", backend: .cpu, requestedTier: .tier1Small,
            vramRequiredBytes: 0, ramRequiredBytes: 10_000_000,
            bridgeFactory: { makeBridge(modelId: "m") })
        let r = try await mgr.load(desc)
        XCTAssertEqual(r.outcome, .insufficientRam)
        XCTAssertNil(reg.resolve("m"))
    }

    func testLifecycleInsufficientVramOnGpuBackend() async throws {
        let reg = InferenceServerModelRegistry()
        let mgr = ModelLifecycleManager(registry: reg, probe: StaticHostResourceProbe(totalPhysicalMemoryBytes: 64_000_000_000, vramBytes: 1_000))
        let desc = ModelLoadDescriptor(modelId: "m", backend: .cuda, requestedTier: .tier2Medium,
            vramRequiredBytes: 8_000_000_000, ramRequiredBytes: 0,
            bridgeFactory: { makeBridge(modelId: "m") })
        let r = try await mgr.load(desc)
        XCTAssertEqual(r.outcome, .insufficientVram)
    }

    func testLifecycleFactoryFailureRollsBack() async throws {
        struct BoomError: Error {}
        let reg = InferenceServerModelRegistry()
        let mgr = ModelLifecycleManager(registry: reg, probe: StaticHostResourceProbe())
        let desc = ModelLoadDescriptor(modelId: "m", backend: .cpu, requestedTier: .tier1Small,
            vramRequiredBytes: 0, ramRequiredBytes: 1000,
            bridgeFactory: { throw BoomError() })
        let r = try await mgr.load(desc)
        XCTAssertEqual(r.outcome, .factoryFailed)
        XCTAssertNil(reg.resolve("m"))
        XCTAssertEqual(mgr.list().count, 0)
    }

    func testLifecycleUnload() async throws {
        let reg = InferenceServerModelRegistry()
        let mgr = ModelLifecycleManager(registry: reg, probe: StaticHostResourceProbe())
        let desc = ModelLoadDescriptor(modelId: "m", backend: .cpu, requestedTier: .tier1Small,
            vramRequiredBytes: 0, ramRequiredBytes: 1000,
            bridgeFactory: { makeBridge(modelId: "m") })
        _ = try await mgr.load(desc)
        let unloadResult = try await mgr.unload("m")
        XCTAssertEqual(unloadResult, .unloaded)
        XCTAssertNil(reg.resolve("m"))
        let secondUnload = try await mgr.unload("m")
        XCTAssertEqual(secondUnload, .notLoaded)
    }

    // MARK: - API-key auth

    func testAuthDisabledSucceedsAnonymous() {
        let h = ApiKeyAuthHandler(options: ApiKeyOptions(enabled: false))
        XCTAssertEqual(h.authenticate(headers: [:]), .success(name: "anonymous"))
    }

    func testAuthMissingHeaderIsNoResult() {
        let h = ApiKeyAuthHandler(options: ApiKeyOptions(enabled: true, keys: ["secret"]))
        XCTAssertEqual(h.authenticate(headers: [:]), .noResult)
    }

    func testAuthValidKeySucceeds() {
        let h = ApiKeyAuthHandler(options: ApiKeyOptions(enabled: true, headerName: "X-CircleAI-Api-Key", keys: ["secret", "other"]))
        XCTAssertEqual(h.authenticate(headers: ["x-circleai-api-key": "other"]), .success(name: "api-key-caller"))
    }

    func testAuthInvalidKeyFails() {
        let h = ApiKeyAuthHandler(options: ApiKeyOptions(enabled: true, keys: ["secret"]))
        if case .fail = h.authenticate(headers: ["X-CircleAI-Api-Key": "wrong"]) {} else { XCTFail("expected fail") }
    }

    func testFixedTimeEqualsCorrectness() {
        XCTAssertTrue(ApiKeyAuthHandler.fixedTimeEquals(Array("abc".utf8), Array("abc".utf8)))
        XCTAssertFalse(ApiKeyAuthHandler.fixedTimeEquals(Array("abc".utf8), Array("abd".utf8)))
        XCTAssertFalse(ApiKeyAuthHandler.fixedTimeEquals(Array("abc".utf8), Array("abcd".utf8)))
    }

    // MARK: - Admission + counters

    func testAdmissionCapsConcurrency() {
        let counters = ServerCounters()
        let ac = AdmissionControl(maxConcurrentRequests: 2, counters: counters)
        let s1 = ac.tryEnter(); let s2 = ac.tryEnter()
        XCTAssertNotNil(s1); XCTAssertNotNil(s2)
        XCTAssertNil(ac.tryEnter(), "third request over cap is rejected")
        XCTAssertEqual(counters.activeRequests, 2)
        XCTAssertEqual(counters.rejectedRequests, 1)
        s1?.release()
        XCTAssertNotNil(ac.tryEnter(), "slot freed after release")
    }

    func testAdmissionSlotReleaseIsIdempotent() {
        let counters = ServerCounters()
        let ac = AdmissionControl(maxConcurrentRequests: 1, counters: counters)
        let s = ac.tryEnter()
        s?.release(); s?.release() // double release must not over-credit
        XCTAssertNotNil(ac.tryEnter())
        XCTAssertNil(ac.tryEnter())
    }

    // MARK: - Native runtime status

    func testNativeRuntimeStatusUpdate() {
        let s = NativeRuntimeStatus()
        XCTAssertNil(s.latest)
        let paths = NativeRuntimePaths(mnnCorePath: "/mnn", bridgePath: "/bridge", extractedRoot: "/root")
        s.update(paths)
        XCTAssertEqual(s.latest, paths)
    }

    // MARK: - Companion resolver

    func testResolverSingleFlightsAndCaches() async throws {
        let calls = CallCounter()
        let resolver = InMemoryCompanionSessionResolver(factory: { identity, _ in
            await calls.inc()
            return StubCompanionSession(sessionId: "s", identityId: identity, reply: "hi")
        })
        async let a = resolver.resolve(sessionId: "s", identityId: "u")
        async let b = resolver.resolve(sessionId: "s", identityId: "u")
        _ = try await a; _ = try await b
        let callCount = await calls.value
        XCTAssertEqual(callCount, 1, "factory runs at most once per key")
        XCTAssertEqual(resolver.cachedSessionCount, 1)
    }

    func testResolverBlankIdsReturnNil() async throws {
        let resolver = InMemoryCompanionSessionResolver(factory: { id, _ in StubCompanionSession(sessionId: "s", identityId: id, reply: "x") })
        let blankSession = try await resolver.resolve(sessionId: "  ", identityId: "u")
        XCTAssertNil(blankSession)
        let blankIdentity = try await resolver.resolve(sessionId: "s", identityId: "")
        XCTAssertNil(blankIdentity)
    }

    func testResolverFailedConstructionDropsSlot() async throws {
        struct BoomError: Error {}
        let calls = CallCounter()
        let resolver = InMemoryCompanionSessionResolver(factory: { _, _ in
            await calls.inc()
            throw BoomError()
        })
        do { _ = try await resolver.resolve(sessionId: "s", identityId: "u"); XCTFail() } catch {}
        XCTAssertEqual(resolver.cachedSessionCount, 0, "failed slot must be evicted")
        do { _ = try await resolver.resolve(sessionId: "s", identityId: "u"); XCTFail() } catch {}
        let retryCount = await calls.value
        XCTAssertEqual(retryCount, 2, "next caller retries cleanly")
    }

    // MARK: - Chat completions handler

    func testChatHandlerMissingModelIs400() async {
        let (h, _) = makeChatHandler()
        let resp = await h.handle(ChatCompletionRequest(model: "", messages: [ChatCompletionMessage(role: "user", content: "hi")]))
        guard case .immediate(let r) = resp else { return XCTFail() }
        XCTAssertEqual(r.status, .badRequest)
    }

    func testChatHandlerMissingMessagesIs400() async {
        let (h, _) = makeChatHandler()
        let resp = await h.handle(ChatCompletionRequest(model: "m", messages: []))
        guard case .immediate(let r) = resp else { return XCTFail() }
        XCTAssertEqual(r.status, .badRequest)
    }

    func testChatHandlerModelNotLoadedIs404() async {
        let (h, _) = makeChatHandler(registerModel: nil)
        let resp = await h.handle(ChatCompletionRequest(model: "ghost", messages: [ChatCompletionMessage(role: "user", content: "hi")]))
        guard case .immediate(let r) = resp else { return XCTFail() }
        XCTAssertEqual(r.status, .notFound)
    }

    func testChatHandlerNonStreamReturnsCompletion() async throws {
        let (h, _) = makeChatHandler(registerModel: "m")
        let resp = await h.handle(ChatCompletionRequest(model: "m", messages: [ChatCompletionMessage(role: "user", content: "hi")], maxTokens: 20))
        guard case .immediate(let r) = resp else { return XCTFail() }
        XCTAssertEqual(r.status, .ok)
        let body = try decode(ChatCompletionResponse.self, r.jsonBody)
        XCTAssertEqual(body.object, "chat.completion")
        XCTAssertEqual(body.model, "m")
        XCTAssertEqual(body.choices.first?.message.role, "assistant")
        XCTAssertFalse(body.choices.first?.message.content.isEmpty ?? true)
        XCTAssertGreaterThan(body.usage.totalTokens, 0)
    }

    func testChatHandlerStreamEmitsRoleFramesAndDone() async throws {
        let (h, _) = makeChatHandler(registerModel: "m")
        let resp = await h.handle(ChatCompletionRequest(model: "m", messages: [ChatCompletionMessage(role: "user", content: "hi")], stream: true))
        guard case .sse(let sse) = resp else { return XCTFail("expected SSE") }
        var frames: [String] = []
        for await f in sse.frames { frames.append(f) }
        XCTAssertGreaterThanOrEqual(frames.count, 3) // role + >=1 content + stop + [DONE]
        XCTAssertEqual(frames.last, "[DONE]")
        XCTAssertTrue(frames.first!.contains("\"role\":\"assistant\""))
        XCTAssertTrue(frames.contains { $0.contains("\"finish_reason\":\"stop\"") })
    }

    func testChatHandlerConcurrencyCap503() async {
        let counters = ServerCounters()
        let reg = InferenceServerModelRegistry(); reg.register("m", bridge: makeBridge(modelId: "m"))
        let ac = AdmissionControl(maxConcurrentRequests: 1, counters: counters)
        _ = ac.tryEnter() // exhaust the only slot
        let h = ChatCompletionsHandler(registry: reg, admission: ac, counters: counters)
        let resp = await h.handle(ChatCompletionRequest(model: "m", messages: [ChatCompletionMessage(role: "user", content: "hi")]))
        guard case .immediate(let r) = resp else { return XCTFail() }
        XCTAssertEqual(r.status, .serviceUnavailable)
        XCTAssertEqual(r.headers["Retry-After"], "1")
    }

    // MARK: - Embeddings handler

    func testEmbeddingsHandlerReturnsVectors() async throws {
        let counters = ServerCounters()
        let reg = InferenceServerModelRegistry(); reg.registerEmbedder("emb", embedder: EchoEmbedder(dims: 3))
        let ac = AdmissionControl(maxConcurrentRequests: 4, counters: counters)
        let h = EmbeddingsHandler(registry: reg, admission: ac, counters: counters)
        let r = await h.handle(model: "emb", input: .many(["a", "bb"]))
        XCTAssertEqual(r.status, .ok)
        let body = try decode(EmbeddingsResponse.self, r.jsonBody)
        XCTAssertEqual(body.data.count, 2)
        XCTAssertEqual(body.data[0].embedding.count, 3)
        XCTAssertEqual(body.data[0].index, 0)
        XCTAssertEqual(body.data[1].index, 1)
        XCTAssertGreaterThan(body.usage.promptTokens, 0)
    }

    func testEmbeddingsHandlerModelNotLoaded404() async {
        let counters = ServerCounters()
        let reg = InferenceServerModelRegistry()
        let ac = AdmissionControl(maxConcurrentRequests: 4, counters: counters)
        let h = EmbeddingsHandler(registry: reg, admission: ac, counters: counters)
        let r = await h.handle(model: "none", input: .single("x"))
        XCTAssertEqual(r.status, .notFound)
    }

    func testEmbeddingsHandlerEmptyArray400() async {
        let counters = ServerCounters()
        let reg = InferenceServerModelRegistry(); reg.registerEmbedder("emb", embedder: EchoEmbedder())
        let ac = AdmissionControl(maxConcurrentRequests: 4, counters: counters)
        let h = EmbeddingsHandler(registry: reg, admission: ac, counters: counters)
        let r = await h.handle(model: "emb", input: .many([]))
        XCTAssertEqual(r.status, .badRequest)
    }

    // MARK: - Companion handler

    func testCompanionHandlerNonStreamReply() async throws {
        let counters = ServerCounters()
        let resolver = InMemoryCompanionSessionResolver(factory: { id, _ in StubCompanionSession(sessionId: "s", identityId: id, reply: "pong") })
        let ac = AdmissionControl(maxConcurrentRequests: 4, counters: counters)
        let h = CompanionHandler(resolver: resolver, admission: ac, counters: counters)
        let resp = await h.handle(CompanionTurnRequest(sessionId: "s", identityId: "u", message: "ping"))
        guard case .immediate(let r) = resp else { return XCTFail() }
        XCTAssertEqual(r.status, .ok)
        let body = try decode(CompanionTurnResponse.self, r.jsonBody)
        XCTAssertEqual(body.reply, "pong")
        XCTAssertEqual(body.turnIndex, 2)
    }

    func testCompanionHandlerAgenticUsesAgentPath() async throws {
        let counters = ServerCounters()
        let resolver = InMemoryCompanionSessionResolver(factory: { id, _ in StubCompanionSession(sessionId: "s", identityId: id, reply: "base") })
        let ac = AdmissionControl(maxConcurrentRequests: 4, counters: counters)
        let h = CompanionHandler(resolver: resolver, admission: ac, counters: counters)
        let resp = await h.handle(CompanionTurnRequest(sessionId: "s", identityId: "u", message: "do", agentic: true))
        guard case .immediate(let r) = resp else { return XCTFail() }
        let body = try decode(CompanionTurnResponse.self, r.jsonBody)
        XCTAssertEqual(body.reply, "agent:base")
        XCTAssertTrue(body.agentic)
    }

    func testCompanionHandlerMissingFields400() async {
        let counters = ServerCounters()
        let resolver = InMemoryCompanionSessionResolver(factory: { id, _ in StubCompanionSession(sessionId: "s", identityId: id, reply: "x") })
        let ac = AdmissionControl(maxConcurrentRequests: 4, counters: counters)
        let h = CompanionHandler(resolver: resolver, admission: ac, counters: counters)
        let resp = await h.handle(CompanionTurnRequest(sessionId: "", identityId: "u", message: "m"))
        guard case .immediate(let r) = resp else { return XCTFail() }
        XCTAssertEqual(r.status, .badRequest)
    }

    func testCompanionHandlerStreamEmitsDeltaAndDone() async throws {
        let counters = ServerCounters()
        let resolver = InMemoryCompanionSessionResolver(factory: { id, _ in StubCompanionSession(sessionId: "s", identityId: id, reply: "streamed") })
        let ac = AdmissionControl(maxConcurrentRequests: 4, counters: counters)
        let h = CompanionHandler(resolver: resolver, admission: ac, counters: counters)
        let resp = await h.handle(CompanionTurnRequest(sessionId: "s", identityId: "u", message: "m", stream: true))
        guard case .sse(let sse) = resp else { return XCTFail("expected SSE") }
        var frames: [String] = []
        for await f in sse.frames { frames.append(f) }
        XCTAssertEqual(frames.last, "[DONE]")
        XCTAssertTrue(frames.contains { $0.contains("streamed") })
    }

    // MARK: - Admin handler

    func testAdminLoadUnloadAndLifecycle() async throws {
        let reg = InferenceServerModelRegistry()
        let mgr = ModelLifecycleManager(registry: reg, probe: StaticHostResourceProbe())
        let factory = ClosureBridgeFactory { id, _, _ in makeBridge(modelId: id) }
        let admin = AdminHandler(manager: mgr, factory: factory)

        let load = await admin.load(AdminLoadRequest(modelId: "m", backend: "Cpu", tier: "Tier1_Small", ramRequiredBytes: 1000))
        XCTAssertEqual(load.status, .ok)

        let life = admin.lifecycle()
        let lifeBody = try decode(AdminLifecycleResponse.self, life.jsonBody)
        XCTAssertEqual(lifeBody.loaded.count, 1)
        XCTAssertEqual(lifeBody.loaded.first?.modelId, "m")

        let unload = await admin.unload("m")
        XCTAssertEqual(unload.status, .ok)
        let unloadAgain = await admin.unload("m")
        XCTAssertEqual(unloadAgain.status, .notFound)
    }

    func testAdminLoadInvalidBackend400() async {
        let reg = InferenceServerModelRegistry()
        let mgr = ModelLifecycleManager(registry: reg, probe: StaticHostResourceProbe())
        let admin = AdminHandler(manager: mgr, factory: ClosureBridgeFactory { id, _, _ in makeBridge(modelId: id) })
        let r = await admin.load(AdminLoadRequest(modelId: "m", backend: "Quantum", tier: "Tier1_Small"))
        XCTAssertEqual(r.status, .badRequest)
    }

    func testUnconfiguredBridgeFactoryThrows() async {
        let f = UnconfiguredBridgeFactory()
        do { _ = try await f.create(modelId: "m", backend: .cpu, tier: .tier1Small); XCTFail() }
        catch { XCTAssertEqual(error as? InferenceServerError, .noBridgeFactory) }
    }

    // MARK: - DTO wire shape

    func testChatCompletionRequestDecodesSnakeCase() throws {
        let json = """
        {"model":"m","messages":[{"role":"user","content":"hi"}],"max_tokens":42,"top_p":0.8,"stream":true,"stop":["<end>"],"user":"u1"}
        """
        let req = try decode(ChatCompletionRequest.self, Data(json.utf8))
        XCTAssertEqual(req.model, "m")
        XCTAssertEqual(req.maxTokens, 42)
        XCTAssertEqual(req.topP ?? 0, 0.8, accuracy: 1e-6)
        XCTAssertTrue(req.stream)
        XCTAssertEqual(req.stop, ["<end>"])
        XCTAssertEqual(req.user, "u1")
    }

    func testChatMessageOmitsReasoningWhenNil() throws {
        let m = ChatCompletionMessage(role: "assistant", content: "hi")
        let data = try JSONEncoder().encode(m)
        let str = String(data: data, encoding: .utf8)!
        XCTAssertFalse(str.contains("reasoning_content"))
    }

    func testChatMessageEmitsReasoningWhenPresent() throws {
        let m = ChatCompletionMessage(role: "assistant", content: "hi", reasoningContent: "because")
        let data = try JSONEncoder().encode(m)
        let str = String(data: data, encoding: .utf8)!
        XCTAssertTrue(str.contains("reasoning_content"))
    }

    func testEmbeddingsRequestDecodesSingleString() throws {
        let req = try decode(EmbeddingsRequest.self, Data(#"{"model":"emb","input":"hello"}"#.utf8))
        XCTAssertEqual(req.model, "emb")
        XCTAssertEqual(req.input, .single("hello"))
    }

    func testEmbeddingsRequestDecodesArray() throws {
        let req = try decode(EmbeddingsRequest.self, Data(#"{"model":"emb","input":["a","b"]}"#.utf8))
        XCTAssertEqual(req.input, .many(["a", "b"]))
    }

    func testEmbeddingsRequestRejectsNonStringInput() {
        XCTAssertThrowsError(try decode(EmbeddingsRequest.self, Data(#"{"model":"emb","input":123}"#.utf8)))
    }

    func testEmbeddingsHandlerViaRequestDto() async throws {
        let counters = ServerCounters()
        let reg = InferenceServerModelRegistry(); reg.registerEmbedder("emb", embedder: EchoEmbedder(dims: 2))
        let ac = AdmissionControl(maxConcurrentRequests: 4, counters: counters)
        let h = EmbeddingsHandler(registry: reg, admission: ac, counters: counters)
        let r = await h.handle(EmbeddingsRequest(model: "emb", input: .single("hi")))
        XCTAssertEqual(r.status, .ok)
        let body = try decode(EmbeddingsResponse.self, r.jsonBody)
        XCTAssertEqual(body.data.count, 1)
        XCTAssertEqual(body.data[0].embedding.count, 2)
    }

    func testErrorResponseShape() throws {
        let e = ErrorResponse.of("boom", type: "invalid_request_error", code: "x")
        let data = try JSONEncoder().encode(e)
        let str = String(data: data, encoding: .utf8)!
        XCTAssertTrue(str.contains("\"message\":\"boom\""))
        XCTAssertTrue(str.contains("\"type\":\"invalid_request_error\""))
        XCTAssertTrue(str.contains("\"code\":\"x\""))
    }

    func testBuildInferenceRequestJoinsMessages() {
        let body = ChatCompletionRequest(model: "m", messages: [
            ChatCompletionMessage(role: "system", content: "sys"),
            ChatCompletionMessage(role: "user", content: "hi"),
        ], maxTokens: 100, user: "u")
        let req = ChatCompletionsHandler.buildInferenceRequest(body)
        XCTAssertTrue(req.prompt.contains("<|system|>\nsys\n<|end|>"))
        XCTAssertTrue(req.prompt.contains("<|user|>\nhi\n<|end|>"))
        XCTAssertEqual(req.maxOutputTokens, 100)
        XCTAssertEqual(req.metadata["user"], "u")
    }

    func testMapFinishReasons() {
        XCTAssertEqual(ChatCompletionsHandler.mapFinish(.completed), "stop")
        XCTAssertEqual(ChatCompletionsHandler.mapFinish(.stoppedByLength), "length")
        XCTAssertEqual(ChatCompletionsHandler.mapFinish(.cancelled), "cancelled")
        XCTAssertEqual(ChatCompletionsHandler.mapFinish(.failed), "error")
    }

    func testBackendAndTierParsing() {
        XCTAssertEqual(BackendKind.parse("cpu"), .cpu)
        XCTAssertEqual(BackendKind.parse("OpenCL"), .openCL)
        XCTAssertNil(BackendKind.parse("bogus"))
        XCTAssertEqual(CapabilityTier.parse("Tier0_Tiny"), .tier0Tiny)
        XCTAssertEqual(CapabilityTier.parse("tier4frontier"), .tier4Frontier)
        XCTAssertNil(CapabilityTier.parse("Tier9"))
    }

    // MARK: - Helpers

    private func makeChatHandler(registerModel: String? = "m") -> (ChatCompletionsHandler, InferenceServerModelRegistry) {
        let counters = ServerCounters()
        let reg = InferenceServerModelRegistry()
        if let id = registerModel { reg.register(id, bridge: makeBridge(modelId: id)) }
        let ac = AdmissionControl(maxConcurrentRequests: 8, counters: counters)
        return (ChatCompletionsHandler(registry: reg, admission: ac, counters: counters), reg)
    }
}

/// Async-safe call counter for resolver single-flight assertions.
private actor CallCounter {
    private(set) var value = 0
    func inc() { value += 1 }
}

/// Closure-backed bridge factory for admin tests.
private struct ClosureBridgeFactory: IBridgeFactory {
    let make: @Sendable (String, BackendKind, CapabilityTier) -> IInferenceBridge
    func create(modelId: String, backend: BackendKind, tier: CapabilityTier) async throws -> IInferenceBridge {
        make(modelId, backend, tier)
    }
}
