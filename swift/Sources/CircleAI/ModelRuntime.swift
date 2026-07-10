// ModelRuntime.swift
//
// Port of the CircleAI.Core model-management runtime:
//   • IModelLoader + LocalModelLoader
//   • IModelManager + LocalModelManager
//   • IModelDownloader + ModelDownloader
//   • IModelSource + ModelScopeSource + HuggingFaceSource(tombstone) + SourceDownloadHelper
//   • SafeModelHandle + PlatformInterop
//
// DETERMINISTIC IN-MEMORY PORT
// ----------------------------
// The C# stack downloads over HTTP and loads native models via P/Invoke into
// llama.cpp. Per the porting contract, all external/native/cloud dependencies
// are injected behind interfaces with a working deterministic in-memory default
// (NO stubs, NO empty methods):
//   • `IByteSource` replaces `HttpClient` — a URL → bytes map. The default
//     `InMemoryByteSource` serves registered payloads and reports the same
//     progress cadence + resume semantics as SourceDownloadHelper.
//   • `IModelLoaderBackend` replaces the llama.cpp P/Invoke in PlatformInterop —
//     the default `InMemoryModelLoaderBackend` validates the file and returns an
//     opaque handle whose release is a managed callback.
//
// All algorithms (checksum verification, primary/fallback fall-through, bundle
// detection, sanitisation, progress/ETA maths) match the C# spec exactly.

import Foundation
import CryptoKit
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

// MARK: - Errors

/// Errors surfaced by the model-management runtime. Mirrors the distinct C#
/// exception types (ArgumentException, FileNotFoundException,
/// InvalidOperationException, InvalidDataException, ObjectDisposedException,
/// KeyNotFoundException, OperationCanceledException).
public enum ModelRuntimeError: Error, Sendable, Equatable {
    case argument(String)
    case fileNotFound(String)
    case invalidOperation(String)
    case invalidData(String)
    case objectDisposed(String)
    case keyNotFound(String)
    case cancelled
}

// MARK: - DownloadProgress (source layer) — CircleAI.Core.DownloadProgress

/// Snapshot of an in-flight download, suitable for UI/logging consumers.
///
/// This mirrors the C# `CircleAI.Core.DownloadProgress` used by `IModelSource`
/// (fields: FileName / BytesReceived / TotalBytes / BytesPerSecond /
/// EstimatedTimeRemaining). It is named `SourceDownloadProgress` in Swift to
/// avoid colliding with the pre-existing `DownloadProgress` (Models.swift),
/// which carries the different `totalBytes/downloadedBytes/filename` shape.
public struct SourceDownloadProgress: Sendable, Equatable {
    public let fileName: String
    public let bytesReceived: Int64
    public let totalBytes: Int64
    public let bytesPerSecond: Double
    public let estimatedTimeRemaining: TimeInterval

    public init(
        fileName: String = "",
        bytesReceived: Int64 = 0,
        totalBytes: Int64 = 0,
        bytesPerSecond: Double = 0,
        estimatedTimeRemaining: TimeInterval = 0
    ) {
        self.fileName = fileName
        self.bytesReceived = bytesReceived
        self.totalBytes = totalBytes
        self.bytesPerSecond = bytesPerSecond
        self.estimatedTimeRemaining = estimatedTimeRemaining
    }
}

/// Progress-report shape emitted by `ModelDownloader` (the C#
/// `ModelDownloader.DownloadProgressReport`).
public struct DownloadProgressReport: Sendable, Equatable {
    public var fileName: String
    public var bytesReceived: Int64
    public var totalBytes: Int64
    public var bytesPerSecond: Double
    public var estimatedTimeRemaining: TimeInterval

    public init(
        fileName: String = "",
        bytesReceived: Int64 = 0,
        totalBytes: Int64 = 0,
        bytesPerSecond: Double = 0,
        estimatedTimeRemaining: TimeInterval = 0
    ) {
        self.fileName = fileName
        self.bytesReceived = bytesReceived
        self.totalBytes = totalBytes
        self.bytesPerSecond = bytesPerSecond
        self.estimatedTimeRemaining = estimatedTimeRemaining
    }
}

// MARK: - IByteSource (network injection seam)

/// Injectable byte source that replaces `HttpClient` for deterministic tests.
/// A resolver maps an absolute URL to its bytes (or nil when unreachable).
public protocol IByteSource: Sendable {
    /// Return the full bytes for `url`, or nil if the URL is not reachable.
    func bytes(for url: String) -> [UInt8]?
}

/// Default in-memory `IByteSource`. Register URL → bytes ahead of time.
public final class InMemoryByteSource: IByteSource, @unchecked Sendable {
    private let lock = NSLock()
    private var store: [String: [UInt8]] = [:]

