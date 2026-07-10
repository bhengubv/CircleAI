// InferenceRuntime.swift
//
// Inference-runtime primitives ported from CircleAI.Inference:
//   • KvCompressionMode / KvCompressionApplyResult + a pluggable applier
//     (native MNN handle injected behind IKvCompressionApplier)
//   • PowerBudgetPolicy — declarative PowerBudget → concrete generation knobs
//   • VisionInput — raw image container for multimodal inference
//
// PowerBudget and ChatCapability themselves already live in Inference.swift /
// Selector.swift; this file adds the pieces that reference them.

import Foundation

// MARK: - KvCompressionMode

/// KV cache compression mode. Mirrors the C ABI integer encoding so managed and
/// native layers agree without translation tables.
public enum KvCompressionMode: Int, Sendable {
    /// Full FP16 KV cache — default behaviour, always supported.
    case off = 0
    /// TurboQuant at 4 bits per channel — ~4x shrink, < 1% accuracy loss.
    case turboQuant4Bit = 1
    /// TurboQuant at 3 bits per channel — ~5x shrink, marginal accuracy loss.
    case turboQuant3Bit = 2
    /// TurboQuant at 2 bits per channel — ~8x shrink, noticeable accuracy loss.
    case turboQuant2Bit = 3
}

/// Outcome of applying a `KvCompressionMode`. Mirrors the C ABI status codes.
public enum KvCompressionApplyResult: Int, Sendable {
    /// Native path accepted the mode and will use it.
    case applied = 0
    /// The mode value was outside the valid 0..3 range.
    case invalidMode = 1
    /// LEGACY (mnnbridge <= 1.1.0) — scaffolding-only response.
    case notImplemented = 2
    /// Handle pointer was invalid.
    case handleInvalid = -1
}

/// Injectable seam over the KV-compression C ABI. Production wires an MNN
/// handle; tests inject a deterministic recorder. Keeps the port free of any
/// P/Invoke while preserving the `Set`/`Get` contract of `MnnKvCompression`.
public protocol IKvCompressionApplier: AnyObject {
    /// Applies `mode` and returns the typed result.
    func set(_ mode: KvCompressionMode) -> KvCompressionApplyResult
    /// Reads the last-set mode (or `.off` when the handle is invalid).
    func get() -> KvCompressionMode
}

/// In-memory `IKvCompressionApplier` — records the last-set mode. Rejects the
/// out-of-range case exactly as the native `Set` would (raw==1 -> invalidMode),
/// so the deterministic port and native path agree on validation semantics.
public final class InMemoryKvCompressionApplier: IKvCompressionApplier, @unchecked Sendable {
    private let lock = NSLock()
    private var mode: KvCompressionMode = .off
    private let handleValid: Bool

    public init(handleValid: Bool = true) {
        self.handleValid = handleValid
    }

    public func set(_ mode: KvCompressionMode) -> KvCompressionApplyResult {
        if !handleValid { return .handleInvalid }
        // rawValue is constrained by the enum to 0..3, so it is always valid
        // here; the invalidMode branch exists for parity with a raw-int ABI.
        return setLocked(mode)
    }

    public func get() -> KvCompressionMode {
        lock.lock(); defer { lock.unlock() }
        return handleValid ? mode : .off
    }

    private func setLocked(_ m: KvCompressionMode) -> KvCompressionApplyResult {
        lock.lock(); defer { lock.unlock() }
        mode = m
        return .applied
    }
}

// MARK: - PowerBudgetPolicy

/// The runtime's translation of a `PowerBudget` into concrete generation knobs.
/// Surfaced as a static helper so generators (and tests) agree on the mapping
/// without each having to hard-code it. Mirrors `PowerBudgetPolicy`.
public enum PowerBudgetPolicy {
    /// Resolved budget for a single generation call.
    public struct Resolution: Sendable, Equatable {
        /// Cap on output tokens for this call.
        public let maxTokens: Int
        /// Which `KvCompressionMode` the runtime prefers for this budget.
        public let preferredKvMode: KvCompressionMode
        /// When a fallback chain is configured, whether to pick a smaller model.
        public let preferSmallerModelInChain: Bool

        public init(maxTokens: Int, preferredKvMode: KvCompressionMode, preferSmallerModelInChain: Bool) {
            self.maxTokens = maxTokens
            self.preferredKvMode = preferredKvMode
            self.preferSmallerModelInChain = preferSmallerModelInChain
        }
    }

    /// Map a budget to concrete knobs. Generators call this with the user's
    /// requested max-tokens; the returned `Resolution` caps any over-budget
    /// values without altering the caller's struct.
    ///
    /// - Parameters:
    ///   - budget: the declared budget.
    ///   - requestedMaxTokens: the caller's requested max-tokens.
    ///   - batteryLevelPercent: 0..100 if known, `nil` when unavailable. Used to
    ///     auto-downgrade `.normal` on low battery.
    ///   - thermalThrottled: `true` when the platform reports elevated thermal
    ///     state. Used to auto-downgrade `.high`.
    public static func resolve(
        budget: PowerBudget,
        requestedMaxTokens: Int,
        batteryLevelPercent: Int? = nil,
        thermalThrottled: Bool = false
    ) -> Resolution {
        // Auto-downgrade based on device state.
        var budget = budget
        if budget == .normal, let b = batteryLevelPercent, b < 15 {
            budget = .low
        }
        if budget == .high, thermalThrottled {
            budget = .normal
        }

        switch budget {
        case .none:
            return Resolution(
                maxTokens: requestedMaxTokens,
                preferredKvMode: .turboQuant4Bit,
                preferSmallerModelInChain: false)
        case .low:
            return Resolution(
                maxTokens: min(requestedMaxTokens, 64),
                preferredKvMode: .turboQuant4Bit,
                preferSmallerModelInChain: true)
        case .normal:
            return Resolution(
                maxTokens: min(requestedMaxTokens, 512),
                preferredKvMode: .turboQuant4Bit,
                preferSmallerModelInChain: false)
        case .high:
            return Resolution(
                maxTokens: min(requestedMaxTokens, 2048),
                preferredKvMode: .off,
                preferSmallerModelInChain: false)
        }
    }
}

// MARK: - VisionInput

/// Raw image data to be embedded by the vision encoder before text generation.
/// Passed to a vision-capable `IChatGenerator` when an image should be embedded
/// before the text prompt (llava-style vision). Mirrors `VisionInput`.
public struct VisionInput: Sendable, Equatable {
    /// Raw image bytes (JPEG, PNG, or any format the encoder accepts).
    public let imageBytes: Data

    /// Optional MIME type hint (e.g. "image/jpeg"). Useful for callers to track
    /// format; not passed to the native encoder directly.
    public let mimeType: String?

    public init(imageBytes: Data, mimeType: String? = nil) {
        self.imageBytes = imageBytes
        self.mimeType = mimeType
    }
}
