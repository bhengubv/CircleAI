// HostingRuntime.swift
//
// Port of the CircleAI.Hosting runtime services:
//   - IThermalThrottleService.cs / ThermalThrottleService.cs
//                              → ThermalState, IThermalThrottleService,
//                                ThermalThrottleService (injected sampler; deterministic)
//   - BackgroundInferenceWorker.cs → BackgroundInferenceWorker (host lifecycle + thermal pause)
//   - IMemoryPressureSource.cs → MemoryPressureLevel, IMemoryPressureSource,
//                                NullMemoryPressureSource, ManualMemoryPressureSource
//   - Warmup/*                 → ArrivalForecast, IRequestPredictor,
//                                HistogramRequestPredictor, PredictiveWarmupOptions,
//                                PredictiveWarmupController
//
// Platform temperature APIs (WMI / sysfs / NSProcessInfo / Android PowerManager)
// have no portable Swift analogue, so the thermal sampler is INJECTED — the poll
// loop, state-change dispatch, and pause semantics are ported faithfully.

import Foundation

// =====================================================================
// Thermal
// =====================================================================

/// Coarse thermal state, ordered coolest→hottest so numeric comparisons are
/// meaningful. Mirrors C# `ThermalState`.
public enum ThermalState: Int, Sendable, Comparable {
    case unknown = 0
    case normal = 1
    case fair = 2
    case serious = 3
    case critical = 4

    public static func < (lhs: ThermalState, rhs: ThermalState) -> Bool {
        lhs.rawValue < rhs.rawValue
    }
}

/// Polls platform thermal APIs and exposes the current temperature state so
/// inference schedulers can pause under thermal pressure. Ported from
/// `IThermalThrottleService`.
public protocol IThermalThrottleService: AnyObject, Sendable {
    /// Most-recently sampled thermal state.
    var currentState: ThermalState { get }
    /// True when `currentState >= .serious`.
    var shouldPauseInference: Bool { get }
    /// Register a state-change handler. Invoked whenever the state changes.
    func onStateChanged(_ handler: @escaping @Sendable (ThermalState) -> Void)
    /// Start the background polling loop. No-op if already running.
    func startMonitoring()
    /// Stop the polling loop. Current state retained.
    func stopMonitoring()
    /// Release resources.
    func dispose()
}

/// Cross-platform thermal poller. The C# implementation reads WMI / sysfs /
/// NSProcessInfo / Android PowerManager; Swift injects the sampler so the loop
/// is deterministic and testable. Ported from `ThermalThrottleService`.
public final class ThermalThrottleService: IThermalThrottleService, @unchecked Sendable {
    private let pollInterval: TimeInterval
    private let sampler: @Sendable () -> ThermalState

    private let lock = NSLock()
    private var currentStateRaw: Int = ThermalState.unknown.rawValue
    private var handlers: [@Sendable (ThermalState) -> Void] = []
    private var pollTask: Task<Void, Never>?
    private var running = false
    private var disposed = false

    /// - Parameters:
    ///   - pollInterval: Sampling cadence. Default 10 s (matches C# PeriodicTimer).
    ///   - sampler: Injected temperature sampler. Default reports `.unknown`
    ///     (no portable sensor); hosts supply a platform sampler.
    public init(pollInterval: TimeInterval = 10, sampler: @escaping @Sendable () -> ThermalState = { .unknown }) {
        self.pollInterval = pollInterval
        self.sampler = sampler
    }

    public var currentState: ThermalState {
        lock.lock(); defer { lock.unlock() }
        return ThermalState(rawValue: currentStateRaw) ?? .unknown
    }

    public var shouldPauseInference: Bool { currentState >= .serious }

    public func onStateChanged(_ handler: @escaping @Sendable (ThermalState) -> Void) {
        lock.lock(); handlers.append(handler); lock.unlock()
    }

    public func startMonitoring() {
        lock.lock()
        if disposed { lock.unlock(); return }
        if running { lock.unlock(); return }
        running = true
        let task = Task<Void, Never> { [weak self] in
            guard let self = self else { return }
            await self.pollLoop()
        }
        pollTask = task
        lock.unlock()
    }

    public func stopMonitoring() {
        lock.lock()
        let task = pollTask; pollTask = nil; running = false
        lock.unlock()
        task?.cancel()
    }

    public func dispose() {
        lock.lock(); if disposed { lock.unlock(); return }; disposed = true; lock.unlock()
        stopMonitoring()
    }

