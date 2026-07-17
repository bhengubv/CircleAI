// NeuronTests.swift — the Swift Neuron port.
//
// Mirrors the C# CircleAI.Tests Neuron suite: the concierge decision table +
// gate, the two-slot admission gate + eviction, the router-gated slot selection
// inside AIService (specialist hot-load, generalist floor), and the NeuronNode
// facade.

import XCTest
@testable import CircleAI

// MARK: - test doubles

/// IChatGenerator + real session round-trip (returns true).
final class NeuronSessionGen: IChatGenerator, @unchecked Sendable {
    let reply: String
    init(reply: String) { self.reply = reply }
    func generate(messages: [ChatMessage], options: GenerationOptions?) async throws -> String { reply }
    func stream(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<String> {
        AsyncStream { c in c.yield(reply); c.finish() }
    }
    func saveSession(path: String) async throws -> Bool { true }
    func loadSession(path: String) async throws -> Bool { true }
}

struct NeuronFixedRouter: INeuronRouter {
    let decision: RouteDecision
    func route(_ context: RouteContext) -> RouteDecision { decision }
}

struct NeuronFakeSelector: IModelSelector {
    let selection: ModelSelection
    func bestFit(_ probe: DeviceProbe, required: ChatCapability) throws -> ModelSelection { selection }
    func allCandidates(_ probe: DeviceProbe) -> [ModelSelection] { [selection] }
}

final class NeuronFakeLoader: IModelLoader, @unchecked Sendable {
    let path: String
    init(path: String) { self.path = path }
    func downloadModel(_ modelName: String, progress: (@Sendable (Float) -> Void)?) async throws -> String { path }
    func getModelPath(_ modelName: String) throws -> String { path }
    func modelExists(_ modelName: String) -> Bool { true }
    func checkForCriticalUpdate() async -> Bool { false }
    func dispose() {}
}

final class NeuronCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var v = 0
    func inc() { lock.lock(); v += 1; lock.unlock() }
    var value: Int { lock.lock(); defer { lock.unlock() }; return v }
}

final class NeuronTests: XCTestCase {

    private func tempModel() -> String {
        let p = FileManager.default.temporaryDirectory
            .appendingPathComponent("neuron-\(UUID().uuidString).model").path
        FileManager.default.createFile(atPath: p, contents: Data("m".utf8))
        return p
    }

    private func sel(_ id: String, _ bytes: Int64) -> ModelSelection {
        ModelSelection(modelId: id, requiresDownload: false, estimatedBytes: bytes, tier: .desktop)
    }

    // MARK: concierge router + gate

    func testRouterPlainGeneralist() {
        let d = HeuristicNeuronRouter().route(RouteContext(query: "what's the weather today?"))
        XCTAssertEqual(d.organ, .generalist)
        XCTAssertEqual(d.capability, .defaultCap)
    }

    func testRouterVision() {
        let d = HeuristicNeuronRouter().route(RouteContext(query: "what is this?", hasImage: true))
        XCTAssertEqual(d.organ, .specialist)
        XCTAssertEqual(d.capability, .vision)
    }

    func testRouterReasoning() {
        let d = HeuristicNeuronRouter().route(RouteContext(query: "please debug this stack trace"))
        XCTAssertEqual(d.organ, .specialist)
        XCTAssertEqual(d.capability, .reasoning)
    }

    func testRouterLongContext() {
        let d = HeuristicNeuronRouter(longContextChars: 50)
            .route(RouteContext(query: String(repeating: "x", count: 60)))
        XCTAssertEqual(d.organ, .specialist)
        XCTAssertEqual(d.capability, .longContext)
    }

    func testRouterGateVeto() {
        let gate = NeuronGate(allowSpecialist: { _ in false })
        let d = HeuristicNeuronRouter(gate: gate).route(RouteContext(query: "solve this equation"))
        XCTAssertEqual(d.organ, .generalist)
    }

    // MARK: resident slot manager

    func testSlotAdmitsWithinBudget() async {
        let m = ResidentSlotManager(generalistReservedBytes: 1000, ramAvailable: { Int64(1_000_000) })
        let g = NeuronSessionGen(reply: "S")
        let a = await m.ensureSpecialist(sel("spec", 5000)) { _ in g }
        XCTAssertEqual(a.outcome, .admitted)
        XCTAssertEqual(m.residentSpecialistModelId, "spec")
    }

    func testSlotDeniesOverBudget() async {
        let m = ResidentSlotManager(generalistReservedBytes: 900_000, ramAvailable: { Int64(1_000_000) })
        let a = await m.ensureSpecialist(sel("spec", 500_000)) { _ in NeuronSessionGen(reply: "S") }
        XCTAssertEqual(a.outcome, .insufficientRam)
        XCTAssertNil(m.residentSpecialistModelId)
    }

    func testSlotAlreadyResident() async {
        let m = ResidentSlotManager(generalistReservedBytes: 0, ramAvailable: { Int64(1_000_000) })
        let builds = NeuronCounter()
        _ = await m.ensureSpecialist(sel("spec", 1)) { _ in builds.inc(); return NeuronSessionGen(reply: "S") }
        let second = await m.ensureSpecialist(sel("spec", 1)) { _ in builds.inc(); return NeuronSessionGen(reply: "S") }
        XCTAssertEqual(second.outcome, .alreadyResident)
        XCTAssertEqual(builds.value, 1)
    }

