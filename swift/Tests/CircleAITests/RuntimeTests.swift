// RuntimeTests.swift
//
// Exercises the Runtime port: HostProfile helpers + Codable, the deterministic
// capability probes, the full BackendSelector routing table (Apple/NVIDIA/
// Ascend/Cambricon/AMD/Qualcomm/Mali/CPU + tier clamping), the native-runtime
// registry lookup + JSON parse, and the in-memory native-runtime fetcher
// (fast-path, fallback, SHA verify, cache flag, no-bundle error).
// Mirrors CircleAI.Runtime/*.

import XCTest
import Foundation
import CryptoKit
@testable import CircleAI

final class RuntimeTests: XCTestCase {

    private func profile(os: OperatingSystemKind = .linux, arch: ArchitectureKind = .x64,
                         ram: Int64 = 16 * 1024 * 1024 * 1024, cpu: String = "Test CPU",
                         gpu: GpuInfo? = nil, npu: NpuInfo? = nil) -> HostProfile {
        HostProfile(os: os, osVersion: "1.0", arch: arch, cpuModel: cpu,
                    logicalCoreCount: 8, physicalCoreCount: 4, totalPhysicalMemoryBytes: ram,
                    gpu: gpu, npu: npu, probedAt: Date(timeIntervalSince1970: 100))
    }

    private let giB: Int64 = 1024 * 1024 * 1024

    // ── HostProfile ───────────────────────────────────────────────────────────

    func testHostProfileCodableRoundTrip() throws {
        let p = profile(gpu: GpuInfo(vendor: .nvidia, model: "RTX", vramBytes: 8 * giB, driverVersion: "1"),
                        npu: NpuInfo(vendor: .appleNeuralEngine, model: "ANE"))
        let decoded = try JSONDecoder().decode(HostProfile.self, from: try JSONEncoder().encode(p))
        XCTAssertEqual(decoded, p)
    }

    func testHostProfileHelpers() {
        let noGpu = profile()
        XCTAssertFalse(noGpu.hasUsableGpu())
        let bigGpu = profile(gpu: GpuInfo(vendor: .nvidia, model: "x", vramBytes: 4 * giB, driverVersion: nil))
        XCTAssertTrue(bigGpu.hasUsableGpu())
        XCTAssertFalse(bigGpu.hasUsableGpu(minimumVramBytes: 8 * giB))
        XCTAssertTrue(profile(arch: .arm64).is64Bit)
        XCTAssertFalse(profile(arch: .x86).is64Bit)
    }

    // ── Capability probes ──────────────────────────────────────────────────────

    func testStaticProbeReturnsFixedProfile() async {
        let p = profile(cpu: "Fixed")
        let got = await StaticCapabilityProbe(p).probe()
        XCTAssertEqual(got, p)
    }

    func testUnknownProbeIsAllUnknown() async {
        let got = await UnknownCapabilityProbe.shared.probe()
        XCTAssertEqual(got.os, .unknown)
        XCTAssertEqual(got.arch, .unknown)
        XCTAssertNil(got.gpu)
        XCTAssertNil(got.npu)
    }

    func testCapabilityProbeDelegates() async {
        let p = profile(cpu: "Delegated")
        let wrapper = CapabilityProbe(StaticCapabilityProbe(p))
        let probed = await wrapper.probe()
        XCTAssertEqual(probed, p)
        // Default ctor falls back to Unknown.
        let defaultProfile = await CapabilityProbe().probe()
        XCTAssertEqual(defaultProfile.os, .unknown)
    }

    // ── BackendSelector routing ────────────────────────────────────────────────

    func testAppleSiliconSelectsMetal() {
        let p = profile(os: .macOS, arch: .arm64, ram: 32 * giB,
                        gpu: GpuInfo(vendor: .apple, model: "M2", vramBytes: 0, driverVersion: nil))
        let sel = BackendSelector().select(profile: p, requestedTier: .tier4Frontier)
        XCTAssertEqual(sel.backend, .metal)
        XCTAssertEqual(sel.actualTier, .tier3Large)  // 32 GiB unified caps at Tier3
        XCTAssertTrue(sel.rationale.contains("Apple Silicon"))
    }

    func testNvidiaSelectsCudaTierByVram() {
        let p = profile(gpu: GpuInfo(vendor: .nvidia, model: "RTX 4090", vramBytes: 24 * giB, driverVersion: "5"))
        let sel = BackendSelector().select(profile: p, requestedTier: .tier4Frontier)
        XCTAssertEqual(sel.backend, .cuda)
        XCTAssertEqual(sel.actualTier, .tier4Frontier)
    }

    func testNvidiaBelow4GibFallsThroughToCpu() {
        let p = profile(gpu: GpuInfo(vendor: .nvidia, model: "GT", vramBytes: 2 * giB, driverVersion: nil))
        let sel = BackendSelector().select(profile: p, requestedTier: .tier4Frontier)
        XCTAssertEqual(sel.backend, .cpu)
    }

