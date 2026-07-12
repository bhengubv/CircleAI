// Observer.swift
//
// Port of src/CircleAI.Observer/ — the perceive-reason-act observation loop:
//   • Contracts.cs              — SensorReading, ObservationTool, ObservationTick;
//                                 ISensor, IObservationToolbox, IObservationLoop
//   • InMemoryObserver.cs       — SensorRecorder (latest-reading capture),
//                                 ObserverDecision, InMemoryObservationLoop (the
//                                 tick loop), InMemoryObservationToolbox
//   • NullImplementations.cs    — NullSensor, NullObservationLoop
//
// Porting notes:
//   • `record` → `struct: Sendable`. `ReadOnlyMemory<byte>?` → `[UInt8]?`.
//     `DateTimeOffset` → `Date`; `TimeSpan` → `TimeInterval`.
//   • `IAsyncDisposable` → `func dispose() async`. `Subscribe(...) -> IDisposable`
//     → returns the tree's `Disposable` (sync `dispose()`), matching the C# token.
//   • The reasoner / tool-invoke / handler closures are `@Sendable` async closures.
//   • CONCURRENCY: the tick loop runs in a detached `Task`. Subscribers are held
//     under an `NSLock`; each tick SNAPSHOTS the subscriber array under the lock,
//     UNLOCKS, then awaits each handler — never awaiting while the lock is held.
//     Handler/tool/reasoner throws are swallowed per-tick (the C# Debug.WriteLine
//     path) so one bad subscriber never kills the loop. Cancellation ends the loop.

import Foundation

// MARK: - Records

/// One snapshot from one sensor.
public struct SensorReading: Sendable, Equatable {
    public let sensorId: String
    public let kind: String
    public let capturedAtUtc: Date
    public let values: [String: String]
    public let payload: [UInt8]?
    public init(sensorId: String, kind: String, capturedAtUtc: Date, values: [String: String], payload: [UInt8]? = nil) {
        self.sensorId = sensorId
        self.kind = kind
        self.capturedAtUtc = capturedAtUtc
        self.values = values
        self.payload = payload
    }
}

/// One tool the observer can invoke during its act tick.
public struct ObservationTool: Sendable {
    public let toolId: String
    public let description: String
    public let tags: [String]
    public let invoke: @Sendable ([String: String]) async throws -> String

    public init(toolId: String, description: String, tags: [String], invoke: @escaping @Sendable ([String: String]) async throws -> String) {
        self.toolId = toolId
        self.description = description
        self.tags = tags
        self.invoke = invoke
    }
}

/// One loop tick — what was perceived, decided, and done.
public struct ObservationTick: Sendable, Equatable {
    public let atUtc: Date
    public let perceived: [SensorReading]
    public let reasoning: String
    public let toolsInvoked: [String]
    public init(atUtc: Date, perceived: [SensorReading], reasoning: String, toolsInvoked: [String]) {
        self.atUtc = atUtc
        self.perceived = perceived
        self.reasoning = reasoning
        self.toolsInvoked = toolsInvoked
    }
}

/// The decision returned by the reasoner: rationale + tools to invoke + args.
public struct ObserverDecision: Sendable, Equatable {
    public let reasoning: String
    public let toolsToInvoke: [String]
    public let toolArgs: [String: String]?
    public init(reasoning: String, toolsToInvoke: [String], toolArgs: [String: String]? = nil) {
        self.reasoning = reasoning
        self.toolsToInvoke = toolsToInvoke
        self.toolArgs = toolArgs
    }
}

// MARK: - Errors

public enum ObserverError: Error, Equatable, CustomStringConvertible {
    case alreadyStarted
    public var description: String {
        switch self {
        case .alreadyStarted: return "already started"
        }
    }
}

// MARK: - Contracts

/// A single perception source — camera / mic / GPS / phone-state / accelerometer.
public protocol ISensor: Sendable {
    var sensorId: String { get }
    var kind: String { get }
    var backendId: String { get }

    func start() async throws
    func stop() async throws

    /// Subscribes to readings; returns a token whose `dispose()` unsubscribes.
    func subscribe(_ handler: @escaping @Sendable (SensorReading) async -> Void) -> Disposable

    /// Async disposal (mirrors `IAsyncDisposable`).
    func dispose() async
}

/// Registry of tools available to the observation loop.
public protocol IObservationToolbox: Sendable {
    var backendId: String { get }
    func registerTool(_ tool: ObservationTool)
    func tryGet(_ toolId: String) -> ObservationTool?
    func listTools() -> [ObservationTool]
}

/// The perceive-reason-act loop itself.
public protocol IObservationLoop: Sendable {
    var backendId: String { get }
    func start(tickInterval: TimeInterval) async throws
    func stop() async
    func subscribe(_ handler: @escaping @Sendable (ObservationTick) async -> Void) -> Disposable
    func dispose() async
}

// MARK: - Sensor recorder

/// Captures the latest reading from a sensor.
public final class SensorRecorder: Disposable, @unchecked Sendable {
    /// Backing store shared with the subscription closure.
    private final class LatestBox: @unchecked Sendable {
        private let lock = NSLock()
        private var value: SensorReading?
        func set(_ r: SensorReading) { lock.lock(); value = r; lock.unlock() }
        func get() -> SensorReading? { lock.lock(); defer { lock.unlock() }; return value }
    }

    private let box = LatestBox()
    private let sub: Disposable

    public init(sensor: ISensor) {
        // Subscribe synchronously so no reading is missed before the consumer
        // starts. The closure captures `box` (already initialised), not `self`.
        let box = self.box
        self.sub = sensor.subscribe { reading in
            box.set(reading)
        }
    }

