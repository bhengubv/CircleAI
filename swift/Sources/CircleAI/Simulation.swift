// Simulation.swift
//
// Port of src/CircleAI.Simulation/:
//   • GraphNode.cs / GraphEdge.cs      → GraphNode, GraphEdge (value types)
//   • KnowledgeGraph.cs                → KnowledgeGraph (mutable, NSLock-guarded)
//   • IGraphBuilder.cs                 → IGraphBuilder
//   • EpisodicGraphExtractor.cs        → EpisodicGraphExtractor
//   • SimulationScenario.cs            → ScenarioKind, SimulationScenario
//   • SimulationResult.cs              → SimulationOutcome, SimulationResult
//   • ISimulationEngine.cs             → ISimulationEngine
//   • NetworkHealthSimulator.cs        → NetworkHealthSimulator + LocalSimulationEngine
//   • MiroFishAdapter.cs               → MiroFishAdapter
//   • ThreatPropagationScenario.cs     → ThreatPropagationScenario (Security bridge)
//
// Porting notes:
//   • C# `Guid` → Swift `UUID`; `DateTimeOffset` → `Date`.
//   • `KnowledgeGraph` mutates internal dictionaries and is handed to the engine,
//     so it is a `final class … @unchecked Sendable` with a single `NSLock`. The
//     read-only `nodes` / `edges` projections snapshot under the lock.
//   • The diffusion math (DecayPerStep 0.01, HighImpactThreshold 0.7, the
//     health→outcome bands, and the findings/recommendation strings) is
//     reproduced verbatim from `LocalSimulationEngine`.
//   • `EpisodicGraphExtractor` references the Swift `EpisodicMemoryEntry` fields
//     (`recordedAt`, `userText`, `appContext`, `tags`, `id`).
//   • `ThreatPropagationScenario` bridges to the Swift `AnomalySignal` /
//     `ThreatVector` in Security.swift. C# `Confidence.ToString("P0")` (percent)
//     is reproduced for the description; the F3 evidence value likewise.

import Foundation

// MARK: - GraphNode

/// A node in the Circle AI knowledge graph. Port of C# record `GraphNode`.
public struct GraphNode: Sendable, Equatable {
    public let id: UUID
    public let label: String
    /// "person" | "topic" | "app" | "event" | "system"
    public let kind: String
    public let properties: [String: String]
    public let extractedAt: Date

    public init(id: UUID, label: String, kind: String, properties: [String: String], extractedAt: Date) {
        self.id = id
        self.label = label
        self.kind = kind
        self.properties = properties
        self.extractedAt = extractedAt
    }

    /// Creates a node with a fresh UUID and the current time.
    public static func create(label: String, kind: String, properties: [String: String] = [:]) -> GraphNode {
        GraphNode(id: UUID(), label: label, kind: kind, properties: properties, extractedAt: Date())
    }
}

// MARK: - GraphEdge

/// A directed, weighted edge between two `GraphNode`s. Port of C# record `GraphEdge`.
public struct GraphEdge: Sendable, Equatable {
    public let id: UUID
    public let sourceId: UUID
    public let targetId: UUID
    /// e.g. "mentions", "causes", "resolves", "depends_on"
    public let relation: String
    /// 0.0–1.0; strength of the relationship.
    public let weight: Float
    public let createdAt: Date

    public init(id: UUID, sourceId: UUID, targetId: UUID, relation: String, weight: Float, createdAt: Date) {
        self.id = id
        self.sourceId = sourceId
        self.targetId = targetId
        self.relation = relation
        self.weight = weight
        self.createdAt = createdAt
    }

    /// Creates an edge with a fresh UUID; `weight` is clamped to [0, 1].
    public static func create(sourceId: UUID, targetId: UUID, relation: String, weight: Float = 1.0) -> GraphEdge {
        GraphEdge(
            id: UUID(), sourceId: sourceId, targetId: targetId, relation: relation,
            weight: max(0, min(1, weight)), createdAt: Date())
    }
}

// MARK: - KnowledgeGraph

/// An in-memory entity–relationship graph extracted from episodic memory. Nodes
/// and edges are last-write-wins on ID collision; graphs are composable via
/// `merge`. Port of C# class `KnowledgeGraph`. State is guarded by a single
/// `NSLock` so the graph is safe to share across the extraction / simulation
/// boundary.
public final class KnowledgeGraph: @unchecked Sendable {
    private let lock = NSLock()
    private var nodesById: [UUID: GraphNode] = [:]
    private var edgesById: [UUID: GraphEdge] = [:]

    public init() {}

    /// Snapshot of all nodes keyed by ID.
    public var nodes: [UUID: GraphNode] {
        lock.lock(); defer { lock.unlock() }
        return nodesById
    }

