// ModelRuntimeTests.swift
//
// Exercises the ported CircleAI.Core model-management runtime:
//   SafeModelHandle, PlatformInterop, ModelScopeSource, ModelDownloader,
//   LocalModelLoader, LocalModelManager, SourceDownloadHelper.
// Network is injected via InMemoryByteSource; the native loader is injected via
// InMemoryModelLoaderBackend — so everything is deterministic and offline.

import XCTest
import CryptoKit
@testable import CircleAI

final class ModelRuntimeTests: XCTestCase {

    // ── temp dir helpers ──────────────────────────────────────────────────

    private func tempDir(_ name: String = UUID().uuidString) -> String {
        let base = NSTemporaryDirectory()
        let dir = (base as NSString).appendingPathComponent("cai-mrt-" + name)
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        return dir
    }

    private func sha256Hex(_ bytes: [UInt8]) -> String {
        SHA256.hash(data: Data(bytes)).map { String(format: "%02x", $0) }.joined()
    }

    // ── SafeModelHandle ───────────────────────────────────────────────────

    func testSafeModelHandleReleaseIsIdempotent() {
        var freed: [UInt] = []
        let h = SafeModelHandle(nativeHandle: 42, releaseCallback: { freed.append($0) })
        XCTAssertFalse(h.isInvalid)
        XCTAssertEqual(h.rawHandle, 42)
        XCTAssertTrue(h.release())
        XCTAssertTrue(h.isInvalid)
        // Second release is a no-op (callback not invoked again).
        XCTAssertTrue(h.release())
        XCTAssertEqual(freed, [42])
    }

    func testSafeModelHandleEmptyThenWired() {
        var freed = 0
        let h = SafeModelHandle()
        XCTAssertTrue(h.isInvalid)
        h.setHandle(7)
        h.withReleaseCallback { _ in freed += 1 }
        XCTAssertFalse(h.isInvalid)
        _ = h.release()
        XCTAssertEqual(freed, 1)
    }

    // ── PlatformInterop ───────────────────────────────────────────────────

    func testPlatformInteropLoadsExistingFile() throws {
        let dir = tempDir()
        let path = (dir as NSString).appendingPathComponent("model.gguf")
        FileManager.default.createFile(atPath: path, contents: Data([0, 1, 2, 3]))

        let backend = InMemoryModelLoaderBackend()
        let handle = try PlatformInterop.loadModel(path, backend: backend)
        XCTAssertFalse(handle.isInvalid)
        XCTAssertEqual(backend.liveCount, 1)
        _ = handle.release()
        XCTAssertEqual(backend.freeCount, 1)
        XCTAssertEqual(backend.liveCount, 0)
    }

    func testPlatformInteropGuards() {
        XCTAssertThrowsError(try PlatformInterop.loadModel("")) { err in
            XCTAssertEqual(err as? ModelRuntimeError, .argument("Model path is required."))
        }
        XCTAssertThrowsError(try PlatformInterop.loadModel("/no/such/file.gguf")) { err in
            guard case .fileNotFound = (err as? ModelRuntimeError) else {
                return XCTFail("expected fileNotFound")
            }
        }
    }

    // ── ModelScopeSource ──────────────────────────────────────────────────

    func testModelScopeSourceDownloadsFromModelScopeHost() async throws {
        let dir = tempDir()
        let out = (dir as NSString).appendingPathComponent("w.bin")
        let payload = [UInt8](repeating: 0xAB, count: 20_000) // > 2 buffers
        let url = "https://modelscope.cn/models/foo/repo/w.bin"
        let src = ModelScopeSource(byteSource: InMemoryByteSource([url: payload]))

        var lastReport: SourceDownloadProgress?
        try await src.download(url: url, localPath: out, progress: { lastReport = $0 })

        let written = FileManager.default.contents(atPath: out)
        XCTAssertEqual(written.map { Array($0) }, payload)
        XCTAssertEqual(lastReport?.bytesReceived, 20_000)
        XCTAssertEqual(lastReport?.totalBytes, 20_000)
    }

    func testModelScopeSourceRejectsNonModelScopeHost() async {
        let src = ModelScopeSource(byteSource: InMemoryByteSource())
        do {
            try await src.download(url: "https://huggingface.co/x/w.bin", localPath: "/tmp/x", progress: nil)
            XCTFail("expected rejection")
        } catch {
            guard case .argument = (error as? ModelRuntimeError) else { return XCTFail("wrong error") }
        }
    }

