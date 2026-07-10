// ModelDownloadServiceTests.swift
//
// Exercises the full download/verify/rename/skip/manifest logic with an
// in-memory IHttpDownloader — no real network.

import XCTest
import CryptoKit
@testable import CircleAI

/// Deterministic downloader. Serves bytes by URL; the primary URL of a file can
/// be made to "fail" so the fallback path is exercised.
private final class FakeDownloader: IHttpDownloader, @unchecked Sendable {
    private let lock = NSLock()
    var byURL: [String: Data] = [:]
    var failURLs: Set<String> = []
    private(set) var requested: [String] = []

    func download(from uri: URL, to destPath: String, progress: (@Sendable (Double) -> Void)?) async throws {
        let key = uri.absoluteString
        lock.lock(); requested.append(key); lock.unlock()
        if failURLs.contains(key) { throw ModelDownloadError.httpStatus(503) }
        guard let data = byURL[key] else { throw ModelDownloadError.httpStatus(404) }
        try data.write(to: URL(fileURLWithPath: destPath), options: .atomic)
        progress?(1.0)
    }
}

final class ModelDownloadServiceTests: XCTestCase {

    private func tempDir() -> String {
        (NSTemporaryDirectory() as NSString).appendingPathComponent("circleai-dl-\(UUID().uuidString)")
    }