    private func pollLoop() async {
        // Sample immediately so callers get a valid state before the first tick.
        applyNewState(sampler())
        while !Task.isCancelled {
            do {
                try await Task.sleep(nanoseconds: UInt64(pollInterval * 1_000_000_000))
            } catch { break }
            if Task.isCancelled { break }
            applyNewState(sampler())
        }
    }

    /// Applies a sampled state; fires handlers only on a change. Public so tests
    /// can drive transitions deterministically without the timer.
    public func applyNewState(_ newState: ThermalState) {
        lock.lock()
        let previousRaw = currentStateRaw
        currentStateRaw = newState.rawValue
        let snapshot = handlers
        lock.unlock()

        if previousRaw != newState.rawValue {
            for h in snapshot { h(newState) }
        }
    }
}

// =====================================================================
// BackgroundInferenceWorker
// =====================================================================

/// Wraps an `IAIService` in a host-lifecycle adapter (the Swift analogue of
/// `IHostedService`). Honours an optional `IThermalThrottleService`: sets
/// `isPaused` while the device is thermally throttled. Ported from
/// `BackgroundInferenceWorker`.
public final class BackgroundInferenceWorker: @unchecked Sendable {
    private let butler: IAIService
    private let thermal: (any IThermalThrottleService)?

    private let lock = NSLock()
    private var paused = false
    private var stopped = false

    public init(butler: IAIService, thermal: (any IThermalThrottleService)? = nil) {
        self.butler = butler
        self.thermal = thermal
    }

    /// True while the device is thermally throttled (`.serious` / `.critical`).
    public var isPaused: Bool { lock.lock(); defer { lock.unlock() }; return paused }

    public func start() async throws {
        if let thermal = thermal {
            thermal.onStateChanged { [weak self] newState in
                self?.onThermalStateChanged(newState)
            }
            thermal.startMonitoring()
        }
        try await butler.start()
    }

    public func stop() async throws {
        lock.lock()
        if stopped { lock.unlock(); return }
        stopped = true
        lock.unlock()

        thermal?.stopMonitoring()
        try await butler.stop()
    }

    public func dispose() async {
        try? await stop()
        await butler.dispose()
    }

    private func onThermalStateChanged(_ newState: ThermalState) {
        let shouldPause = newState >= .serious
        lock.lock()
        if shouldPause && !paused { paused = true }
        else if !shouldPause && paused { paused = false }
        lock.unlock()
    }
}

// =====================================================================
// Memory pressure
// =====================================================================

/// Coarse memory-pressure level. Mirrors Android onTrimMemory / iOS memory
/// warning. Ported from `MemoryPressureLevel`.
public enum MemoryPressureLevel: Int, Sendable {
    /// Plenty of headroom; no action.
    case normal = 0
    /// OS asked apps to release optional caches. Drop prefix cache.
    case trim = 1
    /// OS is about to kill the process. Drop everything; consider downshifting.
    case critical = 2
}

/// A platform-published memory-pressure signal. Subscribers receive (old, new)
/// transitions. Ported from `IMemoryPressureSource`.
public protocol IMemoryPressureSource: Sendable {
    /// Current pressure level as last observed.
    var current: MemoryPressureLevel { get }
    /// Subscribe to transitions. Handler receives (oldLevel, newLevel). Returns
    /// an unsubscribe handle.
    func subscribe(_ handler: @escaping @Sendable (MemoryPressureLevel, MemoryPressureLevel) async -> Void) -> any Disposable
}

/// Always reports Normal and never raises events. Ported from
/// `NullMemoryPressureSource`.
public final class NullMemoryPressureSource: IMemoryPressureSource, @unchecked Sendable {
    public static let instance = NullMemoryPressureSource()
    public init() {}
    public var current: MemoryPressureLevel { .normal }
    public func subscribe(_ handler: @escaping @Sendable (MemoryPressureLevel, MemoryPressureLevel) async -> Void) -> any Disposable {
        EmptyDisposable()
    }
}

/// No-op disposable.
public final class EmptyDisposable: Disposable, @unchecked Sendable {
    public init() {}
    public func dispose() {}
}

/// Manually-driven `IMemoryPressureSource`. Hosts (or tests) call `raise` when
/// the platform publishes a pressure event. Thread-safe. Ported from
/// `ManualMemoryPressureSource`.
public final class ManualMemoryPressureSource: IMemoryPressureSource, @unchecked Sendable {
    private final class Handler {
        let fn: @Sendable (MemoryPressureLevel, MemoryPressureLevel) async -> Void
        init(_ fn: @escaping @Sendable (MemoryPressureLevel, MemoryPressureLevel) async -> Void) { self.fn = fn }
    }