    func testModelScopeSourceIsAvailable() async {
        let up = ModelScopeSource(byteSource: InMemoryByteSource(["https://modelscope.cn/": [0]]))
        let down = ModelScopeSource(byteSource: InMemoryByteSource())
        let a = await up.isAvailable()
        let b = await down.isAvailable()
        XCTAssertTrue(a)
        XCTAssertFalse(b)
    }

    // ── HuggingFaceSource tombstone ───────────────────────────────────────
    // (Constructing HuggingFaceSource is a compile-time error via @available
    //  unavailable; nothing to assert at runtime — its removal is enforced by
    //  the type system, mirroring the C# [Obsolete(error:true)].)

    // ── ModelDownloader ───────────────────────────────────────────────────

    func testModelDownloaderPrimaryThenFallback() async throws {
        let dir = tempDir()
        let payload = [UInt8]("hello-model".utf8)
        let primary = "https://modelscope.cn/models/foo/primary.bin"   // NOT served → fails
        let fallback = "https://modelscope.cn/models/foo/fallback.bin" // served → wins
        let bytes = InMemoryByteSource([fallback: payload, "https://modelscope.cn/": [0]])
        let src = ModelScopeSource(byteSource: bytes)

        let entry = ModelInfoEntry(fileName: "model.bin", primaryUrl: primary, fallbackUrl: fallback)
        let dl = try ModelDownloader(sources: [src], registry: ["m1": entry])

        try await dl.downloadModel(modelId: "m1", localPath: dir)
        let written = FileManager.default.contents(
            atPath: (dir as NSString).appendingPathComponent("model.bin"))
        XCTAssertEqual(written.map { Array($0) }, payload)
    }

    func testModelDownloaderUnknownModelThrowsKeyNotFound() async {
        let src = ModelScopeSource(byteSource: InMemoryByteSource())
        let dl = try! ModelDownloader(sources: [src], registry: [:])
        do {
            try await dl.downloadModel(modelId: "nope", localPath: tempDir())
            XCTFail("expected keyNotFound")
        } catch {
            guard case .keyNotFound = (error as? ModelRuntimeError) else { return XCTFail("wrong error") }
        }
    }

    func testModelDownloaderRejectsBundleEntry() async {
        let src = ModelScopeSource(byteSource: InMemoryByteSource())
        let bundle = ModelInfoEntry(
            repo: "org/model",
            bundleFiles: [BundleFileEntry(name: "llm.mnn.weight", sha256: "ab", sizeBytes: 10)])
        let dl = try! ModelDownloader(sources: [src], registry: ["b": bundle])
        do {
            try await dl.downloadModel(modelId: "b", localPath: tempDir())
            XCTFail("expected invalidOperation")
        } catch {
            guard case .invalidOperation = (error as? ModelRuntimeError) else { return XCTFail("wrong error") }
        }
    }

    func testModelDownloaderRequiresAtLeastOneSource() {
        XCTAssertThrowsError(try ModelDownloader(sources: []))
    }

    func testModelDownloaderProgressEventFires() async throws {
        let dir = tempDir()
        let payload = [UInt8](repeating: 1, count: 12_000)
        let url = "https://modelscope.cn/models/foo/w.bin"
        let src = ModelScopeSource(byteSource: InMemoryByteSource([url: payload]))
        let dl = try ModelDownloader(
            sources: [src],
            registry: ["m": ModelInfoEntry(fileName: "w.bin", primaryUrl: url)])

        let box = Box()
        dl.onProgress { r in box.append(r.bytesReceived) }
        try await dl.downloadModel(modelId: "m", localPath: dir)
        XCTAssert(box.last() == 12_000)
    }

    // ── LocalModelLoader ──────────────────────────────────────────────────

    func testLocalModelLoaderDownloadVerifiesChecksum() async throws {
        let dir = tempDir()
        let payload = [UInt8]("weights-abc".utf8)
        let checksum = "sha256:" + sha256Hex(payload)
        let url = "https://modelscope.cn/models/foo/w.bin"
        let bytes = InMemoryByteSource([url: payload])

        let entry = ModelInfoEntry(fileName: "w.bin", primaryUrl: url, checksum: checksum)
        let loader = LocalModelLoader(modelDirectory: dir, registry: ["Qwen": entry], byteSource: bytes)

        // Case-insensitive lookup (matches C# OrdinalIgnoreCase).
        let path = try await loader.downloadModel("qwen", progress: nil)
        XCTAssertTrue(FileManager.default.fileExists(atPath: path))
        XCTAssertTrue(loader.modelExists("QWEN"))
    }

