// ContextWindowBudgetManager.swift
//
// Tracks token usage against a fixed context window and signals when the KV
// cache should be partially evicted to keep inference latency manageable.
// Ported from CircleAI.Inference.ContextWindowBudgetManager.

import Foundation

/// Errors raised when constructing or driving a `ContextWindowBudgetManager`.
public enum ContextWindowBudgetError: Error, Equatable, CustomStringConvertible {
    case contextSizeNotPositive
    case evictionThresholdOutOfRange
    case negativeTokenCount
    case targetFillRatioOutOfRange

    public var description: String {
        switch self {
        case .contextSizeNotPositive: return "Context size must be greater than zero."
        case .evictionThresholdOutOfRange: return "Eviction threshold must be in the range [0, 1]."
        case .negativeTokenCount: return "Token counts must not be negative."
        case .targetFillRatioOutOfRange: return "Target fill ratio must be in the range [0, 1]."
        }
    }
}

/// Tracks token usage against a fixed context window and signals when the KV
/// cache should be partially evicted.
public final class ContextWindowBudgetManager: @unchecked Sendable {
    private let lock = NSLock()
    private var usedTokens: Int = 0

    /// Maximum number of tokens the model's context window can hold.
    public let contextSize: Int

    /// Fill ratio at or above which `shouldEvict` becomes `true`.
    public let evictionThreshold: Double

    /// Cumulative tokens consumed so far (prompt + completion).
    public var used: Int {
        lock.lock(); defer { lock.unlock() }
        return usedTokens
    }

    /// Tokens still available before the context window is full.
    public var remainingTokens: Int {
        lock.lock(); defer { lock.unlock() }
        return contextSize - usedTokens
    }

    /// Proportion of the context window currently occupied (0-1).
    public var fillRatio: Double {
        lock.lock(); defer { lock.unlock() }
        return Double(usedTokens) / Double(contextSize)
    }

    /// `true` when the fill ratio has reached or exceeded `evictionThreshold`.
    public var shouldEvict: Bool {
        lock.lock(); defer { lock.unlock() }
        return Double(usedTokens) / Double(contextSize) >= evictionThreshold
    }

    /// Initialises a new budget manager.
    ///
    /// - Parameters:
    ///   - contextSize: total context window size in tokens. Must be > 0.
    ///   - evictionThreshold: fill ratio (0-1) that triggers eviction. Default 0.85.
    public init(contextSize: Int, evictionThreshold: Double = 0.85) throws {
        guard contextSize > 0 else { throw ContextWindowBudgetError.contextSizeNotPositive }
        guard evictionThreshold >= 0.0 && evictionThreshold <= 1.0 else {
            throw ContextWindowBudgetError.evictionThresholdOutOfRange
        }
        self.contextSize = contextSize
        self.evictionThreshold = evictionThreshold
    }

    /// Records the token cost of one exchange (a prompt + its completion).
    public func recordExchange(promptTokens: Int, completionTokens: Int) throws {
        guard promptTokens >= 0, completionTokens >= 0 else {
            throw ContextWindowBudgetError.negativeTokenCount
        }
        lock.lock(); defer { lock.unlock() }
        usedTokens += promptTokens + completionTokens
    }

    /// Calculates how many of the oldest tokens should be dropped so that
    /// `fillRatio` returns to `targetFillRatio`. Returns 0 when already at or
    /// below the target.
    public func calculateEvictionCount(targetFillRatio: Double = 0.50) throws -> Int {
        guard targetFillRatio >= 0.0 && targetFillRatio <= 1.0 else {
            throw ContextWindowBudgetError.targetFillRatioOutOfRange
        }
        lock.lock(); defer { lock.unlock() }
        let targetUsed = Int(Double(contextSize) * targetFillRatio)
        let evict = usedTokens - targetUsed
        return evict > 0 ? evict : 0
    }

    /// Resets the used-token counter to zero. Call after clearing the KV cache.
    public func reset() {
        lock.lock(); defer { lock.unlock() }
        usedTokens = 0
    }
}