    private let lock = NSLock()
    private var _current: MemoryPressureLevel = .normal
    private var handlers: [ObjectIdentifier: Handler] = [:]

    public init() {}

    public var current: MemoryPressureLevel { lock.lock(); defer { lock.unlock() }; return _current }

    public func subscribe(_ handler: @escaping @Sendable (MemoryPressureLevel, MemoryPressureLevel) async -> Void) -> any Disposable {
        let h = Handler(handler)
        let id = ObjectIdentifier(h)
        lock.lock(); handlers[id] = h; lock.unlock()
        return Subscription(owner: self, id: id)
    }

    /// Publish a new pressure level. Idempotent for the same level — only
    /// transitions fire handlers.
    public func raise(_ level: MemoryPressureLevel) async {
        let previous: MemoryPressureLevel
        let snapshot: [Handler]
        lock.lock()
        if _current == level { lock.unlock(); return }
        previous = _current
        _current = level
        snapshot = Array(handlers.values)
        lock.unlock()

        for h in snapshot {
            await h.fn(previous, level)
        }
    }

    fileprivate func remove(_ id: ObjectIdentifier) {
        lock.lock(); handlers[id] = nil; lock.unlock()
    }

    private final class Subscription: Disposable, @unchecked Sendable {
        private weak var owner: ManualMemoryPressureSource?
        private let id: ObjectIdentifier
        init(owner: ManualMemoryPressureSource, id: ObjectIdentifier) {
            self.owner = owner; self.id = id
        }
        func dispose() { owner?.remove(id) }
    }
}

// =====================================================================
// Predictive warmup (RT-07)
// =====================================================================

/// Forecast of inbound requests over a window. Mirrors C# `ArrivalForecast`.
public struct ArrivalForecast: Sendable, Equatable {
    public let probabilityOfArrival: Double
    public let expectedCount: Double
    public let confidence: Double

    public init(probabilityOfArrival: Double, expectedCount: Double, confidence: Double) {
        self.probabilityOfArrival = probabilityOfArrival
        self.expectedCount = expectedCount
        self.confidence = confidence
    }
}

/// Local-only predictor that learns request arrival timing and forecasts whether
/// a spike is coming. Ported from `IRequestPredictor`.
public protocol IRequestPredictor: Sendable {
    /// Record one arrival at `utc`.
    func recordArrival(_ utc: Date)
    /// Forecast arrivals in `forecastWindow` starting at `utcNow`.
    func predict(_ utcNow: Date, forecastWindow: TimeInterval) -> ArrivalForecast
    /// Total arrivals observed since construction.
    var observedArrivals: Int64 { get }
}

/// Default `IRequestPredictor` — a histogram of per-minute-of-day arrival rates
/// over a rolling window. Forecast uses the Poisson tail
/// `P(>=1) = 1 - exp(-lambda)`. In-process; no telemetry. Ported from
/// `HistogramRequestPredictor`.
public final class HistogramRequestPredictor: IRequestPredictor, @unchecked Sendable {
    private static let minutesPerDay = 24 * 60
    private static let warmConfidence = 1.0
    private static let minSamplesForFullConfidence = 25

    private let historyDays: Int
    private var perMinuteRate: [Double]
    private var perMinuteCount: [Int]
    private let lock = NSLock()
    private var observed: Int64 = 0

    public init(historyDays: Int = 7) {
        precondition(historyDays > 0, "historyDays must be positive")
        self.historyDays = historyDays
        self.perMinuteRate = [Double](repeating: 0, count: Self.minutesPerDay)
        self.perMinuteCount = [Int](repeating: 0, count: Self.minutesPerDay)
    }

    public var observedArrivals: Int64 { lock.lock(); defer { lock.unlock() }; return observed }

    public func recordArrival(_ utc: Date) {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        let comps = cal.dateComponents([.hour, .minute], from: utc)
        let minute = (comps.hour ?? 0) * 60 + (comps.minute ?? 0)

        lock.lock()
        perMinuteCount[minute] += 1
        let cnt = perMinuteCount[minute]
        // EWMA over the last `historyDays` observations at this slot.
        let alpha = 2.0 / (Double(min(cnt, historyDays)) + 1.0)
        perMinuteRate[minute] = (alpha * 1.0) + ((1 - alpha) * perMinuteRate[minute])
        observed += 1
        lock.unlock()
    }

