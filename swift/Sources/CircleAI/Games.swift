// Games.swift
//
// Port of the Games module from src/CircleAI.Games/{Contracts,InMemoryGames,
// NullImplementations}.cs — the game-runtime contracts plus a working in-memory
// game loop, input map, and scene graph, and their null implementations:
//   • GameTick, InputEvent, SceneNode        — runtime records
//   • IGameLoop, IInputMap, ISceneGraph      — contracts
//   • IGameSubscription                      — disposable subscribe handle
//   • TimerGameLoop, InMemoryInputMap, InMemorySceneGraph — working impls
//   • NullGameLoop, NullInputMap, NullSceneGraph          — no-op impls
//
// Porting notes:
//   • `TimeSpan` → `TimeInterval` (seconds).
//   • `IReadOnlyDictionary<string,string>? Payload` → `[String: String]?`.
//   • `IAsyncDisposable` → an async `dispose()` on the loop; `IDisposable`
//     subscribe handles → `IGameSubscription.dispose()` (idempotent).
//   • The C# subscriber signature `Func<GameTick, ValueTask>` maps to a Sendable
//     async closure. Fan-out mirrors C# `_ = s(tick)`: each subscriber's async
//     handler is spawned as a detached task and never blocks the tick.
//   • `TimerGameLoop` uses a background `Task` that sleeps at the frame interval
//     instead of a `System.Threading.Timer` — deterministic, no wall clock in
//     tests beyond the tick handler being invoked. Subscribers are snapshotted
//     under the lock and invoked outside it (never call back into the loop while
//     holding the lock). `ValueTask` completion/errors from subscribers are
//     swallowed, matching the C# try/catch around each callback.

import Foundation

// MARK: - Records

/// A single game-loop tick.
public struct GameTick: Sendable, Equatable, Codable {
    public let frame: Int
    public let elapsed: TimeInterval

    public init(frame: Int, elapsed: TimeInterval) {
        self.frame = frame
        self.elapsed = elapsed
    }
}

/// An input event with an optional string payload.
public struct InputEvent: Sendable, Equatable, Codable {
    public let action: String
    public let payload: [String: String]?

    public init(action: String, payload: [String: String]? = nil) {
        self.action = action
        self.payload = payload
    }
}

/// A node in the scene graph.
public struct SceneNode: Sendable, Equatable, Codable {
    public let nodeId: String
    public let kind: String
    public let x: Double
    public let y: Double
    public let z: Double

    public init(nodeId: String, kind: String, x: Double, y: Double, z: Double) {
        self.nodeId = nodeId
        self.kind = kind
        self.x = x
        self.y = y
        self.z = z
    }
}

// MARK: - Errors

public enum GamesError: Error, Equatable, CustomStringConvertible {
    case invalidTargetFps
    case alreadyStarted
    case nodeIdRequired

    public var description: String {
        switch self {
        case .invalidTargetFps: return "targetFps must be positive"
        case .alreadyStarted: return "already started"
        case .nodeIdRequired: return "NodeId required"
        }
    }
}

// MARK: - Subscription handle

/// A disposable subscribe handle. Mirrors the C# `IDisposable` returned by
/// `Subscribe`. `dispose()` is idempotent.
public protocol IGameSubscription: AnyObject, Sendable {
    /// Unsubscribe. Idempotent.
    func dispose()
}

/// No-op subscription handle — used by the null implementations.
public final class NullGameSubscription: IGameSubscription, @unchecked Sendable {
    public static let shared = NullGameSubscription()
    public init() {}
    public func dispose() {}
}

// MARK: - Contracts

/// A game loop that fans out ticks to subscribers at a target frame rate.
public protocol IGameLoop: AnyObject, Sendable {
    var backendId: String { get }
    func start(targetFps: Double) async throws
    func stop() async
    func subscribe(_ handler: @escaping @Sendable (GameTick) async -> Void) -> IGameSubscription
    /// Async disposal — stops the loop. Mirrors C# `IAsyncDisposable`.
    func dispose() async
}

public extension IGameLoop {
    /// Convenience overload mirroring the C# default `targetFps = 60`.
    func start() async throws { try await start(targetFps: 60) }
}