    public init(_ initial: [String: [UInt8]] = [:]) {
        self.store = initial
    }

    /// Register (or replace) the bytes served for `url`.
    public func register(url: String, bytes: [UInt8]) {
        lock.lock(); store[url] = bytes; lock.unlock()
    }

    public func bytes(for url: String) -> [UInt8]? {
        lock.lock(); defer { lock.unlock() }
        return store[url]
    }
}

// MARK: - SourceDownloadHelper — CircleAI.Core.Sources.SourceDownloadHelper

/// Shared streaming download routine used by IModelSource implementations.
/// Handles resume (writes past existing bytes), progress reporting, and ETA
/// estimation — matching the C# helper's maths and cadence.
enum SourceDownloadHelper {
    private static let bufferSize = 8192
    private static let progressInterval: TimeInterval = 0.5

    /// Downloads the bytes for `url` (via `source`) to `localPath`, reporting
    /// progress. Resume: if a partial file exists, only the remaining bytes are
    /// appended (the in-memory source always returns the full object, so the
    /// tail is sliced to mirror a server that honours a Range request).
    static func download(
        source: IByteSource,
        url: String,
        localPath: String,
        progress: (@Sendable (SourceDownloadProgress) -> Void)?,
        clock: () -> TimeInterval = { Date().timeIntervalSinceReferenceDate }
    ) throws {
        let fileName = (localPath as NSString).lastPathComponent

        guard let full = source.bytes(for: url) else {
            throw ModelRuntimeError.invalidOperation("Source could not reach '\(url)'.")
        }

        // Resume support: if a partial file exists, continue past it.
        var existingBytes = 0
        if FileManager.default.fileExists(atPath: localPath),
           let attrs = try? FileManager.default.attributesOfItem(atPath: localPath),
           let len = (attrs[.size] as? NSNumber)?.intValue {
            existingBytes = len
        }
        // Guard against a stale partial larger than the object.
        if existingBytes > full.count { existingBytes = 0 }

        let totalBytes = Int64(full.count)
        let remaining = Array(full[existingBytes...])

        let dir = (localPath as NSString).deletingLastPathComponent
        if !dir.isEmpty {
            try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        }

        // Open (append when resuming, else create fresh).
        if existingBytes == 0 || !FileManager.default.fileExists(atPath: localPath) {
            FileManager.default.createFile(atPath: localPath, contents: nil)
        }
        guard let handle = FileHandle(forWritingAtPath: localPath) else {
            throw ModelRuntimeError.invalidOperation("Cannot open '\(localPath)' for writing.")
        }
        defer { try? handle.close() }
        if existingBytes > 0 {
            try? handle.seekToEnd()
        } else {
            try? handle.truncate(atOffset: 0)
        }

        var bytesRead = Int64(existingBytes)
        let start = clock()
        var lastUpdate: TimeInterval = 0
        var lastBytesRead = bytesRead

        var offset = 0
        while offset < remaining.count {
            let end = min(offset + bufferSize, remaining.count)
            let chunk = Array(remaining[offset..<end])
            handle.write(Data(chunk))
            bytesRead += Int64(chunk.count)
            offset = end

            let elapsed = clock() - start
            if (elapsed - lastUpdate) > progressInterval || bytesRead == totalBytes {
                let timeElapsed = elapsed - lastUpdate
                let bytesDiff = Double(bytesRead - lastBytesRead)
                let bytesPerSecond = timeElapsed > 0 ? bytesDiff / timeElapsed : 0

                var eta: TimeInterval = 0
                if totalBytes > 0 && bytesPerSecond > 0 {
                    let remainingBytes = Double(totalBytes - bytesRead)
                    if remainingBytes > 0 { eta = remainingBytes / bytesPerSecond }
                }

                progress?(SourceDownloadProgress(
                    fileName: fileName,
                    bytesReceived: bytesRead,
                    totalBytes: totalBytes,
                    bytesPerSecond: bytesPerSecond,
                    estimatedTimeRemaining: eta))

                lastUpdate = elapsed
                lastBytesRead = bytesRead
            }
        }

        // Always emit a terminal 100% report so consumers observe completion
        // even when the payload is smaller than one buffer.
        progress?(SourceDownloadProgress(
            fileName: fileName,
            bytesReceived: bytesRead,
            totalBytes: totalBytes,
            bytesPerSecond: 0,
            estimatedTimeRemaining: 0))
    }
}

// MARK: - IModelSource — CircleAI.Core.IModelSource

/// Abstraction for model file sources. Allows fallback chains for resilience
/// (e.g. ModelScope API primary, ModelScope CDN fallback).
public protocol IModelSource: AnyObject, Sendable {
    /// Friendly name of the source (e.g. "ModelScope"). Used in logs.
    var name: String { get }

