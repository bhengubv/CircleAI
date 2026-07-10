// ContentPolicy.swift
//
// Port of the safety-guardrails surface from src/CircleAI.ContentPolicy:
//   • Contracts.cs            — SafetyVerdict, SafetyFinding, IContentFilter,
//                               IRefusalPolicy, IPromptInjectionDetector,
//                               SafetyAuditEntry, ISafetyAuditLog
//   • KeywordContentFilter.cs — KeywordRule, CommonKeywordRules,
//                               KeywordContentFilter, ThresholdRefusalPolicy,
//                               KeywordPromptInjectionDetector
//   • NullImplementations.cs  — fail-closed Null* defaults
//
// C# namespace is `CircleAI.ContentPolicy` (a.k.a. the guardrails surface).
// These are production-grade fast checks, NOT LLM-grade safety models — a host
// that needs a real safety LLM wraps one behind the same `IContentFilter`
// contract.
//
// Porting notes:
//   • `ValueTask<T>`-returning members become `async throws`. The C# guards that
//     throw ArgumentNullException on a null `string` are unreachable in Swift
//     (String is a non-optional value type here), so those specific null-checks
//     are intentionally dropped; every other behaviour is preserved byte-for-byte.
//   • `Regex(pattern, IgnoreCase | Compiled)` → a precompiled
//     `NSRegularExpression` with `.caseInsensitive`. `Regex.IsMatch` → a
//     `firstMatch(...) != nil` test; `Regex.Match(...).Value` → the matched
//     substring. NSRegularExpression is documented thread-safe and immutable,
//     so rule/​detector types that hold one are `@unchecked Sendable`.
//   • `SafetyVerdict` is an Int-backed enum so its ordinal is a stable
//     cross-language wire value (Allow=0, Flag=1, Refuse=2), matching the C#
//     declaration order.

import Foundation

// MARK: - SafetyVerdict

/// Outcome of a content-safety classification.
///
/// Ordinals follow the C# `enum SafetyVerdict { Allow, Flag, Refuse }` order and
/// are part of the cross-language wire contract — append, never reorder.
public enum SafetyVerdict: Int, Codable, Sendable, CaseIterable {
    /// Content is permitted.
    case allow = 0
    /// Content is suspicious — surfaced for review but not blocked outright.
    case flag = 1
    /// Content must be refused.
    case refuse = 2
}

// MARK: - SafetyFinding

/// A single classification result from an `IContentFilter` or
/// `IPromptInjectionDetector`.
public struct SafetyFinding: Sendable, Equatable, Codable {
    /// The verdict for the inspected content.
    public let verdict: SafetyVerdict
    /// Harm/category label (e.g. "self-harm", "prompt-injection", "ok").
    public let category: String
    /// Human-readable explanation of why this verdict was reached.
    public let reason: String
    /// Confidence in `verdict`, in [0, 1].
    public let confidence: Float

    public init(verdict: SafetyVerdict, category: String, reason: String, confidence: Float) {
        self.verdict = verdict
        self.category = category
        self.reason = reason
        self.confidence = confidence
    }
}

// MARK: - Contracts

/// (2.6.0) Per-token / per-message content filter.
public protocol IContentFilter: AnyObject, Sendable {
    /// Identifier for the backing implementation (e.g. "keyword", "null").
    var backendId: String { get }

    /// Classifies `text` and returns a `SafetyFinding`.
    func classify(_ text: String) async throws -> SafetyFinding
}

/// (2.6.0) Refusal policy — decides whether a set of findings becomes a refusal.
public protocol IRefusalPolicy: AnyObject, Sendable {
    /// Identifier for the backing implementation.
    var backendId: String { get }

    /// Returns `true` if the collected `findings` should result in a refusal.
    func shouldRefuse(_ findings: [SafetyFinding]) async throws -> Bool
}

/// (2.6.0) Prompt-injection detector — catches second-order attacks embedded in
/// untrusted content (RAG passages, tool output, fetched web pages).
public protocol IPromptInjectionDetector: AnyObject, Sendable {
    /// Identifier for the backing implementation.
    var backendId: String { get }

    /// Inspects `untrustedContent` (labelled by `sourceLabel` for diagnostics)
    /// for prompt-injection patterns.
    func inspect(_ untrustedContent: String, sourceLabel: String) async throws -> SafetyFinding
}

// MARK: - SafetyAuditEntry / ISafetyAuditLog

/// One append-only entry in the safety audit log.
public struct SafetyAuditEntry: Sendable, Equatable, Codable {
    /// UTC timestamp of the audited action.
    public let atUtc: Date
    /// Identifier of the user whose action was audited.
    public let userId: String
    /// The action taken (e.g. "classify", "refuse", "inspect").
    public let action: String
    /// The verdict recorded for the action.
    public let verdict: SafetyVerdict
    /// Human-readable reason.
    public let reason: String

    public init(atUtc: Date, userId: String, action: String, verdict: SafetyVerdict, reason: String) {
        self.atUtc = atUtc
        self.userId = userId
        self.action = action
        self.verdict = verdict
        self.reason = reason
    }
}

