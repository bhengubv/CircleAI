// AutonomousBiz.swift
//
// Port of CircleAI.AutonomousBiz/ — the autonomous-business primitives:
// treasury, revenue loop (pub/sub with history), decision log.
//   • Contracts.cs               — TreasurySnapshot, RevenueEvent,
//                                  AutonomousDecision, ITreasury, IRevenueLoop,
//                                  IDecisionLog
//   • InMemoryAutonomousBiz.cs   — InMemoryRevenueLoop (fan-out + kept history),
//                                  InMemoryTreasury (running balance from the
//                                  loop), InMemoryDecisionLog (append-only)
//   • NullImplementations.cs     — Null* fail-closed backends
//
// Porting notes:
//   • `decimal` → `Decimal`.
//   • `IDisposable Subscribe(Func<RevenueEvent, ValueTask>)` → `subscribe(_:)
//     -> IRevenueSubscription` (idempotent dispose handle).
//   • The C# `Publish` fans out by firing `s(e)` without awaiting (fire-and-
//     forget `_ = s(e)`); the Swift port snapshots subscribers under the lock
//     and spawns a detached Task per handler so publish never blocks and the
//     handler's own (un)subscribe cannot self-deadlock.
//   • `InMemoryTreasury` sums revenue events matching its currency
//     (case-insensitive) via `loop.readAsync(since: .distantPast)`.

import Foundation

// MARK: - Records

/// A treasury balance snapshot. (C# `TreasurySnapshot`.)
public struct TreasurySnapshot: Sendable, Equatable, Codable {
    /// Current balance.
    public let balance: Decimal
    /// ISO currency code.
    public let currency: String
    /// UTC snapshot time.
    public let atUtc: Date

    public init(balance: Decimal, currency: String, atUtc: Date) {
        self.balance = balance
        self.currency = currency
        self.atUtc = atUtc
    }
}

/// A revenue event. (C# `RevenueEvent`.)
public struct RevenueEvent: Sendable, Equatable, Codable {
    /// Event identifier.
    public let eventId: String
    /// Amount.
    public let amount: Decimal
    /// ISO currency code.
    public let currency: String
    /// Source of the revenue.
    public let source: String
    /// UTC timestamp.
    public let atUtc: Date

    public init(eventId: String, amount: Decimal, currency: String, source: String, atUtc: Date) {
        self.eventId = eventId
        self.amount = amount
        self.currency = currency
        self.source = source
        self.atUtc = atUtc
    }
}

/// An autonomous decision record. (C# `AutonomousDecision`.)
public struct AutonomousDecision: Sendable, Equatable, Codable {
    /// Decision identifier.
    public let decisionId: String
    /// Why the decision was made.
    public let rationale: String
    /// The action chosen.
    public let chosenAction: String
    /// UTC timestamp.
    public let atUtc: Date

    public init(decisionId: String, rationale: String, chosenAction: String, atUtc: Date) {
        self.decisionId = decisionId
        self.rationale = rationale
        self.chosenAction = chosenAction
        self.atUtc = atUtc
    }
}

// MARK: - Subscription handle

/// A disposable revenue-loop subscription. `dispose()` is idempotent.
public protocol IRevenueSubscription: AnyObject, Sendable {
    /// Unsubscribe. Idempotent.
    func dispose()
}

/// No-op subscription handle — used by `NullRevenueLoop`.
public final class NullRevenueSubscription: IRevenueSubscription, @unchecked Sendable {
    public static let shared = NullRevenueSubscription()
    public init() {}
    public func dispose() {}
}

// MARK: - Contracts

/// Reports the treasury balance. (C# `ITreasury`.)
public protocol ITreasury: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Returns the current balance snapshot.
    func getSnapshot() async -> TreasurySnapshot
}

/// Fans out revenue events + keeps history. (C# `IRevenueLoop`.)
public protocol IRevenueLoop: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Subscribe to revenue events. Dispose the handle to stop.
    func subscribe(_ handler: @escaping @Sendable (RevenueEvent) async -> Void) -> IRevenueSubscription
    /// Returns all events at or after `since`.
    func read(since: Date) async -> [RevenueEvent]
}

/// Append-only autonomous-decision log. (C# `IDecisionLog`.)
public protocol IDecisionLog: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Appends a decision.
    func append(_ d: AutonomousDecision) async
    /// Returns up to `limit` most-recent decisions, newest first.
    func read(limit: Int) async -> [AutonomousDecision]
}

public extension IDecisionLog {
    /// Overload matching the C# default `limit = 100`.
    func read() async -> [AutonomousDecision] { await read(limit: 100) }
}

// MARK: - InMemoryRevenueLoop

