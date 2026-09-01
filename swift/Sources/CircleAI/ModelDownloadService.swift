// ModelDownloadService.swift
//
// Downloads and manages model files on disk. Supports the legacy single-file
// shape (one URL → one cached weight) and the bundle shape (a per-model
// directory with every file MNN-LLM needs to load).
//
// Ported from CircleAI.Inference.IModelDownloadService + ModelDownloadService.
// The network is injected behind IHttpDownloader so tests exercise the full
// verify/rename/manifest logic deterministically without a real socket. The
// production URLSession-backed downloader ships alongside.

import Foundation
import CryptoKit
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

// MARK: - BundleFileSpec

/// One file in a model bundle (compatible shape with `BundleFile`).
public struct BundleFileSpec: Sendable, Equatable {
    /// Filename relative to the model directory (e.g. `config.json`).
    public let name: String
    /// SHA-256 in `sha256:<hex>` or bare-hex form. The verify path strips the
    /// optional `sha256:` prefix before comparing.
    public let sha256: String
    /// Expected file size for diagnostics.
    public let sizeBytes: Int64

    public init(name: String, sha256: String, sizeBytes: Int64) {
        self.name = name
        self.sha256 = sha256
        self.sizeBytes = sizeBytes
    }
}

// MARK: - Errors

public enum ModelDownloadError: Error, Equatable, CustomStringConvertible {
    case emptyStorageDirectory
    case emptyModelId
    case emptyRepo
    case emptyBundleList
    case bundleFileMissingName(modelId: String)
    case shaMismatch(String)
    case httpStatus(Int)
    case cannotDetermineDriveRoot(String)

    /// A transport failure that carries its own DIAGNOSIS.
    ///
    /// So callers and UI layers stop pattern-matching on error text to work out
    /// whether the person is offline, the mirror is dead, or the file is
    /// corrupt. Those have completely different remedies, and only some of them
    /// are the user's to fix.
    case diagnosed(message: String, diagnosis: NetworkDiagnosis)

    /// What to actually show a person, as opposed to what to put in a log.
    ///
    /// Falls back to something plain rather than to the transport's own words:
    /// "Unable to resolve host modelscope.cn" tells somebody holding a phone
    /// nothing they can act on.
    public var userMessage: String {
        switch self {
        case .diagnosed(_, let d) where !d.remedy.isEmpty:
            return d.remedy
        default:
            return "The model could not be downloaded right now. Please try again later."
        }
    }

    public var description: String {
        switch self {
        case .emptyStorageDirectory: return "Storage directory must not be empty."
        case .emptyModelId: return "Model ID must not be empty."
        case .emptyRepo: return "Repo path is required for bundle entries."
        case .emptyBundleList: return "Bundle file list must not be empty."
        case .bundleFileMissingName(let m): return "Bundle for '\(m)' contains a file with no Name."
        case .shaMismatch(let s): return s
        case .httpStatus(let c): return "HTTP request failed with status \(c)."
        case .cannotDetermineDriveRoot(let d): return "Cannot determine drive root for '\(d)'."
        case .diagnosed(let m, let d): return "\(m) (\(d))"
        }
    }
}

// MARK: - IModelDownloadService

/// Downloads and manages model files on disk.
public protocol IModelDownloadService: Sendable {
    /// Ensures a single model file is present and matches `expectedSha256`.
    /// Returns the absolute path to the cached file.
    func ensureModel(
        modelId: String,
        downloadUri: URL,
        expectedSha256: String?,
        progress: (@Sendable (Double) -> Void)?
    ) async throws -> String

    /// Ensures every file in `bundleFiles` is present under a per-model
    /// directory and matches its pinned SHA-256. Returns the model directory.
    func ensureBundle(
        modelId: String,
        repo: String,
        bundleFiles: [BundleFileSpec],
        progress: (@Sendable (Double) -> Void)?
    ) async throws -> String

    /// `true` when the model file (single-file shape) exists on disk.
    func isModelCached(_ modelId: String) async throws -> Bool

    /// Deletes the model file or directory if it exists. No-op when absent.
    func deleteModel(_ modelId: String) async throws

