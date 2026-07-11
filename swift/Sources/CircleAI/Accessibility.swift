// Accessibility.swift
//
// Port of the Accessibility vertical from
// src/CircleAI.Accessibility/AccessibilityPrimitives.cs and the static
// domain-context constants from AccessibilityDomainContext.cs:
//   • AccessibilityNeed                                  — Visual…Speech
//   • UserAccessibilityProfile, AdaptationHint           — domain records
//   • IAccessibilityBoard                                — profiles + hint derivation
//   • InMemoryAccessibilityBoard                         — deterministic in-memory impl
//   • AccessibilityDomainContext                         — system-prompt snippet + flags
//
// The Companion-facing wrapper (AccessibilityCompanionAdapter) is an
// ICompanionSession decorator that prefixes the accessibility domain prompt.
//
// Porting notes:
//   • `HintsFor` returns [] when no profile; otherwise, in order:
//       – ("contrast","high")  when HighContrast
//       – ("motion","reduced") when ReducedMotion
//       – ("aria","verbose")   when ScreenReader
//       – ("text-scale", TextScale formatted to 2 decimals) when TextScale > 1
//       – ("need", need name) for each need in the profile's order
//     Enum names ("Visual", "Hearing", …) match C# `Enum.ToString()`.
//   • Profile storage guarded by a single `NSLock`.

import Foundation

// MARK: - Enums

/// A category of accessibility need.
public enum AccessibilityNeed: String, Sendable, Equatable, Codable, CaseIterable {
    case visual = "Visual"
    case hearing = "Hearing"
    case motor = "Motor"
    case cognitive = "Cognitive"
    case speech = "Speech"
}

// MARK: - Records

/// A user's accessibility profile.
public struct UserAccessibilityProfile: Sendable, Equatable, Codable {
    public let userId: String
    public let needs: [AccessibilityNeed]
    public let textScale: Double
    public let highContrast: Bool
    public let reducedMotion: Bool
    public let screenReader: Bool

    public init(userId: String, needs: [AccessibilityNeed], textScale: Double, highContrast: Bool, reducedMotion: Bool, screenReader: Bool) {
        self.userId = userId
        self.needs = needs
        self.textScale = textScale
        self.highContrast = highContrast
        self.reducedMotion = reducedMotion
        self.screenReader = screenReader
    }
}

/// A derived UI adaptation hint (kind → value).
public struct AdaptationHint: Sendable, Equatable, Codable {
    public let kind: String
    public let value: String

    public init(kind: String, value: String) {
        self.kind = kind
        self.value = value
    }
}

// MARK: - Contract

/// Accessibility profiles and derived adaptation hints.
public protocol IAccessibilityBoard: AnyObject, Sendable {
    func setProfile(_ p: UserAccessibilityProfile)
    func getProfile(userId: String) -> UserAccessibilityProfile?
    func hintsFor(userId: String) -> [AdaptationHint]
}

// MARK: - InMemoryAccessibilityBoard

/// Deterministic in-memory `IAccessibilityBoard`. All state guarded by a single `NSLock`.
public final class InMemoryAccessibilityBoard: IAccessibilityBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var profiles: [String: UserAccessibilityProfile] = [:]

    public init() {}

    public func setProfile(_ p: UserAccessibilityProfile) {
        lock.lock(); defer { lock.unlock() }
        profiles[p.userId] = p
    }

    public func getProfile(userId: String) -> UserAccessibilityProfile? {
        lock.lock(); defer { lock.unlock() }
        return profiles[userId]
    }

    public func hintsFor(userId: String) -> [AdaptationHint] {
        lock.lock(); defer { lock.unlock() }
        guard let p = profiles[userId] else { return [] }
        var hints: [AdaptationHint] = []
        if p.highContrast { hints.append(AdaptationHint(kind: "contrast", value: "high")) }
        if p.reducedMotion { hints.append(AdaptationHint(kind: "motion", value: "reduced")) }
        if p.screenReader { hints.append(AdaptationHint(kind: "aria", value: "verbose")) }
        if p.textScale > 1 { hints.append(AdaptationHint(kind: "text-scale", value: Self.format2(p.textScale))) }
        for n in p.needs { hints.append(AdaptationHint(kind: "need", value: n.rawValue)) }
        return hints
    }

    /// Formats a value to 2 decimal places, mirroring C# `ToString("F2")`
    /// (invariant culture, round-half-away-from-zero).
    private static func format2(_ v: Double) -> String {
        String(format: "%.2f", v)
    }
}

// MARK: - AccessibilityDomainContext