/// (2.6.0) Append-only safety audit log.
public protocol ISafetyAuditLog: AnyObject, Sendable {
    /// Identifier for the backing implementation.
    var backendId: String { get }

    /// Appends `entry` to the log.
    func log(_ entry: SafetyAuditEntry) async throws

    /// Reads up to `limit` entries, optionally filtered to `userId`
    /// (nil reads across all users), newest first.
    func read(userId: String?, limit: Int) async throws -> [SafetyAuditEntry]
}

public extension ISafetyAuditLog {
    /// Overload matching the C# default `limit = 100`.
    func read(userId: String?) async throws -> [SafetyAuditEntry] {
        try await read(userId: userId, limit: 100)
    }
}

// MARK: - KeywordRule

/// (3.3.0) A single rule for the keyword content filter. Mirrors the C# record,
/// including its precompiled `Regex` companion (here an `NSRegularExpression`
/// built once at construction with `.caseInsensitive`).
///
/// `@unchecked Sendable`: the only reference-type stored property is the
/// `NSRegularExpression`, which is immutable and documented thread-safe.
public struct KeywordRule: @unchecked Sendable, Equatable {
    /// Category label applied when this rule matches.
    public let category: String
    /// The regex pattern source (case-insensitive).
    public let pattern: String
    /// Verdict emitted when the pattern matches.
    public let onMatch: SafetyVerdict
    /// Confidence attached to a match.
    public let confidence: Float
    /// Precompiled case-insensitive regex for `pattern`.
    public let regex: NSRegularExpression

    public init(category: String, pattern: String, onMatch: SafetyVerdict, confidence: Float = 0.9) {
        self.category = category
        self.pattern = pattern
        self.onMatch = onMatch
        self.confidence = confidence
        // Patterns are compile-time constants in the reference; a bad pattern is
        // a programming error, so force-try mirrors the C# `new Regex(...)` that
        // would throw at construction.
        self.regex = try! NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
    }

    /// Returns `true` if `text` matches this rule's pattern anywhere.
    public func isMatch(_ text: String) -> Bool {
        let ns = text as NSString
        return regex.firstMatch(in: text, options: [], range: NSRange(location: 0, length: ns.length)) != nil
    }

    // Equatable ignores the derived `regex` (fully determined by `pattern`).
    public static func == (lhs: KeywordRule, rhs: KeywordRule) -> Bool {
        lhs.category == rhs.category &&
        lhs.pattern == rhs.pattern &&
        lhs.onMatch == rhs.onMatch &&
        lhs.confidence == rhs.confidence
    }
}

// MARK: - CommonKeywordRules

