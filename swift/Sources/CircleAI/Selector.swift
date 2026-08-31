// Selector.swift
//
// ChatCapability + IModelSelector + DeviceAwareModelSelector.

import Foundation

/// Bit-flag capability set. Declared as an OptionSet for ergonomic
/// composition: `[.tools, .vision]`.
public struct ChatCapability: OptionSet, Sendable, Equatable {
    public let rawValue: Int
    public init(rawValue: Int) { self.rawValue = rawValue }

    public static let none = ChatCapability([])
    public static let defaultCap = ChatCapability(rawValue: 1)
    public static let tools = ChatCapability(rawValue: 2)
    public static let vision = ChatCapability(rawValue: 4)
    public static let longContext = ChatCapability(rawValue: 8)
    public static let reasoning = ChatCapability(rawValue: 16)
    /// (3.1.0) Model generates short videos from a text prompt.
    public static let video = ChatCapability(rawValue: 32)
}

public struct ModelSelection: Sendable, Equatable {
    public let modelId: String
    public let requiresDownload: Bool
    public let estimatedBytes: Int64
    public let tier: DeviceTier

    public init(modelId: String, requiresDownload: Bool, estimatedBytes: Int64, tier: DeviceTier) {
        self.modelId = modelId
        self.requiresDownload = requiresDownload
        self.estimatedBytes = estimatedBytes
        self.tier = tier
    }
}

public protocol IModelSelector: Sendable {
    func bestFit(_ probe: DeviceProbe, required: ChatCapability) throws -> ModelSelection
    func allCandidates(_ probe: DeviceProbe) -> [ModelSelection]
}

public enum SelectorError: Error, CustomStringConvertible {
    case emptyRegistry
    case noModelForCapabilities(ChatCapability)

    public var description: String {
        switch self {
        case .emptyRegistry: return "Model registry is empty. Cannot select a model."
        case .noModelForCapabilities(let c): return "No model in the registry satisfies required capabilities \(c.rawValue)."
        }
    }
}

public struct DeviceAwareModelSelector: IModelSelector {
    private let registry: ModelRegistryService
    public init(registry: ModelRegistryService) { self.registry = registry }

    public func bestFit(_ probe: DeviceProbe, required: ChatCapability = .defaultCap) throws -> ModelSelection {
        let entries = registry.allModels
        guard !entries.isEmpty else { throw SelectorError.emptyRegistry }
        let ramGb = Double(probe.ramAvailableBytes) / (1024.0 * 1024 * 1024)
        let storageGb = Double(probe.storageFreeBytes) / (1024.0 * 1024 * 1024)

        let capabilityOk = entries.filter { satisfies($0, required: required) }
        guard !capabilityOk.isEmpty else {
            throw SelectorError.noModelForCapabilities(required)
        }

        let deviceOk = capabilityOk.filter {
            $0.minRamGb <= ramGb + 1e-4 &&
            (storageGb <= 0 || $0.minStorageGb <= storageGb + 1e-4)
        }
        let candidates = deviceOk.isEmpty ? capabilityOk : deviceOk
        let winner = candidates.max(by: { a, b in
            if a.qualityRank != b.qualityRank { return a.qualityRank < b.qualityRank }
            return a.minRamGb > b.minRamGb // smaller min ram preferred -> reverse for `max`
        })!

        return ModelSelection(
            modelId: winner.name,
            requiresDownload: true,
            estimatedBytes: winner.totalBytes,
            tier: probe.classify()
        )
    }

    public func allCandidates(_ probe: DeviceProbe) -> [ModelSelection] {
        let tier = probe.classify()
        return registry.allModels
            .sorted { $0.qualityRank > $1.qualityRank }
            .map {
                ModelSelection(modelId: $0.name, requiresDownload: true, estimatedBytes: $0.totalBytes, tier: tier)
            }
    }

    private func satisfies(_ entry: ModelEntry, required: ChatCapability) -> Bool {
        if required == .none { return true }
        let declared = parseCapabilities(entry.capabilities)
        return declared.contains(required)
    }
}

public func parseCapabilities(_ labels: [String]?) -> ChatCapability {
    guard let labels = labels, !labels.isEmpty else { return .defaultCap }
    var result: ChatCapability = []
    for label in labels {
        let key = label.trimmingCharacters(in: .whitespaces).uppercased().replacingOccurrences(of: " ", with: "_")
        switch key {
        case "DEFAULT": result.insert(.defaultCap)
        case "TOOLS": result.insert(.tools)
        case "VISION": result.insert(.vision)
        case "LONGCONTEXT", "LONG_CONTEXT": result.insert(.longContext)
        case "REASONING": result.insert(.reasoning)
        default: break
        }
    }
    return result.isEmpty ? .defaultCap : result
}

// MARK: - Selection quality and modality plans
//
// Ported from CircleAI.Inference (IModelSelector.cs, SpeechModelSelector.cs).

/// How well a selection actually matches what was asked for.
public enum SelectionQuality: Int, Sendable, Equatable {
    /// An entry satisfied the capability flags AND the device gates.
    case good = 0
    /// Fits the device, but below the caller's requested quality floor.
    case belowFloor
    /// Nothing in the catalogue clears this device.
    case nothingFits
    /// No model at all: a built-in heuristic is standing in.
    case heuristicFallback
    /// The feature is off by design on this device.
    case unavailable
}

/// The outcome of planning one modality: a quality, an optional model, and the
/// reason in words a person can read.
public struct ModalityPlan: Sendable, Equatable {
    public let quality: SelectionQuality
    public let model: ModelSelection?
    public let reason: String

    public init(quality: SelectionQuality, model: ModelSelection?, reason: String) {
        self.quality = quality
        self.model = model
        self.reason = reason
    }

    /// Anything other than `.unavailable` is something the caller can use.
    public var isAvailable: Bool { quality != .unavailable }

    /// True when no real model was chosen and a built-in heuristic answers.
    public var usesBuiltIn: Bool { quality == .heuristicFallback }
}
