// Ambient.swift
//
// Port of the Ambient vertical from src/CircleAI.Ambient/AmbientPrimitives.cs:
//   • AmbientReading, AmbientPreference — domain records
//   • IAmbientBoard                     — readings, preferences, comfort check
//   • InMemoryAmbientBoard              — deterministic in-memory impl
//
// The always-on Companion monitor (AmbientCompanionMonitor) is ported below: an
// ultra-low-CPU background poll loop that periodically drives the injected
// IProactiveReasoningService and forwards the inner session's proactive events.
//
// Porting notes:
//   • `DateTimeOffset` → `Date`.
//   • `Latest(deviceId)` is the most recent reading for the device (nil if none).
//   • `History(deviceId, limit)` orders descending by AtUtc, take limit (50).
//   • `IsComfortable(deviceId, location)` is false when preference or latest
//     reading is missing; otherwise |temp−target| <= 2, |humidity−target| <= 10,
//     and noise <= max. All state guarded by a single `NSLock` (the preference /
//     latest lookups used by the comfort check are non-locking private helpers).

import Foundation

// MARK: - Records

/// An ambient environment reading.
public struct AmbientReading: Sendable, Equatable, Codable {
    public let deviceId: String
    public let temperatureC: Double
    public let humidity: Double
    public let luxLight: Double
    public let dbNoise: Double
    public let atUtc: Date

    public init(deviceId: String, temperatureC: Double, humidity: Double, luxLight: Double, dbNoise: Double, atUtc: Date) {
        self.deviceId = deviceId
        self.temperatureC = temperatureC
        self.humidity = humidity
        self.luxLight = luxLight
        self.dbNoise = dbNoise
        self.atUtc = atUtc
    }
}

/// A per-location comfort preference.
public struct AmbientPreference: Sendable, Equatable, Codable {
    public let location: String
    public let targetTempC: Double
    public let targetHumidity: Double
    public let maxNoiseDb: Double

    public init(location: String, targetTempC: Double, targetHumidity: Double, maxNoiseDb: Double) {
        self.location = location
        self.targetTempC = targetTempC
        self.targetHumidity = targetHumidity
        self.maxNoiseDb = maxNoiseDb
    }
}

// MARK: - Contract

/// Ambient readings, comfort preferences, and comfort evaluation.
public protocol IAmbientBoard: AnyObject, Sendable {
    func record(_ r: AmbientReading)
    func latest(deviceId: String) -> AmbientReading?
    func history(deviceId: String, limit: Int) -> [AmbientReading]
    func setPreference(_ p: AmbientPreference)
    func getPreference(location: String) -> AmbientPreference?
    func isComfortable(deviceId: String, location: String) -> Bool
}

public extension IAmbientBoard {
    /// Convenience overload mirroring the C# default `limit = 50`.
    func history(deviceId: String) -> [AmbientReading] { history(deviceId: deviceId, limit: 50) }
}

// MARK: - InMemoryAmbientBoard

/// Deterministic in-memory `IAmbientBoard`. All state guarded by a single `NSLock`.
public final class InMemoryAmbientBoard: IAmbientBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var readings: [AmbientReading] = []
    private var prefs: [String: AmbientPreference] = [:]

    public init() {}

    public func record(_ r: AmbientReading) {
        lock.lock(); defer { lock.unlock() }
        readings.append(r)
    }

    public func latest(deviceId: String) -> AmbientReading? {
        lock.lock(); defer { lock.unlock() }
        return latestLocked(deviceId: deviceId)
    }

    public func history(deviceId: String, limit: Int = 50) -> [AmbientReading] {
        lock.lock(); defer { lock.unlock() }
        return Array(readings.filter { $0.deviceId == deviceId }.sorted { $0.atUtc > $1.atUtc }.prefix(limit))
    }

    public func setPreference(_ p: AmbientPreference) {
        lock.lock(); defer { lock.unlock() }
        prefs[p.location] = p
    }

    public func getPreference(location: String) -> AmbientPreference? {
        lock.lock(); defer { lock.unlock() }
        return prefs[location]
    }

    public func isComfortable(deviceId: String, location: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let pref = prefs[location], let last = latestLocked(deviceId: deviceId) else { return false }
        return abs(last.temperatureC - pref.targetTempC) <= 2
            && abs(last.humidity - pref.targetHumidity) <= 10
            && last.dbNoise <= pref.maxNoiseDb
    }

    /// Most recent reading for a device. Caller must hold `lock`.
    private func latestLocked(deviceId: String) -> AmbientReading? {
        readings.filter { $0.deviceId == deviceId }.max { $0.atUtc < $1.atUtc }
    }
}