/// A source of input events.
public protocol IInputMap: AnyObject, Sendable {
    var backendId: String { get }
    func subscribe(_ handler: @escaping @Sendable (InputEvent) async -> Void) -> IGameSubscription
}

/// A mutable scene graph.
public protocol ISceneGraph: AnyObject, Sendable {
    var backendId: String { get }
    func add(_ node: SceneNode) async throws
    func remove(nodeId: String) async throws
    func snapshot() async -> [SceneNode]
}

// MARK: - TimerGameLoop

/// A working game loop backed by a background `Task` that sleeps at the frame
/// interval and fans out `GameTick`s to subscribers.
public final class TimerGameLoop: IGameLoop, @unchecked Sendable {
    private let lock = NSLock()
    private var subs: [UUID: @Sendable (GameTick) async -> Void] = [:]
    private var loopTask: Task<Void, Never>?
    private var frame: Int = 0
    private var start: Date = Date()
    /// Set by `stop()` before it returns. Checked under the lock in `onTick`
    /// AND inside each dispatched handler task, because a task can be spawned
    /// and scheduled either side of the flag being set.
    private var stopped: Bool = false
    /// Handler tasks not yet finished. `stop()` awaits these, so delivery is
    /// genuinely over when it returns rather than merely requested to end.
    private var inFlight: [Task<Void, Never>] = []

    public init() {}

    public var backendId: String { "timer" }

    public func start(targetFps: Double = 60) async throws {
        if targetFps <= 0 { throw GamesError.invalidTargetFps }
        lock.lock()
        if loopTask != nil { lock.unlock(); throw GamesError.alreadyStarted }
        // Frame interval in seconds; floor at 1ms as in the C# (max(1, 1000/fps) ms).
        let intervalMs = max(1, Int(1000.0 / targetFps))
        let intervalNs = UInt64(intervalMs) * 1_000_000
        start = Date()
        frame = 0
        stopped = false
        inFlight.removeAll()
        let task = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: intervalNs)
                if Task.isCancelled { break }
                self?.onTick()
            }
        }
        loopTask = task
        lock.unlock()
    }

    /// Stops ticking and WAITS for delivery to finish.
    ///
    /// `cancel()` alone is a request, not an event: the loop may already be
    /// inside `onTick`, and handler tasks it dispatched are queued
    /// independently. Both are drained here so that when this returns, no
    /// further tick can reach a subscriber — which is what callers have always
    /// been told.
    public func stop() async {
        lock.lock()
        let task = loopTask
        let pending = inFlight
        loopTask = nil
        inFlight.removeAll()
        stopped = true
        lock.unlock()

        task?.cancel()
        await task?.value
        for handler in pending {
            await handler.value
        }
    }

    public func subscribe(_ handler: @escaping @Sendable (GameTick) async -> Void) -> IGameSubscription {
        let id = UUID()
        lock.lock()
        subs[id] = handler
        lock.unlock()
        return Handle(owner: self, id: id)
    }

    public func dispose() async {
        await stop()
    }

    /// Number of active subscribers. Useful in tests.
    public var subscriberCount: Int {
        lock.lock(); defer { lock.unlock() }
        return subs.count
    }

    private func onTick() {
        // Advance the frame counter and snapshot the subscribers under the lock;
        // handlers are invoked OUTSIDE the lock so a subscriber that (un)subscribes
        // from within its handler never self-deadlocks.
        lock.lock()
        if stopped { lock.unlock(); return }
        frame += 1
        let tick = GameTick(frame: frame, elapsed: Date().timeIntervalSince(start))
        let snap = Array(subs.values)
        lock.unlock()

        var spawned: [Task<Void, Never>] = []
        for handler in snap {
            // Errors are swallowed, matching C# `_ = s(tick)`. What is NOT
            // fire-and-forget any more is the task's lifetime: it is recorded
            // so `stop()` can wait for it, and it re-checks `stopped` because
            // it may not be scheduled until after the loop was told to stop.
            spawned.append(Task { [weak self] in
                guard let self else { return }
                self.lock.lock()
                let goneAway = self.stopped
                self.lock.unlock()
                if goneAway { return }
                await handler(tick)
            })
        }

        lock.lock()
        // Finished tasks are dropped so a long run does not accumulate them.
        inFlight.removeAll { $0.isCancelled }
        inFlight.append(contentsOf: spawned)
        lock.unlock()
    }

    private func remove(_ id: UUID) {
        lock.lock(); subs[id] = nil; lock.unlock()
    }

    private final class Handle: IGameSubscription, @unchecked Sendable {
        private weak var owner: TimerGameLoop?
        private let id: UUID
        private let disposeLock = NSLock()
        private var disposed = false

        init(owner: TimerGameLoop, id: UUID) {
            self.owner = owner
            self.id = id
        }

        func dispose() {
            disposeLock.lock()
            if disposed { disposeLock.unlock(); return }
            disposed = true
            disposeLock.unlock()
            owner?.remove(id)
        }
    }
}

