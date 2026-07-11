// Wearable.swift
//
// Port of the Wearable vertical from
// src/CircleAI.Wearable/WearablePrimitives.cs and the biometric-snapshot record
// from WearableContext.cs:
//   • WearableKind, WearableTelemetryKind         — device + telemetry taxonomies
//   • WearableDevice, WearableSample              — domain records
//   • WearableContext                             — biometric snapshot record
//   • IWearableBoard                              — devices + telemetry samples
//   • InMemoryWearableBoard                       — deterministic in-memory impl
//
// The Companion-facing wrapper (WearableCompanionAdapter) wraps an
// ICompanionSession with wearable-specific biometric context: it injects heart
// rate, step count, SpO₂, and workout state into each message and forces the
// surface to `.wearable`. Its `currentContext` is a mutable, lock-guarded
// property (the C# `CurrentContext { get; set; }`).
//
// Porting notes:
//   • `DateTimeOffset` → `Date`; nullable numerics → optionals.
//   • `Devices` is ordered ascending by Vendor.
//   • `Record` on a sample for an unknown device throws `.unknownDevice`.
//   • `ReadSince(deviceId, kind, since)` filters + orders ascending by AtUtc.
//   • `LatestValue` is the most recent sample value for device+kind (nil if none).
//   • `AverageValue` returns `Double.nan` when the window is empty (mirrors C#).
//   • All state guarded by a single `NSLock` (the window read used by
//     `AverageValue` is a non-locking private helper).

import Foundation

// MARK: - Enums

/// Kind of wearable device.
public enum WearableKind: String, Sendable, Equatable, Codable, CaseIterable {
    case smartwatch = "Smartwatch"
    case fitnessBand = "FitnessBand"
    case chestStrap = "ChestStrap"
    case patch = "Patch"
    case headset = "Headset"
}

/// Kind of telemetry a wearable emits.
public enum WearableTelemetryKind: String, Sendable, Equatable, Codable, CaseIterable {
    case heartRate = "HeartRate"
    case steps = "Steps"
    case calories = "Calories"
    case sleepStage = "SleepStage"
    case skinTempC = "SkinTempC"
    case stress = "Stress"
    case oxygenPct = "OxygenPct"
}

// MARK: - Records

/// A wearable device descriptor.
public struct WearableDevice: Sendable, Equatable, Codable {
    public let deviceId: String
    public let kind: WearableKind
    public let vendor: String
    public let firmwareVersion: String
    public let batteryPct: Double

    public init(deviceId: String, kind: WearableKind, vendor: String, firmwareVersion: String, batteryPct: Double) {
        self.deviceId = deviceId
        self.kind = kind
        self.vendor = vendor
        self.firmwareVersion = firmwareVersion
        self.batteryPct = batteryPct
    }
}

/// A single wearable telemetry sample.
public struct WearableSample: Sendable, Equatable, Codable {
    public let deviceId: String
    public let kind: WearableTelemetryKind
    public let value: Double
    public let atUtc: Date

    public init(deviceId: String, kind: WearableTelemetryKind, value: Double, atUtc: Date) {
        self.deviceId = deviceId
        self.kind = kind
        self.value = value
        self.atUtc = atUtc
    }
}

/// Biometric snapshot injected into the Companion context on wearable surfaces.
/// Values are optional — only populated when the sensor is available and consented.
public struct WearableContext: Sendable, Equatable, Codable {
    public let heartRateBpm: Double?
    public let stepCountToday: Int?
    public let spO2Percent: Double?
    public let skinTempCelsius: Double?
    public let isWorkoutActive: Bool
    public let capturedAt: Date

    public init(heartRateBpm: Double?, stepCountToday: Int?, spO2Percent: Double?, skinTempCelsius: Double?, isWorkoutActive: Bool, capturedAt: Date) {
        self.heartRateBpm = heartRateBpm
        self.stepCountToday = stepCountToday
        self.spO2Percent = spO2Percent
        self.skinTempCelsius = skinTempCelsius
        self.isWorkoutActive = isWorkoutActive
        self.capturedAt = capturedAt
    }
}

