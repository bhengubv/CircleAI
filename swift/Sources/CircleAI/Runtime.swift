// Runtime.swift
//
// Port of CircleAI.Runtime/ — the host-capability + backend-selection +
// native-runtime-fetch layer that decides which MNN backend and model tier a
// host can run and where the pre-built native binaries live.
//
// Collapses the C# folders:
//   • Capabilities/HostProfile.cs        — OperatingSystemKind, ArchitectureKind,
//                                           GpuVendor, NpuVendor, GpuInfo, NpuInfo,
//                                           HostProfile
//   • Capabilities/ICapabilityProbe.cs   — ICapabilityProbe
//   • Capabilities/CapabilityProbe.cs    — CapabilityProbe (delegating) +
//                                           UnknownCapabilityProbe
//   • Backends/BackendKind.cs            — BackendKind
//   • Backends/CapabilityTier.cs         — CapabilityTier
//   • Backends/IBackendSelector.cs       — BackendSelection, IBackendSelector
//   • Backends/BackendSelector.cs        — BackendSelector (deterministic table)
//   • NativeRuntimes/*                   — NativeRuntimeBundle, NativeRuntimeInstall,
//                                           INativeRuntimeFetcher, NativeRuntimeRegistry,
//                                           in-memory fetcher
//
// Porting notes:
//   • The C# platform-specific probes (Windows/Linux/macOS/Android) shell out
//     to the host (WMI, /proc, sysctl, Build.*). That is external I/O, so the
//     Swift port models `ICapabilityProbe` as an injectable protocol with two
//     deterministic implementations: `StaticCapabilityProbe` (returns a fixed
//     profile) and `UnknownCapabilityProbe` (all-Unknown fallback). The
//     `CapabilityProbe` type here is the delegating wrapper (matches the C#
//     `CapabilityProbe(ICapabilityProbe inner)` constructor).
//   • `NativeRuntimeFetcher` in C# is pure HttpClient + filesystem. That is
//     external, so the Swift port's `InMemoryNativeRuntimeFetcher` operates
//     over an injected byte-store (contentHash → archive bytes stand-in) and
//     tracks which tuples are "installed" in memory, preserving the registry
//     lookup, fast-path, and cache semantics deterministically. The registry
//     lookup logic (`NativeRuntimeRegistry.find`) is pure and ported verbatim.
//   • enums keep their C# integer values (Codable via Int raw value).
//   • `IProgress<double>` → an optional `@Sendable (Double) -> Void` reporter.

import Foundation
import CryptoKit

// MARK: - Capability enums (Capabilities/HostProfile.cs)

/// OS family the probe recognised. (C# `OperatingSystemKind`.)
public enum OperatingSystemKind: Int, Sendable, Codable, CaseIterable {
    /// Probe could not identify the OS.
    case unknown = 0
    /// Microsoft Windows desktop / Server.
    case windows = 1
    /// Any Linux distribution.
    case linux = 2
    /// Apple macOS.
    case macOS = 3
    /// Google Android.
    case android = 4
    /// Apple iOS / iPadOS / tvOS / watchOS.
    case iOS = 5
    /// Huawei HarmonyOS / OpenHarmony.
    case harmonyOS = 6

    /// Lower-cased invariant name (used to build cache directory keys).
    var lowerName: String {
        switch self {
        case .unknown: return "unknown"
        case .windows: return "windows"
        case .linux: return "linux"
        case .macOS: return "macos"
        case .android: return "android"
        case .iOS: return "ios"
        case .harmonyOS: return "harmonyos"
        }
    }
}

/// CPU architecture family. (C# `ArchitectureKind`.)
public enum ArchitectureKind: Int, Sendable, Codable, CaseIterable {
    /// Probe could not identify the architecture.
    case unknown = 0
    /// 32-bit Intel/AMD.
    case x86 = 1
    /// 64-bit Intel/AMD (AMD64 / Intel 64).
    case x64 = 2
    /// 32-bit ARM.
    case arm = 3
    /// 64-bit ARM (ARMv8 / Apple Silicon).
    case arm64 = 4
    /// Loongson LoongArch64.
    case loong64 = 5

    var lowerName: String {
        switch self {
        case .unknown: return "unknown"
        case .x86: return "x86"
        case .x64: return "x64"
        case .arm: return "arm"
        case .arm64: return "arm64"
        case .loong64: return "loong64"
        }
    }
}