    func testLocalModelLoaderRejectsBadChecksum() async {
        let dir = tempDir()
        let payload = [UInt8]("data".utf8)
        let url = "https://modelscope.cn/models/foo/w.bin"
        let entry = ModelInfoEntry(
            fileName: "w.bin", primaryUrl: url,
            checksum: "sha256:deadbeef")  // wrong
        let loader = LocalModelLoader(
            modelDirectory: dir, registry: ["m": entry],
            byteSource: InMemoryByteSource([url: payload]))
        do {
            _ = try await loader.downloadModel("m", progress: nil)
            XCTFail("expected checksum failure")
        } catch {
            guard case .invalidData = (error as? ModelRuntimeError) else { return XCTFail("wrong error: \(error)") }
        }
        // File must have been deleted after the failed verification.
        XCTAssertFalse(FileManager.default.fileExists(atPath: (dir as NSString).appendingPathComponent("w.bin")))
    }

    func testLocalModelLoaderUnknownModelThrows() async {
        let loader = LocalModelLoader(modelDirectory: tempDir(), registry: [:])
        do {
            _ = try await loader.downloadModel("x", progress: nil)
            XCTFail("expected argument error")
        } catch {
            guard case .argument = (error as? ModelRuntimeError) else { return XCTFail("wrong error") }
        }
    }

    func testLocalModelLoaderBundleGetPathAndDownloadGuard() async {
        let dir = tempDir()
        let entry = ModelInfoEntry(
            repo: "org/m",
            bundleFiles: [BundleFileEntry(name: "llm.mnn.weight", sha256: "ab", sizeBytes: 5)])
        let loader = LocalModelLoader(modelDirectory: dir, registry: ["b": entry])
        // getModelPath returns the per-model anchor path.
        let path = try? loader.getModelPath("b")
        XCTAssertNotNil(path)
        XCTAssertTrue(path!.hasSuffix("llm.mnn.weight"))
        // download refuses bundles.
        do {
            _ = try await loader.downloadModel("b", progress: nil)
            XCTFail("expected invalidOperation")
        } catch {
            guard case .invalidOperation = (error as? ModelRuntimeError) else { return XCTFail("wrong error") }
        }
    }

    func testLocalModelLoaderCriticalUpdateProbe() async {
        let versionsUrl = "https://raw.githubusercontent.com/BhenguAI/models/main/versions.txt"
        let yes = LocalModelLoader(
            modelDirectory: tempDir(), registry: [:],
            byteSource: InMemoryByteSource([versionsUrl: [UInt8]("v1 [CRITICAL] patch".utf8)]))
        let no = LocalModelLoader(
            modelDirectory: tempDir(), registry: [:],
            byteSource: InMemoryByteSource([versionsUrl: [UInt8]("v1 ok".utf8)]))
        let a = await yes.checkForCriticalUpdate()
        let b = await no.checkForCriticalUpdate()
        XCTAssertTrue(a)
        XCTAssertFalse(b)
    }

    func testLocalModelLoaderDisposedThrows() async {
        let loader = LocalModelLoader(modelDirectory: tempDir(), registry: [:])
        loader.dispose()
        do {
            _ = try await loader.downloadModel("x", progress: nil)
            XCTFail("expected objectDisposed")
        } catch {
            guard case .objectDisposed = (error as? ModelRuntimeError) else { return XCTFail("wrong error") }
        }
    }

    // ── LocalModelManager ─────────────────────────────────────────────────