// MARK: - InMemoryInputMap

/// A working input map. Sources call `raise` to fan an event out to subscribers.
public final class InMemoryInputMap: IInputMap, @unchecked Sendable {
    private let lock = NSLock()
    private var subs: [UUID: @Sendable (InputEvent) async -> Void] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    /// Raise an input event, fanning it out to all subscribers.
    public func raise(_ ev: InputEvent) {
        lock.lock()
        let snap = Array(subs.values)
        lock.unlock()
        for handler in snap {
            Task { await handler(ev) }
        }
    }

    public func subscribe(_ handler: @escaping @Sendable (InputEvent) async -> Void) -> IGameSubscription {
        let id = UUID()
        lock.lock()
        subs[id] = handler
        lock.unlock()
        return Handle(owner: self, id: id)
    }

    /// Number of active subscribers. Useful in tests.
    public var subscriberCount: Int {
        lock.lock(); defer { lock.unlock() }
        return subs.count
    }

    private func remove(_ id: UUID) {
        lock.lock(); subs[id] = nil; lock.unlock()
    }

    private final class Handle: IGameSubscription, @unchecked Sendable {
        private weak var owner: InMemoryInputMap?
        private let id: UUID
        private let disposeLock = NSLock()
        private var disposed = false

        init(owner: InMemoryInputMap, id: UUID) {
            self.owner = owner
            self.id = id
        }

        func dispose() {
            disposeLock.lock()
            if disposed { disposeLock.unlock(); return }
            disposed = true
            disposeLock.unlock()
            owner?.remove(id)
        }
    }
}

// MARK: - InMemorySceneGraph

/// A working in-memory scene graph.
public final class InMemorySceneGraph: ISceneGraph, @unchecked Sendable {
    private let lock = NSLock()
    private var nodes: [String: SceneNode] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    public func add(_ node: SceneNode) async throws {
        if node.nodeId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw GamesError.nodeIdRequired }
        lock.lock(); defer { lock.unlock() }
        nodes[node.nodeId] = node
    }

    public func remove(nodeId: String) async throws {
        if nodeId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw GamesError.nodeIdRequired }
        lock.lock(); defer { lock.unlock() }
        nodes[nodeId] = nil
    }

    public func snapshot() async -> [SceneNode] {
        lock.lock(); defer { lock.unlock() }
        return Array(nodes.values)
    }
}

// MARK: - Null implementations

/// A no-op game loop.
public final class NullGameLoop: IGameLoop, @unchecked Sendable {
    public init() {}
    public var backendId: String { "null" }
    public func start(targetFps: Double = 60) async throws {}
    public func stop() async {}
    public func subscribe(_ handler: @escaping @Sendable (GameTick) async -> Void) -> IGameSubscription { NullGameSubscription.shared }
    public func dispose() async {}
}

/// A no-op input map.
public final class NullInputMap: IInputMap, @unchecked Sendable {
    public static let shared = NullInputMap()
    public init() {}
    public var backendId: String { "null" }
    public func subscribe(_ handler: @escaping @Sendable (InputEvent) async -> Void) -> IGameSubscription { NullGameSubscription.shared }
}

/// A no-op scene graph.
public final class NullSceneGraph: ISceneGraph, @unchecked Sendable {
    public static let shared = NullSceneGraph()
    public init() {}
    public var backendId: String { "null" }
    public func add(_ node: SceneNode) async throws {}
    public func remove(nodeId: String) async throws {}
    public func snapshot() async -> [SceneNode] { [] }
}