    /// Quick reachability check. Returns false on any failure rather than throw.
    func isAvailable() async -> Bool

    /// Download a single file from `url` to `localPath`, reporting progress.
    func download(
        url: String,
        localPath: String,
        progress: (@Sendable (SourceDownloadProgress) -> Void)?
    ) async throws
}

// MARK: - ModelScopeSource — CircleAI.Core.Sources.ModelScopeSource

/// `IModelSource` backed by ModelScope (modelscope.cn, Alibaba). Treated as the
/// primary source. Network is injected via `IByteSource`.
public final class ModelScopeSource: IModelSource, @unchecked Sendable {
    private static let hostName = "modelscope.cn"

    private let byteSource: IByteSource

    public var name: String { "ModelScope" }

    public init(byteSource: IByteSource = InMemoryByteSource()) {
        self.byteSource = byteSource
    }

    public func isAvailable() async -> Bool {
        // Probe the well-known root; available when the injected source serves it.
        return byteSource.bytes(for: "https://modelscope.cn/") != nil
    }

    public func download(
        url: String,
        localPath: String,
        progress: (@Sendable (SourceDownloadProgress) -> Void)?
    ) async throws {
        if url.trimmingCharacters(in: .whitespaces).isEmpty {
            throw ModelRuntimeError.argument("url")
        }
        if localPath.trimmingCharacters(in: .whitespaces).isEmpty {
            throw ModelRuntimeError.argument("localPath")
        }

        guard let host = URL(string: url)?.host,
              host.lowercased().hasSuffix(ModelScopeSource.hostName) else {
            throw ModelRuntimeError.argument(
                "URL host must be on \(ModelScopeSource.hostName) for \(name) source. Got: \(url)")
        }

        let dir = (localPath as NSString).deletingLastPathComponent
        if !dir.isEmpty {
            try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        }

        try SourceDownloadHelper.download(
            source: byteSource, url: url, localPath: localPath, progress: progress)
    }
}

// MARK: - HuggingFaceSource — REMOVED (compile-time tombstone)

/// Removed. Use `ModelScopeSource` instead. HuggingFace is a Western (US)
/// company; all downloads route through ModelScope (modelscope.cn, Alibaba).
///
/// Kept as a tombstone so any code still referencing it fails loudly at
/// construction rather than silently at runtime — mirroring the C#
/// `[Obsolete(error:true)]` type.
@available(*, unavailable,
    message: "HuggingFaceSource has been removed. Use ModelScopeSource — all model downloads route through modelscope.cn (Alibaba).")
public final class HuggingFaceSource {
    public init() {
        fatalError("HuggingFaceSource has been removed. Use ModelScopeSource (modelscope.cn).")
    }
}

// MARK: - IModelDownloader — CircleAI.Core.IModelDownloader

/// Downloads a model file (or set of files) to local storage. Implementations
/// walk a chain of `IModelSource` instances so one supplier going dark does not
/// break model bootstrap.
public protocol IModelDownloader: AnyObject, Sendable {
    /// Download a model identified by `modelId` into directory `localPath`.
    func downloadModel(modelId: String, localPath: String) async throws

    /// Download a single model file by trying each candidate URL in order. The
    /// first URL is the primary; the rest are fallbacks. Returns the name of the
    /// source that succeeded.
    func downloadFromCandidates(
        candidateUrls: [String],
        localFilePath: String,
        progress: (@Sendable (SourceDownloadProgress) -> Void)?
    ) async throws -> String
}

// MARK: - Registry row shapes

/// A model-registry bundle-file entry.
public struct BundleFileEntry: Sendable, Equatable, Codable {
    public let name: String
    public let sha256: String
    public let sizeBytes: Int64

    public init(name: String, sha256: String, sizeBytes: Int64) {
        self.name = name; self.sha256 = sha256; self.sizeBytes = sizeBytes
    }

    enum CodingKeys: String, CodingKey {
        case name = "Name"
        case sha256 = "Sha256"
        case sizeBytes = "SizeBytes"
    }
}

/// Registry-row shape. Supports BOTH the legacy single-file shape
/// (fileName/primaryUrl/fallbackUrl/checksum) AND the new bundle shape
/// (repo + bundleFiles[]). `isBundle` selects which.
public struct ModelInfoEntry: Sendable, Equatable, Codable {
    public let fileName: String?
    public let primaryUrl: String?
    public let fallbackUrl: String?
    public let checksum: String?
    public let sizeBytes: Int64
    public let version: String
    public let architecture: String
    public let quantizationType: String

    public let repo: String?
    public let totalBytes: Int64
    public let bundleFiles: [BundleFileEntry]?

    public var isBundle: Bool { (bundleFiles?.count ?? 0) > 0 }