    public func predict(_ utcNow: Date, forecastWindow: TimeInterval) -> ArrivalForecast {
        if forecastWindow <= 0 { return ArrivalForecast(probabilityOfArrival: 0, expectedCount: 0, confidence: 0) }
        let obs = observedArrivals
        if obs == 0 { return ArrivalForecast(probabilityOfArrival: 0, expectedCount: 0, confidence: 0) }

        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        let comps = cal.dateComponents([.hour, .minute], from: utcNow)
        let minute = (comps.hour ?? 0) * 60 + (comps.minute ?? 0)
        let minutes = max(1, Int(ceil(forecastWindow / 60.0)))

        var expected = 0.0
        var coveredSamples = 0
        lock.lock()
        for i in 0..<minutes {
            let idx = (minute + i) % Self.minutesPerDay
            expected += perMinuteRate[idx]
            coveredSamples += perMinuteCount[idx]
        }
        lock.unlock()

        let probability = 1.0 - exp(-expected)
        let confidence = min(Self.warmConfidence,
            Double(coveredSamples) / Double(Self.minSamplesForFullConfidence * minutes))
        return ArrivalForecast(probabilityOfArrival: probability, expectedCount: expected, confidence: confidence)
    }

    /// Test-only — wipe state.
    func resetForTests() {
        lock.lock()
        for i in 0..<Self.minutesPerDay { perMinuteRate[i] = 0; perMinuteCount[i] = 0 }
        observed = 0
        lock.unlock()
    }
}

/// Configuration for `PredictiveWarmupController`. Mirrors C#
/// `PredictiveWarmupOptions`.
public struct PredictiveWarmupOptions: Sendable {
    public var enabled: Bool
    public var pollInterval: TimeInterval
    public var forecastWindow: TimeInterval
    public var warmupThreshold: Double
    public var minTimeBetweenWarmups: TimeInterval

    public init(
        enabled: Bool = false,
        pollInterval: TimeInterval = 30,
        forecastWindow: TimeInterval = 60,
        warmupThreshold: Double = 0.5,
        minTimeBetweenWarmups: TimeInterval = 300
    ) {
        self.enabled = enabled
        self.pollInterval = pollInterval
        self.forecastWindow = forecastWindow
        self.warmupThreshold = warmupThreshold
        self.minTimeBetweenWarmups = minTimeBetweenWarmups
    }
}

/// Background loop that polls an `IRequestPredictor` and pre-warms the generator
/// before predicted spikes. Ported from `PredictiveWarmupController`.
public final class PredictiveWarmupController: @unchecked Sendable {
    private let service: IAIService
    private let predictor: any IRequestPredictor
    private let options: PredictiveWarmupOptions
    private let clock: @Sendable () -> Date

    private let lock = NSLock()
    private var loopTask: Task<Void, Never>?
    private var lastWarmup = Date.distantPast
    private var disposed = false

    public init(
        service: IAIService,
        predictor: any IRequestPredictor,
        options: PredictiveWarmupOptions,
        clock: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.service = service
        self.predictor = predictor
        self.options = options
        self.clock = clock
    }

    /// Begin polling on a background loop. No-op when disabled or already running.
    public func start() {
        lock.lock()
        if disposed { lock.unlock(); return }
        if !options.enabled || loopTask != nil { lock.unlock(); return }
        let task = Task<Void, Never> { [weak self] in
            guard let self = self else { return }
            await self.runLoop()
        }
        loopTask = task
        lock.unlock()
    }

    /// Record a request arrival at "now" on the underlying predictor.
    public func notifyArrival() { predictor.recordArrival(clock()) }

    /// One prediction + decide-and-maybe-warm cycle. Returns true when warmup
    /// fired. Public for tests + manual poking.
    @discardableResult
    public func tick() async -> Bool {
        let now = clock()
        let forecast = predictor.predict(now, forecastWindow: options.forecastWindow)
        let score = forecast.probabilityOfArrival * forecast.confidence
        if score < options.warmupThreshold { return false }

        lock.lock(); let last = lastWarmup; lock.unlock()
        if now.timeIntervalSince(last) < options.minTimeBetweenWarmups { return false }

        lock.lock(); lastWarmup = now; lock.unlock()
        do {
            try await service.prewarm()
            return true
        } catch {
            return false
        }
    }

    private func runLoop() async {
        // Tick immediately, then on the interval (mirrors the C# do/while).
        await tick()
        while !Task.isCancelled {
            do {
                try await Task.sleep(nanoseconds: UInt64(options.pollInterval * 1_000_000_000))
            } catch { break }
            if Task.isCancelled { break }
            await tick()
        }
    }

    public func dispose() async {
        lock.lock()
        if disposed { lock.unlock(); return }
        disposed = true
        let task = loopTask; loopTask = nil
        lock.unlock()
        task?.cancel()
        await task?.value
    }
}