/// GPU vendor identifier. (C# `GpuVendor`.)
public enum GpuVendor: Int, Sendable, Codable, CaseIterable {
    /// No GPU detected, or vendor unknown.
    case none = 0
    /// NVIDIA Corp.
    case nvidia = 1
    /// Advanced Micro Devices.
    case amd = 2
    /// Intel Corp. (integrated and Arc).
    case intel = 3
    /// Apple Silicon GPU (M1/M2/M3/M4 family).
    case apple = 4
    /// Qualcomm Adreno.
    case qualcomm = 5
    /// Huawei Maleoon / Mali-licensed GPUs on Kirin SoCs.
    case huawei = 6
    /// ARM Mali (third-party SoCs not covered by other vendors).
    case arm = 7
    /// Vendor identified but not in this enum yet.
    case other = 99
}

/// NPU / neural accelerator vendor identifier. (C# `NpuVendor`.)
public enum NpuVendor: Int, Sendable, Codable, CaseIterable {
    /// No NPU detected.
    case none = 0
    /// Apple Neural Engine (ANE) on Apple Silicon.
    case appleNeuralEngine = 1
    /// Qualcomm Hexagon DSP / NPU.
    case qualcommHexagon = 2
    /// Huawei Ascend.
    case huaweiAscend = 3
    /// Intel VPU (Movidius / Meteor Lake NPU).
    case intelVpu = 4
    /// Cambricon MLU.
    case cambriconMlu = 5
    /// Vendor identified but not in this enum yet.
    case other = 99
}

/// Discovered GPU details. (C# `GpuInfo`.)
public struct GpuInfo: Sendable, Equatable, Codable {
    /// Vendor family.
    public let vendor: GpuVendor
    /// Marketing name (e.g. "NVIDIA GeForce RTX 4080").
    public let model: String
    /// Dedicated video memory in bytes. 0 when probe could not determine.
    public let vramBytes: Int64
    /// Driver version string when known.
    public let driverVersion: String?

    public init(vendor: GpuVendor, model: String, vramBytes: Int64, driverVersion: String?) {
        self.vendor = vendor
        self.model = model
        self.vramBytes = vramBytes
        self.driverVersion = driverVersion
    }
}

/// Discovered NPU details. (C# `NpuInfo`.)
public struct NpuInfo: Sendable, Equatable, Codable {
    /// Vendor family.
    public let vendor: NpuVendor
    /// Marketing name (e.g. "Apple Neural Engine 16-core").
    public let model: String

    public init(vendor: NpuVendor, model: String) {
        self.vendor = vendor
        self.model = model
    }
}

/// Full host capability snapshot — the result of an `ICapabilityProbe.probe`
/// call. (C# `HostProfile`.)
public struct HostProfile: Sendable, Equatable, Codable {
    /// OS family.
    public let os: OperatingSystemKind
    /// OS version string (e.g. "10.0.22631", "14.4.1").
    public let osVersion: String
    /// CPU architecture family.
    public let arch: ArchitectureKind
    /// CPU marketing name.
    public let cpuModel: String
    /// Logical CPU core count (includes SMT siblings).
    public let logicalCoreCount: Int
    /// Physical CPU core count (HT pairs counted once).
    public let physicalCoreCount: Int
    /// Installed RAM in bytes.
    public let totalPhysicalMemoryBytes: Int64
    /// GPU details. `nil` when no usable GPU was detected.
    public let gpu: GpuInfo?
    /// NPU details. `nil` when no NPU was detected.
    public let npu: NpuInfo?
    /// UTC timestamp the probe was taken at.
    public let probedAt: Date

    public init(os: OperatingSystemKind, osVersion: String, arch: ArchitectureKind,
                cpuModel: String, logicalCoreCount: Int, physicalCoreCount: Int,
                totalPhysicalMemoryBytes: Int64, gpu: GpuInfo?, npu: NpuInfo?, probedAt: Date) {
        self.os = os
        self.osVersion = osVersion
        self.arch = arch
        self.cpuModel = cpuModel
        self.logicalCoreCount = logicalCoreCount
        self.physicalCoreCount = physicalCoreCount
        self.totalPhysicalMemoryBytes = totalPhysicalMemoryBytes
        self.gpu = gpu
        self.npu = npu
        self.probedAt = probedAt
    }