    /// Snapshot of all edges keyed by ID.
    public var edges: [UUID: GraphEdge] {
        lock.lock(); defer { lock.unlock() }
        return edgesById
    }

    /// Adds or replaces a node (last-write wins on ID collision).
    public func addNode(_ node: GraphNode) {
        lock.lock(); defer { lock.unlock() }
        nodesById[node.id] = node
    }

    /// Adds or replaces an edge (last-write wins on ID collision).
    public func addEdge(_ edge: GraphEdge) {
        lock.lock(); defer { lock.unlock() }
        edgesById[edge.id] = edge
    }

    /// All edges where `nodeId` is the source or target.
    public func edgesFor(_ nodeId: UUID) -> [GraphEdge] {
        lock.lock(); defer { lock.unlock() }
        return edgesById.values.filter { $0.sourceId == nodeId || $0.targetId == nodeId }
    }

    /// All nodes reachable from `startId` by BFS (including the start node).
    public func reachableFrom(_ startId: UUID) -> [GraphNode] {
        lock.lock(); defer { lock.unlock() }
        var visited = Set<UUID>()
        var queue = [startId]
        var head = 0
        var result: [GraphNode] = []
        while head < queue.count {
            let current = queue[head]
            head += 1
            if !visited.insert(current).inserted { continue }
            if let node = nodesById[current] { result.append(node) }
            for edge in edgesById.values where edge.sourceId == current || edge.targetId == current {
                let next = edge.sourceId == current ? edge.targetId : edge.sourceId
                if !visited.contains(next) { queue.append(next) }
            }
        }
        return result
    }

    /// Merges another graph's nodes and edges into this one (last-write wins).
    public func merge(_ other: KnowledgeGraph) {
        let (otherNodes, otherEdges) = (other.nodes, other.edges)
        lock.lock(); defer { lock.unlock() }
        for n in otherNodes.values { nodesById[n.id] = n }
        for e in otherEdges.values { edgesById[e.id] = e }
    }
}

// MARK: - IGraphBuilder

/// Builds a `KnowledgeGraph` from a list of episodic memory entries. Port of C#
/// interface `IGraphBuilder`.
public protocol IGraphBuilder: Sendable {
    func build(_ entries: [EpisodicMemoryEntry]) -> KnowledgeGraph
}

// MARK: - EpisodicGraphExtractor

/// Extracts a `KnowledgeGraph` from episodic memory using keyword/tag heuristics.
/// Fully offline — no LLM dependency. Port of C# `EpisodicGraphExtractor`.
///
/// Extraction rules (in order):
///   1. Each entry becomes an "event" node (label = first 60 chars of userText).
///   2. appContext becomes an "app" node; edge event → app "occurred_in", w=1.0.
///   3. Each tag key becomes a "topic" node; edge event → topic "tagged_with", w=1.0.
///   4. Consecutive entries within 1 hour are joined by a "followed_by" edge, w=0.5.
public final class EpisodicGraphExtractor: IGraphBuilder, @unchecked Sendable {
    public init() {}

    public func build(_ entries: [EpisodicMemoryEntry]) -> KnowledgeGraph {
        let graph = KnowledgeGraph()
        var appNodes: [String: GraphNode] = [:]      // key: lowercased appContext
        var topicNodes: [String: GraphNode] = [:]    // key: lowercased tag
        var prev: GraphNode?
        var prevTime = Date.distantPast

        for entry in entries.sorted(by: { $0.recordedAt < $1.recordedAt }) {
            let label = entry.userText.count > 60
                ? String(entry.userText.prefix(60))
                : entry.userText
            let evNode = GraphNode.create(
                label: label, kind: "event",
                properties: ["episode_id": entry.id.uuidString])
            graph.addNode(evNode)

            // App context → node + edge
            if let app = entry.appContext, !app.trimmingCharacters(in: .whitespaces).isEmpty {
                let key = app.lowercased()
                let appNode: GraphNode
                if let existing = appNodes[key] {
                    appNode = existing
                } else {
                    appNode = GraphNode.create(label: app, kind: "app")
                    appNodes[key] = appNode
                    graph.addNode(appNode)
                }
                graph.addEdge(GraphEdge.create(sourceId: evNode.id, targetId: appNode.id, relation: "occurred_in"))
            }

            // Tags → topic nodes + edges
            if let tags = entry.tags {
                // C# iterates Dictionary.Keys; order is unspecified there too.
                for tag in tags.keys {
                    let key = tag.lowercased()
                    let topicNode: GraphNode
                    if let existing = topicNodes[key] {
                        topicNode = existing
                    } else {
                        topicNode = GraphNode.create(label: tag, kind: "topic")
                        topicNodes[key] = topicNode
                        graph.addNode(topicNode)
                    }
                    graph.addEdge(GraphEdge.create(sourceId: evNode.id, targetId: topicNode.id, relation: "tagged_with"))
                }
            }

            // Temporal sequence — connect to previous event if within 1 hour.
            if let p = prev, entry.recordedAt.timeIntervalSince(prevTime) <= 3600.0 {
                graph.addEdge(GraphEdge.create(sourceId: p.id, targetId: evNode.id, relation: "followed_by", weight: 0.5))
            }

            prev = evNode
            prevTime = entry.recordedAt
        }

        return graph
    }
}

