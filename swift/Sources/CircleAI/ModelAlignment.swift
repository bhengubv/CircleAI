// ModelAlignment.swift
//
// Port of the model-alignment surface from src/CircleAI.ModelAlignment:
//   • Contracts.cs               — AlignmentProfile, AlignmentResult,
//                                  IAlignmentToolkit, IAlignmentAuditor
//   • InMemoryModelAlignment.cs  — InMemoryAlignmentToolkit,
//                                  RefuseAlignedPublishAuditor
//   • NullImplementations.cs     — fail-closed NullAlignmentToolkit /
//                                  NullAlignmentAuditor
//
// Targeted abliteration lives behind these contracts so a host applies / reverts
// it deliberately, and so publishing abliterated weights can be refused. The
// in-memory toolkit only permits REVERSIBLE profiles (our "no permanent
// abliteration" licence stance), and the default auditor REFUSES to publish any
// model that has alignment profiles applied.
//
// Porting notes:
//   • `ValueTask<T>` members become `async throws`.
//   • C# `ArgumentException` (blank id) and `InvalidOperationException` (aligned
//     publish) map onto the `ModelAlignmentError` enum. The exact refusal message
//     built by `AssertOkToPublishAsync` is preserved.
//   • `ConcurrentDictionary<string, List<AlignmentProfile>>` + `lock` becomes an
//     NSLock-guarded `[String: [AlignmentProfile]]`. All mutation is confined to
//     synchronous helpers that take the lock; nothing is awaited while held.

import Foundation

// MARK: - AlignmentProfile

/// (2.6.0) Describes an alignment change applied to a model — which refusal
/// categories it removes and whether it can be reverted.
public struct AlignmentProfile: Sendable, Equatable, Codable {
    /// Stable identifier for this profile.
    public let profileId: String
    /// Human-readable description of what the profile does.
    public let description: String
    /// Refusal categories this profile removes (e.g. "self-harm", "violence").
    public let refusalCategoriesRemoved: [String]
    /// UTC creation timestamp.
    public let createdAtUtc: Date
    /// Whether applying this profile can later be reverted.
    public let isReversible: Bool

    public init(
        profileId: String,
        description: String,
        refusalCategoriesRemoved: [String],
        createdAtUtc: Date,
        isReversible: Bool
    ) {
        self.profileId = profileId
        self.description = description
        self.refusalCategoriesRemoved = refusalCategoriesRemoved
        self.createdAtUtc = createdAtUtc
        self.isReversible = isReversible
    }
}

// MARK: - AlignmentResult

/// (2.6.0) Result of an apply/revert operation.
public struct AlignmentResult: Sendable, Equatable, Codable {
    /// The profile the operation targeted.
    public let profileId: String
    /// Whether the operation succeeded.
    public let success: Bool
    /// Failure reason when `success` is false; `nil` on success.
    public let failureReason: String?

    public init(profileId: String, success: Bool, failureReason: String?) {
        self.profileId = profileId
        self.success = success
        self.failureReason = failureReason
    }
}

// MARK: - Errors

/// Errors thrown by the model-alignment surface. Maps the C# `ArgumentException`
/// / `InvalidOperationException` throw sites onto typed Swift errors.
public enum ModelAlignmentError: Error, Equatable, CustomStringConvertible {
    /// A required argument was blank (mirrors C# `ArgumentException`).
    case argument(String)
    /// Publishing was refused because alignment profiles are applied (mirrors
    /// C# `InvalidOperationException`).
    case invalidOperation(String)

    public var description: String {
        switch self {
        case .argument(let m):         return m
        case .invalidOperation(let m): return m
        }
    }
}

// MARK: - Contracts

/// (2.6.0) Targeted abliteration toolkit. Apply / revert / list alignment
/// profiles for a model.
public protocol IAlignmentToolkit: AnyObject, Sendable {
    /// Identifier for the backing implementation (e.g. "in-memory", "null").
    var backendId: String { get }

    /// Applies `profile` to `modelId`.
    func apply(modelId: String, profile: AlignmentProfile) async throws -> AlignmentResult

    /// Reverts the profile identified by `profileId` from `modelId`.
    func revert(modelId: String, profileId: String) async throws -> AlignmentResult

    /// Lists the alignment profiles currently applied to `modelId`.
    func listApplied(modelId: String) async throws -> [AlignmentProfile]
}

/// (2.6.0) Refuses to upload / publish weights that carry alignment deltas.
public protocol IAlignmentAuditor: AnyObject, Sendable {
    /// Identifier for the backing implementation.
    var backendId: String { get }

    /// Throws if `modelId` has applied alignment profiles and the intended action
    /// is "publish upstream".
    func assertOkToPublish(modelId: String) async throws
}