/// Static domain-context constants for the accessibility vertical.
public enum AccessibilityDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Accessibility] Expert accessibility and inclusive design assistant. Help with WCAG 2.2 compliance audits, screen reader compatibility, alternative text guidance, disability accommodation requests, and assistive technology selection. Always centre the lived experience of disabled users. Compliance: WCAG 2.2, UNCRPD, SA Promotion of Equality Act, POPIA."
    public static let complianceFlags: [String] = ["WCAG_2_2", "UNCRPD", "Equality_Act", "POPIA"]
    public static let suggestedTools: [String] = ["screen_reader_test", "document_editor", "web_audit", "analytics"]
}

// MARK: - AccessibilityCompanionAdapter

/// An `ICompanionSession` decorator that prepends the accessibility domain
/// system prompt to every conversational call and adds accessibility helper
/// methods. Port of `CircleAI.Accessibility.AccessibilityCompanionAdapter`.
/// Identity/context/feedback are forwarded to the inner session; proactive
/// events forward through the inner session's `proactiveEvents` stream (the
/// Swift protocol has no disposal).
///
/// C# overloads `AuditWcagAsync` (a 1-arg and a 2-arg-with-default form); both
/// are ported, disambiguated by argument label (`htmlOrDescription:` vs
/// `content:targetLevel:`). C# default parameter values (`targetLevel = "AA"`,
/// `readingAge = "plain English"`) become Swift default argument values.
public final class AccessibilityCompanionAdapter: ICompanionSession, @unchecked Sendable {
    private let inner: ICompanionSession

    public init(_ inner: ICompanionSession) {
        self.inner = inner
    }

    public var sessionId: String { inner.sessionId }
    public var identityId: String { inner.identityId }
    public var interface: InterfaceKind { inner.interface }
    public var history: [CompanionTurn] { inner.history }

    public func getContext() -> CompanionContext { inner.getContext() }
    public func refreshContext() async throws { try await inner.refreshContext() }
    public func signalFeedback(positive: Bool, note: String?) async throws {
        try await inner.signalFeedback(positive: positive, note: note)
    }
    public var proactiveEvents: AsyncStream<CompanionProactiveEvent> { inner.proactiveEvents }

    public func send(_ message: String) async throws -> String { try await inner.send(enrich(message)) }
    public func stream(_ message: String) -> AsyncStream<String> { inner.stream(enrich(message)) }
    public func agent(_ instruction: String) async throws -> String { try await inner.agent(enrich(instruction)) }

    private func enrich(_ m: String) -> String { "\(AccessibilityDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Accessibility helpers ─────────────────────────────────────────────────

    /// Audit an interface for WCAG 2.2 AA (C# `AuditWcagAsync` — single arg).
    public func auditWcag(htmlOrDescription: String) async throws -> String {
        try await inner.agent(
            "Audit this interface for WCAG 2.2 AA compliance. Identify violations, their impact on disabled users, and remediation steps:\n\(htmlOrDescription)")
    }

    /// Write descriptive alt text (C# `WriteAltTextAsync`).
    public func writeAltText(imageDescription: String, context: String) async throws -> String {
        try await inner.agent(
            "Write descriptive alt text for an image. Image: \(imageDescription). Context: \(context). Follow WCAG 2.2 alt text best practices.")
    }

    /// Audit content at a target level (C# `AuditWcagAsync` — content + targetLevel).
    public func auditWcag(content: String, targetLevel: String = "AA") async throws -> String {
        try await inner.agent(
            "Audit this content/UI for WCAG 2.2 \(targetLevel) compliance: \(content). List violations by criterion id, severity, and a concrete fix.")
    }

    /// Describe an image for a screen reader (C# `DescribeImageForScreenReaderAsync`).
    public func describeImageForScreenReader(imageContext: String) async throws -> String {
        try await inner.agent(
            "Write a screen-reader alt-text for the image. Context: \(imageContext). Aim for 1-2 sentences, no 'image of', present tense.")
    }

    /// Simplify language to a reading level (C# `SimplifyLanguageAsync`).
    public func simplifyLanguage(text: String, readingAge: String = "plain English") async throws -> String {
        try await inner.agent(
            "Rewrite this for \(readingAge): \(text). Keep the meaning, drop jargon, use short sentences.")
    }

    /// Suggest an accessible keyboard shortcut (C# `SuggestKeyboardShortcutAsync`).
    public func suggestKeyboardShortcut(action: String, platform: String) async throws -> String {
        try await inner.agent(
            "Suggest an accessible keyboard shortcut for '\(action)' on \(platform). Avoid chords that conflict with screen-reader defaults.")
    }
}
