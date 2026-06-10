// Device.swift
//
// DeviceProbe + DeviceTier + DeviceTierDefaults + IDeviceContext.

import Foundation

public enum GpuKind: Int, Sendable { case none = 0, integrated, discrete, npu, metal, vulkan, openCL }
public enum ThermalClass: Int, Sendable { case active = 0, passive, constrained, sealed }
public enum Connectivity: Int, Sendable { case unknown = 0, offline, meshOnly, metered, unlimited }
public enum DeviceTier: Int, Sendable { case wearable = 0, phone, tablet, desktop, workstation }

public struct DeviceProbe: Sendable, Equatable {
    public let ramAvailableBytes: Int64
    public let storageFreeBytes: Int64
    public let cpuCores: Int
    public let gpuKind: GpuKind
    public let thermalClass: ThermalClass
    public let connectivity: Connectivity

    public init(
        ramAvailableBytes: Int64,
        storageFreeBytes: Int64,
        cpuCores: Int,
        gpuKind: GpuKind = .none,
        thermalClass: ThermalClass = .active,
        connectivity: Connectivity = .unknown
    ) {
        self.ramAvailableBytes = ramAvailableBytes
        self.storageFreeBytes = storageFreeBytes
        self.cpuCores = cpuCores
        self.gpuKind = gpuKind
        self.thermalClass = thermalClass
        self.connectivity = connectivity
    }

    public func classify() -> DeviceTier {
        let gb = Double(ramAvailableBytes) / (1024.0 * 1024 * 1024)
        if thermalClass == .sealed { return .wearable }
        if gb < 2 || thermalClass == .constrained { return .phone }
        if gb < 8 || thermalClass == .passive { return .tablet }
        if gb < 32 { return .desktop }
        return .workstation
    }

    public static func snapshot(
        modelCacheDirectory: String? = nil,
        gpuOverride: GpuKind? = nil,
        thermalOverride: ThermalClass? = nil
    ) -> DeviceProbe {
        return DeviceProbe(
            ramAvailableBytes: probeRamAvailable(),
            storageFreeBytes: probeStorageFree(modelCacheDirectory),
            cpuCores: ProcessInfo.processInfo.processorCount,
            gpuKind: gpuOverride ?? .none,
            thermalClass: thermalOverride ?? .active,
            connectivity: .unknown
        )
    }

    private static func probeRamAvailable() -> Int64 {
        // host_page_size + vm_statistics64 would be the canonical Darwin
        // path; for the portable surface we ship ProcessInfo.physicalMemory.
        // Callers needing free-pages-vs-total can supply an IDeviceContext.
        return Int64(ProcessInfo.processInfo.physicalMemory)
    }

    private static func probeStorageFree(_ path: String?) -> Int64 {
        guard let path = path, !path.isEmpty else { return 0 }
        let url = URL(fileURLWithPath: path)
        if let attrs = try? FileManager.default.attributesOfFileSystem(forPath: url.path),
           let free = attrs[.systemFreeSize] as? NSNumber {
            return free.int64Value
        }
        return 0
    }
}

public enum DeviceTierDefaults {
    public static func contextWindow(_ tier: DeviceTier) -> Int {
        switch tier {
        case .wearable: return 2048
        case .phone: return 4096
        case .tablet: return 8192
        case .desktop: return 32_768
        case .workstation: return 131_072
        }
    }

    public static func maxConcurrency(_ tier: DeviceTier, cpuCores: Int) -> Int {
        switch tier {
        case .wearable: return 1
        case .phone: return 2
        case .tablet: return 4
        case .desktop: return 8
        case .workstation: return min(16, max(1, cpuCores - 2))
        }
    }

    public static func agenticMaxIterations(_ tier: DeviceTier) -> Int {
        switch tier {
        case .wearable: return 2
        case .phone: return 3
        case .tablet: return 5
        case .desktop, .workstation: return 10
        }
    }
}

/// Sensorium contract — all fields optional.
public protocol IDeviceContext: Sendable {
    var activeAppId: String? { get }
    var locale: String? { get }
    var timeZoneId: String? { get }
    var localTime: Date? { get }
    var latitude: Double? { get }
    var longitude: Double? { get }
    var locationHint: String? { get }
    var batteryLevel: Float? { get }
    var isCharging: Bool? { get }
    var networkType: String? { get }
    var cpuUsagePercent: Float? { get }
    var availableMemoryBytes: Int64? { get }
    var thermalState: String? { get }
    var storageFreeBytes: Int64? { get }
    var lastActiveUtc: Date? { get }
}

public struct NullDeviceContext: IDeviceContext {
    public init() {}
    public var activeAppId: String? { nil }
    public var locale: String? { nil }
    public var timeZoneId: String? { nil }
    public var localTime: Date? { nil }
    public var latitude: Double? { nil }
    public var longitude: Double? { nil }
    public var locationHint: String? { nil }
    public var batteryLevel: Float? { nil }
    public var isCharging: Bool? { nil }
    public var networkType: String? { nil }
    public var cpuUsagePercent: Float? { nil }
    public var availableMemoryBytes: Int64? { nil }
    public var thermalState: String? { nil }
    public var storageFreeBytes: Int64? { nil }
    public var lastActiveUtc: Date? { nil }
}

public struct DefaultDeviceContext: IDeviceContext {
    private let modelCacheDir: String
    private let thermalHint: ThermalClass

    public init(modelCacheDir: String = "", thermalHint: ThermalClass = .active) {
        self.modelCacheDir = modelCacheDir
        self.thermalHint = thermalHint
    }

    public var activeAppId: String? { nil }
    public var locale: String? { Locale.current.identifier }
    public var timeZoneId: String? { TimeZone.current.identifier }
    public var localTime: Date? { Date() }
    public var latitude: Double? { nil }
    public var longitude: Double? { nil }
    public var locationHint: String? { nil }
    public var batteryLevel: Float? { nil }
    public var isCharging: Bool? { nil }
    public var networkType: String? { nil }
    public var cpuUsagePercent: Float? { nil }
    public var availableMemoryBytes: Int64? { Int64(ProcessInfo.processInfo.physicalMemory) }
    public var thermalState: String? { "normal" }
    public var storageFreeBytes: Int64? {
        guard !modelCacheDir.isEmpty,
              let attrs = try? FileManager.default.attributesOfFileSystem(forPath: modelCacheDir),
              let free = attrs[.systemFreeSize] as? NSNumber else { return nil }
        return free.int64Value
    }
    public var lastActiveUtc: Date? { nil }

    public func buildProbe(gpuOverride: GpuKind? = nil) -> DeviceProbe {
        return DeviceProbe.snapshot(
            modelCacheDirectory: modelCacheDir.isEmpty ? nil : modelCacheDir,
            gpuOverride: gpuOverride,
            thermalOverride: thermalHint
        )
    }
}
