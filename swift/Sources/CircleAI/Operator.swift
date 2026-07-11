// Operator.swift
//
// Port of CircleAI.Operator/ — the Kubernetes-operator-style model deployment
// reconciler (kagent pattern).
//   • Contracts.cs           — ModelLifecyclePhase, ModelDeployment, ModelStatus,
//                              IModelOperator, IDeploymentObserver
//   • InMemoryOperator.cs    — InMemoryModelOperator (lifecycle state machine +
//                              phase-transition observers)
//   • NullImplementations.cs — NullModelOperator, NullDeploymentObserver
//
// Porting notes:
//   • `IDisposable Subscribe(Func<ModelStatus, ValueTask>)` → `subscribe(_:)
//     -> IOperatorSubscription` (a disposable handle whose `dispose()` is
//     idempotent), mirroring the existing Aether/Games subscription pattern.
//   • The C# `InMemoryModelOperator` implements BOTH `IModelOperator` and
//     `IDeploymentObserver`; the Swift port keeps that (one class conforms to
//     both protocols).
//   • Phase transitions notify observers OUTSIDE the lock (snapshot-then-release)
//     so an observer that (un)subscribes from its handler cannot self-deadlock.
//   • `ApplyAsync` validation throws (`OperatorError`); `ValueTask` methods
//     become `async` returning the value directly.

import Foundation

// MARK: - Enums + records

/// Lifecycle phase of a model deployment. (C# `ModelLifecyclePhase`.)
public enum ModelLifecyclePhase: Int, Sendable, Codable, CaseIterable {
    case pending = 0
    case downloading = 1
    case loading = 2
    case ready = 3
    case brownout = 4
    case unloading = 5
    case failed = 6
}

/// A desired model deployment (CRD-style spec). (C# `ModelDeployment`.)
public struct ModelDeployment: Sendable, Equatable, Codable {
    /// Model identifier.
    public let modelId: String
    /// Kubernetes-style namespace.
    public let namespace: String
    /// Desired replica count.
    public let replicas: Int
    /// Label of the compute tier to schedule onto.
    public let targetTierLabel: String

    public init(modelId: String, namespace: String, replicas: Int, targetTierLabel: String) {
        self.modelId = modelId
        self.namespace = namespace
        self.replicas = replicas
        self.targetTierLabel = targetTierLabel
    }
}

/// Observed model deployment status. (C# `ModelStatus`.)
public struct ModelStatus: Sendable, Equatable, Codable {
    /// Model identifier.
    public let modelId: String
    /// Namespace.
    public let namespace: String
    /// Current lifecycle phase.
    public let phase: ModelLifecyclePhase
    /// Number of ready replicas.
    public let readyReplicas: Int
    /// Last error, or `nil`.
    public let lastError: String?

    public init(modelId: String, namespace: String, phase: ModelLifecyclePhase,
                readyReplicas: Int, lastError: String?) {
        self.modelId = modelId
        self.namespace = namespace
        self.phase = phase
        self.readyReplicas = readyReplicas
        self.lastError = lastError
    }
}

// MARK: - Errors

/// Validation errors raised by `IModelOperator.apply`. Mirrors the C#
/// `ArgumentException` / `ArgumentOutOfRangeException`.
public enum OperatorError: Error, Equatable, CustomStringConvertible {
    case modelIdRequired
    case namespaceRequired
    case negativeReplicas

    public var description: String {
        switch self {
        case .modelIdRequired: return "ModelId required"
        case .namespaceRequired: return "Namespace required"
        case .negativeReplicas: return "Replicas must be non-negative"
        }
    }
}

// MARK: - Subscription handle

/// A disposable subscription handle. Mirrors the C# `IDisposable` returned by
/// `IDeploymentObserver.Subscribe`. `dispose()` is idempotent.
public protocol IOperatorSubscription: AnyObject, Sendable {
    /// Unsubscribe. Idempotent.
    func dispose()
}

/// No-op subscription handle — used by `NullDeploymentObserver`.
public final class NullOperatorSubscription: IOperatorSubscription, @unchecked Sendable {
    public static let shared = NullOperatorSubscription()
    public init() {}
    public func dispose() {}
}

// MARK: - Contracts

/// Reconciles model deployments against CRDs. (C# `IModelOperator`.)
public protocol IModelOperator: Sendable {
    /// Identifier of the concrete backend.
    var backendId: String { get }
    /// Applies (reconciles) a deployment through the lifecycle. Throws
    /// `OperatorError` on invalid input.
    func apply(_ deployment: ModelDeployment) async throws
    /// Deletes the deployment for (modelId, namespace).
    func delete(modelId: String, namespace: String) async throws
    /// Returns the current status for (modelId, namespace), or `nil`.
    func getStatus(modelId: String, namespace: String) async throws -> ModelStatus?
}