    /// True when `gpu` is present and has at least `minimumVramBytes` of VRAM.
    /// Default 2 GiB (matches the C# default parameter).
    public func hasUsableGpu(minimumVramBytes: Int64 = 2 * 1024 * 1024 * 1024) -> Bool {
        guard let gpu = gpu else { return false }
        return gpu.vramBytes >= minimumVramBytes
    }

    /// True when the host runs on a 64-bit architecture (X64, Arm64, Loong64).
    public var is64Bit: Bool {
        arch == .x64 || arch == .arm64 || arch == .loong64
    }
}

// MARK: - ICapabilityProbe (Capabilities/ICapabilityProbe.cs + CapabilityProbe.cs)

/// Discovers the host's hardware capabilities and returns a normalised
/// `HostProfile`. Implementations MUST NOT throw on probe failure — instead,
/// fields the probe could not resolve are returned as `.unknown` / `nil` / `0`.
public protocol ICapabilityProbe: Sendable {
    /// Runs the probe.
    func probe() async -> HostProfile
}

/// Deterministic probe that returns a fixed `HostProfile`. Replaces the C#
/// platform-specific probes (which shell out to WMI / /proc / sysctl) in the
/// portable Swift surface — hosts inject one built from their own detection.
public struct StaticCapabilityProbe: ICapabilityProbe {
    private let profile: HostProfile
    public init(_ profile: HostProfile) { self.profile = profile }
    public func probe() async -> HostProfile { profile }
}

/// Returned on platforms where no in-process probe is registered. All fields
/// fall back to `.unknown` / `0` / `nil`. Mirrors the C#
/// `UnknownCapabilityProbe`.
public struct UnknownCapabilityProbe: ICapabilityProbe {
    public static let shared = UnknownCapabilityProbe()
    public init() {}
    public func probe() async -> HostProfile {
        HostProfile(
            os: .unknown,
            osVersion: "0.0.0",
            arch: .unknown,
            cpuModel: "Unknown CPU",
            logicalCoreCount: ProcessInfo.processInfo.processorCount,
            physicalCoreCount: ProcessInfo.processInfo.processorCount,
            totalPhysicalMemoryBytes: 0,
            gpu: nil,
            npu: nil,
            probedAt: Date())
    }
}

/// Default `ICapabilityProbe` — delegates to an injected inner probe. Mirrors
/// the C# `CapabilityProbe(ICapabilityProbe inner)` constructor; the OS-detection
/// switch that picks a platform shell in C# is host-specific and lives in the
/// port packages, so the Swift default wraps `UnknownCapabilityProbe`.
public struct CapabilityProbe: ICapabilityProbe {
    private let inner: any ICapabilityProbe
    /// Construct with an explicit inner probe.
    public init(_ inner: any ICapabilityProbe) { self.inner = inner }
    /// Construct the default probe (Unknown fallback).
    public init() { self.inner = UnknownCapabilityProbe.shared }
    public func probe() async -> HostProfile { await inner.probe() }
}

// MARK: - Backend enums (Backends/BackendKind.cs + CapabilityTier.cs)
//
// `BackendKind` and `CapabilityTier` are already defined once in
// InferenceServer.swift (the CircleAI.Inference.Server.Enterprise port pulls
// from the same CircleAI.Runtime.Backends enums). They must NOT be redefined
// here. This module only adds the extra conformances + helpers the Runtime
// surface needs: `Comparable` (for tier clamping), `Codable` (so
// `BackendSelection` can round-trip), and a lower-invariant name (for cache
// directory keys and the registry's case-insensitive parse).

/// `Comparable` via raw value so `BackendSelector` can clamp requested vs
/// ceiling tiers.
extension CapabilityTier: Comparable {
    public static func < (lhs: CapabilityTier, rhs: CapabilityTier) -> Bool {
        lhs.rawValue < rhs.rawValue
    }
}

/// `Codable` via the Int raw value (matches how the C# enum serialises).
extension CapabilityTier: Codable {}
extension BackendKind: Codable {}