/// In-memory revenue loop: fan-out pub/sub with a kept history.
/// (C# `InMemoryRevenueLoop`.)
public final class InMemoryRevenueLoop: IRevenueLoop, @unchecked Sendable {
    private let lock = NSLock()
    private var history: [RevenueEvent] = []
    private var subs: [UUID: @Sendable (RevenueEvent) async -> Void] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    /// Records `e` in history and fans it out to all subscribers. Handlers run
    /// on detached tasks so publish never blocks (matches the C# fire-and-forget
    /// `_ = s(e)`), and the subscriber set is snapshotted under the lock so a
    /// handler that (un)subscribes cannot self-deadlock.
    public func publish(_ e: RevenueEvent) {
        lock.lock()
        history.append(e)
        let snap = Array(subs.values)
        lock.unlock()
        for s in snap {
            Task { await s(e) }
        }
    }

    public func subscribe(_ handler: @escaping @Sendable (RevenueEvent) async -> Void) -> IRevenueSubscription {
        let id = UUID()
        lock.lock(); subs[id] = handler; lock.unlock()
        return Handle(owner: self, id: id)
    }

    public func read(since: Date) async -> [RevenueEvent] {
        lock.lock(); defer { lock.unlock() }
        return history.filter { $0.atUtc >= since }
    }

    /// Number of active subscribers. Useful in tests.
    public var subscriberCount: Int {
        lock.lock(); defer { lock.unlock() }
        return subs.count
    }

    private func remove(_ id: UUID) {
        lock.lock(); subs[id] = nil; lock.unlock()
    }

    private final class Handle: IRevenueSubscription, @unchecked Sendable {
        private weak var owner: InMemoryRevenueLoop?
        private let id: UUID
        private let disposeLock = NSLock()
        private var disposed = false

        init(owner: InMemoryRevenueLoop, id: UUID) { self.owner = owner; self.id = id }

        func dispose() {
            disposeLock.lock()
            if disposed { disposeLock.unlock(); return }
            disposed = true
            disposeLock.unlock()
            owner?.remove(id)
        }
    }
}

// MARK: - InMemoryTreasury

/// In-memory treasury: running balance from the revenue loop's history for its
/// currency (case-insensitive). (C# `InMemoryTreasury`.)
public final class InMemoryTreasury: ITreasury, @unchecked Sendable {
    private let loop: any IRevenueLoop
    private let currency: String

    public init(loop: any IRevenueLoop, currency: String = "ZAR") {
        self.loop = loop
        self.currency = currency
    }

    public var backendId: String { "in-memory" }

    public func getSnapshot() async -> TreasurySnapshot {
        let events = await loop.read(since: IntegrationDates.minValue)
        let bal = events
            .filter { $0.currency.caseInsensitiveCompare(currency) == .orderedSame }
            .reduce(Decimal(0)) { $0 + $1.amount }
        return TreasurySnapshot(balance: bal, currency: currency, atUtc: Date())
    }
}

// MARK: - InMemoryDecisionLog

/// In-memory append-only decision log. (C# `InMemoryDecisionLog`.)
public final class InMemoryDecisionLog: IDecisionLog, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [AutonomousDecision] = []

    public init() {}

    public var backendId: String { "in-memory" }

    public func append(_ d: AutonomousDecision) async {
        lock.lock(); items.append(d); lock.unlock()
    }

    public func read(limit: Int) async -> [AutonomousDecision] {
        precondition(limit > 0, "limit must be positive")
        lock.lock(); defer { lock.unlock() }
        return Array(items.sorted { $0.atUtc > $1.atUtc }.prefix(limit))
    }
}

// MARK: - Null implementations

/// Fail-closed treasury — zero balance at MinValue. (C# `NullTreasury`.)
public final class NullTreasury: ITreasury, @unchecked Sendable {
    public static let instance = NullTreasury()
    public init() {}
    public var backendId: String { "null" }
    public func getSnapshot() async -> TreasurySnapshot {
        TreasurySnapshot(balance: 0, currency: "ZAR", atUtc: IntegrationDates.minValue)
    }
}

/// Fail-closed revenue loop — never notifies, empty history. (C# `NullRevenueLoop`.)
public final class NullRevenueLoop: IRevenueLoop, @unchecked Sendable {
    public static let instance = NullRevenueLoop()
    public init() {}
    public var backendId: String { "null" }
    public func subscribe(_ handler: @escaping @Sendable (RevenueEvent) async -> Void) -> IRevenueSubscription {
        NullRevenueSubscription.shared
    }
    public func read(since: Date) async -> [RevenueEvent] { [] }
}

/// Fail-closed decision log — discards appends, empty reads. (C# `NullDecisionLog`.)
public final class NullDecisionLog: IDecisionLog, @unchecked Sendable {
    public static let instance = NullDecisionLog()
    public init() {}
    public var backendId: String { "null" }
    public func append(_ d: AutonomousDecision) async {}
    public func read(limit: Int) async -> [AutonomousDecision] { [] }
}
