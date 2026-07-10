// LayerStreamingInferenceTests.swift

import XCTest
@testable import CircleAI

/// Deterministic runner: each layer adds its index to every hidden value and
/// records the evict order.
private final class RecordingRunner: ILayerStreamingRunner, @unchecked Sendable {
    let backendId = "recording"
    let isAvailable = true
    private(set) var evicted: [Int] = []
    private let lock = NSLock()

    func runLayer(shard: LayerWeightShard, inputHidden: [Float]) async throws -> LayerActivations {
        let out = inputHidden.map { $0 + Float(shard.layerIndex) }
        return LayerActivations(layerIndex: shard.layerIndex, hidden: out)
    }

    func evict(layerIndex: Int) async throws {
        lock.lock(); evicted.append(layerIndex); lock.unlock()
    }
}

final class LayerStreamingInferenceTests: XCTestCase {

    func testNullRunnerThrowsOnRun() async {
        let r = NullLayerStreamingRunner.instance
        XCTAssertFalse(r.isAvailable)
        do {
            _ = try await r.runLayer(shard: LayerWeightShard(layerIndex: 0, weightShardPath: "x", approxBytes: 1), inputHidden: [1])
            XCTFail("expected throw")
        } catch {
            XCTAssertEqual(error as? LayerStreamingError, .noRunnerWired)
        }
    }

    func testOrchestratorRunsAllLayersInOrderAndEvicts() async throws {
        let runner = RecordingRunner()
        let orch = LayerStreamingOrchestrator(runner: runner)
        let shards = (0..<3).map { LayerWeightShard(layerIndex: $0, weightShardPath: "layer_\($0)", approxBytes: 10) }
        let plan = LayerStreamingPlan(modelId: "m", totalLayers: 3, shards: shards, approxParameterBytes: 30)

        var completed: [Int] = []
        let final = try await orch.forward(plan: plan, initialHidden: [0, 0]) { act in
            completed.append(act.layerIndex)
        }
        // Hidden accumulates 0+0 +0 +1 +2 = 3 per element.
        XCTAssertEqual(final.hidden, [3, 3])
        XCTAssertEqual(final.layerIndex, 2)
        XCTAssertEqual(completed, [0, 1, 2])
        XCTAssertEqual(runner.evicted, [0, 1, 2])
    }

    func testOrchestratorEmptyPlanThrows() async {
        let orch = LayerStreamingOrchestrator(runner: RecordingRunner())
        let plan = LayerStreamingPlan(modelId: "m", totalLayers: 0, shards: [], approxParameterBytes: 0)
        do {
            _ = try await orch.forward(plan: plan, initialHidden: [1])
            XCTFail("expected throw")
        } catch {
            XCTAssertEqual(error as? LayerStreamingError, .emptyPlan)
        }
    }

    func testShardDiscoveryParsesAndSorts() throws {
        let dir = (NSTemporaryDirectory() as NSString).appendingPathComponent("circleai-shards-\(UUID().uuidString)")
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(atPath: dir) }

        // Create out-of-order layer files + one non-layer file that must be ignored.
        for idx in [2, 0, 1] {
            let p = (dir as NSString).appendingPathComponent("layer_\(idx).safetensors")
            try Data(repeating: 0, count: idx + 1).write(to: URL(fileURLWithPath: p))
        }
        try Data("noise".utf8).write(to: URL(fileURLWithPath: (dir as NSString).appendingPathComponent("config.json")))

        let plan = try LayerShardDiscovery.discover(modelId: "m", modelDirectory: dir)
        XCTAssertEqual(plan.totalLayers, 3)
        XCTAssertEqual(plan.shards.map { $0.layerIndex }, [0, 1, 2])
        XCTAssertEqual(plan.approxParameterBytes, Int64(1 + 2 + 3))
    }

    func testShardDiscoveryThrowsForMissingDirectory() {
        XCTAssertThrowsError(try LayerShardDiscovery.discover(modelId: "m", modelDirectory: "/no/such/dir/xyz")) { err in
            if case LayerStreamingError.modelDirectoryNotFound = err { } else { XCTFail("wrong error \(err)") }
        }
    }
}