extension BackendKind {
    /// Lower-invariant enum name (`b.ToString().ToLowerInvariant()`), used to
    /// build cache directory keys.
    var lowerName: String {
        switch self {
        case .cpu: return "cpu"
        case .cuda: return "cuda"
        case .vulkan: return "vulkan"
        case .openCL: return "opencl"
        case .metal: return "metal"
        case .ascend: return "ascend"
        case .cambricon: return "cambricon"
        case .coreML: return "coreml"
        }
    }
}

// MARK: - IBackendSelector (Backends/IBackendSelector.cs + BackendSelector.cs)

/// Result of an `IBackendSelector.select` call. (C# `BackendSelection`.)
public struct BackendSelection: Sendable, Equatable, Codable {
    /// Chosen MNN execution backend.
    public let backend: BackendKind
    /// Tier the host can actually run (≤ requested).
    public let actualTier: CapabilityTier
    /// Human-readable explanation of the choice.
    public let rationale: String

    public init(backend: BackendKind, actualTier: CapabilityTier, rationale: String) {
        self.backend = backend
        self.actualTier = actualTier
        self.rationale = rationale
    }
}

/// Picks the MNN backend and model tier for a given host. Implementations must
/// NEVER throw and must NEVER return nil — every host can run the CPU backend
/// at Tier 0 as a last resort. (C# `IBackendSelector`.)
public protocol IBackendSelector: Sendable {
    /// Pick the best backend + tier combo for the given host. `requestedTier`
    /// is the upper bound — the returned tier may be lower.
    func select(profile: HostProfile, requestedTier: CapabilityTier) -> BackendSelection
}

/// Default `IBackendSelector` — deterministic table-style selector, no I/O.
/// Ported verbatim from the C# `BackendSelector`. (C# `BackendSelector`.)
public struct BackendSelector: IBackendSelector {
    private static let giB: Int64 = 1024 * 1024 * 1024

    public init() {}