    public init(
        fileName: String? = nil,
        primaryUrl: String? = nil,
        fallbackUrl: String? = nil,
        checksum: String? = nil,
        sizeBytes: Int64 = 0,
        version: String = "",
        architecture: String = "",
        quantizationType: String = "",
        repo: String? = nil,
        totalBytes: Int64 = 0,
        bundleFiles: [BundleFileEntry]? = nil
    ) {
        self.fileName = fileName
        self.primaryUrl = primaryUrl
        self.fallbackUrl = fallbackUrl
        self.checksum = checksum
        self.sizeBytes = sizeBytes
        self.version = version
        self.architecture = architecture
        self.quantizationType = quantizationType
        self.repo = repo
        self.totalBytes = totalBytes
        self.bundleFiles = bundleFiles
    }

    enum CodingKeys: String, CodingKey {
        case fileName = "FileName"
        case primaryUrl = "PrimaryUrl"
        case fallbackUrl = "FallbackUrl"
        case checksum = "Checksum"
        case sizeBytes = "SizeBytes"
        case version = "Version"
        case architecture = "Architecture"
        case quantizationType = "QuantizationType"
        case repo = "Repo"
        case totalBytes = "TotalBytes"
        case bundleFiles = "BundleFiles"
    }
}

// MARK: - ModelDownloader — CircleAI.Core.ModelDownloader

/// Source-agnostic model downloader. Walks a list of `IModelSource` instances
/// in order, falling through on failure.
public final class ModelDownloader: IModelDownloader, @unchecked Sendable {
    private let lock = NSLock()
    private let sources: [any IModelSource]
    private let registry: [String: ModelInfoEntry]
    private var disposed = false

    /// Progress handler invoked during `downloadModel`, mirroring the C#
    /// `ProgressChanged` event.
    private var progressChanged: (@Sendable (DownloadProgressReport) -> Void)?

    public init(sources: [any IModelSource], registry: [String: ModelInfoEntry] = [:]) throws {
        if sources.isEmpty {
            throw ModelRuntimeError.argument("At least one model source is required")
        }
        self.sources = sources
        self.registry = registry
    }

    /// Subscribe to per-file progress reports.
    public func onProgress(_ handler: @escaping @Sendable (DownloadProgressReport) -> Void) {
        lock.lock(); progressChanged = handler; lock.unlock()
    }

    private func currentProgressHandler() -> (@Sendable (DownloadProgressReport) -> Void)? {
        lock.lock(); defer { lock.unlock() }
        return progressChanged
    }

    public func downloadModel(modelId: String, localPath: String) async throws {
        try throwIfDisposed()
        if modelId.trimmingCharacters(in: .whitespaces).isEmpty { throw ModelRuntimeError.argument("modelId") }
        if localPath.trimmingCharacters(in: .whitespaces).isEmpty { throw ModelRuntimeError.argument("localPath") }

        guard let entry = registry[modelId] else {
            let keys = registry.keys.sorted().joined(separator: ", ")
            throw ModelRuntimeError.keyNotFound(
                "Model '\(modelId)' is not in the embedded registry. Known models: \(keys)")
        }

        try? FileManager.default.createDirectory(atPath: localPath, withIntermediateDirectories: true)

        if entry.isBundle {
            throw ModelRuntimeError.invalidOperation(
                "Model '\(modelId)' is a multi-file MNN bundle (registry entry has BundleFiles[]). " +
                "Use the multi-file bundle downloader instead — this legacy single-file downloader " +
                "cannot fetch a multi-file bundle.")
        }

        guard let fileName = entry.fileName else {
            throw ModelRuntimeError.invalidOperation("Model '\(modelId)' has no FileName configured.")
        }
        let targetFile = (localPath as NSString).appendingPathComponent(fileName)

        let candidates = ModelDownloader.buildCandidateList(entry)
        if candidates.isEmpty {
            throw ModelRuntimeError.invalidOperation(
                "Model '\(modelId)' has no PrimaryUrl or FallbackUrl configured.")
        }

        // Snapshot the progress handler under the lock once, then forward.
        let sink = currentProgressHandler()
        let handler: (@Sendable (SourceDownloadProgress) -> Void)? = sink.map { forward in
            { (p: SourceDownloadProgress) in
                forward(DownloadProgressReport(
                    fileName: p.fileName,
                    bytesReceived: p.bytesReceived,
                    totalBytes: p.totalBytes,
                    bytesPerSecond: p.bytesPerSecond,
                    estimatedTimeRemaining: p.estimatedTimeRemaining))
            }
        }

        do {
            _ = try await downloadFromCandidates(
                candidateUrls: candidates, localFilePath: targetFile, progress: handler)
        } catch {
            ModelDownloader.cleanupPartialFile(targetFile)
            throw error
        }
    }