    func testHuaweiAscendSelectsAscend() {
        let p = profile(npu: NpuInfo(vendor: .huaweiAscend, model: "910"))
        let sel = BackendSelector().select(profile: p, requestedTier: .tier4Frontier)
        XCTAssertEqual(sel.backend, .ascend)
        XCTAssertEqual(sel.actualTier, .tier3Large)  // Ascend ceiling
    }

    func testCambriconSelectsCambricon() {
        let p = profile(npu: NpuInfo(vendor: .cambriconMlu, model: "MLU"))
        let sel = BackendSelector().select(profile: p, requestedTier: .tier4Frontier)
        XCTAssertEqual(sel.backend, .cambricon)
        XCTAssertEqual(sel.actualTier, .tier3Large)
    }

    func testAmdDiscreteSelectsVulkan() {
        let p = profile(gpu: GpuInfo(vendor: .amd, model: "RX", vramBytes: 12 * giB, driverVersion: nil))
        let sel = BackendSelector().select(profile: p, requestedTier: .tier4Frontier)
        XCTAssertEqual(sel.backend, .vulkan)
        XCTAssertEqual(sel.actualTier, .tier3Large)  // 12 GiB VRAM
    }

    func testQualcommSelectsOpenCL() {
        let p = profile(os: .android, arch: .arm64,
                        gpu: GpuInfo(vendor: .qualcomm, model: "Adreno", vramBytes: 0, driverVersion: nil))
        let sel = BackendSelector().select(profile: p, requestedTier: .tier4Frontier)
        XCTAssertEqual(sel.backend, .openCL)
        XCTAssertEqual(sel.actualTier, .tier1Small)
    }

    func testArmMaliSelectsVulkanTier1() {
        let p = profile(os: .android, arch: .arm64,
                        gpu: GpuInfo(vendor: .arm, model: "Mali-G", vramBytes: 0, driverVersion: nil))
        let sel = BackendSelector().select(profile: p, requestedTier: .tier4Frontier)
        XCTAssertEqual(sel.backend, .vulkan)
        XCTAssertEqual(sel.actualTier, .tier1Small)
    }

    func testCpuFallbackClampsToRam() {
        let p = profile(ram: 4 * giB)  // < 8 GiB → Tier0
        let sel = BackendSelector().select(profile: p, requestedTier: .tier4Frontier)
        XCTAssertEqual(sel.backend, .cpu)
        XCTAssertEqual(sel.actualTier, .tier0Tiny)
    }

    func testTierClampNeverExceedsRequested() {
        // Request Tier0 on a monster NVIDIA box → stays Tier0.
        let p = profile(gpu: GpuInfo(vendor: .nvidia, model: "H100", vramBytes: 80 * giB, driverVersion: nil))
        let sel = BackendSelector().select(profile: p, requestedTier: .tier0Tiny)
        XCTAssertEqual(sel.actualTier, .tier0Tiny)
    }

    // ── NativeRuntimeRegistry ──────────────────────────────────────────────────

    private func bundle(_ v: String, _ os: OperatingSystemKind, _ arch: ArchitectureKind,
                        _ backend: BackendKind, sha: String? = nil, fallback: URL? = nil) -> NativeRuntimeBundle {
        NativeRuntimeBundle(mnnVersion: v, os: os, arch: arch, backend: backend,
                            primaryUri: URL(string: "https://cdn/\(v)-\(os.lowerName).zip")!,
                            fallbackUri: fallback, archiveSha256Hex: sha,
                            mnnCoreLibraryName: NativeRuntimeRegistry.defaultCoreLibName(os))
    }

    func testRegistryFindPicksHighestVersion() {
        let reg = NativeRuntimeRegistry([
            bundle("3.4.0", .linux, .x64, .cpu),
            bundle("3.5.0", .linux, .x64, .cpu),
            bundle("3.5.0", .windows, .x64, .cpu),
        ])
        let found = reg.find(os: .linux, arch: .x64, backend: .cpu)
        XCTAssertEqual(found?.mnnVersion, "3.5.0")
        XCTAssertEqual(found?.os, .linux)
        XCTAssertNil(reg.find(os: .macOS, arch: .arm64, backend: .metal))
    }

    func testRegistryLoadsFromJson() throws {
        let json = """
        { "mnn_versions": [
            { "version": "3.5.0", "bundles": [
                { "os": "windows", "arch": "x64", "backend": "cpu", "url": "https://cdn/win.zip", "sha256": "AABB" },
                { "os": "macos", "arch": "arm64", "backend": "metal", "url": "https://cdn/mac.zip" },
                { "note": "this is a header, not a bundle" }
            ]}
        ]}
        """
        let reg = NativeRuntimeRegistry.load(fromJson: Data(json.utf8))
        XCTAssertEqual(reg.all.count, 2)
        let win = reg.find(os: .windows, arch: .x64, backend: .cpu)
        XCTAssertEqual(win?.archiveSha256Hex, "AABB")
        XCTAssertEqual(win?.mnnCoreLibraryName, "MNN.dll")
        let mac = reg.find(os: .macOS, arch: .arm64, backend: .metal)
        XCTAssertEqual(mac?.mnnCoreLibraryName, "MNN")  // framework binary name
    }

    // ── InMemoryNativeRuntimeFetcher ───────────────────────────────────────────