    public func select(profile: HostProfile, requestedTier: CapabilityTier) -> BackendSelection {
        let giB = Self.giB

        // 1. Apple Silicon — Metal + ANE via unified memory.
        if profile.os == .macOS, profile.arch == .arm64, profile.gpu?.vendor == .apple {
            let tier = Self.clampTier(requestedTier,
                                      ceiling: Self.tierForUnifiedMemory(profile.totalPhysicalMemoryBytes))
            return BackendSelection(
                backend: .metal, actualTier: tier,
                rationale: "Apple Silicon (\(profile.cpuModel)); Metal over unified-memory GPU; tier capped to \(tier) by \(profile.totalPhysicalMemoryBytes / giB) GiB unified RAM.")
        }

        // 2. NVIDIA + CUDA.
        if let gpu = profile.gpu, gpu.vendor == .nvidia, gpu.vramBytes >= 4 * giB {
            let tier = Self.clampTier(requestedTier, ceiling: Self.tierForVram(gpu.vramBytes))
            return BackendSelection(
                backend: .cuda, actualTier: tier,
                rationale: "NVIDIA \(gpu.model) with \(gpu.vramBytes / giB) GiB VRAM; CUDA backend; tier capped to \(tier) by VRAM.")
        }

        // 3. Huawei Ascend NPU.
        if let npu = profile.npu, npu.vendor == .huaweiAscend {
            let tier = Self.clampTier(requestedTier, ceiling: .tier3Large)
            return BackendSelection(
                backend: .ascend, actualTier: tier,
                rationale: "Huawei Ascend NPU detected (\(npu.model)); Ascend (CANN) backend; tier capped to \(tier).")
        }

        // 4. Cambricon MLU.
        if let npu = profile.npu, npu.vendor == .cambriconMlu {
            let tier = Self.clampTier(requestedTier, ceiling: .tier3Large)
            return BackendSelection(
                backend: .cambricon, actualTier: tier,
                rationale: "Cambricon MLU detected; Cambricon backend; tier capped to \(tier).")
        }

        // 5. AMD / Intel discrete GPU — Vulkan.
        if let g = profile.gpu, (g.vendor == .amd || g.vendor == .intel), g.vramBytes >= 4 * giB {
            let tier = Self.clampTier(requestedTier, ceiling: Self.tierForVram(g.vramBytes))
            return BackendSelection(
                backend: .vulkan, actualTier: tier,
                rationale: "\(g.vendor) \(g.model) with \(g.vramBytes / giB) GiB VRAM; Vulkan backend; tier capped to \(tier) by VRAM.")
        }

        // 6. Qualcomm Hexagon NPU / Adreno — OpenCL.
        if profile.npu?.vendor == .qualcommHexagon || profile.gpu?.vendor == .qualcomm {
            let tier = Self.clampTier(requestedTier, ceiling: .tier1Small)
            return BackendSelection(
                backend: .openCL, actualTier: tier,
                rationale: "Qualcomm Snapdragon platform; OpenCL backend (Adreno/Hexagon shared compute); tier capped to \(tier).")
        }

        // 7. ARM Mali via Vulkan.
        if let gpu = profile.gpu, gpu.vendor == .arm || gpu.vendor == .huawei {
            let tier = Self.clampTier(requestedTier, ceiling: .tier1Small)
            return BackendSelection(
                backend: .vulkan, actualTier: tier,
                rationale: "ARM/Mali class GPU (\(gpu.model)); Vulkan backend; tier capped to \(tier).")
        }

        // 8. CPU fallback — always selectable.
        let cpuTier = Self.clampTier(requestedTier, ceiling: Self.tierForCpuRam(profile.totalPhysicalMemoryBytes))
        return BackendSelection(
            backend: .cpu, actualTier: cpuTier,
            rationale: "No usable accelerator detected; CPU SIMD backend on \(profile.cpuModel) "
                + "(\(profile.logicalCoreCount) logical cores, \(profile.totalPhysicalMemoryBytes / giB) GiB RAM); "
                + "tier capped to \(cpuTier) by available RAM.")
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static func clampTier(_ requested: CapabilityTier, ceiling: CapabilityTier) -> CapabilityTier {
        requested <= ceiling ? requested : ceiling
    }

    private static func tierForVram(_ vramBytes: Int64) -> CapabilityTier {
        if vramBytes >= 24 * giB { return .tier4Frontier }
        if vramBytes >= 12 * giB { return .tier3Large }
        if vramBytes >= 8 * giB { return .tier2Medium }
        if vramBytes >= 4 * giB { return .tier1Small }
        return .tier0Tiny
    }

    private static func tierForUnifiedMemory(_ ramBytes: Int64) -> CapabilityTier {
        if ramBytes >= 64 * giB { return .tier4Frontier }
        if ramBytes >= 32 * giB { return .tier3Large }
        if ramBytes >= 16 * giB { return .tier2Medium }
        if ramBytes >= 8 * giB { return .tier1Small }
        return .tier0Tiny
    }

    private static func tierForCpuRam(_ ramBytes: Int64) -> CapabilityTier {
        if ramBytes >= 64 * giB { return .tier3Large }
        if ramBytes >= 32 * giB { return .tier2Medium }
        if ramBytes >= 16 * giB { return .tier1Small }
        if ramBytes >= 8 * giB { return .tier1Small }
        return .tier0Tiny
    }
}

// MARK: - Native runtime records (NativeRuntimes/NativeRuntimeBundle.cs)

/// A single fetchable MNN runtime bundle for one (OS, arch, backend) tuple.
/// (C# `NativeRuntimeBundle`.) `Uri` → `URL`.
public struct NativeRuntimeBundle: Sendable, Equatable, Codable {
    /// MNN release version (e.g. "3.5.0").
    public let mnnVersion: String
    /// Target OS family.
    public let os: OperatingSystemKind
    /// Target CPU architecture.
    public let arch: ArchitectureKind
    /// Execution backend the bundle implements.
    public let backend: BackendKind
    /// Primary download URI.
    public let primaryUri: URL
    /// Fallback URI, or `nil`.
    public let fallbackUri: URL?
    /// SHA-256 of the archive in hex, or `nil` when not pinned.
    public let archiveSha256Hex: String?
    /// File name of the MNN core library to locate inside the extracted tree.
    public let mnnCoreLibraryName: String

