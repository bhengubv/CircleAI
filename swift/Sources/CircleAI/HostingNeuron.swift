// HostingNeuron.swift
//
// The Neuron — concierge router + two-slot residency + host-neutral facade.
// Port of CircleAI.Hosting.Neuron. The Swift AIService is rich (owns selector +
// loader + brownout), so the two-slot path mirrors the C# reference directly:
// route → bestFit(capability) → build the specialist by model id.

import Foundation

// MARK: - Concierge router

/// Which organ answers a turn.
public enum Organ: Sendable, Equatable {
    case generalist
    case specialist
}

/// Inputs the concierge classifies for a single turn.
public struct RouteContext: Sendable {
    public let query: String
    public let hasImage: Bool
    public init(query: String, hasImage: Bool = false) {
        self.query = query
        self.hasImage = hasImage
    }
}

/// The concierge's per-turn decision.
public struct RouteDecision: Sendable {
    public let organ: Organ
    public let capability: ChatCapability
    public let reason: String
    public init(organ: Organ, capability: ChatCapability, reason: String) {
        self.organ = organ
        self.capability = capability
        self.reason = reason
    }
    /// Route to the always-warm generalist.
    public static func generalist(_ reason: String = "generalist") -> RouteDecision {
        RouteDecision(organ: .generalist, capability: .defaultCap, reason: reason)
    }
    /// Route to a capability-matched specialist.
    public static func specialist(_ capability: ChatCapability, _ reason: String) -> RouteDecision {
        RouteDecision(organ: .specialist, capability: capability, reason: reason)
    }
}

/// The concierge's decision layer. Mirrors `INeuronRouter`.
public protocol INeuronRouter: Sendable {
    func route(_ context: RouteContext) -> RouteDecision
}

/// Guardrail checkpoint. A nil predicate applies no veto — the honest default.
public struct NeuronGate: Sendable {
    private let allowSpecialist: (@Sendable (String) -> Bool)?
    public init(allowSpecialist: (@Sendable (String) -> Bool)? = nil) {
        self.allowSpecialist = allowSpecialist
    }
    public func apply(_ decision: RouteDecision, _ context: RouteContext) -> RouteDecision {
        if decision.organ == .specialist, let pred = allowSpecialist, !pred(context.query) {
            return .generalist("gate: specialist vetoed -> generalist")
        }
        return decision
    }
}

/// Default router: modality (image -> vision), length (long -> long-context),
/// and reasoning cues (-> reasoning); else the generalist. Mirrors
/// `HeuristicNeuronRouter`.
public struct HeuristicNeuronRouter: INeuronRouter {
    private let gate: NeuronGate
    private let longContextChars: Int

    public init(gate: NeuronGate = NeuronGate(), longContextChars: Int = 4000) {
        self.gate = gate
        self.longContextChars = longContextChars > 0 ? longContextChars : 4000
    }

    private static let reasoningCues = [
        "prove", "solve", "calculate", "derive", "algorithm", "complexity", "debug",
        "stack trace", "refactor", "regex", "step by step", "step-by-step", "theorem",
        "equation", "big-o", "big o",
    ]

    public func route(_ context: RouteContext) -> RouteDecision {
        gate.apply(classify(context), context)
    }

    private func classify(_ context: RouteContext) -> RouteDecision {
        if context.hasImage {
            return .specialist(.vision, "image attached -> vision specialist")
        }
        if context.query.count >= longContextChars {
            return .specialist(.longContext, "long prompt -> long-context specialist")
        }
        let lower = context.query.lowercased()
        for cue in Self.reasoningCues where lower.contains(cue) {
            return .specialist(.reasoning, "reasoning cue -> reasoning specialist")
        }
        return .generalist("no specialist cue -> generalist")
    }
}

// MARK: - Two-slot residency

/// Outcome of a specialist-slot admission attempt.
public enum SlotOutcome: Sendable, Equatable {
    case admitted
    case alreadyResident
    case insufficientRam
    case buildFailed
}

/// Result of `ResidentSlotManager.ensureSpecialist`.
public struct SlotAdmission {
    public let outcome: SlotOutcome
    public let generator: IChatGenerator?
    public let message: String
}

/// Manages one evictable specialist slot beside the always-warm generalist floor
/// (held by AIService). Only the generalist's reserved footprint counts against
/// the RAM gate. Mirrors `ResidentSlotManager`.
public final class ResidentSlotManager: @unchecked Sendable {
    private let generalistReservedBytes: Int64
    private let ramAvailable: @Sendable () -> Int64
    private let lock = NSLock()
    private var specialist: IChatGenerator?
    private var specialistModelId: String?