    func testFetcherEnsureFastPathAndCacheFlag() async throws {
        let b = bundle("3.5.0", .linux, .x64, .cpu)
        let store = InMemoryNativeRuntimeContentStore()
        store.add(uri: b.primaryUri, bytes: Data([1, 2, 3]))
        let fetcher = InMemoryNativeRuntimeFetcher(cacheRoot: "/cache",
                                                   registry: NativeRuntimeRegistry([b]), content: store)

        let cachedBefore = await fetcher.isRuntimeCached(os: .linux, arch: .x64, backend: .cpu)
        XCTAssertFalse(cachedBefore)
        var reported: Double = 0
        let install = try await fetcher.ensureRuntime(os: .linux, arch: .x64, backend: .cpu,
                                                      progress: { reported = $0 })
        XCTAssertEqual(install.bundle.mnnVersion, "3.5.0")
        XCTAssertEqual(install.extractedRoot, "/cache/3.5.0-linux-x64-cpu")
        XCTAssertEqual(install.mnnCorePath, "/cache/3.5.0-linux-x64-cpu/libMNN.so")
        XCTAssertEqual(reported, 1.0)
        let cachedAfter = await fetcher.isRuntimeCached(os: .linux, arch: .x64, backend: .cpu)
        XCTAssertTrue(cachedAfter)

        // Second ensure hits the fast path and returns the same install.
        let again = try await fetcher.ensureRuntime(os: .linux, arch: .x64, backend: .cpu)
        XCTAssertEqual(again, install)
    }

    func testFetcherFallsBackToFallbackUri() async throws {
        let fb = URL(string: "https://mirror/win.zip")!
        let b = bundle("3.5.0", .windows, .x64, .cpu, fallback: fb)
        let store = InMemoryNativeRuntimeContentStore()
        // Only the fallback has content.
        store.add(uri: fb, bytes: Data([9]))
        let fetcher = InMemoryNativeRuntimeFetcher(cacheRoot: "/c",
                                                   registry: NativeRuntimeRegistry([b]), content: store)
        let install = try await fetcher.ensureRuntime(os: .windows, arch: .x64, backend: .cpu)
        XCTAssertEqual(install.mnnCorePath, "/c/3.5.0-windows-x64-cpu/MNN.dll")
    }

    func testFetcherThrowsWhenNoBundle() async {
        let fetcher = InMemoryNativeRuntimeFetcher(cacheRoot: "/c",
                                                   registry: NativeRuntimeRegistry([]),
                                                   content: InMemoryNativeRuntimeContentStore())
        do {
            _ = try await fetcher.ensureRuntime(os: .linux, arch: .x64, backend: .cpu)
            XCTFail("expected throw")
        } catch let e as NativeRuntimeError {
            XCTAssertEqual(e, .noBundleRegistered(.linux, .x64, .cpu))
        } catch { XCTFail("wrong error \(error)") }
    }

    func testFetcherThrowsOnShaMismatch() async {
        // Pin a bogus SHA so verification fails on the served bytes.
        let b = bundle("3.5.0", .linux, .x64, .cpu, sha: "00")
        let store = InMemoryNativeRuntimeContentStore()
        store.add(uri: b.primaryUri, bytes: Data([1, 2, 3]))
        let fetcher = InMemoryNativeRuntimeFetcher(cacheRoot: "/c",
                                                   registry: NativeRuntimeRegistry([b]), content: store)
        do {
            _ = try await fetcher.ensureRuntime(os: .linux, arch: .x64, backend: .cpu)
            XCTFail("expected throw")
        } catch let e as NativeRuntimeError {
            XCTAssertEqual(e, .shaMismatch(.linux, .x64, .cpu, mnnVersion: "3.5.0"))
        } catch { XCTFail("wrong error \(error)") }
    }

    func testFetcherVerifiesCorrectSha() async throws {
        // Pin the real uppercase-hex SHA-256 of the payload so verification passes.
        let payload = Data([1, 2, 3, 4])
        let hex = SwiftSha256Upper.hex(payload)
        let b = bundle("3.5.0", .linux, .arm64, .cpu, sha: hex)
        let store = InMemoryNativeRuntimeContentStore()
        store.add(uri: b.primaryUri, bytes: payload)
        let fetcher = InMemoryNativeRuntimeFetcher(cacheRoot: "/c",
                                                   registry: NativeRuntimeRegistry([b]), content: store)
        let install = try await fetcher.ensureRuntime(os: .linux, arch: .arm64, backend: .cpu)
        XCTAssertEqual(install.bundle.archiveSha256Hex, hex)
    }

    func testListAvailableBundles() {
        let b = bundle("1.0", .linux, .x64, .cpu)
        let fetcher = InMemoryNativeRuntimeFetcher(cacheRoot: "/c",
                                                   registry: NativeRuntimeRegistry([b]),
                                                   content: InMemoryNativeRuntimeContentStore())
        XCTAssertEqual(fetcher.listAvailableBundles().count, 1)
    }
}

// Small helper so the test can compute the same digest the fetcher verifies.
enum SwiftSha256Upper {
    static func hex(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02X", $0) }.joined()
    }
}