    public init(mnnVersion: String, os: OperatingSystemKind, arch: ArchitectureKind,
                backend: BackendKind, primaryUri: URL, fallbackUri: URL?,
                archiveSha256Hex: String?, mnnCoreLibraryName: String) {
        self.mnnVersion = mnnVersion
        self.os = os
        self.arch = arch
        self.backend = backend
        self.primaryUri = primaryUri
        self.fallbackUri = fallbackUri
        self.archiveSha256Hex = archiveSha256Hex
        self.mnnCoreLibraryName = mnnCoreLibraryName
    }
}

/// Result of a successful `INativeRuntimeFetcher.ensureRuntime` call — describes
/// where the runtime now lives on disk and where MNN was found. (C#
/// `NativeRuntimeInstall`.)
public struct NativeRuntimeInstall: Sendable, Equatable, Codable {
    /// The bundle that was fetched (or matched in cache).
    public let bundle: NativeRuntimeBundle
    /// Absolute directory the archive was extracted into.
    public let extractedRoot: String
    /// Absolute path to the MNN core library at its nested location.
    public let mnnCorePath: String

    public init(bundle: NativeRuntimeBundle, extractedRoot: String, mnnCorePath: String) {
        self.bundle = bundle
        self.extractedRoot = extractedRoot
        self.mnnCorePath = mnnCorePath
    }
}

// MARK: - NativeRuntimeRegistry (NativeRuntimes/NativeRuntimeRegistry.cs)

/// Loads a native-runtime registry and exposes lookup by tuple. The C# version
/// parses an embedded JSON resource; the Swift port keeps the (pure) lookup and
/// JSON-parse logic and accepts bundles either directly or from JSON `Data`.
/// (C# `NativeRuntimeRegistry`.)
public struct NativeRuntimeRegistry: Sendable {
    private let bundles: [NativeRuntimeBundle]

    /// Construct from an explicit list of bundles.
    public init(_ bundles: [NativeRuntimeBundle]) { self.bundles = bundles }

    /// All loaded bundles.
    public var all: [NativeRuntimeBundle] { bundles }

    /// Look up the newest bundle matching (os, arch, backend). Highest MNN
    /// version string wins (ordinal descending), matching the C# `Find`.
    public func find(os: OperatingSystemKind, arch: ArchitectureKind, backend: BackendKind) -> NativeRuntimeBundle? {
        bundles
            .filter { $0.os == os && $0.arch == arch && $0.backend == backend }
            .sorted { $0.mnnVersion > $1.mnnVersion }
            .first
    }

    /// Parse the `embedded_native_registry.json` shape:
    /// `{ "mnn_versions": [ { "version": "3.5.0", "bundles": [ {os,arch,backend,url,...} ] } ] }`.
    /// Non-object / malformed entries are tolerated (skipped), matching the C#
    /// `LoadFromStream`.
    public static func load(fromJson data: Data) -> NativeRuntimeRegistry {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let versions = root["mnn_versions"] as? [[String: Any]] else {
            return NativeRuntimeRegistry([])
        }
        var list: [NativeRuntimeBundle] = []
        for versionEntry in versions {
            guard let mnnVersion = versionEntry["version"] as? String,
                  let bundlesArr = versionEntry["bundles"] as? [[String: Any]] else { continue }
            for b in bundlesArr {
                if let bundle = parseBundle(mnnVersion: mnnVersion, b) { list.append(bundle) }
            }
        }
        return NativeRuntimeRegistry(list)
    }

    private static func parseBundle(mnnVersion: String, _ b: [String: Any]) -> NativeRuntimeBundle? {
        guard let osStr = b["os"] as? String,
              let archStr = b["arch"] as? String,
              let backendStr = b["backend"] as? String,
              let urlStr = b["url"] as? String,
              let os = osKind(osStr),
              let arch = archKind(archStr),
              let backend = BackendKind.parse(backendStr),  // reuse InferenceServer's parser
              let primaryUri = URL(string: urlStr) else {
            return nil
        }
        var fallback: URL? = nil
        if let fbStr = b["fallback_url"] as? String, let fbUri = URL(string: fbStr) {
            fallback = fbUri
        }
        let sha = b["sha256"] as? String
        let coreLib = (b["mnn_lib"] as? String) ?? defaultCoreLibName(os)
        return NativeRuntimeBundle(mnnVersion: mnnVersion, os: os, arch: arch, backend: backend,
                                   primaryUri: primaryUri, fallbackUri: fallback,
                                   archiveSha256Hex: sha, mnnCoreLibraryName: coreLib)
    }

    static func defaultCoreLibName(_ os: OperatingSystemKind) -> String {
        switch os {
        case .windows: return "MNN.dll"
        case .macOS, .iOS: return "MNN"
        default: return "libMNN.so"
        }
    }