    /// Free bytes available on the drive hosting the storage directory.
    func availableDiskSpaceBytes() async throws -> Int64
}

// MARK: - IHttpDownloader (injected network seam)

/// Injected network seam. `download` writes the response body to `destPath`,
/// reporting 0-1 progress. Production wires `URLSessionHttpDownloader`; tests
/// wire a deterministic in-memory downloader.
public protocol IHttpDownloader: Sendable {
    func download(
        from uri: URL,
        to destPath: String,
        progress: (@Sendable (Double) -> Void)?
    ) async throws
}

/// Default `IHttpDownloader` backed by URLSession. Streams to disk and reports
/// coarse progress. Sets a realistic User-Agent so ModelScope's CDN (which
/// 403s clients with no UA) serves the fallback URL.
public struct URLSessionHttpDownloader: IHttpDownloader {
    private let userAgent: String

    public init(userAgent: String =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36 CircleAI/1.0") {
        self.userAgent = userAgent
    }

    public func download(
        from uri: URL,
        to destPath: String,
        progress: (@Sendable (Double) -> Void)?
    ) async throws {
        var req = URLRequest(url: uri)
        req.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        let (data, resp) = try await URLSession.shared.data(for: req)
        if let http = resp as? HTTPURLResponse, http.statusCode != 200 {
            throw ModelDownloadError.httpStatus(http.statusCode)
        }
        let url = URL(fileURLWithPath: destPath)
        try data.write(to: url, options: .atomic)
        progress?(1.0)
    }
}

// MARK: - ModelDownloadService

/// Default `IModelDownloadService`.
///
/// Single-file entries land at `{storageDirectory}/{modelId}.gguf`; bundle
/// entries land at `{storageDirectory}/{modelId}/` with every bundle file
/// written under that directory.
public final class ModelDownloadService: IModelDownloadService, @unchecked Sendable {
    private let storageDirectory: String
    private let http: IHttpDownloader

    public init(storageDirectory: String, downloader: IHttpDownloader = URLSessionHttpDownloader()) {
        precondition(
            !storageDirectory.trimmingCharacters(in: .whitespaces).isEmpty,
            "Storage directory must not be empty.")
        self.storageDirectory = storageDirectory
        self.http = downloader
        try? FileManager.default.createDirectory(atPath: storageDirectory, withIntermediateDirectories: true)
    }

    // MARK: Single-file (legacy)

    public func ensureModel(
        modelId: String,
        downloadUri: URL,
        expectedSha256: String?,
        progress: (@Sendable (Double) -> Void)?
    ) async throws -> String {
        try Self.validateModelId(modelId)
        let filePath = singleFilePath(modelId)
        let fm = FileManager.default

        if fm.fileExists(atPath: filePath), let expected = expectedSha256 {
            if try await Self.verifySha256(filePath: filePath, expectedHex: expected) {
                progress?(1.0)
                return filePath
            }
            try? fm.removeItem(atPath: filePath)
        } else if fm.fileExists(atPath: filePath), expectedSha256 == nil {
            progress?(1.0)
            return filePath
        }

        let tempPath = filePath + ".tmp"
        do {
            try await http.download(from: downloadUri, to: tempPath, progress: progress)

            if let expected = expectedSha256 {
                let ok = try await Self.verifySha256(filePath: tempPath, expectedHex: expected)
                if !ok {
                    try? fm.removeItem(atPath: tempPath)
                    throw ModelDownloadError.shaMismatch(
                        "SHA-256 mismatch for model '\(modelId)'. The downloaded file has been deleted.")
                }
            }

            if fm.fileExists(atPath: filePath) { try? fm.removeItem(atPath: filePath) }
            try fm.moveItem(atPath: tempPath, toPath: filePath)
        } catch {
            if fm.fileExists(atPath: tempPath) { try? fm.removeItem(atPath: tempPath) }
            throw error
        }
        return filePath
    }

    // MARK: Bundle