    func testSlotSwapEvicts() async {
        let m = ResidentSlotManager(generalistReservedBytes: 0, ramAvailable: { Int64(1_000_000) })
        _ = await m.ensureSpecialist(sel("A", 1)) { _ in NeuronSessionGen(reply: "A") }
        _ = await m.ensureSpecialist(sel("B", 1)) { _ in NeuronSessionGen(reply: "B") }
        XCTAssertEqual(m.residentSpecialistModelId, "B")
    }

    func testSlotBuildFailure() async {
        let m = ResidentSlotManager(generalistReservedBytes: 0, ramAvailable: { Int64(1_000_000) })
        let a = await m.ensureSpecialist(sel("spec", 1)) { _ in nil }
        XCTAssertEqual(a.outcome, .buildFailed)
        XCTAssertNil(m.residentSpecialistModelId)
    }

    func testSlotEvict() async {
        let m = ResidentSlotManager(generalistReservedBytes: 0, ramAvailable: { Int64(1_000_000) })
        _ = await m.ensureSpecialist(sel("spec", 1)) { _ in NeuronSessionGen(reply: "S") }
        m.evictSpecialist()
        XCTAssertNil(m.residentSpecialistModelId)
    }

    // MARK: AIService two-slot residency

    func testRouterNilUsesGeneralist() async throws {
        let svc = AIService(
            options: AIOptions(modelPath: tempModel(), warmOnStart: false),
            generatorFactory: { _ in NeuronSessionGen(reply: "GEN") })
        try await svc.start()
        let r = try await svc.ask("solve this equation") // reasoning cue, but no router
        XCTAssertEqual(r, "GEN")
    }

    func testHotLoadsSpecialist() async throws {
        let genPath = tempModel()
        let specPath = tempModel()
        let gen = NeuronSessionGen(reply: "GEN")
        let spec = NeuronSessionGen(reply: "SPEC")
        let svc = AIService(
            options: AIOptions(modelId: "gen-model", modelPath: genPath, warmOnStart: false),
            modelLoader: NeuronFakeLoader(path: specPath),
            generatorFactory: { path in path == specPath ? spec : gen },
            modelSelector: NeuronFakeSelector(selection: sel("spec-model", 1024)),
            router: NeuronFixedRouter(decision: .specialist(.reasoning, "t")))
        try await svc.start()
        let r = try await svc.ask("anything")
        XCTAssertEqual(r, "SPEC")
    }

    func testBestFitEqualsGeneralist() async throws {
        let genPath = tempModel()
        let gen = NeuronSessionGen(reply: "GEN")
        let svc = AIService(
            options: AIOptions(modelId: "gen-model", modelPath: genPath, warmOnStart: false),
            modelLoader: NeuronFakeLoader(path: genPath),
            generatorFactory: { _ in gen },
            modelSelector: NeuronFakeSelector(selection: sel("gen-model", 1024)),
            router: NeuronFixedRouter(decision: .specialist(.reasoning, "t")))
        try await svc.start()
        let r = try await svc.ask("anything")
        XCTAssertEqual(r, "GEN") // best-fit resolved to the generalist itself
    }

    func testSessionRoundTrip() async throws {
        let svc = AIService(
            options: AIOptions(modelPath: tempModel(), warmOnStart: false),
            generatorFactory: { _ in NeuronSessionGen(reply: "GEN") })
        try await svc.start()
        let snap = tempModel()
        let saved = await svc.saveSession(path: snap)
        XCTAssertTrue(saved)
        let loaded = await svc.loadSession(path: snap)
        XCTAssertTrue(loaded)
    }

    // MARK: NeuronNode facade + NullChatRuntime

    func testNeuronNodeOverBrain() async throws {
        let svc = AIService(
            options: AIOptions(modelId: "qwen-x", modelPath: tempModel(), warmOnStart: false),
            generatorFactory: { _ in NeuronSessionGen(reply: "hello") })
        let node = NeuronNode(brain: svc)

        XCTAssertEqual(node.id, "circleai-neuron")
        XCTAssertFalse(node.isReady)
        XCTAssertEqual(node.statusMessage, "loading model…")

        try await svc.start()
        XCTAssertTrue(node.isReady)
        XCTAssertEqual(node.statusMessage, "ready")
        XCTAssertTrue(node.engineLabel.contains("qwen-x"))

        var out = ""
        for try await c in node.stream([ChatTurn(role: "user", content: "hi")]) { out += c }
        XCTAssertEqual(out, "hello")
        XCTAssertNotNil(node.sessionSnapshotPath)
    }

    func testNullRuntime() async throws {
        let null = NullChatRuntime()
        XCTAssertFalse(null.isReady)
        var out = ""
        for try await c in null.stream([ChatTurn(role: "user", content: "hi")]) { out += c }
        XCTAssertTrue(out.contains("No chat engine"))
    }
}