    public var latest: SensorReading? { box.get() }

    public func dispose() { sub.dispose() }
}

// MARK: - Observation loop

/// The perceive-reason-act loop. Ticks at a fixed interval, gathers the latest
/// reading from each sensor, asks the reasoner for a decision, runs the chosen
/// tools, and fans the resulting tick out to subscribers.
public final class InMemoryObservationLoop: IObservationLoop, @unchecked Sendable {
    public typealias Reasoner = @Sendable ([SensorReading]) async throws -> ObserverDecision

    private let recorders: [SensorRecorder]
    private let toolbox: any IObservationToolbox
    private let reason: Reasoner

    private let lock = NSLock()
    private var subs: [Int: @Sendable (ObservationTick) async -> Void] = [:]
    private var nextSubId = 0
    private var runTask: Task<Void, Never>?
    private var started = false

    public init(sensors: [ISensor], toolbox: any IObservationToolbox, reason: @escaping Reasoner) {
        self.toolbox = toolbox
        self.reason = reason
        self.recorders = sensors.map { SensorRecorder(sensor: $0) }
    }

    public var backendId: String { "in-memory" }

    public func start(tickInterval: TimeInterval) async throws {
        lock.lock()
        if started {
            lock.unlock()
            throw ObserverError.alreadyStarted
        }
        started = true
        lock.unlock()

        let task = Task { [weak self] in
            _ = await self?.runLoop(interval: tickInterval)
        }
        lock.lock()
        runTask = task
        lock.unlock()
    }

    public func stop() async {
        lock.lock()
        let task = runTask
        runTask = nil
        started = false
        lock.unlock()
        task?.cancel()
        await task?.value
    }

    public func subscribe(_ handler: @escaping @Sendable (ObservationTick) async -> Void) -> Disposable {
        lock.lock()
        let id = nextSubId
        nextSubId += 1
        subs[id] = handler
        lock.unlock()
        return Token(loop: self, id: id)
    }

    public func dispose() async {
        await stop()
        for r in recorders { r.dispose() }
    }

    // MARK: Loop body

    private func runLoop(interval: TimeInterval) async {
        let nanos = UInt64((interval * 1_000_000_000).rounded())
        while !Task.isCancelled {
            // Perceive.
            let readings = recorders.compactMap { $0.latest }

            // Reason (swallow throws → skip this tick's action but keep looping).
            var decision: ObserverDecision?
            do {
                decision = try await reason(readings)
            } catch {
                decision = nil
            }

            if let decision {
                // Act — invoke each chosen tool, collecting the ones that ran.
                var invoked: [String] = []
                for toolId in decision.toolsToInvoke {
                    if let tool = toolbox.tryGet(toolId) {
                        do {
                            _ = try await tool.invoke(decision.toolArgs ?? [:])
                            invoked.append(toolId)
                        } catch {
                            // tool threw — skip it (C# Debug.WriteLine path).
                        }
                    }
                }

                let tick = ObservationTick(atUtc: Date(), perceived: readings, reasoning: decision.reasoning, toolsInvoked: invoked)

                // Fan out: SNAPSHOT subscribers under the lock, UNLOCK, then await.
                lock.lock()
                let snapshot = Array(subs.values)
                lock.unlock()
                for handler in snapshot {
                    await handler(tick)
                }
            }

            if Task.isCancelled { break }
            do {
                try await Task.sleep(nanoseconds: nanos)
            } catch {
                break // cancelled during sleep
            }
        }
    }

    private func removeSub(_ id: Int) {
        lock.lock(); defer { lock.unlock() }
        subs[id] = nil
    }

    private final class Token: Disposable, @unchecked Sendable {
        private weak var loop: InMemoryObservationLoop?
        private let id: Int
        init(loop: InMemoryObservationLoop, id: Int) { self.loop = loop; self.id = id }
        func dispose() { loop?.removeSub(id) }
    }
}

// MARK: - Toolbox

/// In-memory tool registry.
public final class InMemoryObservationToolbox: IObservationToolbox, @unchecked Sendable {
    private let lock = NSLock()
    private var tools: [String: ObservationTool] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    public func registerTool(_ tool: ObservationTool) {
        lock.lock(); defer { lock.unlock() }
        tools[tool.toolId] = tool
    }

    public func tryGet(_ toolId: String) -> ObservationTool? {
        lock.lock(); defer { lock.unlock() }
        return tools[toolId]
    }

    public func listTools() -> [ObservationTool] {
        lock.lock(); defer { lock.unlock() }
        return Array(tools.values)
    }
}

// MARK: - Null backends

/// A sensor that emits nothing.
public final class NullSensor: ISensor, @unchecked Sendable {
    public init() {}
    public var sensorId: String { "null" }
    public var kind: String { "null" }
    public var backendId: String { "null" }
    public func start() async throws {}
    public func stop() async throws {}
    public func subscribe(_ handler: @escaping @Sendable (SensorReading) async -> Void) -> Disposable { EmptyDisposable() }
    public func dispose() async {}
}

/// A loop that never ticks.
public final class NullObservationLoop: IObservationLoop, @unchecked Sendable {
    public init() {}
    public var backendId: String { "null" }
    public func start(tickInterval: TimeInterval) async throws {}
    public func stop() async {}
    public func subscribe(_ handler: @escaping @Sendable (ObservationTick) async -> Void) -> Disposable { EmptyDisposable() }
    public func dispose() async {}
}

// Note: `EmptyDisposable` (a no-op `Disposable`) is already defined in
// HostingRuntime.swift and reused here for the Null* Observer backends.