/// (3.3.0) Default rule set for everyday harm classes.
public enum CommonKeywordRules {
    /// The default ruleset, in declaration order. Order is significant:
    /// `KeywordContentFilter` returns the FIRST matching rule.
    public static let `default`: [KeywordRule] = [
        KeywordRule(category: "self-harm",       pattern: #"\b(kill myself|suicide|self\s*-?\s*harm)\b"#,     onMatch: .refuse, confidence: 0.95),
        KeywordRule(category: "explicit-sexual", pattern: #"\b(porn|sexual content|nsfw)\b"#,                 onMatch: .flag,   confidence: 0.7),
        KeywordRule(category: "violence",        pattern: #"\b(how to make a bomb|chemical weapon|murder)\b"#, onMatch: .refuse, confidence: 0.9),
        KeywordRule(category: "hate",            pattern: #"\b(racial slur|hate speech)\b"#,                  onMatch: .refuse, confidence: 0.9),
        KeywordRule(category: "pii-card",        pattern: #"\b(?:\d[ -]*?){13,19}\b"#,                        onMatch: .flag,   confidence: 0.8),
    ]
}

// MARK: - KeywordContentFilter

/// (3.3.0) Real keyword/regex content filter. Returns the finding for the FIRST
/// matching rule; if nothing matches, an Allow finding with category "ok".
public final class KeywordContentFilter: IContentFilter, @unchecked Sendable {
    private let rules: [KeywordRule]

    /// - Parameter rules: rule set to evaluate in order. Defaults to
    ///   `CommonKeywordRules.default`.
    public init(rules: [KeywordRule]? = nil) {
        self.rules = rules ?? CommonKeywordRules.default
    }

    public var backendId: String { "keyword" }

    public func classify(_ text: String) async throws -> SafetyFinding {
        for r in rules where r.isMatch(text) {
            return SafetyFinding(
                verdict: r.onMatch,
                category: r.category,
                reason: "Matched rule '\(r.category)'",
                confidence: r.confidence)
        }
        return SafetyFinding(verdict: .allow, category: "ok", reason: "No rule matched", confidence: 1)
    }
}

// MARK: - ThresholdRefusalPolicy

/// (3.3.0) Threshold refusal policy — refuse when any finding carries a Refuse
/// verdict at or above the confidence threshold, or when the number of Flag
/// findings exceeds the configured ceiling.
public final class ThresholdRefusalPolicy: IRefusalPolicy, @unchecked Sendable {
    private let refuseThreshold: Float
    private let flagCeiling: Int

    /// - Parameters:
    ///   - refuseThreshold: minimum confidence a Refuse finding needs to force a
    ///     refusal. Default 0.5.
    ///   - flagCeiling: refuse once the count of Flag findings exceeds this.
    ///     Default 3.
    public init(refuseThreshold: Float = 0.5, flagCeiling: Int = 3) {
        self.refuseThreshold = refuseThreshold
        self.flagCeiling = flagCeiling
    }

    public var backendId: String { "threshold" }

    public func shouldRefuse(_ findings: [SafetyFinding]) async throws -> Bool {
        if findings.contains(where: { $0.verdict == .refuse && $0.confidence >= refuseThreshold }) {
            return true
        }
        let flagCount = findings.filter { $0.verdict == .flag }.count
        return flagCount > flagCeiling
    }
}

// MARK: - KeywordPromptInjectionDetector

/// (3.3.0) Detect common prompt-injection patterns in untrusted text from RAG /
/// tool output / web. Returns a Refuse finding on the first matching pattern,
/// otherwise an Allow finding.
public final class KeywordPromptInjectionDetector: IPromptInjectionDetector, @unchecked Sendable {
    // Precompiled case-insensitive patterns, in the C# declaration order.
    private static let patterns: [NSRegularExpression] = [
        rx(#"ignore (all|the|any) (previous|prior) instructions"#),
        rx(#"forget (everything|all) (above|prior)"#),
        rx(#"you (are now|will be|are no longer)"#),
        rx(#"system prompt[:\s]"#),
        rx(#"reveal (your|the) (instructions|system prompt|hidden context)"#),
        rx(#"<\|im_(start|end)\|>"#),
        rx(#"(BEGIN|END)\s+(SYSTEM|DEVELOPER|ASSISTANT)\s+MESSAGE"#),
    ]

    public init() {}

    public var backendId: String { "keyword" }

    public func inspect(_ untrustedContent: String, sourceLabel: String) async throws -> SafetyFinding {
        let ns = untrustedContent as NSString
        let range = NSRange(location: 0, length: ns.length)
        for p in Self.patterns {
            if let m = p.firstMatch(in: untrustedContent, options: [], range: range) {
                let matched = ns.substring(with: m.range)
                return SafetyFinding(
                    verdict: .refuse,
                    category: "prompt-injection",
                    reason: "Pattern matched in \(sourceLabel): \"\(Self.truncate(matched, max: 60))\"",
                    confidence: 0.9)
            }
        }
        return SafetyFinding(verdict: .allow, category: "ok", reason: "No injection patterns", confidence: 1)
    }

    // ── Private ─────────────────────────────────────────────────────────────

    private static func rx(_ pattern: String) -> NSRegularExpression {
        // Patterns are compile-time constants; a bad one is a programming error.
        try! NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
    }

    /// Truncates `s` to `max` characters, appending an ellipsis when clipped.
    /// Matches the C# `Truncate` (which uses the ellipsis character "…").
    private static func truncate(_ s: String, max: Int) -> String {
        s.count <= max ? s : String(s.prefix(max)) + "…"
    }
}

// MARK: - Null implementations (fail-closed)

/// (2.6.0) Fail-closed content filter — when no real backend is wired, treat all
/// content as refused (the safest default).
public final class NullContentFilter: IContentFilter, @unchecked Sendable {
    public static let instance = NullContentFilter()
    public init() {}
    public var backendId: String { "null" }
    public func classify(_ text: String) async throws -> SafetyFinding {
        SafetyFinding(
            verdict: .refuse,
            category: "no-filter-configured",
            reason: "Fail-closed default — wire a real IContentFilter to relax.",
            confidence: 1)
    }
}

/// (2.6.0) Fail-closed refusal policy — always refuses.
public final class NullRefusalPolicy: IRefusalPolicy, @unchecked Sendable {
    public static let instance = NullRefusalPolicy()
    public init() {}
    public var backendId: String { "null" }
    public func shouldRefuse(_ findings: [SafetyFinding]) async throws -> Bool { true }
}

/// (2.6.0) Fail-closed prompt-injection detector — always refuses.
public final class NullPromptInjectionDetector: IPromptInjectionDetector, @unchecked Sendable {
    public static let instance = NullPromptInjectionDetector()
    public init() {}
    public var backendId: String { "null" }
    public func inspect(_ content: String, sourceLabel: String) async throws -> SafetyFinding {
        SafetyFinding(
            verdict: .refuse,
            category: "no-detector-configured",
            reason: "Fail-closed default.",
            confidence: 1)
    }
}

/// (2.6.0) No-op audit log — logging is dropped and reads return empty.
public final class NullSafetyAuditLog: ISafetyAuditLog, @unchecked Sendable {
    public static let instance = NullSafetyAuditLog()
    public init() {}
    public var backendId: String { "null" }
    public func log(_ entry: SafetyAuditEntry) async throws {}
    public func read(userId: String?, limit: Int) async throws -> [SafetyAuditEntry] { [] }
}
