// Distribution.swift
//
// Port of the named CircleAI.Distribution.Ubiquity DISTRIBUTION-section types —
// the app-store / signed-delta / OEM+carrier preload rails.
//   • UbiquityRails.cs (DISTRIBUTION section) —
//       AppStorePackage, IAppStoreSubmitter,
//       DeltaUpdate, ISignedDeltaUpdater,
//       IOemPreloadCatalog + DefaultOemPreloadCatalog,
//       ICarrierPreloadCatalog + DefaultCarrierPreloadCatalog
//
// Porting notes:
//   • Only the four named work-unit types (+ their DTOs and the two default
//     catalogues) are ported; the other UBI rails in UbiquityRails.cs are out
//     of this work unit's scope.
//   • `IReadOnlyDictionary<string, string>` → `[String: String]`.
//   • `byte[]` → `Data`.
//   • `IAppStoreSubmitter` / `ISignedDeltaUpdater` are host-implemented; the
//     Swift port adds deterministic in-memory implementations (recording
//     submitters/updaters) so the surface is usable + testable with no stubs.

import Foundation

// MARK: - App-store submission

/// A packaged app ready for store submission. (C# `AppStorePackage`.)
public struct AppStorePackage: Sendable, Equatable, Codable {
    /// Target store name.
    public let storeName: String
    /// Local package path.
    public let packagePath: String
    /// Version string.
    public let version: String
    /// Store-specific metadata.
    public let metadata: [String: String]

    public init(storeName: String, packagePath: String, version: String, metadata: [String: String]) {
        self.storeName = storeName
        self.packagePath = packagePath
        self.version = version
        self.metadata = metadata
    }
}

/// Submits an app package to a store. (C# `IAppStoreSubmitter`.)
public protocol IAppStoreSubmitter: Sendable {
    /// Submits `package`. Returns whether the submission was accepted.
    func submit(_ package: AppStorePackage) async -> Bool
}

/// In-memory submitter — records submissions and accepts them. Replaces the
/// host-specific store integration for tests / local runs.
public final class InMemoryAppStoreSubmitter: IAppStoreSubmitter, @unchecked Sendable {
    private let lock = NSLock()
    private var submitted: [AppStorePackage] = []

    public init() {}

    public func submit(_ package: AppStorePackage) async -> Bool {
        lock.lock(); submitted.append(package); lock.unlock()
        return true
    }

    /// Snapshot of all submitted packages.
    public var allSubmissions: [AppStorePackage] {
        lock.lock(); defer { lock.unlock() }
        return submitted
    }
}

// MARK: - Signed delta updates

/// A signed delta update between two versions on a channel. (C# `DeltaUpdate`.)
/// `byte[]` → `Data`.
public struct DeltaUpdate: Sendable, Equatable, Codable {
    /// Release channel.
    public let channel: String
    /// Version updating from.
    public let fromVersion: String
    /// Version updating to.
    public let toVersion: String
    /// Update payload.
    public let payload: Data
    /// Signature over the payload.
    public let signature: Data

    public init(channel: String, fromVersion: String, toVersion: String, payload: Data, signature: Data) {
        self.channel = channel
        self.fromVersion = fromVersion
        self.toVersion = toVersion
        self.payload = payload
        self.signature = signature
    }
}

/// Applies signed delta updates. (C# `ISignedDeltaUpdater`.)
public protocol ISignedDeltaUpdater: Sendable {
    /// Applies `update`. Returns whether the update was applied.
    func apply(_ update: DeltaUpdate) async -> Bool
}

/// In-memory updater — records applied updates, gated by an injected signature
/// validator (default: accept all with a non-empty signature). Replaces the
/// host-specific updater.
public final class InMemorySignedDeltaUpdater: ISignedDeltaUpdater, @unchecked Sendable {
    private let lock = NSLock()
    private var applied: [DeltaUpdate] = []
    private let signatureValidator: @Sendable (DeltaUpdate) -> Bool

    public init(signatureValidator: @escaping @Sendable (DeltaUpdate) -> Bool = { !$0.signature.isEmpty }) {
        self.signatureValidator = signatureValidator
    }

    public func apply(_ update: DeltaUpdate) async -> Bool {
        guard signatureValidator(update) else { return false }
        lock.lock(); applied.append(update); lock.unlock()
        return true
    }

    /// Snapshot of all applied updates.
    public var allApplied: [DeltaUpdate] {
        lock.lock(); defer { lock.unlock() }
        return applied
    }
}