// MARK: - ScenarioKind

/// Kinds of simulation scenarios supported by the engine. Ordinals match C#.
public enum ScenarioKind: Int, Sendable {
    case configurationShift = 0
    case dataPipelineChange = 1
    case softwareDeployment = 2
    case securityPatch = 3
    case threatPropagation = 4
}

// MARK: - SimulationScenario

/// Describes a single simulation scenario. Port of C# record `SimulationScenario`.
public struct SimulationScenario: Sendable, Equatable {
    public let id: UUID
    public let kind: ScenarioKind
    public let description: String
    /// Scenario-specific config.
    public let parameters: [String: String]
    /// Simulation depth (default 10).
    public let stepCount: Int
    public let createdAt: Date

    public init(
        id: UUID, kind: ScenarioKind, description: String,
        parameters: [String: String], stepCount: Int, createdAt: Date
    ) {
        self.id = id
        self.kind = kind
        self.description = description
        self.parameters = parameters
        self.stepCount = stepCount
        self.createdAt = createdAt
    }

    /// Creates a scenario with a fresh UUID and the current time.
    public static func create(
        kind: ScenarioKind, description: String,
        parameters: [String: String] = [:], steps: Int = 10
    ) -> SimulationScenario {
        SimulationScenario(
            id: UUID(), kind: kind, description: description,
            parameters: parameters, stepCount: steps, createdAt: Date())
    }
}

// MARK: - SimulationOutcome

/// The overall health outcome of a simulation run. Ordinals match C#.
public enum SimulationOutcome: Int, Sendable {
    case healthy = 0
    case degraded = 1
    case critical = 2
    case unknown = 3
}

// MARK: - SimulationResult

/// Captures the outcome of a single simulation run. Port of C# record
/// `SimulationResult`.
public struct SimulationResult: Sendable, Equatable {
    public let scenarioId: UUID
    public let outcome: SimulationOutcome
    /// 0.0–1.0; higher = healthier.
    public let healthScore: Float
    public let findings: [String]
    public let recommendations: [String]
    public let stepsRun: Int
    public let completedAt: Date

    public init(
        scenarioId: UUID, outcome: SimulationOutcome, healthScore: Float,
        findings: [String], recommendations: [String], stepsRun: Int, completedAt: Date
    ) {
        self.scenarioId = scenarioId
        self.outcome = outcome
        self.healthScore = healthScore
        self.findings = findings
        self.recommendations = recommendations
        self.stepsRun = stepsRun
        self.completedAt = completedAt
    }
}

// MARK: - ISimulationEngine

/// Runs a simulation scenario against a knowledge graph. Port of C# interface
/// `ISimulationEngine`.
public protocol ISimulationEngine: Sendable {
    func run(_ scenario: SimulationScenario, graph: KnowledgeGraph) async throws -> SimulationResult
}

// MARK: - LocalSimulationEngine

/// Deterministic graph-diffusion engine used when no external MiroFish engine is
/// registered. Port of the internal C# `LocalSimulationEngine`.
public final class LocalSimulationEngine: ISimulationEngine, @unchecked Sendable {
    private static let decayPerStep: Float = 0.01
    private static let highImpactThreshold: Float = 0.7

    public init() {}

    public func run(_ scenario: SimulationScenario, graph: KnowledgeGraph) async throws -> SimulationResult {
        try Task.checkCancellation()

        let nodes = graph.nodes
        let edges = graph.edges

        var health: Float = 1.0
        var highImpact = Set<String>()

        var step = 0
        while step < scenario.stepCount && health > 0 {
            for edge in edges.values {
                health -= (1 - edge.weight) * Self.decayPerStep
                if edge.weight >= Self.highImpactThreshold, let src = nodes[edge.sourceId] {
                    highImpact.insert(src.label)
                }
            }
            try Task.checkCancellation()
            step += 1
        }

        health = max(0, min(1, health))

        let outcome: SimulationOutcome
        switch health {
        case let h where h >= 0.8: outcome = .healthy
        case let h where h >= 0.5: outcome = .degraded
        case let h where h >= 0.2: outcome = .critical
        default: outcome = .unknown
        }

        let findings: [String] = highImpact.isEmpty
            ? ["No high-impact nodes detected."]
            : highImpact.map { "High-impact node detected: \($0)" }

        let recommendations: [String] = (outcome == .degraded || outcome == .critical)
            ? ["Review high-weight edges before deployment.", "Consider incremental rollout."]
            : ["Network health nominal — proceed with deployment."]

        return SimulationResult(
            scenarioId: scenario.id, outcome: outcome, healthScore: health,
            findings: findings, recommendations: recommendations,
            stepsRun: scenario.stepCount, completedAt: Date())
    }
}

