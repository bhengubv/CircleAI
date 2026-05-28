// Companion.swift
//
// InterfaceKind, CompanionContext, CompanionTurn, CompanionProactiveEvent,
// and the ICompanionSession protocol.
// The Companion is the HER + JARVIS persona — available on every surface,
// with memory and identity that travels with the person.

import Foundation

// MARK: - InterfaceKind

/// The surface on which the Companion session is running.
/// Determines sensory capabilities, available UI affordances, and
/// how the Companion adapts its communication style.
public enum InterfaceKind: String, Sendable, CaseIterable {
    /// Mobile phone or tablet (MAUI).
    case mobile
    /// Smartwatch or fitness band with a small display.
    case wearable
    /// Desktop or laptop computer (MAUI or WPF).
    case desktop
    /// Browser-based experience (Blazor).
    case web
    /// Embedded IoT device — voice in, voice out, minimal compute.
    case iot
    /// Always-on ambient surface — smart speaker, room display, car.
    case ambient
    /// Programmatic / background / testing context (no UI).
    case headless
}

// MARK: - CompanionContext

/// Snapshot of all context injected into the Companion's system prompt.
/// Rebuilt at the start of each session and refreshed on request.
public struct CompanionContext: Sendable {
    public var identityId: String
    public var displayName: String
    public var preferredLanguage: String?
    public var interface: InterfaceKind
    public var personaHints: String
    public var affectSummary: String
    public var recentMemorySnippets: [String]
    public var activeGoals: [String]
    public var contextBuiltAt: Date

    public init(
        identityId: String,
        displayName: String,
        preferredLanguage: String? = nil,
        interface: InterfaceKind,
        personaHints: String,
        affectSummary: String,
        recentMemorySnippets: [String],
        activeGoals: [String],
        contextBuiltAt: Date = Date()
    ) {
        self.identityId = identityId
        self.displayName = displayName
        self.preferredLanguage = preferredLanguage
        self.interface = interface
        self.personaHints = personaHints
        self.affectSummary = affectSummary
        self.recentMemorySnippets = recentMemorySnippets
        self.activeGoals = activeGoals
        self.contextBuiltAt = contextBuiltAt
    }
}

// MARK: - CompanionTurn

/// A single turn in the Companion conversation log, held in memory for the
/// duration of the session.
public struct CompanionTurn: Sendable {
    /// "user" or "assistant"
    public var role: String
    public var content: String
    public var timestamp: Date

    public init(role: String, content: String, timestamp: Date = Date()) {
        self.role = role
        self.content = content
        self.timestamp = timestamp
    }
}

// MARK: - CompanionProactiveEvent

/// Metadata emitted when the Companion proactively initiates contact.
public struct CompanionProactiveEvent: Sendable {
    public var sessionId: String
    public var identityId: String
    public var interface: InterfaceKind
    public var message: String
    public var triggerName: String
    public var generatedAt: Date

    public init(
        sessionId: String,
        identityId: String,
        interface: InterfaceKind,
        message: String,
        triggerName: String,
        generatedAt: Date = Date()
    ) {
        self.sessionId = sessionId
        self.identityId = identityId
        self.interface = interface
        self.message = message
        self.triggerName = triggerName
        self.generatedAt = generatedAt
    }
}

// MARK: - FaceAffectMapper

/// Applies facial expression observations to an AffectState.
/// Called once per detected frame; the AffectState is mutated in-place.
///
/// Exact deltas (must match fixtures/facex_biometric_vectors.json):
///   happy:     engagement += 0.03, energy     += 0.02
///   surprised: curiosity  += 0.04
///   confused:  uncertainty += 0.05
///   stressed:  uncertainty += 0.08, energy    -= 0.05
///   angry:     engagement  -= 0.04, rapport   -= 0.02
///   all others: no change
///
/// Frames with confidenceScore < 0.5 are discarded entirely.
public enum FaceAffectMapper {