    // Case-insensitive enum name parsing (matches Enum.TryParse ignoreCase).
    private static func osKind(_ s: String) -> OperatingSystemKind? {
        switch s.lowercased() {
        case "unknown": return .unknown
        case "windows": return .windows
        case "linux": return .linux
        case "macos": return .macOS
        case "android": return .android
        case "ios": return .iOS
        case "harmonyos": return .harmonyOS
        default: return nil
        }
    }

    private static func archKind(_ s: String) -> ArchitectureKind? {
        switch s.lowercased() {
        case "unknown": return .unknown
        case "x86": return .x86
        case "x64": return .x64
        case "arm": return .arm
        case "arm64": return .arm64
        case "loong64": return .loong64
        default: return nil
        }
    }
}

// MARK: - INativeRuntimeFetcher (NativeRuntimes/INativeRuntimeFetcher.cs)

/// Errors raised by the native-runtime fetcher. Mirrors the C#
/// `InvalidOperationException` cases.
public enum NativeRuntimeError: Error, Equatable, CustomStringConvertible {
    /// No registry entry exists for the requested tuple.
    case noBundleRegistered(OperatingSystemKind, ArchitectureKind, BackendKind)
    /// SHA-256 verification failed after download.
    case shaMismatch(OperatingSystemKind, ArchitectureKind, BackendKind, mnnVersion: String)
    /// The (injected) content store had no bytes for the bundle's URI.
    case contentUnavailable(URL)

    public var description: String {
        switch self {
        case let .noBundleRegistered(os, arch, backend):
            return "No native runtime bundle registered for (\(os), \(arch), \(backend))."
        case let .shaMismatch(os, arch, backend, v):
            return "SHA-256 mismatch for runtime bundle (\(os), \(arch), \(backend), MNN \(v))."
        case let .contentUnavailable(uri):
            return "No archive content available for \(uri)."
        }
    }
}

/// Pre-built MNN native runtime fetcher. Resolves the right runtime for
/// (os, arch, backend), materialises it, verifies it, and returns the on-disk
/// paths the caller uses. (C# `INativeRuntimeFetcher`.)
public protocol INativeRuntimeFetcher: Sendable {
    /// Ensure the runtime matching (os, arch, backend) is present + extracted.
    /// `progress` reports in [0.0, 1.0] and 1.0 on completion. Throws
    /// `NativeRuntimeError` when no bundle is registered or SHA verify fails.
    func ensureRuntime(os: OperatingSystemKind, arch: ArchitectureKind, backend: BackendKind,
                       progress: (@Sendable (Double) -> Void)?) async throws -> NativeRuntimeInstall

    /// True when the runtime for the tuple is already materialised. No network.
    func isRuntimeCached(os: OperatingSystemKind, arch: ArchitectureKind, backend: BackendKind) async -> Bool

    /// Lists the runtime bundles known to the registry.
    func listAvailableBundles() -> [NativeRuntimeBundle]
}

public extension INativeRuntimeFetcher {
    /// Overload matching the C# default `progress = null`.
    func ensureRuntime(os: OperatingSystemKind, arch: ArchitectureKind,
                       backend: BackendKind) async throws -> NativeRuntimeInstall {
        try await ensureRuntime(os: os, arch: arch, backend: backend, progress: nil)
    }
}

/// A store of runtime-archive bytes keyed by download URI. Replaces the C#
/// fetcher's `HttpClient` — hosts inject real bytes (or a network-backed
/// implementation); tests inject a dictionary. Returning `nil` means the
/// content is unavailable, which surfaces as `NativeRuntimeError.contentUnavailable`
/// unless a fallback URI succeeds.
public protocol INativeRuntimeContentStore: Sendable {
    /// Returns the archive bytes for `uri`, or `nil` when unavailable.
    func fetch(_ uri: URL) async -> Data?
}

/// Deterministic in-memory content store. Seed with `add(uri:bytes:)`.
public final class InMemoryNativeRuntimeContentStore: INativeRuntimeContentStore, @unchecked Sendable {
    private let lock = NSLock()
    private var contents: [URL: Data] = [:]

    public init() {}

