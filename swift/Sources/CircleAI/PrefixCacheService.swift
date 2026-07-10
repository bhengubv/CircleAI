// PrefixCacheService.swift
//
// RT-06: cross-session prefix cache. Snapshots the model's KV state once per
// (modelId, systemPrompt) pair and reloads it on the next chat with the same
// pair, skipping the system-prompt prefill.
//
// Cache layout:
//   <root>/<modelHash>_<systemHash>.session   ← native KV snapshot
//
// Eviction policy: simple LRU by file mtime, cap at 500 MB total, oldest first.
//
// Ported from CircleAI.Inference.PrefixCacheService. SHA-256 via CryptoKit
// (deterministic; matches the C# `Sha256` helper's lower-hex output).

import Foundation
import CryptoKit

/// Manages an on-disk cache of "warm" model sessions keyed by the hash of
/// (modelId, systemPrompt). Thread-safe; shared across generators. Default
/// instance is `PrefixCacheService.default`.
public final class PrefixCacheService: @unchecked Sendable {
    private static let capBytes: Int64 = 500 * 1024 * 1024 // 500 MB
    private let ioLock = NSLock()
    private let root: String

    /// The default per-app instance rooted at `%LOCALAPPDATA%/CircleAI/prefix-cache`
    /// on Windows and `~/.circleai/prefix-cache` on Unix / iOS / Android.
    public static let `default` = PrefixCacheService(root: PrefixCacheService.defaultRoot())

    /// Construct a cache service rooted at `root`. The directory is created on
    /// demand.
    public init(root: String) {
        precondition(!root.trimmingCharacters(in: .whitespaces).isEmpty, "root is required.")
        self.root = root
        try? FileManager.default.createDirectory(atPath: root, withIntermediateDirectories: true)
    }

    /// Compute the cache key for a (modelId, systemPrompt) pair. Returns `nil`
    /// when `systemPrompt` is nil/empty — there is nothing to cache without a
    /// system prompt to key against.
    public static func keyFor(modelId: String, systemPrompt: String?) -> String? {
        if modelId.trimmingCharacters(in: .whitespaces).isEmpty { return nil }
        guard let systemPrompt = systemPrompt, !systemPrompt.isEmpty else { return nil }

        let modelHash = sha256Hex(modelId)
        let systemHash = sha256Hex(systemPrompt)
        // First 16 hex chars per component — collision-free at any single
        // device's cache scale, much shorter on disk.
        let m = String(modelHash.prefix(16))
        let s = String(systemHash.prefix(16))
        return "\(m)_\(s)"
    }

    /// Returns the cache path for `key`. The path may or may not exist; use
    /// `hasEntry` to check.
    public func path(for key: String) -> String {
        (root as NSString).appendingPathComponent("\(key).session")
    }

    /// `true` when a cached entry exists for `key`.
    public func hasEntry(_ key: String) -> Bool {
        FileManager.default.fileExists(atPath: path(for: key))
    }

    /// Touch the entry's mtime so LRU eviction treats it as recently used.
    /// Called after a successful load.
    public func touch(_ key: String) {
        let p = path(for: key)
        if FileManager.default.fileExists(atPath: p) {
            try? FileManager.default.setAttributes([.modificationDate: Date()], ofItemAtPath: p)
        }
    }

    /// Evict oldest entries until the directory is under the 500 MB cap. Called
    /// after every successful save to keep the cache bounded. Best-effort.
    public func evictIfNeeded() {
        ioLock.lock(); defer { ioLock.unlock() }
        let fm = FileManager.default
        guard let names = try? fm.contentsOfDirectory(atPath: root) else { return }

        struct Entry { let path: String; let mtime: Date; let size: Int64 }
        var files: [Entry] = []
        for name in names where name.hasSuffix(".session") {
            let p = (root as NSString).appendingPathComponent(name)
            guard let attrs = try? fm.attributesOfItem(atPath: p) else { continue }
            let mtime = (attrs[.modificationDate] as? Date) ?? Date.distantPast
            let size = (attrs[.size] as? NSNumber)?.int64Value ?? 0
            files.append(Entry(path: p, mtime: mtime, size: size))
        }
        files.sort { $0.mtime < $1.mtime }

        var total: Int64 = files.reduce(0) { $0 + $1.size }
        var i = 0
        while total > Self.capBytes && i < files.count {
            let f = files[i]; i += 1
            do {
                try fm.removeItem(atPath: f.path)
                total -= f.size
            } catch {
                // best effort
            }
        }
    }

    // MARK: - helpers

    private static func sha256Hex(_ input: String) -> String {
        let digest = SHA256.hash(data: Data(input.utf8))
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    private static func defaultRoot() -> String {
        // Windows: %LOCALAPPDATA%/CircleAI/prefix-cache
        // Unix-like: ~/.circleai/prefix-cache
        if let local = ProcessInfo.processInfo.environment["LOCALAPPDATA"],
           !local.trimmingCharacters(in: .whitespaces).isEmpty {
            return (local as NSString).appendingPathComponent("CircleAI/prefix-cache")
        }
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        return (home as NSString).appendingPathComponent(".circleai/prefix-cache")
    }
}