    private func sha256Hex(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    // MARK: - stripShaAlgorithmPrefix

    func testStripShaPrefix() {
        XCTAssertEqual(ModelDownloadService.stripShaAlgorithmPrefix("sha256:ABCD"), "ABCD")
        XCTAssertEqual(ModelDownloadService.stripShaAlgorithmPrefix("SHA-256: abcd "), "abcd")
        XCTAssertEqual(ModelDownloadService.stripShaAlgorithmPrefix("abcd"), "abcd")
        XCTAssertEqual(ModelDownloadService.stripShaAlgorithmPrefix(""), "")
        // A colon that is not an algorithm token is left intact.
        XCTAssertEqual(ModelDownloadService.stripShaAlgorithmPrefix("not-an-algorithm-name-way-too-long:xx"),
                       "not-an-algorithm-name-way-too-long:xx")
    }

    // MARK: - single-file

    func testEnsureModelDownloadsVerifiesAndCaches() async throws {
        let dir = tempDir(); defer { try? FileManager.default.removeItem(atPath: dir) }
        let payload = Data("weights".utf8)
        let uri = URL(string: "https://example.test/model.gguf")!
        let fake = FakeDownloader(); fake.byURL[uri.absoluteString] = payload
        let svc = ModelDownloadService(storageDirectory: dir, downloader: fake)

        let path = try await svc.ensureModel(modelId: "m1", downloadUri: uri, expectedSha256: "sha256:\(sha256Hex(payload))", progress: nil)
        XCTAssertTrue(path.hasSuffix("m1.gguf"))
        XCTAssertEqual(try Data(contentsOf: URL(fileURLWithPath: path)), payload)

        // Second call: cached + valid → no new download.
        let before = fake.requested.count
        _ = try await svc.ensureModel(modelId: "m1", downloadUri: uri, expectedSha256: "sha256:\(sha256Hex(payload))", progress: nil)
        XCTAssertEqual(fake.requested.count, before)
    }

    func testEnsureModelShaMismatchDeletesAndThrows() async throws {
        let dir = tempDir(); defer { try? FileManager.default.removeItem(atPath: dir) }
        let uri = URL(string: "https://example.test/model.gguf")!
        let fake = FakeDownloader(); fake.byURL[uri.absoluteString] = Data("bad".utf8)
        let svc = ModelDownloadService(storageDirectory: dir, downloader: fake)

        do {
            _ = try await svc.ensureModel(modelId: "m1", downloadUri: uri, expectedSha256: "sha256:deadbeef", progress: nil)
            XCTFail("expected mismatch")
        } catch {
            if case ModelDownloadError.shaMismatch = error {} else { XCTFail("wrong error \(error)") }
        }
        let cached = try await svc.isModelCached("m1")
        XCTAssertFalse(cached)
    }

    // MARK: - bundle

    func testEnsureBundleFetchesAllFilesAndReportsDir() async throws {
        let dir = tempDir(); defer { try? FileManager.default.removeItem(atPath: dir) }
        let repo = "MNN/Qwen3-0.6B-MNN"
        let cfg = Data("{\"k\":1}".utf8)
        let wts = Data("weights-blob".utf8)
        let files = [
            BundleFileSpec(name: "config.json", sha256: sha256Hex(cfg), sizeBytes: Int64(cfg.count)),
            BundleFileSpec(name: "model.mnn", sha256: "sha256:\(sha256Hex(wts))", sizeBytes: Int64(wts.count)),
        ]
        let fake = FakeDownloader()
        fake.byURL[ModelDownloadService.buildPrimaryUrl(repo: repo, fileName: "config.json").absoluteString] = cfg
        fake.byURL[ModelDownloadService.buildPrimaryUrl(repo: repo, fileName: "model.mnn").absoluteString] = wts
        let svc = ModelDownloadService(storageDirectory: dir, downloader: fake)

        let modelDir = try await svc.ensureBundle(modelId: "qwen", repo: repo, bundleFiles: files, progress: nil)
        XCTAssertTrue(FileManager.default.fileExists(atPath: (modelDir as NSString).appendingPathComponent("config.json")))
        XCTAssertTrue(FileManager.default.fileExists(atPath: (modelDir as NSString).appendingPathComponent("model.mnn")))
    }

    func testEnsureBundleFallsBackToCdnUrlWhenPrimaryFails() async throws {
        let dir = tempDir(); defer { try? FileManager.default.removeItem(atPath: dir) }
        let repo = "MNN/Qwen3-0.6B-MNN"
        let cfg = Data("cfg".utf8)
        let primary = ModelDownloadService.buildPrimaryUrl(repo: repo, fileName: "config.json").absoluteString
        let fallback = ModelDownloadService.buildFallbackUrl(repo: repo, fileName: "config.json").absoluteString
        let fake = FakeDownloader()
        fake.failURLs.insert(primary)               // primary 503s
        fake.byURL[fallback] = cfg                   // fallback serves the bytes
        let svc = ModelDownloadService(storageDirectory: dir, downloader: fake)

        let files = [BundleFileSpec(name: "config.json", sha256: sha256Hex(cfg), sizeBytes: Int64(cfg.count))]
        let modelDir = try await svc.ensureBundle(modelId: "qwen", repo: repo, bundleFiles: files, progress: nil)
        XCTAssertTrue(FileManager.default.fileExists(atPath: (modelDir as NSString).appendingPathComponent("config.json")))
        XCTAssertTrue(fake.requested.contains(primary))
        XCTAssertTrue(fake.requested.contains(fallback))
    }

    func testEnsureBundleSkipsAlreadyValidFiles() async throws {
        let dir = tempDir(); defer { try? FileManager.default.removeItem(atPath: dir) }
        let repo = "MNN/R"
        let cfg = Data("cfg".utf8)
        let fake = FakeDownloader()
        fake.byURL[ModelDownloadService.buildPrimaryUrl(repo: repo, fileName: "config.json").absoluteString] = cfg
        let svc = ModelDownloadService(storageDirectory: dir, downloader: fake)
        let files = [BundleFileSpec(name: "config.json", sha256: sha256Hex(cfg), sizeBytes: Int64(cfg.count))]

        _ = try await svc.ensureBundle(modelId: "r", repo: repo, bundleFiles: files, progress: nil)
        let after1 = fake.requested.count
        _ = try await svc.ensureBundle(modelId: "r", repo: repo, bundleFiles: files, progress: nil)
        XCTAssertEqual(fake.requested.count, after1, "cached+valid file must not be re-downloaded")
    }

    func testEnsureBundleValidatesArgs() async {
        let dir = tempDir(); defer { try? FileManager.default.removeItem(atPath: dir) }
        let svc = ModelDownloadService(storageDirectory: dir, downloader: FakeDownloader())
        do {
            _ = try await svc.ensureBundle(modelId: "m", repo: "", bundleFiles: [BundleFileSpec(name: "c", sha256: "x", sizeBytes: 1)], progress: nil)
            XCTFail("expected empty-repo throw")
        } catch { XCTAssertEqual(error as? ModelDownloadError, .emptyRepo) }
        do {
            _ = try await svc.ensureBundle(modelId: "m", repo: "R", bundleFiles: [], progress: nil)
            XCTFail("expected empty-list throw")
        } catch { XCTAssertEqual(error as? ModelDownloadError, .emptyBundleList) }
    }

    // MARK: - cache mgmt + manifest

    func testIsCachedAndDeleteForBothShapes() async throws {
        let dir = tempDir(); defer { try? FileManager.default.removeItem(atPath: dir) }
        let repo = "MNN/R"
        let cfg = Data("cfg".utf8)
        let fake = FakeDownloader()
        fake.byURL[ModelDownloadService.buildPrimaryUrl(repo: repo, fileName: "config.json").absoluteString] = cfg
        let svc = ModelDownloadService(storageDirectory: dir, downloader: fake)

        let cachedBefore = try await svc.isModelCached("r")
        XCTAssertFalse(cachedBefore)
        _ = try await svc.ensureBundle(modelId: "r", repo: repo, bundleFiles: [BundleFileSpec(name: "config.json", sha256: sha256Hex(cfg), sizeBytes: Int64(cfg.count))], progress: nil)
        let cachedAfter = try await svc.isModelCached("r")
        XCTAssertTrue(cachedAfter)
        try await svc.deleteModel("r")
        let cachedAfterDelete = try await svc.isModelCached("r")
        XCTAssertFalse(cachedAfterDelete)
    }

    func testWriteInstalledManifestRoundTrips() async throws {
        let dir = tempDir(); defer { try? FileManager.default.removeItem(atPath: dir) }
        let svc = ModelDownloadService(storageDirectory: dir, downloader: FakeDownloader())
        let modelDir = (dir as NSString).appendingPathComponent("r")
        try FileManager.default.createDirectory(atPath: modelDir, withIntermediateDirectories: true)
        let files = [BundleFileSpec(name: "config.json", sha256: "sha256:abc", sizeBytes: 10)]
        await svc.writeInstalledManifest(modelDir: modelDir, modelId: "r", version: "v1", repo: "MNN/R", bundleFiles: files)

        let path = (modelDir as NSString).appendingPathComponent("installed.json")
        XCTAssertTrue(FileManager.default.fileExists(atPath: path))
        let data = try Data(contentsOf: URL(fileURLWithPath: path))
        let dec = JSONDecoder(); dec.dateDecodingStrategy = .iso8601
        let manifest = try dec.decode(InstalledManifest.self, from: data)
        XCTAssertEqual(manifest.modelId, "r")
        XCTAssertEqual(manifest.version, "v1")
        XCTAssertEqual(manifest.repo, "MNN/R")
        XCTAssertEqual(manifest.totalBytes, 10)
        XCTAssertEqual(manifest.files.count, 1)
    }

    func testValidateModelIdRejectsEmpty() async {
        let dir = tempDir(); defer { try? FileManager.default.removeItem(atPath: dir) }
        let svc = ModelDownloadService(storageDirectory: dir, downloader: FakeDownloader())
        do {
            _ = try await svc.isModelCached("   ")
            XCTFail("expected empty-id throw")
        } catch { XCTAssertEqual(error as? ModelDownloadError, .emptyModelId) }
    }
}