// MARK: - OEM / carrier preload catalogues

/// Lists OEM preload partners. (C# `IOemPreloadCatalog`.)
public protocol IOemPreloadCatalog: Sendable {
    /// OEM partner names.
    var partners: [String] { get }
}

/// Default OEM preload catalogue. (C# `DefaultOemPreloadCatalog`.)
public struct DefaultOemPreloadCatalog: IOemPreloadCatalog {
    public init() {}
    public let partners: [String] = ["Tecno", "Itel", "Samsung mid-tier", "Xiaomi", "Huawei"]
}

/// Lists carrier preload partners. (C# `ICarrierPreloadCatalog`.)
public protocol ICarrierPreloadCatalog: Sendable {
    /// Carrier names.
    var carriers: [String] { get }
}

/// Default carrier preload catalogue. (C# `DefaultCarrierPreloadCatalog`.)
public struct DefaultCarrierPreloadCatalog: ICarrierPreloadCatalog {
    public init() {}
    public let carriers: [String] = ["MTN", "Vodacom", "Cell C", "Telkom", "Safaricom", "Airtel"]
}

// MARK: - Abuse-safe mode (safety phrase)

/// Abuse-safe mode: a user in a coercive environment can silently invoke a
/// safe mode by speaking a per-user test phrase. (C# `IAbusiveEnvironmentMode`.)
public protocol IAbusiveEnvironmentMode: Sendable {
    /// Engages abuse-safe mode for `ownerId`.
    func engage(_ ownerId: String) async throws
    /// The deterministic per-user phrase the user can speak to silently invoke
    /// abuse-safe mode.
    func safetyPhrase(_ ownerId: String) throws -> String
    /// Whether abuse-safe mode is currently engaged for `ownerId`.
    func isEngaged(_ ownerId: String) -> Bool
}

/// Default `IAbusiveEnvironmentMode`. (C# `DefaultAbusiveEnvironmentMode`.)
///
/// The safety phrase is derived deterministically from the owner id via
/// FNV-1a-32 over UTF-8 (NOT Swift's `hashValue`/`Hasher`, which is seeded per
/// process) so the phrase is stable across restarts AND byte-identical across
/// every language port.
public final class DefaultAbusiveEnvironmentMode: IAbusiveEnvironmentMode, @unchecked Sendable {
    private let lock = NSLock()
    private var engaged: Set<String> = []
    private var phrases: [String: String] = [:]

    public init() {}

    public func engage(_ ownerId: String) async throws {
        if ownerId.isBlank { throw DistributionError.ownerIdRequired }
        lock.lock(); engaged.insert(ownerId); lock.unlock()
    }

    public func safetyPhrase(_ ownerId: String) throws -> String {
        if ownerId.isBlank { throw DistributionError.ownerIdRequired }
        lock.lock(); defer { lock.unlock() }
        if let cached = phrases[ownerId] { return cached }
        // Deterministic per-owner safety phrase from an 8-word benign vocabulary.
        let vocab = ["thunder", "river", "amber", "field", "rain", "stone", "harbor", "linen"]
        let h = Self.fnv1a32(ownerId)
        let phrase = "the \(vocab[Int(h % 8)]) \(vocab[Int((h >> 8) % 8)]) is \(vocab[Int((h >> 16) % 8)])"
        phrases[ownerId] = phrase
        return phrase
    }

    public func isEngaged(_ ownerId: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return engaged.contains(ownerId)
    }

    /// FNV-1a 32-bit over UTF-8 — deterministic and identical across all
    /// language ports (unlike Swift's `Hasher`, which is seeded per process).
    /// `&*` is the wrapping multiply: plain `*` would trap on overflow, whereas
    /// FNV requires the product to wrap mod 2^32.
    static func fnv1a32(_ s: String) -> UInt32 {
        var h: UInt32 = 2166136261 // FNV offset basis
        for b in Array(s.utf8) {
            h = (h ^ UInt32(b)) &* 16777619 // XOR byte, multiply by FNV prime (wraps mod 2^32)
        }
        return h
    }
}

// MARK: - Errors

/// Errors raised by the distribution / ubiquity rails.
public enum DistributionError: Error, Equatable, CustomStringConvertible {
    case ownerIdRequired

    public var description: String {
        switch self {
        case .ownerIdRequired: return "ownerId required"
        }
    }
}