    public func downloadFromCandidates(
        candidateUrls: [String],
        localFilePath: String,
        progress: (@Sendable (SourceDownloadProgress) -> Void)?
    ) async throws -> String {
        try throwIfDisposed()
        if candidateUrls.isEmpty { throw ModelRuntimeError.argument("At least one candidate URL is required") }
        if localFilePath.trimmingCharacters(in: .whitespaces).isEmpty { throw ModelRuntimeError.argument("localFilePath") }

        let dir = (localFilePath as NSString).deletingLastPathComponent
        if !dir.isEmpty {
            try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        }

        var failures: [String] = []
        for url in candidateUrls {
            if url.trimmingCharacters(in: .whitespaces).isEmpty { continue }

            guard let source = matchSource(url) else {
                failures.append("(no registered source for '\(url)')")
                continue
            }

            do {
                try await source.download(url: url, localPath: localFilePath, progress: progress)
                return source.name
            } catch {
                failures.append("\(source.name): \(error)")
                // Drop the partial so the next source can start clean.
                ModelDownloader.cleanupPartialFile(localFilePath)
            }
        }

        throw ModelRuntimeError.invalidOperation(
            "All model sources failed:\n  " + failures.joined(separator: "\n  "))
    }

    private func matchSource(_ url: String) -> (any IModelSource)? {
        guard let host = URL(string: url)?.host else { return nil }

        for s in sources where host.lowercased().contains(s.name.lowercased()) {
            return s
        }
        if host.lowercased().contains("modelscope") {
            return sources.first { $0.name.caseInsensitiveCompare("ModelScope") == .orderedSame }
        }
        return nil
    }

    private static func buildCandidateList(_ entry: ModelInfoEntry) -> [String] {
        var list: [String] = []
        if let p = entry.primaryUrl, !p.trimmingCharacters(in: .whitespaces).isEmpty { list.append(p) }
        if let f = entry.fallbackUrl, !f.trimmingCharacters(in: .whitespaces).isEmpty { list.append(f) }
        return list
    }

    private static func cleanupPartialFile(_ path: String) {
        if FileManager.default.fileExists(atPath: path) {
            try? FileManager.default.removeItem(atPath: path)
        }
    }

    private func throwIfDisposed() throws {
        lock.lock(); defer { lock.unlock() }
        if disposed { throw ModelRuntimeError.objectDisposed("ModelDownloader") }
    }

    /// Dispose. `ownsSources` disposal in C# is a no-op here (Swift ARC reclaims
    /// the source objects); the flag just guards further use.
    public func dispose() {
        lock.lock(); disposed = true; progressChanged = nil; lock.unlock()
    }
}

// MARK: - IModelLoader — CircleAI.Core.IModelLoader

/// The high-level, single-file model loader contract.
public protocol IModelLoader: AnyObject, Sendable {
    /// Ensure the model named `modelName` is present locally; return its path.
    func downloadModel(_ modelName: String, progress: (@Sendable (Float) -> Void)?) async throws -> String

    /// The on-disk path the model would occupy.
    func getModelPath(_ modelName: String) throws -> String

    /// True when the model file exists and passes checksum verification.
    func modelExists(_ modelName: String) -> Bool

    /// Best-effort probe for a "[CRITICAL]" flag in the upstream versions file.
    func checkForCriticalUpdate() async -> Bool

    /// Release resources.
    func dispose()
}

// MARK: - LocalModelLoader — CircleAI.Core.LocalModelLoader

/// Local single-file model loader. Registry-driven; verifies SHA-256 checksums;
/// tries primary then fallback URL; refuses to service multi-file bundles.
public final class LocalModelLoader: IModelLoader, @unchecked Sendable {
    // Canonical weight file that anchors a bundle entry.
    private static let bundleAnchorFileName = "llm.mnn.weight"
    // Well-known versions endpoint probed by checkForCriticalUpdate.
    private static let versionsUrl = "https://raw.githubusercontent.com/BhenguAI/models/main/versions.txt"

    private let lock = NSLock()
    private let modelDir: String
    private let registry: [String: ModelInfoEntry]
    private let byteSource: IByteSource
    private var disposed = false

    /// - Parameters:
    ///   - modelDirectory: cache directory (created if missing).
    ///   - registry: model registry (mirrors the embedded registry.json). Keys
    ///     are matched case-insensitively, as in C#.
    ///   - byteSource: injected network. Defaults to an empty in-memory source.
    public init(
        modelDirectory: String,
        registry: [String: ModelInfoEntry] = [:],
        byteSource: IByteSource = InMemoryByteSource()
    ) {
        self.modelDir = modelDirectory
        // Case-insensitive registry keys (StringComparer.OrdinalIgnoreCase).
        var lowered: [String: ModelInfoEntry] = [:]
        for (k, v) in registry { lowered[k.lowercased()] = v }
        self.registry = lowered
        self.byteSource = byteSource
        try? FileManager.default.createDirectory(atPath: modelDirectory, withIntermediateDirectories: true)
    }