/// Lifecycle observer — fires on every phase change. (C# `IDeploymentObserver`.)
public protocol IDeploymentObserver: Sendable {
    /// Identifier of the concrete backend.
    var backendId: String { get }
    /// Subscribe to phase-change notifications. Dispose the handle to stop.
    func subscribe(_ handler: @escaping @Sendable (ModelStatus) async -> Void) -> IOperatorSubscription
}

// MARK: - InMemoryModelOperator

/// In-memory model deployment store + lifecycle observers. Applies deployments
/// through a state machine (Pending → Downloading → Loading → Ready) and
/// notifies subscribers on every transition. Conforms to both `IModelOperator`
/// and `IDeploymentObserver`, matching the C# `InMemoryModelOperator`.
public final class InMemoryModelOperator: IModelOperator, IDeploymentObserver, @unchecked Sendable {
    private let lock = NSLock()
    private var statuses: [String: ModelStatus] = [:]
    private var observers: [UUID: @Sendable (ModelStatus) async -> Void] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    public func apply(_ deployment: ModelDeployment) async throws {
        if deployment.modelId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw OperatorError.modelIdRequired
        }
        if deployment.namespace.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw OperatorError.namespaceRequired
        }
        if deployment.replicas < 0 { throw OperatorError.negativeReplicas }

        let key = Self.key(deployment.modelId, deployment.namespace)
        await transition(key: key, d: deployment, phase: .pending, readyReplicas: 0)
        await transition(key: key, d: deployment, phase: .downloading, readyReplicas: 0)
        await transition(key: key, d: deployment, phase: .loading, readyReplicas: 0)
        await transition(key: key, d: deployment, phase: .ready, readyReplicas: deployment.replicas)
    }

    public func delete(modelId: String, namespace: String) async throws {
        if modelId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw OperatorError.modelIdRequired }
        if namespace.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw OperatorError.namespaceRequired }
        lock.lock(); defer { lock.unlock() }
        statuses[Self.key(modelId, namespace)] = nil
    }

    public func getStatus(modelId: String, namespace: String) async throws -> ModelStatus? {
        if modelId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw OperatorError.modelIdRequired }
        if namespace.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw OperatorError.namespaceRequired }
        lock.lock(); defer { lock.unlock() }
        return statuses[Self.key(modelId, namespace)]
    }

    public func subscribe(_ handler: @escaping @Sendable (ModelStatus) async -> Void) -> IOperatorSubscription {
        let id = UUID()
        lock.lock()
        observers[id] = handler
        lock.unlock()
        return Handle(owner: self, id: id)
    }

    /// Number of active observers. Useful in tests.
    public var observerCount: Int {
        lock.lock(); defer { lock.unlock() }
        return observers.count
    }

    private func transition(key: String, d: ModelDeployment, phase: ModelLifecyclePhase, readyReplicas: Int) async {
        let status = ModelStatus(modelId: d.modelId, namespace: d.namespace, phase: phase,
                                 readyReplicas: readyReplicas, lastError: nil)
        lock.lock()
        statuses[key] = status
        let snap = Array(observers.values)
        lock.unlock()
        for o in snap { await o(status) }
    }

    private func remove(_ id: UUID) {
        lock.lock(); observers[id] = nil; lock.unlock()
    }

    private static func key(_ id: String, _ ns: String) -> String { "\(ns)/\(id)" }

    private final class Handle: IOperatorSubscription, @unchecked Sendable {
        private weak var owner: InMemoryModelOperator?
        private let id: UUID
        private let disposeLock = NSLock()
        private var disposed = false

        init(owner: InMemoryModelOperator, id: UUID) { self.owner = owner; self.id = id }

        func dispose() {
            disposeLock.lock()
            if disposed { disposeLock.unlock(); return }
            disposed = true
            disposeLock.unlock()
            owner?.remove(id)
        }
    }
}

// MARK: - Null implementations

/// Fail-closed `IModelOperator` — no reconciliation. (C# `NullModelOperator`.)
public final class NullModelOperator: IModelOperator, @unchecked Sendable {
    public static let instance = NullModelOperator()
    public init() {}
    public var backendId: String { "null" }
    public func apply(_ deployment: ModelDeployment) async throws {}
    public func delete(modelId: String, namespace: String) async throws {}
    public func getStatus(modelId: String, namespace: String) async throws -> ModelStatus? { nil }
}

/// Fail-closed `IDeploymentObserver` — never notifies. (C# `NullDeploymentObserver`.)
public final class NullDeploymentObserver: IDeploymentObserver, @unchecked Sendable {
    public static let instance = NullDeploymentObserver()
    public init() {}
    public var backendId: String { "null" }
    public func subscribe(_ handler: @escaping @Sendable (ModelStatus) async -> Void) -> IOperatorSubscription {
        NullOperatorSubscription.shared
    }
}