    /// Applies the expression delta from matrix to affect.
    /// No-op when matrix.confidenceScore < 0.5.
    public static func apply(_ matrix: FacialMetricMatrix, to affect: AffectState) {
        guard matrix.confidenceScore >= 0.5 else { return }
        switch matrix.expression {
        case .happy:
            affect.engagement = min(1.0, affect.engagement + 0.03)
            affect.energy     = min(1.0, affect.energy     + 0.02)
        case .surprised:
            affect.curiosity  = min(1.0, affect.curiosity  + 0.04)
        case .confused:
            affect.uncertainty = min(1.0, affect.uncertainty + 0.05)
        case .stressed:
            affect.uncertainty = min(1.0, affect.uncertainty + 0.08)
            affect.energy      = max(0.0, affect.energy      - 0.05)
        case .angry:
            affect.engagement = max(0.0, affect.engagement - 0.04)
            affect.rapport    = max(0.0, affect.rapport    - 0.02)
        default:
            return
        }
        affect.lastUpdatedAt = Date()
    }
}

// MARK: - FaceCompanionBridge

/// Bridges the face analysis pipeline to the Companion session.
/// Applies affect updates and emits proactive events when confusion is detected.
public enum FaceCompanionBridge {

    /// Uncertainty threshold above which a confusion proactive event is emitted.
    public static let confusionThreshold: Float = 0.70

    /// Observes a facial metric matrix, applies it to the affect state,
    /// and returns a proactive event if the confusion threshold is crossed
    /// while the expression is .confused or .stressed.
    ///
    /// - Returns: A `CompanionProactiveEvent` when the confusion threshold is
    ///   met, otherwise nil.
    public static func observe(
        _ matrix: FacialMetricMatrix,
        affect: AffectState,
        sessionId: String,
        identityId: String,
        surface: InterfaceKind
    ) -> CompanionProactiveEvent? {
        FaceAffectMapper.apply(matrix, to: affect)
        let crossed = affect.uncertainty >= confusionThreshold &&
            (matrix.expression == .confused || matrix.expression == .stressed)
        guard crossed else { return nil }
        return CompanionProactiveEvent(
            sessionId: sessionId,
            identityId: identityId,
            interface: surface,
            message: "I notice you might be finding this a bit tricky. Would you like me to slow down or explain it differently?",
            triggerName: "face.confusion_detected",
            generatedAt: Date()
        )
    }
}

// MARK: - ICompanionSession

/// A Companion conversation session. Combines identity awareness, cross-device
/// memory, language adaptation, affect sensing, and proactive reasoning into a
/// single coherent interface.
public protocol ICompanionSession: AnyObject {

    // ── Identity ──────────────────────────────────────────────────────────

    /// Stable unique identifier for this session.
    var sessionId: String { get }

    /// The authenticated identity driving this session.
    var identityId: String { get }

    /// The surface on which this session is running.
    var interface: InterfaceKind { get }

    // ── Core conversation ─────────────────────────────────────────────────

    /// Send a message to the Companion and receive a complete reply.
    func send(_ message: String) async throws -> String

    /// Stream the Companion's reply token-by-token for low-latency rendering.
    func stream(_ message: String) -> AsyncStream<String>

    /// Agentic mode: sends the instruction, detects tool calls, executes them,
    /// and re-prompts until the model produces a plain-text answer.
    func agent(_ instruction: String) async throws -> String

    // ── Context ───────────────────────────────────────────────────────────

    /// Returns the most recent CompanionContext snapshot.
    func getContext() -> CompanionContext

    /// Refreshes the context from backing stores.
    func refreshContext() async throws

    // ── History ───────────────────────────────────────────────────────────

    /// The in-session conversation history (this session only, not persisted).
    var history: [CompanionTurn] { get }

    // ── Feedback ──────────────────────────────────────────────────────────

    /// Signal satisfaction with the last reply.
    func signalFeedback(positive: Bool, note: String?) async throws

    // ── Proactive ─────────────────────────────────────────────────────────

    /// Stream of proactive events the Companion initiates without being prompted.
    var proactiveEvents: AsyncStream<CompanionProactiveEvent> { get }
}