    public init(generalistReservedBytes: Int64, ramAvailable: @escaping @Sendable () -> Int64) {
        self.generalistReservedBytes = max(0, generalistReservedBytes)
        self.ramAvailable = ramAvailable
    }

    public var residentSpecialistModelId: String? {
        lock.lock(); defer { lock.unlock() }
        return specialistModelId
    }

    /// Ensure a specialist for `selection` is resident, building it via `build`
    /// when needed. Admission gate: the generalist floor + specialist footprint
    /// must fit under the RAM ceiling. On denial / build failure the slot is left
    /// empty and the caller answers from the generalist. `build` runs outside the
    /// lock (it may await a model download).
    public func ensureSpecialist(
        _ selection: ModelSelection,
        build: (String) async throws -> IChatGenerator?
    ) async -> SlotAdmission {
        lock.lock()
        if let sid = specialistModelId, let g = specialist,
           sid.caseInsensitiveCompare(selection.modelId) == .orderedSame {
            lock.unlock()
            return SlotAdmission(outcome: .alreadyResident, generator: g,
                                 message: "Specialist '\(selection.modelId)' already resident.")
        }
        let ceiling = max(0, ramAvailable())
        lock.unlock()

        let needed = generalistReservedBytes + max(0, selection.estimatedBytes)
        if ceiling > 0 && needed > ceiling {
            return SlotAdmission(outcome: .insufficientRam, generator: nil,
                                 message: "Specialist '\(selection.modelId)' needs \(needed >> 20) MiB; ceiling \(ceiling >> 20) MiB.")
        }

        // Evict the incumbent (only one specialist slot).
        evictSpecialist()

        do {
            guard let built = try await build(selection.modelId) else {
                return SlotAdmission(outcome: .buildFailed, generator: nil,
                                     message: "Specialist '\(selection.modelId)' build returned nil.")
            }
            lock.lock()
            specialist = built
            specialistModelId = selection.modelId
            lock.unlock()
            return SlotAdmission(outcome: .admitted, generator: built,
                                 message: "Specialist '\(selection.modelId)' resident.")
        } catch {
            return SlotAdmission(outcome: .buildFailed, generator: nil,
                                 message: "Specialist '\(selection.modelId)' build failed: \(error)")
        }
    }

    /// Evict the specialist (the generalist floor is never touched).
    public func evictSpecialist() {
        lock.lock()
        let old = specialist
        specialist = nil
        specialistModelId = nil
        lock.unlock()
        (old as? Disposable)?.dispose()
    }
}

// MARK: - NeuronNode facade

/// Host-neutral `IChatRuntime` + `IPersistableChatRuntime` over an `IAIService`
/// brain. Streaming rides the brain's full pipeline (enrichment + concierge
/// routing + two-slot residency). Mirrors `NeuronNode`.
public final class NeuronNode: IChatRuntime, IPersistableChatRuntime, @unchecked Sendable {
    private let brainService: any IAIService
    private let idValue: String
    private let snapshotPath: String

    public init(brain: any IAIService, id: String = "circleai-neuron", sessionSnapshotPath: String? = nil) {
        self.brainService = brain
        let trimmed = id.trimmingCharacters(in: .whitespaces)
        self.idValue = trimmed.isEmpty ? "circleai-neuron" : id
        self.snapshotPath = sessionSnapshotPath ?? Self.defaultSnapshotPath()
    }

    /// The on-device brain. A companion session consumes it unchanged.
    public var brain: any IAIService { brainService }

    public var id: String { idValue }

    public var engineLabel: String {
        if let svc = brainService as? AIService, let m = svc.resolvedModelIdValue, !m.isEmpty {
            return "\(m) (CircleAI)"
        }
        return "CircleAI Neuron"
    }

    public var isReady: Bool { brainService.isReady }

    public var statusMessage: String { brainService.isReady ? "ready" : "loading model…" }

    public func stream(_ messages: [ChatTurn]) -> AsyncThrowingStream<String, Error> {
        let mapped = messages.map { ChatMessage(role: $0.role, content: $0.content) }
        return brainService.stream(mapped, options: nil)
    }

    public var sessionSnapshotPath: String? { snapshotPath }

    public func saveSession(path: String) async -> Bool {
        await brainService.saveSession(path: path)
    }

    public func loadSession(path: String) async -> Bool {
        await brainService.loadSession(path: path)
    }

    private static func defaultSnapshotPath() -> String {
        let base = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSTemporaryDirectory())
        return base.appendingPathComponent("CircleAI/sessions/active.session").path
    }
}