// MARK: - Errors

public enum WearableError: Error, Equatable, CustomStringConvertible {
    case unknownDevice(String)

    public var description: String {
        switch self {
        case .unknownDevice(let id): return "Unknown device \(id)"
        }
    }
}

// MARK: - Contract

/// Devices and telemetry samples for the wearable vertical.
public protocol IWearableBoard: AnyObject, Sendable {
    func add(_ d: WearableDevice)
    func getDevice(_ id: String) -> WearableDevice?
    var devices: [WearableDevice] { get }
    func record(_ s: WearableSample) throws
    func readSince(deviceId: String, kind: WearableTelemetryKind, since: Date) -> [WearableSample]
    func latestValue(deviceId: String, kind: WearableTelemetryKind) -> Double?
    func averageValue(deviceId: String, kind: WearableTelemetryKind, since: Date) -> Double
}

// MARK: - InMemoryWearableBoard

/// Deterministic in-memory `IWearableBoard`. All state guarded by a single `NSLock`.
public final class InMemoryWearableBoard: IWearableBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var deviceMap: [String: WearableDevice] = [:]
    private var samples: [WearableSample] = []

    public init() {}

    public func add(_ d: WearableDevice) {
        lock.lock(); defer { lock.unlock() }
        deviceMap[d.deviceId] = d
    }

    public func getDevice(_ id: String) -> WearableDevice? {
        lock.lock(); defer { lock.unlock() }
        return deviceMap[id]
    }

    public var devices: [WearableDevice] {
        lock.lock(); defer { lock.unlock() }
        return deviceMap.values.sorted { $0.vendor < $1.vendor }
    }

    public func record(_ s: WearableSample) throws {
        lock.lock(); defer { lock.unlock() }
        guard deviceMap[s.deviceId] != nil else { throw WearableError.unknownDevice(s.deviceId) }
        samples.append(s)
    }

    public func readSince(deviceId: String, kind: WearableTelemetryKind, since: Date) -> [WearableSample] {
        lock.lock(); defer { lock.unlock() }
        return readSinceLocked(deviceId: deviceId, kind: kind, since: since)
    }

    public func latestValue(deviceId: String, kind: WearableTelemetryKind) -> Double? {
        lock.lock(); defer { lock.unlock() }
        return samples
            .filter { $0.deviceId == deviceId && $0.kind == kind }
            .max { $0.atUtc < $1.atUtc }?
            .value
    }

    public func averageValue(deviceId: String, kind: WearableTelemetryKind, since: Date) -> Double {
        lock.lock(); defer { lock.unlock() }
        let items = readSinceLocked(deviceId: deviceId, kind: kind, since: since)
        if items.isEmpty { return Double.nan }
        return items.reduce(0.0) { $0 + $1.value } / Double(items.count)
    }

    /// Window read for a device+kind. Caller must hold `lock`.
    private func readSinceLocked(deviceId: String, kind: WearableTelemetryKind, since: Date) -> [WearableSample] {
        samples.filter { $0.deviceId == deviceId && $0.kind == kind && $0.atUtc >= since }.sorted { $0.atUtc < $1.atUtc }
    }
}

// MARK: - WearableCompanionAdapter

/// Wraps an `ICompanionSession` with wearable-specific biometric context.
/// Injects heart rate, step count, and workout state into each message so the
/// Companion can respond with health-aware, appropriately concise replies.
/// Port of `CircleAI.Wearable.WearableCompanionAdapter`.
///
/// Unlike the domain-prompt adapters, this one:
///   • forces `interface` to `.wearable` (not forwarded from the inner session),
///   • exposes a mutable `currentContext` (the C# `CurrentContext { get; set; }`),
///     guarded by an `NSLock` for `@unchecked Sendable` correctness, and
///   • enriches messages with a trailing `[Biometrics] …` line built from the
///     current context (nil context ⇒ message passed through unchanged).
///
/// Proactive events forward through the inner session's `proactiveEvents` stream
/// (the Swift `ICompanionSession` protocol declares no disposal).
public final class WearableCompanionAdapter: ICompanionSession, @unchecked Sendable {
    private let inner: ICompanionSession
    private let lock = NSLock()
    private var _currentContext: WearableContext?