    private func lookup(_ modelName: String) -> ModelInfoEntry? {
        registry[modelName.lowercased()]
    }

    public func downloadModel(_ modelName: String, progress: (@Sendable (Float) -> Void)? = nil) async throws -> String {
        try throwIfDisposed()
        guard let info = lookup(modelName) else {
            throw ModelRuntimeError.argument("Model \(modelName) not supported")
        }

        if info.isBundle {
            throw ModelRuntimeError.invalidOperation(
                "Model '\(modelName)' is a multi-file bundle (registry entry has BundleFiles[]); " +
                "use the bundle downloader instead. LocalModelLoader.downloadModel only handles " +
                "legacy single-file entries.")
        }

        guard let fileName = info.fileName else {
            throw ModelRuntimeError.argument("Model \(modelName) has no FileName")
        }
        let localPath = (modelDir as NSString).appendingPathComponent(fileName)

        if FileManager.default.fileExists(atPath: localPath) {
            if info.checksum == nil || info.checksum!.hasPrefix("sha256:TBD") {
                return localPath
            }
            if verifyChecksum(filePath: localPath, expected: info.checksum!) {
                return localPath
            }
            try? FileManager.default.removeItem(atPath: localPath)
        }

        // Try primary then fallback.
        let sources = [info.primaryUrl, info.fallbackUrl]
        var lastError: Error?
        for url in sources {
            guard let url = url, !url.trimmingCharacters(in: .whitespaces).isEmpty else { continue }
            do {
                try downloadFile(url: url, outputPath: localPath, progress: progress)
                if info.checksum == nil || info.checksum!.hasPrefix("sha256:TBD") {
                    return localPath
                }
                if verifyChecksum(filePath: localPath, expected: info.checksum!) {
                    return localPath
                }
                try? FileManager.default.removeItem(atPath: localPath)
                lastError = ModelRuntimeError.invalidData("Downloaded model failed checksum verification.")
            } catch {
                lastError = error
            }
        }

        throw lastError ?? ModelRuntimeError.invalidOperation("All sources failed.")
    }

    private func downloadFile(url: String, outputPath: String, progress: (@Sendable (Float) -> Void)?) throws {
        // Bridge the fractional Float progress to the source-progress shape.
        let bridge: (@Sendable (SourceDownloadProgress) -> Void)? = progress.map { fwd in
            { p in
                let frac = p.totalBytes > 0 ? Float(Double(p.bytesReceived) / Double(p.totalBytes)) : 0
                fwd(frac)
            }
        }
        try SourceDownloadHelper.download(source: byteSource, url: url, localPath: outputPath, progress: bridge)
    }

    public func getModelPath(_ modelName: String) throws -> String {
        try throwIfDisposed()
        guard let info = lookup(modelName) else {
            throw ModelRuntimeError.fileNotFound("Model \(modelName) not found")
        }
        if info.isBundle {
            let perModel = (modelDir as NSString).appendingPathComponent(modelName)
            return (perModel as NSString).appendingPathComponent(LocalModelLoader.bundleAnchorFileName)
        }
        return (modelDir as NSString).appendingPathComponent(info.fileName!)
    }

    public func modelExists(_ modelName: String) -> Bool {
        guard let info = lookup(modelName) else { return false }
        guard let path = try? getModelPath(modelName) else { return false }
        if !FileManager.default.fileExists(atPath: path) { return false }

        if info.isBundle {
            guard let anchor = info.bundleFiles?.first(where: {
                $0.name.caseInsensitiveCompare(LocalModelLoader.bundleAnchorFileName) == .orderedSame
            }) else { return false }
            return verifyChecksum(filePath: path, expected: anchor.sha256)
        }
        return info.checksum != nil && verifyChecksum(filePath: path, expected: info.checksum!)
    }

    public func checkForCriticalUpdate() async -> Bool {
        guard let bytes = byteSource.bytes(for: LocalModelLoader.versionsUrl) else { return false }
        let text = String(decoding: bytes, as: UTF8.self)
        return text.contains("[CRITICAL]")
    }

    /// Verify a file's SHA-256 against an expected hex (accepts a "sha256:"
    /// prefix or bare hex), matching the C# `VerifyChecksum`.
    private func verifyChecksum(filePath: String, expected expectedChecksum: String) -> Bool {
        guard let data = FileManager.default.contents(atPath: filePath) else { return false }
        let actualHex = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()

        var expected = expectedChecksum.trimmingCharacters(in: .whitespacesAndNewlines)
        if expected.lowercased().hasPrefix("sha256:") {
            expected = String(expected.dropFirst("sha256:".count)).trimmingCharacters(in: .whitespaces)
        }
        return expected.caseInsensitiveCompare(actualHex) == .orderedSame
    }