    public func ensureBundle(
        modelId: String,
        repo: String,
        bundleFiles: [BundleFileSpec],
        progress: (@Sendable (Double) -> Void)?
    ) async throws -> String {
        try Self.validateModelId(modelId)
        guard !repo.trimmingCharacters(in: .whitespaces).isEmpty else { throw ModelDownloadError.emptyRepo }
        guard !bundleFiles.isEmpty else { throw ModelDownloadError.emptyBundleList }

        let fm = FileManager.default
        let modelDir = (storageDirectory as NSString).appendingPathComponent(modelId)
        try fm.createDirectory(atPath: modelDir, withIntermediateDirectories: true)

        var totalBytes: Int64 = 0
        for f in bundleFiles { totalBytes += max(0, f.sizeBytes) }
        var doneBytes: Int64 = 0

        for file in bundleFiles {
            try Task.checkCancellation()
            guard !file.name.trimmingCharacters(in: .whitespaces).isEmpty else {
                throw ModelDownloadError.bundleFileMissingName(modelId: modelId)
            }

            let destPath = (modelDir as NSString).appendingPathComponent(file.name)
            let destDir = (destPath as NSString).deletingLastPathComponent
            try fm.createDirectory(atPath: destDir, withIntermediateDirectories: true)

            // Skip when cached + valid.
            if fm.fileExists(atPath: destPath),
               try await Self.verifySha256(filePath: destPath, expectedHex: file.sha256) {
                doneBytes += file.sizeBytes
                Self.reportOverall(progress, done: doneBytes, total: totalBytes)
                continue
            }
            if fm.fileExists(atPath: destPath) { try? fm.removeItem(atPath: destPath) }

            let tempPath = destPath + ".tmp"
            let capturedDone = doneBytes
            let fileSize = file.sizeBytes
            do {
                let perFile: (@Sendable (Double) -> Void)?
                if progress == nil {
                    perFile = nil
                } else {
                    perFile = { p in
                        Self.reportOverall(progress, done: capturedDone + Int64(Double(fileSize) * p), total: totalBytes)
                    }
                }

                // PrimaryUrl (API form) → FallbackUrl (CDN form). Same bytes;
                // try both before giving up so a transient CDN hiccup doesn't
                // kill an otherwise viable bundle download.
                let primary = Self.buildPrimaryUrl(repo: repo, fileName: file.name)
                let fallback = Self.buildFallbackUrl(repo: repo, fileName: file.name)
                do {
                    try await http.download(from: primary, to: tempPath, progress: perFile)
                } catch {
                    if fm.fileExists(atPath: tempPath) { try? fm.removeItem(atPath: tempPath) }
                    try await http.download(from: fallback, to: tempPath, progress: perFile)
                }

                let ok = try await Self.verifySha256(filePath: tempPath, expectedHex: file.sha256)
                if !ok {
                    try? fm.removeItem(atPath: tempPath)
                    throw ModelDownloadError.shaMismatch(
                        "SHA-256 mismatch for bundle file '\(file.name)' of model '\(modelId)'. " +
                        "The downloaded file has been deleted.")
                }
                if fm.fileExists(atPath: destPath) { try? fm.removeItem(atPath: destPath) }
                try fm.moveItem(atPath: tempPath, toPath: destPath)
                doneBytes += file.sizeBytes
                Self.reportOverall(progress, done: doneBytes, total: totalBytes)
            } catch {
                if fm.fileExists(atPath: tempPath) { try? fm.removeItem(atPath: tempPath) }
                throw error
            }
        }

        progress?(1.0)
        return modelDir
    }

    /// Stamps an `installed.json` file in `modelDir` describing what's on disk.
    /// Best-effort — silent failures are swallowed so a manifest hiccup never
    /// breaks a working install.
    public func writeInstalledManifest(
        modelDir: String,
        modelId: String,
        version: String,
        repo: String?,
        bundleFiles: [BundleFileSpec]
    ) async {
        guard !modelDir.trimmingCharacters(in: .whitespaces).isEmpty,
              !modelId.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        var totalBytes: Int64 = 0
        var files: [BundleFile] = []
        for f in bundleFiles {
            files.append(BundleFile(name: f.name, sha256: f.sha256, sizeBytes: f.sizeBytes))
            totalBytes += max(0, f.sizeBytes)
        }
        let manifest = InstalledManifest(
            modelId: modelId,
            version: version,
            repo: repo,
            totalBytes: totalBytes,
            files: files,
            installedAtUtc: Date())
        let path = (modelDir as NSString).appendingPathComponent("installed.json")
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = .prettyPrinted
        if let data = try? encoder.encode(manifest) {
            try? data.write(to: URL(fileURLWithPath: path))
        }
    }

