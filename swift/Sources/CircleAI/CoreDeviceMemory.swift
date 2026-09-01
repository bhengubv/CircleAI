// CoreDeviceMemory.swift
//
// Where the RAM figure came from, and the platform hook that supplies a real
// one.
//
// A PROBE THAT GUESSED WAS INDISTINGUISHABLE FROM ONE THAT MEASURED, and every
// verdict downstream was then stated with full confidence about a number that is
// the managed heap limit — roughly 100 MB inside an Android sandbox. The device
// reads as a wearable, every model comes back "nothing fits", and nothing
// anywhere says the input was invented. Recording the SOURCE is what lets the
// answer admit it.
//
// Ported from the platform-memory half of src/CircleAI.Core/DeviceProbe.cs.

import Foundation

/// Real device memory, supplied by a platform head that can read it.
///
/// Two different numbers on purpose: `ramTotalBytes` is the device CLASS (what
/// hardware this is), `ramAvailableBytes` is what is free right now (what will
/// actually fit). Collapsing them makes a busy 8 GB phone look like a 2 GB one.
public struct PlatformMemory: Sendable, Equatable {
    public let ramAvailableBytes: Int64?
    public let storageFreeBytes: Int64?
    public let ramTotalBytes: Int64?

    public init(ramAvailableBytes: Int64? = nil,
                storageFreeBytes: Int64? = nil,
                ramTotalBytes: Int64? = nil) {
        self.ramAvailableBytes = ramAvailableBytes
        self.storageFreeBytes = storageFreeBytes
        self.ramTotalBytes = ramTotalBytes
    }
}

/// Where the RAM figure actually came from.
public enum RamMeasurement: Int, Sendable, Equatable, Codable, CaseIterable {
    /// A caller stated it outright (tests, hosts that already know).
    case explicit = 0
    /// Read from the device by a platform head via ``DeviceProbe/platformMemoryProbe``.
    case platformMeasured
    /// Nobody supplied one, so it was inferred. On mobile that is a guess.
    case heuristic
}

public extension DeviceProbe {

    /// Optional platform hook.
    ///
    /// The platform-neutral core cannot read a mobile device's real RAM or
    /// storage: the managed runtime reports the per-app heap limit, and the
    /// sandboxed data partition denies a free-space query. A 3 GB phone is
    /// therefore misclassified as a wearable and every model comes back as not
    /// fitting. An Android or iOS head sets this once at startup so every
    /// snapshot reports real hardware. Left nil on desktop and server, where the
    /// heuristics are accurate.
    static var platformMemoryProbe: (@Sendable () -> PlatformMemory)? {
        get { DeviceMemoryHook.probe }
        set { DeviceMemoryHook.probe = newValue }
    }

    /// A plain-language warning when the RAM figure is a guess that looks wrong,
    /// or nil when there is nothing to say.
    ///
    /// Deliberately NARROW. The heuristic is perfectly good on desktop and
    /// server, where it returns GB-scale numbers, and warning there would be
    /// noise nobody reads. It fires only on the actual signature of the bug: an
    /// inferred figure too small for any real device, which is what a mobile
    /// head that never set the probe produces.
    func measurementWarning(source: RamMeasurement) -> String? {
        guard source == .heuristic, ramAvailableBytes < 512 * 1024 * 1024 else { return nil }
        let mb = Double(ramAvailableBytes) / (1024.0 * 1024)
        return String(format: "this device's RAM was not measured — %.0f MB is the ", mb)
            + "managed heap limit, not the hardware. The platform head has not set "
            + "DeviceProbe.platformMemoryProbe, so every size decision here is based on a guess"
    }

    /// A snapshot that says where its numbers came from.
    ///
    /// Returned as a pair rather than folded into `DeviceProbe` so the existing
    /// value type keeps its shape and every caller that does not care about
    /// provenance is untouched.
    static func measuredSnapshot(
        modelCacheDirectory: String? = nil,
        gpuOverride: GpuKind? = nil,
        thermalOverride: ThermalClass? = nil,
        ramAvailableBytes: Int64? = nil,
        storageFreeBytes: Int64? = nil
    ) -> (probe: DeviceProbe, source: RamMeasurement, warning: String?) {

        var ram = ramAvailableBytes
        var storage = storageFreeBytes
        var source: RamMeasurement = ram == nil ? .heuristic : .explicit

        // The platform head is asked ONLY when the caller did not state a
        // figure. A test that passes an explicit number must not have it
        // overwritten by whatever hardware happens to be running the test.
        if ram == nil, let hook = platformMemoryProbe {
            let m = hook()
            if let measured = m.ramAvailableBytes ?? m.ramTotalBytes {
                ram = measured
                source = .platformMeasured
            }
            if storage == nil { storage = m.storageFreeBytes }
        }

        let heuristic = DeviceProbe.snapshot(modelCacheDirectory: modelCacheDirectory,
                                             gpuOverride: gpuOverride,
                                             thermalOverride: thermalOverride)

        let probe = DeviceProbe(
            ramAvailableBytes: ram ?? heuristic.ramAvailableBytes,
            storageFreeBytes: storage ?? heuristic.storageFreeBytes,
            cpuCores: heuristic.cpuCores,
            gpuKind: heuristic.gpuKind,
            thermalClass: heuristic.thermalClass,
            connectivity: heuristic.connectivity)

        return (probe, source, probe.measurementWarning(source: source))
    }
}

/// The hook's storage. A separate type because a Swift extension cannot hold a
/// stored property, and the alternative — a global — would not be findable from
/// the type a caller is already holding.
enum DeviceMemoryHook {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var stored: (@Sendable () -> PlatformMemory)?

    static var probe: (@Sendable () -> PlatformMemory)? {
        get { lock.lock(); defer { lock.unlock() }; return stored }
        set { lock.lock(); stored = newValue; lock.unlock() }
    }
}

// MARK: - A device context built only from what the runtime already knows

/// Cross-platform `IDeviceContext` using nothing but Foundation.
///
/// Everything it cannot honestly answer is nil rather than zero. A zero battery
/// level and an unknown battery level are different facts, and a context that
/// reports 0% to avoid an optional tells the assistant the phone is about to
/// die. A platform head that CAN read these registers its own.
public final class SystemInfoDeviceContext: IDeviceContext, @unchecked Sendable {

    public let activeAppId: String?

    private let lock = NSLock()
    private var lastActive: Date

    public init(activeAppId: String? = nil) {
        self.activeAppId = activeAppId
        self.lastActive = Date()
    }

    // Identity and locale — the runtime does know these.
    public var locale: String? { Locale.current.identifier }
    public var timeZoneId: String? { TimeZone.current.identifier }
    public var localTime: Date? { Date() }

    // Location — unavailable without platform APIs, and never guessed.
    public var latitude: Double? { nil }
    public var longitude: Double? { nil }
    public var locationHint: String? { nil }

    // Device health — unavailable without platform APIs.
    public var batteryLevel: Float? { nil }
    public var isCharging: Bool? { nil }
    public var networkType: String? { nil }

    // Diagnostics — unavailable without platform APIs.
    public var cpuUsagePercent: Float? { nil }
    public var availableMemoryBytes: Int64? { nil }
    public var thermalState: String? { nil }
    public var storageFreeBytes: Int64? { nil }

    public var lastActiveUtc: Date? {
        lock.lock(); defer { lock.unlock() }
        return lastActive
    }

    public func recordInteraction() {
        lock.lock(); lastActive = Date(); lock.unlock()
    }
}