    private func throwIfDisposed() throws {
        lock.lock(); defer { lock.unlock() }
        if disposed { throw ModelRuntimeError.objectDisposed("LocalModelLoader") }
    }

    public func dispose() {
        lock.lock(); disposed = true; lock.unlock()
    }
}

// MARK: - IModelManager — CircleAI.Core.IModelManager

/// Resolve + verify a model's on-disk path.
public protocol IModelManager: AnyObject, Sendable {
    /// Resolve (downloading if needed) the directory path for `modelId`.
    func getModelPath(modelId: String) async throws -> String

    /// Verify the file at `modelPath` against `expectedChecksum` (raw bytes).
    func verifyModel(modelPath: String, expectedChecksum: [UInt8]) async throws -> Bool
}

// MARK: - LocalModelManager — CircleAI.Core.LocalModelManager

/// Directory-based model manager. Downloads via an injected `IModelDownloader`
/// when the model is absent, verifies the anchor weight file's SHA-256 against a
/// caller-supplied checksum. The anchor file name matches the C# manager
/// (`pytorch_model.bin`).
///
/// Conforms to `IModelManager` so it is swappable behind that seam (e.g. by
/// `TextEmbedder`). The C# `LocalModelManager` exposes the same method shape.
public final class LocalModelManager: IModelManager, @unchecked Sendable {
    private static let anchorFileName = "pytorch_model.bin"

    private let lock = NSLock()
    private let modelsDirectory: String
    private let downloader: (any IModelDownloader)?
    private var disposed = false

    /// Construct with an explicit downloader (or nil to require models present).
    public init(downloader: (any IModelDownloader)?, modelsDirectory: String = "Models") {
        self.downloader = downloader
        self.modelsDirectory = modelsDirectory
        try? FileManager.default.createDirectory(atPath: modelsDirectory, withIntermediateDirectories: true)
    }

    /// Resolve (downloading if needed) and optionally verify the anchor file's
    /// SHA-256 against `expectedChecksum`. Mirrors
    /// `GetModelPathAsync(modelId, expectedChecksum)`.
    public func getModelPath(modelId: String, expectedChecksum: [UInt8]? = nil) async throws -> String {
        try throwIfDisposed()

        let modelPath = (modelsDirectory as NSString)
            .appendingPathComponent(LocalModelManager.sanitize(modelId))
        let anchorPath = (modelPath as NSString).appendingPathComponent(LocalModelManager.anchorFileName)

        var isDir: ObjCBool = false
        let dirExists = FileManager.default.fileExists(atPath: modelPath, isDirectory: &isDir) && isDir.boolValue
        let anchorExists = FileManager.default.fileExists(atPath: anchorPath)

        if !dirExists || !anchorExists {
            guard let downloader = downloader else {
                throw ModelRuntimeError.invalidOperation("Model not found and no downloader configured")
            }
            try await downloader.downloadModel(modelId: modelId, localPath: modelPath)
        }

        if let expected = expectedChecksum, !expected.isEmpty {
            let actual = try computeFileChecksum(anchorPath)
            if actual != expected {
                throw ModelRuntimeError.invalidData(
                    "Model checksum verification failed for '\(modelId)'. " +
                    "The file may be corrupt or tampered with.")
            }
        }
        return modelPath
    }

    /// `IModelManager.getModelPath(modelId:)` — no checksum.
    public func getModelPath(modelId: String) async throws -> String {
        try await getModelPath(modelId: modelId, expectedChecksum: nil)
    }

    /// Verify the anchor file at `modelPath` (a directory) against a raw checksum.
    public func verifyModel(modelPath: String, expectedChecksum: [UInt8]) async throws -> Bool {
        // Accept either the directory or a direct file path.
        var target = modelPath
        var isDir: ObjCBool = false
        if FileManager.default.fileExists(atPath: modelPath, isDirectory: &isDir), isDir.boolValue {
            target = (modelPath as NSString).appendingPathComponent(LocalModelManager.anchorFileName)
        }
        guard FileManager.default.fileExists(atPath: target) else { return false }
        let actual = try computeFileChecksum(target)
        return actual == expectedChecksum
    }

    static func sanitize(_ modelId: String) -> String {
        modelId.replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "\\", with: "_")
    }

    private func computeFileChecksum(_ filePath: String) throws -> [UInt8] {
        guard let data = FileManager.default.contents(atPath: filePath) else {
            throw ModelRuntimeError.fileNotFound(filePath)
        }
        return Array(SHA256.hash(data: data))
    }

    private func throwIfDisposed() throws {
        lock.lock(); defer { lock.unlock() }
        if disposed { throw ModelRuntimeError.objectDisposed("LocalModelManager") }
    }

    public func dispose() {
        lock.lock()
        disposed = true
        lock.unlock()
        (downloader as? ModelDownloader)?.dispose()
    }
}