    /// Register archive bytes for a download URI.
    public func add(uri: URL, bytes: Data) {
        lock.lock(); defer { lock.unlock() }
        contents[uri] = bytes
    }

    public func fetch(_ uri: URL) async -> Data? {
        lock.lock(); defer { lock.unlock() }
        return contents[uri]
    }
}

/// Deterministic in-memory `INativeRuntimeFetcher`. Resolves bundles via the
/// registry, "downloads" archive bytes from the injected content store (primary
/// then fallback), verifies the pinned SHA-256, and records the tuple as
/// installed — preserving the C# fetcher's registry lookup + fast-path/cache +
/// fallback + SHA-verify semantics without touching the filesystem or network.
///
/// The synthetic `extractedRoot` / `mnnCorePath` are computed deterministically
/// from the bundle tuple + core library name, mirroring the C# cache directory
/// layout (`{version}-{os}-{arch}-{backend}`).
public final class InMemoryNativeRuntimeFetcher: INativeRuntimeFetcher, @unchecked Sendable {
    private let registry: NativeRuntimeRegistry
    private let content: any INativeRuntimeContentStore
    private let cacheRoot: String
    private let lock = NSLock()
    /// Cache directory key → resolved install.
    private var installed: [String: NativeRuntimeInstall] = [:]

    public init(cacheRoot: String, registry: NativeRuntimeRegistry, content: any INativeRuntimeContentStore) {
        precondition(!cacheRoot.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                     "Cache root must not be empty.")
        self.cacheRoot = cacheRoot
        self.registry = registry
        self.content = content
    }

    public func listAvailableBundles() -> [NativeRuntimeBundle] { registry.all }

    public func isRuntimeCached(os: OperatingSystemKind, arch: ArchitectureKind, backend: BackendKind) async -> Bool {
        guard let bundle = registry.find(os: os, arch: arch, backend: backend) else { return false }
        let key = extractDir(bundle)
        lock.lock(); defer { lock.unlock() }
        return installed[key] != nil
    }

    public func ensureRuntime(os: OperatingSystemKind, arch: ArchitectureKind, backend: BackendKind,
                              progress: (@Sendable (Double) -> Void)?) async throws -> NativeRuntimeInstall {
        guard let bundle = registry.find(os: os, arch: arch, backend: backend) else {
            throw NativeRuntimeError.noBundleRegistered(os, arch, backend)
        }
        let key = extractDir(bundle)

        // Fast path: already materialised.
        lock.lock()
        if let cached = installed[key] {
            lock.unlock()
            progress?(1.0)
            return cached
        }
        lock.unlock()

        // Slow path: fetch archive (primary then fallback), verify SHA.
        let bytes = try await download(bundle: bundle, progress: progress)

        if let expected = bundle.archiveSha256Hex,
           !Self.verifySha256(bytes, expectedHex: expected) {
            throw NativeRuntimeError.shaMismatch(os, arch, backend, mnnVersion: bundle.mnnVersion)
        }

        let corePath = key + "/" + bundle.mnnCoreLibraryName
        let install = NativeRuntimeInstall(bundle: bundle, extractedRoot: key, mnnCorePath: corePath)

        lock.lock()
        installed[key] = install
        lock.unlock()
        progress?(1.0)
        return install
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private func download(bundle: NativeRuntimeBundle, progress: (@Sendable (Double) -> Void)?) async throws -> Data {
        if let primary = await content.fetch(bundle.primaryUri) {
            progress?(1.0)
            return primary
        }
        if let fb = bundle.fallbackUri, let fallback = await content.fetch(fb) {
            progress?(1.0)
            return fallback
        }
        throw NativeRuntimeError.contentUnavailable(bundle.primaryUri)
    }

    private func extractDir(_ b: NativeRuntimeBundle) -> String {
        "\(cacheRoot)/\(b.mnnVersion)-\(b.os.lowerName)-\(b.arch.lowerName)-\(b.backend.lowerName)"
    }

    /// Uppercase-hex SHA-256, compared case-insensitively (matches the C#
    /// `Convert.ToHexString` + case-insensitive compare).
    static func verifySha256(_ data: Data, expectedHex: String) -> Bool {
        let actual = SHA256.hash(data: data).map { String(format: "%02X", $0) }.joined()
        return actual.caseInsensitiveCompare(expectedHex) == .orderedSame
    }
}