    // MARK: Common

    public func isModelCached(_ modelId: String) async throws -> Bool {
        try Self.validateModelId(modelId)
        let fm = FileManager.default
        if fm.fileExists(atPath: singleFilePath(modelId)) { return true }
        let dir = (storageDirectory as NSString).appendingPathComponent(modelId)
        var isDir: ObjCBool = false
        return fm.fileExists(atPath: dir, isDirectory: &isDir) && isDir.boolValue
    }

    public func deleteModel(_ modelId: String) async throws {
        try Self.validateModelId(modelId)
        let fm = FileManager.default
        let single = singleFilePath(modelId)
        if fm.fileExists(atPath: single) { try fm.removeItem(atPath: single) }
        let dir = (storageDirectory as NSString).appendingPathComponent(modelId)
        var isDir: ObjCBool = false
        if fm.fileExists(atPath: dir, isDirectory: &isDir), isDir.boolValue {
            try fm.removeItem(atPath: dir)
        }
    }

    public func availableDiskSpaceBytes() async throws -> Int64 {
        let absoluteDir = (storageDirectory as NSString).standardizingPath
        let values = try URL(fileURLWithPath: absoluteDir)
            .resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey, .volumeAvailableCapacityKey])
        if let important = values.volumeAvailableCapacityForImportantUsage {
            return Int64(important)
        }
        if let cap = values.volumeAvailableCapacity {
            return Int64(cap)
        }
        throw ModelDownloadError.cannotDetermineDriveRoot(absoluteDir)
    }

    // MARK: Helpers

    private func singleFilePath(_ modelId: String) -> String {
        (storageDirectory as NSString).appendingPathComponent("\(modelId).gguf")
    }

    private static func validateModelId(_ modelId: String) throws {
        if modelId.trimmingCharacters(in: .whitespaces).isEmpty {
            throw ModelDownloadError.emptyModelId
        }
    }

    static func buildPrimaryUrl(repo: String, fileName: String) -> URL {
        let escaped = fileName.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? fileName
        return URL(string: "https://modelscope.cn/api/v1/models/\(repo)/repo?Revision=master&FilePath=\(escaped)")!
    }

    static func buildFallbackUrl(repo: String, fileName: String) -> URL {
        let escaped = fileName.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? fileName
        return URL(string: "https://modelscope.cn/models/\(repo)/resolve/master/\(escaped)")!
    }

    private static func reportOverall(_ p: (@Sendable (Double) -> Void)?, done: Int64, total: Int64) {
        guard let p = p else { return }
        if total <= 0 { p(0.0) }
        else { p(min(0.999, Double(done) / Double(total))) }
    }

    static func verifySha256(filePath: String, expectedHex: String) async throws -> Bool {
        let data = try Data(contentsOf: URL(fileURLWithPath: filePath))
        let digest = SHA256.hash(data: data)
        let actualHex = digest.map { String(format: "%02X", $0) }.joined()
        let expectedNormalised = stripShaAlgorithmPrefix(expectedHex)
        return actualHex.caseInsensitiveCompare(expectedNormalised) == .orderedSame
    }

    /// Returns the hex portion of a SHA-256 checksum, stripping an optional
    /// leading algorithm token of the form `sha256:`, `SHA-256:`, etc.
    static func stripShaAlgorithmPrefix(_ raw: String) -> String {
        if raw.isEmpty { return "" }
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let colon = trimmed.firstIndex(of: ":") else { return trimmed }
        let prefix = trimmed[trimmed.startIndex..<colon]
        if prefix.count > 0 && prefix.count <= 16 {
            var isAlgName = true
            for c in prefix {
                if !(c.isLetter || c.isNumber || c == "-" || c == "_") { isAlgName = false; break }
            }
            if isAlgName {
                let after = trimmed.index(after: colon)
                return String(trimmed[after...]).trimmingCharacters(in: .whitespacesAndNewlines)
            }
        }
        return trimmed
    }
}
