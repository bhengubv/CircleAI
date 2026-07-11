// IoT.swift
//
// Port of the IoT vertical from src/CircleAI.IoT/IoTPrimitives.cs:
//   • IoTDevice, IoTTelemetry, IoTCommand — domain records
//   • IIoTBoard              — device registry, telemetry, commands
//   • InMemoryIoTBoard       — deterministic in-memory impl
//
// The voice-in/voice-out wrapper (IoTCompanionPipeline) is intentionally NOT
// ported (it wires the Voice + Companion infrastructure, not board state). The
// IoT module has no DomainContext in the C# source.
//
// Porting notes:
//   • `DateTimeOffset` → `Date`.
//   • `LatestValue` returns `Double.nan` when there is no matching telemetry,
//     otherwise the most-recent (by AtUtc) value.
//   • `Devices` is ordered ascending by Name.
//   • `History` returns matching telemetry newest-first, `Take(limit)`;
//     non-positive limit throws `IoTError.limitOutOfRange`.
//   • `CommandsFor` returns the device's commands newest-first (by SentUtc).
//   • All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// An IoT device.
public struct IoTDevice: Sendable, Equatable, Codable {
    public let deviceId: String
    public let name: String
    public let kind: String
    public let firmwareVersion: String
    public let lastSeenUtc: Date

    public init(deviceId: String, name: String, kind: String, firmwareVersion: String, lastSeenUtc: Date) {
        self.deviceId = deviceId
        self.name = name
        self.kind = kind
        self.firmwareVersion = firmwareVersion
        self.lastSeenUtc = lastSeenUtc
    }
}

/// A telemetry reading from a device.
public struct IoTTelemetry: Sendable, Equatable, Codable {
    public let deviceId: String
    public let metric: String
    public let value: Double
    public let atUtc: Date

    public init(deviceId: String, metric: String, value: Double, atUtc: Date) {
        self.deviceId = deviceId
        self.metric = metric
        self.value = value
        self.atUtc = atUtc
    }
}

/// A command sent to a device.
public struct IoTCommand: Sendable, Equatable, Codable {
    public let commandId: String
    public let deviceId: String
    public let action: String
    public let argumentsJson: String
    public let sentUtc: Date

    public init(commandId: String, deviceId: String, action: String, argumentsJson: String, sentUtc: Date) {
        self.commandId = commandId
        self.deviceId = deviceId
        self.action = action
        self.argumentsJson = argumentsJson
        self.sentUtc = sentUtc
    }
}

// MARK: - Errors

public enum IoTError: Error, Equatable, CustomStringConvertible {
    case limitOutOfRange

    public var description: String {
        switch self {
        case .limitOutOfRange: return "limit out of range"
        }
    }
}

// MARK: - Contract

/// Device registry, telemetry, and commands for the IoT vertical.
public protocol IIoTBoard: AnyObject, Sendable {
    func register(_ d: IoTDevice)
    func getDevice(_ id: String) -> IoTDevice?
    var devices: [IoTDevice] { get }
    func recordTelemetry(_ t: IoTTelemetry)
    func latestValue(deviceId: String, metric: String) -> Double
    func history(deviceId: String, metric: String, limit: Int) throws -> [IoTTelemetry]
    func sendCommand(_ c: IoTCommand)
    func commandsFor(deviceId: String) -> [IoTCommand]
}

public extension IIoTBoard {
    /// Overload matching the C# default `limit = 100`.
    func history(deviceId: String, metric: String) throws -> [IoTTelemetry] {
        try history(deviceId: deviceId, metric: metric, limit: 100)
    }
}

// MARK: - InMemoryIoTBoard

/// Deterministic in-memory `IIoTBoard`. All state guarded by a single `NSLock`.
public final class InMemoryIoTBoard: IIoTBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var devicesMap: [String: IoTDevice] = [:]
    private var telemetry: [IoTTelemetry] = []
    private var commands: [IoTCommand] = []

    public init() {}

    public func register(_ d: IoTDevice) {
        lock.lock(); defer { lock.unlock() }
        devicesMap[d.deviceId] = d
    }

    public func getDevice(_ id: String) -> IoTDevice? {
        lock.lock(); defer { lock.unlock() }
        return devicesMap[id]
    }

    public var devices: [IoTDevice] {
        lock.lock(); defer { lock.unlock() }
        return devicesMap.values.sorted { $0.name < $1.name }
    }

    public func recordTelemetry(_ t: IoTTelemetry) {
        lock.lock(); defer { lock.unlock() }
        telemetry.append(t)
    }

    public func latestValue(deviceId: String, metric: String) -> Double {
        lock.lock(); defer { lock.unlock() }
        let hit = telemetry.filter { $0.deviceId == deviceId && $0.metric == metric }
            .max { $0.atUtc < $1.atUtc }
        return hit?.value ?? Double.nan
    }

    public func history(deviceId: String, metric: String, limit: Int) throws -> [IoTTelemetry] {
        if limit <= 0 { throw IoTError.limitOutOfRange }
        lock.lock(); defer { lock.unlock() }
        let hits = telemetry.filter { $0.deviceId == deviceId && $0.metric == metric }
            .sorted { $0.atUtc > $1.atUtc }
        return Array(hits.prefix(limit))
    }

    public func sendCommand(_ c: IoTCommand) {
        lock.lock(); defer { lock.unlock() }
        commands.append(c)
    }

    public func commandsFor(deviceId: String) -> [IoTCommand] {
        lock.lock(); defer { lock.unlock() }
        return commands.filter { $0.deviceId == deviceId }.sorted { $0.sentUtc > $1.sentUtc }
    }
}
