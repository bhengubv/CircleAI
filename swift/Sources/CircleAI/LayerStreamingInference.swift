// LayerStreamingInference.swift
//
// (3.3.0) Layer-by-layer streaming inference — load one transformer layer's
// weights at a time from disk into RAM, run forward, save activations, evict
// the layer, load the next. Lets a 70B model fit on a 4 GB device at the cost
// of disk bandwidth per token.
//
// The actual MNN/CUDA glue is host-supplied via ILayerStreamingRunner. This
// file defines the contract + a null default + simple orchestrator + shard
// discovery. Ported from CircleAI.Inference.LayerStreamingInference.

import Foundation

/// (3.3.0) One layer's weights packed for streaming.
public struct LayerWeightShard: Sendable, Equatable {
    /// 0-based transformer layer index.
    public let layerIndex: Int
    /// Path on disk to this layer's tensor shard.
    public let weightShardPath: String
    /// Size of the shard, for memory accounting.
    public let approxBytes: Int64

    public init(layerIndex: Int, weightShardPath: String, approxBytes: Int64) {
        self.layerIndex = layerIndex
        self.weightShardPath = weightShardPath
        self.approxBytes = approxBytes
    }
}

/// (3.3.0) Layer-streaming model plan.
public struct LayerStreamingPlan: Sendable, Equatable {
    public let modelId: String
    public let totalLayers: Int
    public let shards: [LayerWeightShard]
    public let approxParameterBytes: Int64

    public init(modelId: String, totalLayers: Int, shards: [LayerWeightShard], approxParameterBytes: Int64) {
        self.modelId = modelId
        self.totalLayers = totalLayers
        self.shards = shards
        self.approxParameterBytes = approxParameterBytes
    }
}

/// (3.3.0) One layer's hidden-state output after forward.
public struct LayerActivations: Sendable, Equatable {
    public let layerIndex: Int
    public let hidden: [Float]

    public init(layerIndex: Int, hidden: [Float]) {
        self.layerIndex = layerIndex
        self.hidden = hidden
    }
}

/// (3.3.0) Host-supplied per-layer runner (load + forward + evict).
public protocol ILayerStreamingRunner: Sendable {
    var backendId: String { get }
    var isAvailable: Bool { get }

    /// Forward one layer; returns hidden states.
    func runLayer(shard: LayerWeightShard, inputHidden: [Float]) async throws -> LayerActivations

    /// Drop the layer from RAM after forward.
    func evict(layerIndex: Int) async throws
}

/// Errors raised by the layer-streaming orchestrator + null runner.
public enum LayerStreamingError: Error, Equatable, CustomStringConvertible {
    case noRunnerWired
    case emptyPlan
    case modelDirectoryNotFound(String)

    public var description: String {
        switch self {
        case .noRunnerWired:
            return "No ILayerStreamingRunner is wired. Register one to enable layer-streaming."
        case .emptyPlan:
            return "Plan has no layer shards."
        case .modelDirectoryNotFound(let d):
            return "Model directory not found: \(d)"
        }
    }
}

/// (3.3.0) Null runner that throws on use — drop-in default.
public struct NullLayerStreamingRunner: ILayerStreamingRunner {
    public static let instance = NullLayerStreamingRunner()
    public init() {}
    public var backendId: String { "null" }
    public var isAvailable: Bool { false }

    public func runLayer(shard: LayerWeightShard, inputHidden: [Float]) async throws -> LayerActivations {
        throw LayerStreamingError.noRunnerWired
    }

    public func evict(layerIndex: Int) async throws {}
}

/// (3.3.0) Drives a full forward pass layer by layer.
public struct LayerStreamingOrchestrator: Sendable {
    private let runner: ILayerStreamingRunner

    public init(runner: ILayerStreamingRunner) {
        self.runner = runner
    }

    /// (3.3.0) Stream every layer in `plan`, evicting after each. Returns the
    /// final hidden state. `onLayerComplete` fires after each layer so callers
    /// can update progress / cancel mid-pass.
    public func forward(
        plan: LayerStreamingPlan,
        initialHidden: [Float],
        onLayerComplete: (@Sendable (LayerActivations) -> Void)? = nil
    ) async throws -> LayerActivations {
        guard !plan.shards.isEmpty else { throw LayerStreamingError.emptyPlan }

        var hidden = initialHidden
        var last: LayerActivations?
        for shard in plan.shards {
            try Task.checkCancellation()
            let activations = try await runner.runLayer(shard: shard, inputHidden: hidden)
            last = activations
            hidden = activations.hidden
            onLayerComplete?(activations)
            try await runner.evict(layerIndex: shard.layerIndex)
        }
        return last!
    }
}

/// (3.3.0) Discover layer shards on disk from a manifest directory.
public enum LayerShardDiscovery {
    /// Scan `modelDirectory` for files named `layer_NNN.<ext>` and build a
    /// `LayerStreamingPlan`. Shards are sorted by ascending layer index.
    public static func discover(modelId: String, modelDirectory: String) throws -> LayerStreamingPlan {
        precondition(!modelId.trimmingCharacters(in: .whitespaces).isEmpty, "modelId required")
        let fm = FileManager.default
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: modelDirectory, isDirectory: &isDir), isDir.boolValue else {
            throw LayerStreamingError.modelDirectoryNotFound(modelDirectory)
        }

        var shards: [LayerWeightShard] = []
        var total: Int64 = 0
        let names = (try? fm.contentsOfDirectory(atPath: modelDirectory)) ?? []
        for name in names where name.hasPrefix("layer_") {
            let stem = (name as NSString).deletingPathExtension // "layer_NNN"
            guard let underscore = stem.firstIndex(of: "_") else { continue }
            let idxStr = String(stem[stem.index(after: underscore)...])
            guard let index = Int(idxStr) else { continue }
            let path = (modelDirectory as NSString).appendingPathComponent(name)
            let attrs = try? fm.attributesOfItem(atPath: path)
            let size = (attrs?[.size] as? NSNumber)?.int64Value ?? 0
            shards.append(LayerWeightShard(layerIndex: index, weightShardPath: path, approxBytes: size))
            total += size
        }

        shards.sort { $0.layerIndex < $1.layerIndex }
        return LayerStreamingPlan(modelId: modelId, totalLayers: shards.count, shards: shards, approxParameterBytes: total)
    }
}