    func testLocalModelManagerDownloadsWhenAbsentAndVerifies() async throws {
        let root = tempDir()
        let modelsDir = (root as NSString).appendingPathComponent("Models")
        let anchorBytes = [UInt8]("pytorch-weights".utf8)

        // A downloader stub that writes the anchor file when asked.
        let dl = WritingDownloader(anchorName: "pytorch_model.bin", contents: anchorBytes)
        let mgr = LocalModelManager(downloader: dl, modelsDirectory: modelsDir)

        let expected = Array(SHA256.hash(data: Data(anchorBytes)))
        let path = try await mgr.getModelPath(modelId: "org/model", expectedChecksum: expected)
        // Sanitised directory name: "org/model" → "org_model".
        XCTAssertTrue(path.hasSuffix("org_model"))
        XCTAssertEqual(dl.calls, 1)

        // verifyModel against the directory resolves the anchor and matches.
        let ok = try await mgr.verifyModel(modelPath: path, expectedChecksum: expected)
        XCTAssertTrue(ok)
        // Wrong checksum fails.
        let bad = try await mgr.verifyModel(modelPath: path, expectedChecksum: [0, 1, 2])
        XCTAssertFalse(bad)
    }

    func testLocalModelManagerChecksumMismatchThrows() async {
        let modelsDir = (tempDir() as NSString).appendingPathComponent("Models")
        let dl = WritingDownloader(anchorName: "pytorch_model.bin", contents: [UInt8]("abc".utf8))
        let mgr = LocalModelManager(downloader: dl, modelsDirectory: modelsDir)
        do {
            _ = try await mgr.getModelPath(modelId: "m", expectedChecksum: [9, 9, 9])
            XCTFail("expected invalidData")
        } catch {
            guard case .invalidData = (error as? ModelRuntimeError) else { return XCTFail("wrong error") }
        }
    }

    func testLocalModelManagerNoDownloaderAndMissingThrows() async {
        let modelsDir = (tempDir() as NSString).appendingPathComponent("Models")
        let mgr = LocalModelManager(downloader: nil, modelsDirectory: modelsDir)
        do {
            _ = try await mgr.getModelPath(modelId: "m")
            XCTFail("expected invalidOperation")
        } catch {
            guard case .invalidOperation = (error as? ModelRuntimeError) else { return XCTFail("wrong error") }
        }
    }

    func testSanitizeReplacesSlashes() {
        XCTAssertEqual(LocalModelManager.sanitize("a/b\\c"), "a_b_c")
    }

    // ── SourceDownloadHelper resume ───────────────────────────────────────

    func testSourceDownloadHelperResumesFromPartial() throws {
        let dir = tempDir()
        let out = (dir as NSString).appendingPathComponent("resume.bin")
        let full = [UInt8](0..<200).map { UInt8($0) } + [UInt8](0..<56).map { UInt8($0) } // 256 bytes
        let url = "https://modelscope.cn/w"
        let src = InMemoryByteSource([url: full])

        // Pre-write the first 100 bytes as a partial.
        FileManager.default.createFile(atPath: out, contents: Data(Array(full[0..<100])))

        try SourceDownloadHelper.download(source: src, url: url, localPath: out, progress: nil)
        let written = FileManager.default.contents(atPath: out).map { Array($0) }
        XCTAssertEqual(written, full)  // resumed to the full object without corruption
    }
}

// MARK: - test doubles

/// Collects Int64 progress values across sendable closure boundaries.
private final class Box: @unchecked Sendable {
    private let lock = NSLock()
    private var values: [Int64] = []
    func append(_ v: Int64) { lock.lock(); values.append(v); lock.unlock() }
    func last() -> Int64? { lock.lock(); defer { lock.unlock() }; return values.last }
}

/// A downloader that writes a single anchor file into the requested directory.
private final class WritingDownloader: IModelDownloader, @unchecked Sendable {
    private let anchorName: String
    private let contents: [UInt8]
    private let lock = NSLock()
    private var callCount = 0
    var calls: Int { lock.lock(); defer { lock.unlock() }; return callCount }

    init(anchorName: String, contents: [UInt8]) {
        self.anchorName = anchorName
        self.contents = contents
    }

    func downloadModel(modelId: String, localPath: String) async throws {
        lock.lock(); callCount += 1; lock.unlock()
        try FileManager.default.createDirectory(atPath: localPath, withIntermediateDirectories: true)
        let anchor = (localPath as NSString).appendingPathComponent(anchorName)
        FileManager.default.createFile(atPath: anchor, contents: Data(contents))
    }

    func downloadFromCandidates(
        candidateUrls: [String],
        localFilePath: String,
        progress: (@Sendable (SourceDownloadProgress) -> Void)?
    ) async throws -> String {
        "test"
    }
}