// MARK: - InMemoryAlignmentToolkit

/// (3.3.0) Real in-memory alignment toolkit. `apply` only permits reversible
/// profiles (matching the "no permanent abliteration" licence stance).
public final class InMemoryAlignmentToolkit: IAlignmentToolkit, @unchecked Sendable {
    private let lock = NSLock()
    private var byModel: [String: [AlignmentProfile]] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    public func apply(modelId: String, profile: AlignmentProfile) async throws -> AlignmentResult {
        guard !isBlank(modelId) else { throw ModelAlignmentError.argument("modelId required") }

        if !profile.isReversible {
            return AlignmentResult(
                profileId: profile.profileId,
                success: false,
                failureReason: "Non-reversible alignment refused by InMemoryAlignmentToolkit")
        }

        addLocked(modelId: modelId, profile: profile)
        return AlignmentResult(profileId: profile.profileId, success: true, failureReason: nil)
    }

    public func revert(modelId: String, profileId: String) async throws -> AlignmentResult {
        guard !isBlank(modelId)   else { throw ModelAlignmentError.argument("modelId required") }
        guard !isBlank(profileId) else { throw ModelAlignmentError.argument("profileId required") }
        return removeLocked(modelId: modelId, profileId: profileId)
    }

    public func listApplied(modelId: String) async throws -> [AlignmentProfile] {
        guard !isBlank(modelId) else { throw ModelAlignmentError.argument("modelId required") }
        lock.lock(); defer { lock.unlock() }
        return byModel[modelId] ?? []
    }

    // ── Private (lock-confined mutation) ─────────────────────────────────────

    private func addLocked(modelId: String, profile: AlignmentProfile) {
        lock.lock(); defer { lock.unlock() }
        byModel[modelId, default: []].append(profile)
    }

    private func removeLocked(modelId: String, profileId: String) -> AlignmentResult {
        lock.lock(); defer { lock.unlock() }
        guard var list = byModel[modelId] else {
            return AlignmentResult(profileId: profileId, success: false, failureReason: "Unknown model")
        }
        let before = list.count
        list.removeAll { $0.profileId == profileId }
        let removed = before - list.count
        byModel[modelId] = list
        return removed > 0
            ? AlignmentResult(profileId: profileId, success: true,  failureReason: nil)
            : AlignmentResult(profileId: profileId, success: false, failureReason: "Profile not applied to this model")
    }

    private func isBlank(_ s: String) -> Bool {
        s.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
}

// MARK: - RefuseAlignedPublishAuditor

/// (3.3.0) Refuses to publish weights that carry alignment deltas. Wired by
/// default. Hosts that need different policy can swap auditors.
public final class RefuseAlignedPublishAuditor: IAlignmentAuditor, @unchecked Sendable {
    private let toolkit: IAlignmentToolkit

    public init(toolkit: IAlignmentToolkit) {
        self.toolkit = toolkit
    }

    public var backendId: String { "refuse-aligned" }

    public func assertOkToPublish(modelId: String) async throws {
        guard !modelId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ModelAlignmentError.argument("modelId required")
        }
        let applied = try await toolkit.listApplied(modelId: modelId)
        if applied.count > 0 {
            throw ModelAlignmentError.invalidOperation(
                "Cannot publish '\(modelId)': \(applied.count) alignment profile(s) applied — " +
                "this would distribute weights with safety modifications.")
        }
    }
}

// MARK: - Null implementations (fail-closed)

/// (2.6.0) Fail-closed toolkit — refuses to apply/revert anything and lists
/// nothing.
public final class NullAlignmentToolkit: IAlignmentToolkit, @unchecked Sendable {
    public static let instance = NullAlignmentToolkit()
    public init() {}
    public var backendId: String { "null" }

    public func apply(modelId: String, profile: AlignmentProfile) async throws -> AlignmentResult {
        AlignmentResult(
            profileId: profile.profileId,
            success: false,
            failureReason: "NullAlignmentToolkit: no real backend wired.")
    }

    public func revert(modelId: String, profileId: String) async throws -> AlignmentResult {
        AlignmentResult(
            profileId: profileId,
            success: false,
            failureReason: "NullAlignmentToolkit: nothing to revert.")
    }

    public func listApplied(modelId: String) async throws -> [AlignmentProfile] { [] }
}

/// (2.6.0) Fail-closed auditor — always asserts ok-to-publish (since the null
/// toolkit never applies anything).
public final class NullAlignmentAuditor: IAlignmentAuditor, @unchecked Sendable {
    public static let instance = NullAlignmentAuditor()
    public init() {}
    public var backendId: String { "null" }
    public func assertOkToPublish(modelId: String) async throws {}
}