// MARK: - MiroFishAdapter

/// Adapter for the MiroFish GraphRAG simulation engine. When a real MiroFish
/// engine is injected it is preferred; otherwise falls back to
/// `LocalSimulationEngine`. Port of C# `MiroFishAdapter`.
public final class MiroFishAdapter: ISimulationEngine, @unchecked Sendable {
    private let inner: ISimulationEngine

    public init(externalEngine: ISimulationEngine? = nil) {
        self.inner = externalEngine ?? LocalSimulationEngine()
    }

    public func run(_ scenario: SimulationScenario, graph: KnowledgeGraph) async throws -> SimulationResult {
        try await inner.run(scenario, graph: graph)
    }
}

// MARK: - NetworkHealthSimulator

/// Offline network-health simulator. Extracts a knowledge graph from episodic
/// memory, then runs a deterministic diffusion model to forecast the health
/// impact of the given scenario. Port of C# `NetworkHealthSimulator`.
public final class NetworkHealthSimulator: @unchecked Sendable {
    private let extractor: IGraphBuilder
    private let engine: ISimulationEngine

    public init(extractor: IGraphBuilder? = nil, engine: ISimulationEngine? = nil) {
        self.extractor = extractor ?? EpisodicGraphExtractor()
        self.engine = engine ?? MiroFishAdapter()
    }

    /// Builds a knowledge graph from `history` and runs `scenario` through the
    /// simulation engine.
    public func forecast(
        history: [EpisodicMemoryEntry],
        scenario: SimulationScenario
    ) async throws -> SimulationResult {
        let graph = extractor.build(history)
        return try await engine.run(scenario, graph: graph)
    }
}

// MARK: - ThreatPropagationScenario

/// Factory for building `ScenarioKind.threatPropagation` scenarios from an
/// `AnomalySignal`. The Simulation ↔ Security integration point. Port of C#
/// static `ThreatPropagationScenario`.
public enum ThreatPropagationScenario {

    /// Number of diffusion steps to run for a given `ThreatVector`. Higher
    /// severity → deeper simulation depth. Matches the C# `StepCountFor`.
    private static func stepCount(for vector: ThreatVector) -> Int {
        switch vector {
        case .networkPivot: return 30
        case .controlFlowDrift: return 25
        case .privilegeEscalation: return 25
        case .stateCorruption: return 20
        case .memoryAnomaly: return 15
        case .agentPatchRejected: return 15
        case .biometricSpoofAttempt: return 12
        case .unknown: return 10
        }
    }

    /// PascalCase name for a `ThreatVector`, matching C# `Enum.ToString()` — used
    /// in the scenario description and the `vector` evidence value.
    private static func name(_ vector: ThreatVector) -> String {
        switch vector {
        case .memoryAnomaly: return "MemoryAnomaly"
        case .controlFlowDrift: return "ControlFlowDrift"
        case .privilegeEscalation: return "PrivilegeEscalation"
        case .biometricSpoofAttempt: return "BiometricSpoofAttempt"
        case .networkPivot: return "NetworkPivot"
        case .stateCorruption: return "StateCorruption"
        case .agentPatchRejected: return "AgentPatchRejected"
        case .unknown: return "Unknown"
        }
    }

    /// Creates a `SimulationScenario` describing how the threat in `signal` would
    /// propagate through the peer network if unmitigated.
    public static func from(
        anomalySignal signal: AnomalySignal,
        stepOverride: Int? = nil
    ) -> SimulationScenario {
        // Start from the signal's evidence, then overlay the derived fields.
        var parameters = signal.evidence
        parameters["signal_id"] = signal.id.uuidString
        parameters["vector"] = name(signal.vector)
        parameters["confidence"] = String(format: "%.3f", signal.confidence)
        parameters["affected_module"] = signal.affectedModule
        parameters["detected_at"] = Self.iso8601.string(from: signal.detectedAt)

        let steps = stepOverride ?? stepCount(for: signal.vector)
        let confidencePct = Int((signal.confidence * 100).rounded())

        return SimulationScenario(
            id: UUID(),
            kind: .threatPropagation,
            description:
                "threat-propagation: \(name(signal.vector)) in \(signal.affectedModule) "
                + "(confidence \(confidencePct)%)",
            parameters: parameters,
            stepCount: steps,
            createdAt: Date())
    }

    private static let iso8601: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
}