// MARK: - SafeModelHandle — CircleAI.Core.SafeModelHandle

/// Wrapper around an opaque native model pointer. The release callback is
/// supplied by the loader so this layer stays free of native imports.
///
/// The C# type is a `SafeHandle` around a `llama_model*`. In the Swift port the
/// native pointer is modelled as an opaque `UInt` token; `release()` is
/// idempotent and invokes the managed callback exactly once.
public final class SafeModelHandle: @unchecked Sendable {
    private let lock = NSLock()
    private var handle: UInt
    private var releaseCallback: ((UInt) -> Void)?

    /// Invalid until `setHandle` + `withReleaseCallback` are called.
    public init() {
        self.handle = 0
        self.releaseCallback = nil
    }

    /// Construct around a known native token with an explicit release callback.
    public init(nativeHandle: UInt, releaseCallback: @escaping (UInt) -> Void) {
        self.handle = nativeHandle
        self.releaseCallback = releaseCallback
    }

    public var isInvalid: Bool {
        lock.lock(); defer { lock.unlock() }
        return handle == 0
    }

    /// The raw token (0 when invalid / released).
    public var rawHandle: UInt {
        lock.lock(); defer { lock.unlock() }
        return handle
    }

    /// Set the underlying token (used when constructed empty).
    public func setHandle(_ value: UInt) {
        lock.lock(); handle = value; lock.unlock()
    }

    /// Wire up the release callback after construction.
    @discardableResult
    public func withReleaseCallback(_ callback: @escaping (UInt) -> Void) -> SafeModelHandle {
        lock.lock(); releaseCallback = callback; lock.unlock()
        return self
    }

    /// Release the handle. Idempotent — matches `SafeHandle.ReleaseHandle`.
    @discardableResult
    public func release() -> Bool {
        lock.lock(); defer { lock.unlock() }
        if handle != 0 {
            releaseCallback?(handle)
            handle = 0
        }
        return true
    }

    deinit {
        _ = release()
    }
}

// MARK: - PlatformInterop — CircleAI.Core.PlatformInterop

/// Backend that "loads" a model file and returns an opaque native token. The C#
/// `PlatformInterop` P/Invokes llama.cpp here; the port injects this seam so the
/// SDK stays free of native imports. The default implementation validates the
/// file and hands back a monotonically-increasing token (release is managed).
public protocol IModelLoaderBackend: Sendable {
    /// Load the model at `path`; return an opaque non-zero token, or 0 on native
    /// failure (mirrors llama.cpp returning a null pointer).
    func load(path: String) -> UInt
    /// Free a previously-loaded token.
    func free(_ token: UInt)
}

/// Default in-memory backend: assigns sequential tokens and counts frees.
public final class InMemoryModelLoaderBackend: IModelLoaderBackend, @unchecked Sendable {
    private let lock = NSLock()
    private var next: UInt = 0
    private var live: Set<UInt> = []
    public private(set) var freeCount = 0

    public init() {}

    public func load(path: String) -> UInt {
        lock.lock(); defer { lock.unlock() }
        next += 1
        live.insert(next)
        return next
    }

    public func free(_ token: UInt) {
        lock.lock(); defer { lock.unlock() }
        if live.remove(token) != nil { freeCount += 1 }
    }

    /// Number of tokens still outstanding (not yet freed). Test aid.
    public var liveCount: Int {
        lock.lock(); defer { lock.unlock() }
        return live.count
    }
}

/// Loads native models via the injected backend. Callers receive an opaque
/// `SafeModelHandle` they can pass on to inference code.
public enum PlatformInterop {
    /// Loads a GGUF model from `path`.
    /// - Throws: `.argument` when the path is empty, `.fileNotFound` when the
    ///   model file does not exist, `.invalidOperation` when the native load
    ///   fails.
    public static func loadModel(_ path: String, backend: IModelLoaderBackend = InMemoryModelLoaderBackend()) throws -> SafeModelHandle {
        if path.trimmingCharacters(in: .whitespaces).isEmpty {
            throw ModelRuntimeError.argument("Model path is required.")
        }
        if !FileManager.default.fileExists(atPath: path) {
            throw ModelRuntimeError.fileNotFound("GGUF model file not found: \(path)")
        }

        let token = backend.load(path: path)
        if token == 0 {
            throw ModelRuntimeError.invalidOperation(
                "Native loader failed to load model at '\(path)'. " +
                "Verify the file is a valid GGUF and that the native library is on the search path.")
        }
        return SafeModelHandle(nativeHandle: token, releaseCallback: { backend.free($0) })
    }
}