    /// The latest biometric snapshot to inject. Settable; thread-safe.
    public var currentContext: WearableContext? {
        get { lock.lock(); defer { lock.unlock() }; return _currentContext }
        set { lock.lock(); defer { lock.unlock() }; _currentContext = newValue }
    }

    public init(_ inner: ICompanionSession) {
        self.inner = inner
    }

    public var sessionId: String { inner.sessionId }
    public var identityId: String { inner.identityId }
    /// Always `.wearable` — the C# adapter hard-codes `InterfaceKind.Wearable`.
    public var interface: InterfaceKind { .wearable }
    public var history: [CompanionTurn] { inner.history }

    public func getContext() -> CompanionContext { inner.getContext() }
    public func refreshContext() async throws { try await inner.refreshContext() }
    public func signalFeedback(positive: Bool, note: String?) async throws {
        try await inner.signalFeedback(positive: positive, note: note)
    }
    public var proactiveEvents: AsyncStream<CompanionProactiveEvent> { inner.proactiveEvents }

    public func send(_ message: String) async throws -> String { try await inner.send(enrich(message)) }
    public func stream(_ message: String) -> AsyncStream<String> { inner.stream(enrich(message)) }
    public func agent(_ instruction: String) async throws -> String { try await inner.agent(enrich(instruction)) }

    /// Append a `[Biometrics] …` line to the message from the current context.
    /// Port of the C# `EnrichMessage`: no context ⇒ message unchanged; otherwise
    /// a newline, then `[Biometrics] ` followed by whichever fields are present
    /// (HR/steps/SpO₂/workout), then trailing whitespace trimmed. `HR` and `SpO₂`
    /// are formatted with no decimal places (C# `:F0`).
    private func enrich(_ message: String) -> String {
        guard let ctx = currentContext else { return message }
        var s = message + "\n[Biometrics] "
        if let hr = ctx.heartRateBpm { s += "HR:\(String(format: "%.0f", hr))bpm " }
        if let steps = ctx.stepCountToday { s += "Steps:\(steps) " }
        if let spo2 = ctx.spO2Percent { s += "SpO₂:\(String(format: "%.0f", spo2))% " }
        if ctx.isWorkoutActive { s += "Workout:active " }
        // Mirror C# StringBuilder.ToString().TrimEnd() — trim trailing whitespace only.
        while let last = s.last, last == " " || last == "\n" || last == "\t" || last == "\r" {
            s.removeLast()
        }
        return s
    }

    // ── Wearable helpers ──────────────────────────────────────────────────────

    /// Interpret wearable readings vs baseline (C# `InterpretReadingsAsync`).
    public func interpretReadings(metric: String, sampleData: String, baseline: String) async throws -> String {
        try await inner.agent(
            "Interpret wearable \(metric) from samples: \(sampleData) vs baseline: \(baseline). Signal vs noise, what to do.")
    }

    /// Correlate a metric with behaviour (C# `CorrelateWithBehaviourAsync`).
    public func correlateWithBehaviour(metric: String, behaviourLog: String) async throws -> String {
        try await inner.agent(
            "Correlate \(metric) trend with behaviour log: \(behaviourLog). Hypotheses + experiment to test the strongest one.")
    }

    /// Suggest a tracking experiment (C# `SuggestTrackingExperimentAsync`).
    public func suggestTrackingExperiment(goal: String, availableMetrics: String) async throws -> String {
        try await inner.agent(
            "Suggest a 2-week tracking experiment for goal '\(goal)' using metrics: \(availableMetrics). Protocol + success criteria.")
    }

    /// Explain battery savings (C# `ExplainBatterySavingsAsync`).
    public func explainBatterySavings(deviceModel: String, currentBatteryPct: String, usagePattern: String) async throws -> String {
        try await inner.agent(
            "Suggest battery savings for \(deviceModel) at \(currentBatteryPct)% with usage: \(usagePattern). Ranked by impact.")
    }
}
