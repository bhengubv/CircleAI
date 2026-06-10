// UpgradeTests.swift
//
// Parity test — 7 upgrade-detection cases + correlation ID auto-synth.

import Foundation
import XCTest
@testable import CircleAI

private func makeRegistry(_ entries: ModelEntry...) -> ModelRegistryService {
    let svc = ModelRegistryService()
    svc.setRegistry(ModelRegistry(registryUrl: "https://stub", lastUpdated: Date(), models: entries))
    return svc
}

private func makeEntry(_ name: String, _ version: String, _ files: BundleFile...) -> ModelEntry {
    return ModelEntry(
        name: name, version: version, quantization: "Q4",
        repo: "MNN/\(name)", totalBytes: files.reduce(0) { $0 + $1.sizeBytes },
        bundleFiles: files
    )
}

private func tempDir() throws -> String {
    let d = FileManager.default.temporaryDirectory
        .appendingPathComponent("circleai-swift-up-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: d, withIntermediateDirectories: true)
    return d.path
}

final class UpgradeTests: XCTestCase {
    func testCase1_NotInstalled_Empty() throws {
        let d = try tempDir(); defer { _ = try? FileManager.default.removeItem(atPath: d) }
        let svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.0.0",
            BundleFile(name: "config.json", sha256: "abc", sizeBytes: 100),
            BundleFile(name: "llm.mnn", sha256: "def", sizeBytes: 200)))
        XCTAssertEqual(svc.checkForUpgrades(storageDirectory: d).count, 0)
    }

    func testCase2_NoManifest_Unknown() throws {
        let d = try tempDir(); defer { _ = try? FileManager.default.removeItem(atPath: d) }
        let mDir = (d as NSString).appendingPathComponent("Qwen3-0.6B-MNN")
        try FileManager.default.createDirectory(atPath: mDir, withIntermediateDirectories: true)
        try "stub".write(toFile: (mDir as NSString).appendingPathComponent("config.json"), atomically: true, encoding: .utf8)
        let svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.0.0",
            BundleFile(name: "config.json", sha256: "abc", sizeBytes: 100)))
        let ups = svc.checkForUpgrades(storageDirectory: d)
        XCTAssertEqual(ups.count, 1)
        XCTAssertEqual(ups[0].reason, UpgradeReason.unknown)
        XCTAssertNil(ups[0].installedVersion)
    }

    func testCase3_AllShasMatch_Empty() throws {
        let d = try tempDir(); defer { _ = try? FileManager.default.removeItem(atPath: d) }
        writeInstalledManifest(
            modelDir: (d as NSString).appendingPathComponent("Qwen3-0.6B-MNN"),
            modelId: "Qwen3-0.6B-MNN", version: "1.0.0", repo: "MNN/Qwen3-0.6B-MNN",
            bundleFiles: [
                BundleFile(name: "config.json", sha256: "abc", sizeBytes: 100),
                BundleFile(name: "llm.mnn", sha256: "def", sizeBytes: 200),
            ])
        let svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.0.0",
            BundleFile(name: "config.json", sha256: "abc", sizeBytes: 100),
            BundleFile(name: "llm.mnn", sha256: "def", sizeBytes: 200)))
        XCTAssertEqual(svc.checkForUpgrades(storageDirectory: d).count, 0)
    }

    func testCase4_VersionDrift_VersionChanged_ZeroBytes() throws {
        let d = try tempDir(); defer { _ = try? FileManager.default.removeItem(atPath: d) }
        writeInstalledManifest(
            modelDir: (d as NSString).appendingPathComponent("Qwen3-0.6B-MNN"),
            modelId: "Qwen3-0.6B-MNN", version: "1.0.0", repo: "MNN/Qwen3-0.6B-MNN",
            bundleFiles: [
                BundleFile(name: "config.json", sha256: "abc", sizeBytes: 100),
                BundleFile(name: "llm.mnn", sha256: "def", sizeBytes: 200),
            ])
        let svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.1.0",
            BundleFile(name: "config.json", sha256: "abc", sizeBytes: 100),
            BundleFile(name: "llm.mnn", sha256: "def", sizeBytes: 200)))
        let ups = svc.checkForUpgrades(storageDirectory: d)
        XCTAssertEqual(ups.count, 1)
        XCTAssertEqual(ups[0].reason, UpgradeReason.versionChanged)
        XCTAssertEqual(ups[0].estimatedDownloadBytes, 0)
    }

    func testCase5_ShaDrift_ShaChanged_OnlyDriftedBytes() throws {
        let d = try tempDir(); defer { _ = try? FileManager.default.removeItem(atPath: d) }
        writeInstalledManifest(
            modelDir: (d as NSString).appendingPathComponent("Qwen3-0.6B-MNN"),
            modelId: "Qwen3-0.6B-MNN", version: "1.0.0", repo: "MNN/Qwen3-0.6B-MNN",
            bundleFiles: [
                BundleFile(name: "config.json", sha256: "abc", sizeBytes: 100),
                BundleFile(name: "llm.mnn", sha256: "OLD", sizeBytes: 200),
            ])
        let svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.0.0",
            BundleFile(name: "config.json", sha256: "abc", sizeBytes: 100),
            BundleFile(name: "llm.mnn", sha256: "NEW", sizeBytes: 200)))
        let ups = svc.checkForUpgrades(storageDirectory: d)
        XCTAssertEqual(ups.count, 1)
        XCTAssertEqual(ups[0].reason, UpgradeReason.shaChanged)
        XCTAssertEqual(ups[0].estimatedDownloadBytes, 200)
    }

    func testCase6_VersionAndSha_Both_TotalBytes() throws {
        let d = try tempDir(); defer { _ = try? FileManager.default.removeItem(atPath: d) }
        writeInstalledManifest(
            modelDir: (d as NSString).appendingPathComponent("Qwen3-0.6B-MNN"),
            modelId: "Qwen3-0.6B-MNN", version: "1.0.0", repo: "MNN/Qwen3-0.6B-MNN",
            bundleFiles: [
                BundleFile(name: "config.json", sha256: "abc", sizeBytes: 100),
                BundleFile(name: "llm.mnn", sha256: "OLD", sizeBytes: 200),
            ])
        let svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "2.0.0",
            BundleFile(name: "config.json", sha256: "abc2", sizeBytes: 100),
            BundleFile(name: "llm.mnn", sha256: "NEW", sizeBytes: 200)))
        let ups = svc.checkForUpgrades(storageDirectory: d)
        XCTAssertEqual(ups.count, 1)
        XCTAssertEqual(ups[0].reason, UpgradeReason.both)
        XCTAssertEqual(ups[0].estimatedDownloadBytes, 300)
    }

    func testCase7_WriteInstalledManifestRoundTrip_Empty() throws {
        let d = try tempDir(); defer { _ = try? FileManager.default.removeItem(atPath: d) }
        writeInstalledManifest(
            modelDir: (d as NSString).appendingPathComponent("Qwen3-0.6B-MNN"),
            modelId: "Qwen3-0.6B-MNN", version: "1.0.0", repo: "MNN/Qwen3-0.6B-MNN",
            bundleFiles: [
                BundleFile(name: "config.json", sha256: "abc", sizeBytes: 100),
                BundleFile(name: "llm.mnn", sha256: "def", sizeBytes: 200),
            ])
        let svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.0.0",
            BundleFile(name: "config.json", sha256: "abc", sizeBytes: 100),
            BundleFile(name: "llm.mnn", sha256: "def", sizeBytes: 200)))
        XCTAssertEqual(svc.checkForUpgrades(storageDirectory: d).count, 0)
    }

    func testAgentMessageCorrelationIdAutosynth() {
        let m1 = AgentMessage.create(
            kind: .greet, fromUhid: "a", toUhid: "b", contentType: "text/plain",
            payload: Data([1, 2, 3]), signature: Data([4, 5, 6]))
        XCTAssertEqual(m1.correlationId.count, 32)

        let m2 = AgentMessage.create(
            kind: .greet, fromUhid: "a", toUhid: "b", contentType: "text/plain",
            payload: Data([1, 2, 3]), signature: Data([4, 5, 6]),
            correlationId: "trace-abc")
        XCTAssertEqual(m2.correlationId, "trace-abc")

        let m3 = AgentMessage.create(
            kind: .greet, fromUhid: "a", toUhid: "b", contentType: "text/plain",
            payload: Data([1, 2, 3]), signature: Data([4, 5, 6]))
        XCTAssertNotEqual(m1.correlationId, m3.correlationId)
    }
}
