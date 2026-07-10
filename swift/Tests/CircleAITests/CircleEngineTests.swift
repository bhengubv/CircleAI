// CircleEngineTests.swift
//
// Exercises CircleEngine's type-keyed module bag + the settable embeddingService
// property (port of CircleAI.Core.CircleEngine), plus the ICircleModule /
// IEmbeddingService contracts.

import XCTest
@testable import CircleAI

// A protocol the engine can key a module by (interface-type registration).
private protocol IFakeService: AnyObject { var tag: String { get } }

private final class FakeService: IFakeService, @unchecked Sendable {
    let tag: String
    init(tag: String) { self.tag = tag }
}

// A minimal embedding module.
private final class FakeEmbeddingService: IEmbeddingService, @unchecked Sendable {
    let moduleName = "fake-embed"
    private(set) var isModelLoaded = false
    let embeddingSize = 3

    func initAsync(engine: CircleEngine) async throws { isModelLoaded = true }
    func dispose() { isModelLoaded = false }

    func generateEmbedding(_ text: String) -> [Float] {
        // Deterministic: length-based fill so tests can assert.
        [Float(text.count), 0, 1]
    }
}

// A loader stub for the engine's required IModelLoader.
private final class StubLoader: IModelLoader, @unchecked Sendable {
    func downloadModel(_ modelName: String, progress: (@Sendable (Float) -> Void)?) async throws -> String { "/x" }
    func getModelPath(_ modelName: String) throws -> String { "/x" }
    func modelExists(_ modelName: String) -> Bool { false }
    func checkForCriticalUpdate() async -> Bool { false }
    func dispose() {}
}

final class CircleEngineTests: XCTestCase {

    func testRegisterAndGetModuleByConcreteType() {
        let engine = CircleEngine(modelLoader: StubLoader())
        let svc = FakeService(tag: "concrete")
        engine.registerModule(svc)
        XCTAssertTrue(engine.hasModule(FakeService.self))
        XCTAssertEqual(engine.getModule(FakeService.self)?.tag, "concrete")
        XCTAssertNil(engine.getModule(FakeEmbeddingService.self))
    }

    func testRegisterModuleByInterfaceType() {
        let engine = CircleEngine(modelLoader: StubLoader())
        let svc = FakeService(tag: "iface")
        engine.registerModule(svc as IFakeService, as: IFakeService.self)
        XCTAssertTrue(engine.hasModule(IFakeService.self))
        XCTAssertEqual(engine.getModule(IFakeService.self)?.tag, "iface")
        // Not registered under the concrete type.
        XCTAssertFalse(engine.hasModule(FakeService.self))
    }

    func testRegisterReturnsSelfForChaining() {
        let engine = CircleEngine(modelLoader: StubLoader())
        let returned = engine.registerModule(FakeService(tag: "a"))
        XCTAssertTrue(returned === engine)
    }

    func testEmbeddingServiceProperty() {
        let engine = CircleEngine(modelLoader: StubLoader())
        XCTAssertNil(engine.embeddingService)
        let embed = FakeEmbeddingService()
        engine.embeddingService = embed
        XCTAssertNotNil(engine.embeddingService)
        XCTAssertTrue((engine.embeddingService as? FakeEmbeddingService) === embed)
    }

    func testEmbeddingModuleContract() async throws {
        let embed = FakeEmbeddingService()
        XCTAssertFalse(embed.isModelLoaded)
        let engine = CircleEngine(modelLoader: StubLoader())
        try await embed.initAsync(engine: engine)
        XCTAssertTrue(embed.isModelLoaded)
        XCTAssertEqual(embed.embeddingSize, 3)
        XCTAssertEqual(embed.generateEmbedding("abcd"), [4, 0, 1])
        embed.dispose()
        XCTAssertFalse(embed.isModelLoaded)
    }

    func testModelLoaderExposed() {
        let loader = StubLoader()
        let engine = CircleEngine(modelLoader: loader)
        XCTAssertTrue((engine.modelLoader as? StubLoader) === loader)
    }
}