// MARK: - AmbientCompanionMonitor

/// Always-on background monitor. Periodically evaluates proactive triggers via
/// an injected `IProactiveReasoningService` and surfaces any generated messages
/// to the host (smart speaker, room display, car screen). Designed for
/// ultra-low CPU budgets between trigger checks.
/// Port of `CircleAI.Ambient.AmbientCompanionMonitor`.
///
/// Porting notes:
///   • The C# `ProactiveMessageReady` event (which add/remove-forwards onto the
///     inner session) is modelled as the `proactiveEvents` async stream, which
///     forwards the inner session's stream straight through.
///   • `start()` launches a detached poll `Task` and is idempotent (a second
///     call while running is a no-op). `stop()` cancels it. The poll loop sleeps
///     `pollInterval`, then invokes `proactive?.check(userId:)`; cancellation
///     ends the loop and every other error is swallowed so the monitor can never
///     crash the host process (mirrors the C# `catch { }`).
///   • The task handle is guarded by an `NSLock`; the loop never runs while
///     holding the lock. `dispose()` stops the loop (the Swift `ICompanionSession`
///     protocol declares no disposal, so — unlike the C# `DisposeAsync` — the
///     inner session is not disposed here).
public final class AmbientCompanionMonitor: @unchecked Sendable {
    private let session: ICompanionSession
    private let proactive: IProactiveReasoningService?
    private let pollInterval: TimeInterval

    private let lock = NSLock()
    private var pollTask: Task<Void, Never>?
    private var disposed = false

    /// Creates the monitor. `pollInterval` defaults to 5 minutes (300s), matching
    /// the C# `TimeSpan.FromMinutes(5)` default.
    public init(session: ICompanionSession,
                proactive: IProactiveReasoningService? = nil,
                pollInterval: TimeInterval = 300) {
        self.session = session
        self.proactive = proactive
        self.pollInterval = pollInterval
    }

    /// Proactive events surfaced by the Companion — forwards the inner session's
    /// stream (mirrors the C# `ProactiveMessageReady` re-raise of inner events).
    public var proactiveEvents: AsyncStream<CompanionProactiveEvent> { session.proactiveEvents }

    /// Starts the background poll loop. Non-blocking. Idempotent while running.
    public func start() {
        lock.lock()
        if disposed { lock.unlock(); return }
        if pollTask != nil { lock.unlock(); return } // Already running.
        let interval = pollInterval
        let proactive = self.proactive
        let identityId = session.identityId
        let intervalNs = UInt64(max(0, interval) * 1_000_000_000)
        let task = Task {
            while !Task.isCancelled {
                // Delay first (matches C# `await Task.Delay(_pollInterval, ct)`
                // at the top of the loop body). A cancelled sleep ends the loop.
                do {
                    try await Task.sleep(nanoseconds: intervalNs)
                } catch {
                    break
                }
                if Task.isCancelled { break }
                // Swallow every error — the ambient monitor must never crash the
                // host process (mirrors C# catch(OperationCanceledException)=>break
                // and catch{}=>ignore; a cancelled check simply ends the loop).
                if let proactive {
                    do {
                        try await proactive.check(userId: identityId)
                    } catch is CancellationError {
                        break
                    } catch {
                        // ignore and keep polling
                    }
                }
            }
        }
        pollTask = task
        lock.unlock()
    }

    /// Stops the background poll loop.
    public func stop() {
        lock.lock()
        let task = pollTask
        pollTask = nil
        lock.unlock()
        task?.cancel()
    }

    /// Stops the loop and marks the monitor disposed. Port of the C#
    /// `DisposeAsync` (minus inner-session disposal, which the Swift
    /// `ICompanionSession` protocol does not expose).
    public func dispose() {
        lock.lock()
        if disposed { lock.unlock(); return }
        disposed = true
        let task = pollTask
        pollTask = nil
        lock.unlock()
        task?.cancel()
    }
}
