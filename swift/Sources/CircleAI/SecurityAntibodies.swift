// SecurityAntibodies.swift
//
// Defensive-only threat awareness: is this file, this link, this address of
// mine, known bad? Every capability sits behind an authorized-use gate that
// DENIES BY DEFAULT, and nothing here reaches the network - lookups go to a
// corpus the device already holds.
//
// Ported from src/CircleAI.Security.Antibodies.
//
// NAMING: Security.Defense already owns IndicatorKind, IndicatorMatch and
// ThreatSeverity with different members. Swift has no namespaces, so these are
// AntibodyIndicatorKind, AntibodyIndicatorMatch and DefensiveThreatSeverity.

import Foundation
import CryptoKit

// MARK: - The gate

/// What an antibody is allowed to do. Nothing broader exists on purpose: the
/// set is small, named, and each member is a defensive question about the
/// device owner, never an action against someone else.
public enum AntibodyCapability: Int, Sendable, Equatable, Hashable, CaseIterable {
    case fileReputationAwareness = 0
    case networkIndicatorAwareness
    case breachExposureAwareness

    public var name: String {
        switch self {
        case .fileReputationAwareness: return "FileReputationAwareness"
        case .networkIndicatorAwareness: return "NetworkIndicatorAwareness"
        case .breachExposureAwareness: return "BreachExposureAwareness"
        }
    }
}

/// How serious the situation that prompted the request is.
public enum DefensiveThreatSeverity: Int, Sendable, Equatable, Comparable {
    case informational = 0
    case elevated
    case high
    case critical

    public static func < (a: DefensiveThreatSeverity, b: DefensiveThreatSeverity) -> Bool {
        a.rawValue < b.rawValue
    }
}

/// The defined threat an antibody runs under. There is no way to ask for one of
/// these capabilities without naming a threat - that is the point.
public struct DefensiveThreatContext: Sendable, Equatable {
    public let reason: String
    public let severity: DefensiveThreatSeverity
    public let raisedBy: String
    public let raisedAtUtc: Date
    public let correlationId: UUID

    public init(reason: String, severity: DefensiveThreatSeverity, raisedBy: String,
                raisedAtUtc: Date, correlationId: UUID) {
        self.reason = reason
        self.severity = severity
        self.raisedBy = raisedBy
        self.raisedAtUtc = raisedAtUtc
        self.correlationId = correlationId
    }

    /// Fails rather than inventing a reason: an empty justification is exactly
    /// the case this gate exists to refuse.
    public static func raise(reason: String, severity: DefensiveThreatSeverity,
                             raisedBy: String, now: Date = Date()) -> DefensiveThreatContext? {
        guard !reason.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !raisedBy.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        return DefensiveThreatContext(reason: reason, severity: severity, raisedBy: raisedBy,
                                      raisedAtUtc: now, correlationId: UUID())
    }
}

/// One ask: this capability, under this threat, for this stated reason.
public struct AuthorizedUseRequest: Sendable, Equatable {
    public let requestId: UUID
    public let capability: AntibodyCapability
    public let threat: DefensiveThreatContext
    public let justification: String
    public let requestedAtUtc: Date

    public init(requestId: UUID, capability: AntibodyCapability, threat: DefensiveThreatContext,
                justification: String, requestedAtUtc: Date) {
        self.requestId = requestId
        self.capability = capability
        self.threat = threat
        self.justification = justification
        self.requestedAtUtc = requestedAtUtc
    }

    public static func again(_ capability: AntibodyCapability, threat: DefensiveThreatContext,
                             justification: String, now: Date = Date()) -> AuthorizedUseRequest? {
        guard !justification.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        return AuthorizedUseRequest(requestId: UUID(), capability: capability, threat: threat,
                                    justification: justification, requestedAtUtc: now)
    }
}

/// The answer, always with a reason a person can read.
public struct AuthorizationDecision: Sendable, Equatable {
    public let requestId: UUID
    public let capability: AntibodyCapability
    public let granted: Bool
    public let reason: String
    public let decidedAtUtc: Date
    public let expiresAtUtc: Date?

    public init(requestId: UUID, capability: AntibodyCapability, granted: Bool, reason: String,
                decidedAtUtc: Date, expiresAtUtc: Date?) {
        self.requestId = requestId
        self.capability = capability
        self.granted = granted
        self.reason = reason
        self.decidedAtUtc = decidedAtUtc
        self.expiresAtUtc = expiresAtUtc
    }

    public static func deny(_ request: AuthorizedUseRequest, reason: String,
                            now: Date = Date()) -> AuthorizationDecision {
        AuthorizationDecision(requestId: request.requestId, capability: request.capability,
                              granted: false, reason: reason, decidedAtUtc: now, expiresAtUtc: nil)
    }

    public static func grant(_ request: AuthorizedUseRequest, reason: String,
                             expiresAtUtc: Date? = nil, now: Date = Date()) -> AuthorizationDecision {
        AuthorizationDecision(requestId: request.requestId, capability: request.capability,
                              granted: true, reason: reason, decidedAtUtc: now, expiresAtUtc: expiresAtUtc)
    }
}

/// A consent somebody actually gave, for one capability, for a bounded time.
public struct AuthorizedUseConsent: Sendable, Equatable {
    public let consentId: UUID
    public let capability: AntibodyCapability
    public let grantedBy: String
    public let scope: String
    public let grantedAtUtc: Date
    public let expiresAtUtc: Date

    public init(consentId: UUID, capability: AntibodyCapability, grantedBy: String,
                scope: String, grantedAtUtc: Date, expiresAtUtc: Date) {
        self.consentId = consentId
        self.capability = capability
        self.grantedBy = grantedBy
        self.scope = scope
        self.grantedAtUtc = grantedAtUtc
        self.expiresAtUtc = expiresAtUtc
    }

    /// Half-open: active from the moment it was granted, dead the instant it
    /// expires. An expired consent is exactly as good as no consent.
    public func isActive(for capability: AntibodyCapability, now: Date) -> Bool {
        self.capability == capability && now >= grantedAtUtc && now < expiresAtUtc
    }

    /// A consent with no end date is not a consent, so a non-positive duration
    /// is refused rather than silently made permanent.
    public static func grant(_ capability: AntibodyCapability, grantedBy: String, scope: String,
                             duration: TimeInterval, now: Date = Date()) -> AuthorizedUseConsent? {
        guard !grantedBy.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !scope.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              duration > 0 else { return nil }
        return AuthorizedUseConsent(consentId: UUID(), capability: capability, grantedBy: grantedBy,
                                    scope: scope, grantedAtUtc: now,
                                    expiresAtUtc: now.addingTimeInterval(duration))
    }
}

public protocol IAuthorizedUseConsentStore: Sendable {
    func findActiveConsent(_ capability: AntibodyCapability, now: Date) async -> AuthorizedUseConsent?
}

public final class InMemoryAuthorizedUseConsentStore: IAuthorizedUseConsentStore, @unchecked Sendable {
    private let lock = NSLock()
    private var consents: [AntibodyCapability: AuthorizedUseConsent] = [:]

    public init() {}

    public func record(_ consent: AuthorizedUseConsent) {
        lock.lock(); consents[consent.capability] = consent; lock.unlock()
    }

    public func revoke(_ capability: AntibodyCapability) {
        lock.lock(); consents.removeValue(forKey: capability); lock.unlock()
    }

    public func revokeAll() {
        lock.lock(); consents.removeAll(); lock.unlock()
    }

    // The critical section is a plain synchronous read, kept out of the async
    // function so the lock never spans a suspension point.
    private func stored(_ capability: AntibodyCapability) -> AuthorizedUseConsent? {
        lock.lock(); defer { lock.unlock() }
        return consents[capability]
    }

    public func findActiveConsent(_ capability: AntibodyCapability, now: Date) async -> AuthorizedUseConsent? {
        guard let c = stored(capability), c.isActive(for: capability, now: now) else { return nil }
        return c
    }
}

public protocol IAuthorizedUseGate: Sendable {
    func requestAuthorization(_ request: AuthorizedUseRequest) async -> AuthorizationDecision
}

/// The default gate. It cannot grant anything - a host must deliberately wire
/// one that can, which is what makes "deny by default" a property of the build
/// rather than a promise in a comment.
public struct NullAuthorizedUseGate: IAuthorizedUseGate {
    public static let denialReason =
        "No authorized-use gate is configured. Antibodies are denied by default; " +
        "a host must explicitly wire a gate that can grant before any antibody can run."

    public static let instance = NullAuthorizedUseGate()
    public init() {}

    public func requestAuthorization(_ request: AuthorizedUseRequest) async -> AuthorizationDecision {
        AuthorizationDecision.deny(request, reason: Self.denialReason)
    }
}

/// Grants only against a recorded, unexpired consent - and only when a real
/// threat accompanies the request.
public struct ExplicitConsentAuthorizedUseGate: IAuthorizedUseGate {
    private let consents: any IAuthorizedUseConsentStore
    private let clock: @Sendable () -> Date

    public init(consents: any IAuthorizedUseConsentStore,
                clock: @escaping @Sendable () -> Date = { Date() }) {
        self.consents = consents
        self.clock = clock
    }

    public func requestAuthorization(_ request: AuthorizedUseRequest) async -> AuthorizationDecision {
        let now = clock()

        // No threat, no antibody. A capability asked for "just to check" is the
        // one this whole module is built to refuse.
        guard !request.threat.reason.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return .deny(request,
                reason: "No defined threat accompanies the request; antibodies run only under a defined threat.",
                now: now)
        }

        guard let consent = await consents.findActiveConsent(request.capability, now: now) else {
            return .deny(request,
                reason: "No active authorized-use consent for \(request.capability.name); denied by default.",
                now: now)
        }

        return .grant(request,
            reason: "Authorized by consent \(consent.consentId) (granted by \(consent.grantedBy)).",
            expiresAtUtc: consent.expiresAtUtc, now: now)
    }
}

// MARK: - Indicators

/// What sort of thing is being asked about.
public enum AntibodyIndicatorKind: Int, Sendable, Equatable, Hashable {
    case fileHashSha256 = 0
    case url
    case ipAddress
    case domainName
    case emailAddress
    case username
    case phoneNumber
}

public struct ThreatIndicator: Sendable, Equatable {
    public let kind: AntibodyIndicatorKind
    public let value: String
    public init(kind: AntibodyIndicatorKind, value: String) {
        self.kind = kind
        self.value = value
    }
}

/// A link, address or hostname to ask about.
public struct NetworkIndicator: Sendable, Equatable {
    public let kind: AntibodyIndicatorKind
    public let value: String
    public init(kind: AntibodyIndicatorKind, value: String) {
        self.kind = kind
        self.value = value
    }
    public static func forUrl(_ url: String) -> NetworkIndicator? {
        url.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? nil : NetworkIndicator(kind: .url, value: url)
    }
    public static func forIp(_ ip: String) -> NetworkIndicator? {
        ip.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? nil : NetworkIndicator(kind: .ipAddress, value: ip)
    }
    public static func forDomain(_ domain: String) -> NetworkIndicator? {
        domain.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? nil : NetworkIndicator(kind: .domainName, value: domain)
    }
}

/// One of the device owner-s own identities. This is only ever used to tell
/// somebody about their OWN exposure - never to look anybody else up.
public struct IdentityIndicator: Sendable, Equatable {
    public let kind: AntibodyIndicatorKind
    public let value: String
    public init(kind: AntibodyIndicatorKind, value: String) {
        self.kind = kind
        self.value = value
    }
    public static func email(_ email: String) -> IdentityIndicator? {
        email.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? nil : IdentityIndicator(kind: .emailAddress, value: email)
    }
    public static func username(_ username: String) -> IdentityIndicator? {
        username.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? nil : IdentityIndicator(kind: .username, value: username)
    }
    public static func phone(_ phone: String) -> IdentityIndicator? {
        phone.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? nil : IdentityIndicator(kind: .phoneNumber, value: phone)
    }
}

/// A file, identified by its hash rather than its contents.
public struct FileArtifact: Sendable, Equatable {
    public let fileName: String
    public let sha256Hex: String
    public let sizeBytes: Int64

    public init(fileName: String, sha256Hex: String, sizeBytes: Int64) {
        self.fileName = fileName
        self.sha256Hex = sha256Hex
        self.sizeBytes = sizeBytes
    }

    public static func fromContent(fileName: String, content: Data) -> FileArtifact? {
        guard !fileName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        return FileArtifact(fileName: fileName,
                            sha256Hex: IndicatorNormalizer.sha256HexLower(content),
                            sizeBytes: Int64(content.count))
    }
}

/// Canonical forms. Everything looked up goes through here so that "WWW.X.COM"
/// and "x.com." are the same question, and so that an identity is HASHED before
/// it is looked up - the corpus never holds the address itself.
enum IndicatorNormalizer {

    static func sha256HexLower(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    static func sha256HexLower(_ value: String) -> String {
        sha256HexLower(Data(value.utf8))
    }

    static func normalizeNetwork(_ kind: AntibodyIndicatorKind, _ value: String) -> String? {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty { return nil }
        var v = trimmed.lowercased()
        if kind == .domainName && v.hasPrefix("www.") { v = String(v.dropFirst(4)) }
        return v
    }

    /// A phone number keeps a LEADING plus and its digits, and nothing else, so
    /// "+27 82 555 0142" and "+27825550142" hash the same.
    static func normalizeIdentityToHash(_ kind: AntibodyIndicatorKind, _ value: String) -> String? {
        if value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return nil }

        let canonical: String
        if kind == .phoneNumber {
            var out = ""
            var leadingPlusAllowed = true
            for c in value.trimmingCharacters(in: .whitespacesAndNewlines) {
                if c.isNumber && c.isASCII {
                    out.append(c)
                    leadingPlusAllowed = false
                } else if c == "+" && leadingPlusAllowed && out.isEmpty {
                    out.append("+")
                    leadingPlusAllowed = false
                }
            }
            canonical = out
        } else {
            canonical = value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        }
        return canonical.isEmpty ? nil : sha256HexLower(canonical)
    }
}

// MARK: - Verdicts

public enum ThreatAwarenessVerdict: Int, Sendable, Equatable {
    /// Nothing ran. This is what a denied gate looks like.
    case notAssessed = 0
    case noKnownThreat
    case suspicious
    case knownBad
    case inconclusive
}

/// One entry in the local corpus.
public struct AntibodyIndicatorMatch: Sendable, Equatable {
    public let kind: AntibodyIndicatorKind
    public let verdict: ThreatAwarenessVerdict
    public let note: String
    public let protectiveGuidance: String
    public let source: String

    public init(kind: AntibodyIndicatorKind, verdict: ThreatAwarenessVerdict,
                note: String, protectiveGuidance: String, source: String) {
        self.kind = kind
        self.verdict = verdict
        self.note = note
        self.protectiveGuidance = protectiveGuidance
        self.source = source
    }
}

/// What the user is told. Every one of these carries protective guidance,
/// because a verdict without a next step is just an alarm.
public struct ThreatAwarenessResult: Sendable, Equatable {
    public let indicatorKind: AntibodyIndicatorKind
    public let verdict: ThreatAwarenessVerdict
    public let wasAuthorized: Bool
    public let summary: String
    public let protectiveGuidance: String
    public let source: String
    public let assessedAtUtc: Date

    public init(indicatorKind: AntibodyIndicatorKind, verdict: ThreatAwarenessVerdict,
                wasAuthorized: Bool, summary: String, protectiveGuidance: String,
                source: String, assessedAtUtc: Date) {
        self.indicatorKind = indicatorKind
        self.verdict = verdict
        self.wasAuthorized = wasAuthorized
        self.summary = summary
        self.protectiveGuidance = protectiveGuidance
        self.source = source
        self.assessedAtUtc = assessedAtUtc
    }

    public static func notAuthorized(_ kind: AntibodyIndicatorKind, gateReason: String,
                                     now: Date = Date()) -> ThreatAwarenessResult {
        ThreatAwarenessResult(
            indicatorKind: kind, verdict: .notAssessed, wasAuthorized: false,
            summary: "No check was performed - the authorized-use gate denied it: \(gateReason)",
            protectiveGuidance: "Nothing was assessed. If you believe there is a real threat, raise it " +
                                "through the defensive flow so the check can be explicitly authorized.",
            source: "authorized-use gate", assessedAtUtc: now)
    }

    /// NOT a clean bill of health, and it says so - a local corpus knows only
    /// what it has been given.
    public static func noKnownThreat(_ kind: AntibodyIndicatorKind, source: String,
                                     protectiveGuidance: String, now: Date = Date()) -> ThreatAwarenessResult {
        ThreatAwarenessResult(
            indicatorKind: kind, verdict: .noKnownThreat, wasAuthorized: true,
            summary: "No match against your local threat set. This is not proof of safety - " +
                     "only that nothing known-bad was found.",
            protectiveGuidance: protectiveGuidance, source: source, assessedAtUtc: now)
    }

    public static func suspicious(_ kind: AntibodyIndicatorKind, source: String, summary: String,
                                  protectiveGuidance: String, now: Date = Date()) -> ThreatAwarenessResult {
        ThreatAwarenessResult(indicatorKind: kind, verdict: .suspicious, wasAuthorized: true,
                              summary: summary, protectiveGuidance: protectiveGuidance,
                              source: source, assessedAtUtc: now)
    }

    public static func knownBad(_ kind: AntibodyIndicatorKind, source: String, summary: String,
                                protectiveGuidance: String, now: Date = Date()) -> ThreatAwarenessResult {
        ThreatAwarenessResult(indicatorKind: kind, verdict: .knownBad, wasAuthorized: true,
                              summary: summary, protectiveGuidance: protectiveGuidance,
                              source: source, assessedAtUtc: now)
    }

    public static func inconclusive(_ kind: AntibodyIndicatorKind, source: String,
                                    protectiveGuidance: String, now: Date = Date()) -> ThreatAwarenessResult {
        ThreatAwarenessResult(
            indicatorKind: kind, verdict: .inconclusive, wasAuthorized: true,
            summary: "The assessment ran but could not reach a verdict for this indicator.",
            protectiveGuidance: protectiveGuidance, source: source, assessedAtUtc: now)
    }
}

// MARK: - The corpus
//
// LOCAL. Nothing in this module reaches the network: asking a remote service
// "have you seen this hash / this address of mine" tells that service what the
// user is doing, which is the opposite of the point.

public protocol ILocalIndicatorCorpus: Sendable {
    func lookup(_ kind: AntibodyIndicatorKind, normalizedValue: String) async -> AntibodyIndicatorMatch?
}

/// Knows nothing, and says so. The honest default on a fresh device.
public struct EmptyIndicatorCorpus: ILocalIndicatorCorpus {
    public static let instance = EmptyIndicatorCorpus()
    public init() {}
    public func lookup(_ kind: AntibodyIndicatorKind, normalizedValue: String) async -> AntibodyIndicatorMatch? {
        nil
    }
}

public final class InMemoryIndicatorCorpus: ILocalIndicatorCorpus, @unchecked Sendable {
    private struct Key: Hashable { let kind: AntibodyIndicatorKind; let value: String }

    private let lock = NSLock()
    private var entries: [Key: AntibodyIndicatorMatch] = [:]

    public init() {}

    public var count: Int {
        lock.lock(); defer { lock.unlock() }
        return entries.count
    }

    /// Every field is required: an entry without guidance would produce a
    /// warning nobody can act on.
    @discardableResult
    public func add(kind: AntibodyIndicatorKind, normalizedKey: String,
                    verdict: ThreatAwarenessVerdict, note: String,
                    protectiveGuidance: String, source: String) -> Bool {
        guard !normalizedKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !note.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !protectiveGuidance.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !source.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return false }
        let match = AntibodyIndicatorMatch(kind: kind, verdict: verdict, note: note,
                                           protectiveGuidance: protectiveGuidance, source: source)
        lock.lock(); entries[Key(kind: kind, value: normalizedKey)] = match; lock.unlock()
        return true
    }

    private func stored(_ key: Key) -> AntibodyIndicatorMatch? {
        lock.lock(); defer { lock.unlock() }
        return entries[key]
    }

    public func lookup(_ kind: AntibodyIndicatorKind, normalizedValue: String) async -> AntibodyIndicatorMatch? {
        guard !normalizedValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        return stored(Key(kind: kind, value: normalizedValue))
    }
}

// MARK: - The three assessors

public protocol IFileThreatAwareness: Sendable {
    func inspect(_ artifact: FileArtifact) async -> ThreatAwarenessResult
}

public protocol INetworkThreatAwareness: Sendable {
    func inspect(_ indicator: NetworkIndicator) async -> ThreatAwarenessResult
}

public protocol IBreachExposureAwareness: Sendable {
    func inspect(_ identity: IdentityIndicator) async -> ThreatAwarenessResult
}

public struct FileThreatAwarenessAssessor: IFileThreatAwareness {
    private static let source = "local indicator corpus"
    private static let kind = AntibodyIndicatorKind.fileHashSha256

    private let corpus: any ILocalIndicatorCorpus
    private let clock: @Sendable () -> Date

    public init(corpus: any ILocalIndicatorCorpus, clock: @escaping @Sendable () -> Date = { Date() }) {
        self.corpus = corpus
        self.clock = clock
    }

    public func inspect(_ artifact: FileArtifact) async -> ThreatAwarenessResult {
        let now = clock()
        guard !artifact.sha256Hex.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return .inconclusive(Self.kind, source: Self.source,
                protectiveGuidance: "The file had no usable SHA-256 hash to check. Treat it with caution " +
                                    "and only open files you trust.", now: now)
        }

        let key = artifact.sha256Hex.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard let match = await corpus.lookup(Self.kind, normalizedValue: key) else {
            return .noKnownThreat(Self.kind, source: Self.source,
                protectiveGuidance: "\(artifact.fileName) did not match any known-bad signature in your local " +
                                    "threat set. Only open files you trust - a clean check is not a guarantee.",
                now: now)
        }

        switch match.verdict {
        case .knownBad:
            return .knownBad(Self.kind, source: match.source,
                summary: "\(artifact.fileName) matches a known-bad signature in your local threat set: \(match.note)",
                protectiveGuidance: "Do not open or run \(artifact.fileName). \(match.protectiveGuidance)",
                now: now)
        case .suspicious:
            return .suspicious(Self.kind, source: match.source,
                summary: "\(artifact.fileName) matches a suspicious signature in your local threat set: \(match.note)",
                protectiveGuidance: "Be very cautious with \(artifact.fileName). \(match.protectiveGuidance)",
                now: now)
        case .noKnownThreat:
            return .noKnownThreat(Self.kind, source: match.source,
                protectiveGuidance: "\(artifact.fileName) is recorded as benign in your local set, but stay " +
                                    "cautious with files you did not expect.", now: now)
        default:
            return .inconclusive(Self.kind, source: match.source,
                protectiveGuidance: "The local set has an entry for \(artifact.fileName) but no clear verdict. " +
                                    "Treat it with caution.", now: now)
        }
    }
}

public struct NetworkThreatAwarenessAssessor: INetworkThreatAwareness {
    private static let source = "local indicator corpus"

    private let corpus: any ILocalIndicatorCorpus
    private let clock: @Sendable () -> Date

    public init(corpus: any ILocalIndicatorCorpus, clock: @escaping @Sendable () -> Date = { Date() }) {
        self.corpus = corpus
        self.clock = clock
    }

    public func inspect(_ indicator: NetworkIndicator) async -> ThreatAwarenessResult {
        let now = clock()
        let kind = indicator.kind

        guard let key = IndicatorNormalizer.normalizeNetwork(kind, indicator.value) else {
            return .inconclusive(kind, source: Self.source,
                protectiveGuidance: "The network location could not be read. Do not connect to something " +
                                    "you cannot verify.", now: now)
        }

        guard let match = await corpus.lookup(kind, normalizedValue: key) else {
            return .noKnownThreat(kind, source: Self.source,
                protectiveGuidance: "This location did not match anything known-bad in your local threat set. " +
                                    "Be careful with links you did not expect - a clean check is not a guarantee.",
                now: now)
        }

        switch match.verdict {
        case .knownBad:
            return .knownBad(kind, source: match.source,
                summary: "This location is flagged as known-bad in your local threat set: \(match.note)",
                protectiveGuidance: "Do not connect to it or enter any details. \(match.protectiveGuidance)",
                now: now)
        case .suspicious:
            return .suspicious(kind, source: match.source,
                summary: "This location is flagged as suspicious in your local threat set: \(match.note)",
                protectiveGuidance: "Avoid it unless you are certain it is genuine. \(match.protectiveGuidance)",
                now: now)
        case .noKnownThreat:
            return .noKnownThreat(kind, source: match.source,
                protectiveGuidance: "This location is recorded as benign in your local set, but stay alert " +
                                    "for anything unexpected.", now: now)
        default:
            return .inconclusive(kind, source: match.source,
                protectiveGuidance: "The local set has an entry for this location but no clear verdict. " +
                                    "Treat it with caution.", now: now)
        }
    }
}

/// Tells somebody whether their OWN address appears in a breach set the device
/// already holds. The value is hashed before it is looked up, so the corpus
/// never sees the address itself.
public struct BreachExposureAssessor: IBreachExposureAwareness {
    private static let source = "local breach set"

    private let corpus: any ILocalIndicatorCorpus
    private let clock: @Sendable () -> Date

    public init(corpus: any ILocalIndicatorCorpus, clock: @escaping @Sendable () -> Date = { Date() }) {
        self.corpus = corpus
        self.clock = clock
    }

    public func inspect(_ identity: IdentityIndicator) async -> ThreatAwarenessResult {
        let now = clock()
        let kind = identity.kind

        guard let hash = IndicatorNormalizer.normalizeIdentityToHash(kind, identity.value) else {
            return .inconclusive(kind, source: Self.source,
                protectiveGuidance: "Your identity value could not be read, so nothing was looked up.", now: now)
        }

        guard let match = await corpus.lookup(kind, normalizedValue: hash) else {
            return .noKnownThreat(kind, source: Self.source,
                protectiveGuidance: "Your \(Self.describe(kind)) was not found in your local breach set. " +
                                    "New breaches appear over time - keep using a unique, strong password " +
                                    "and turn on 2-factor authentication anyway.", now: now)
        }

        // The guidance is the same either way, because the action is the same:
        // rotate it now, everywhere it was reused.
        let rotate = "Change the password for your \(Self.describe(kind)) now, and anywhere you reused it, " +
                     "and turn on 2-factor authentication. \(match.protectiveGuidance)"

        return match.verdict == .suspicious
            ? .suspicious(kind, source: match.source,
                summary: "Your \(Self.describe(kind)) may be exposed in a breach recorded in your local set: \(match.note)",
                protectiveGuidance: rotate, now: now)
            : .knownBad(kind, source: match.source,
                summary: "Your \(Self.describe(kind)) appears in a known breach recorded in your local set: \(match.note)",
                protectiveGuidance: rotate, now: now)
    }

    static func describe(_ kind: AntibodyIndicatorKind) -> String {
        switch kind {
        case .emailAddress: return "email address"
        case .username: return "username"
        case .phoneNumber: return "phone number"
        default: return "identity"
        }
    }
}

// MARK: - The system

public protocol IDefensiveAntibodySystem: Sendable {
    func assessFile(_ artifact: FileArtifact, threat: DefensiveThreatContext) async -> ThreatAwarenessResult
    func assessNetworkIndicator(_ indicator: NetworkIndicator, threat: DefensiveThreatContext) async -> ThreatAwarenessResult
    func assessOwnIdentityExposure(_ identity: IdentityIndicator, threat: DefensiveThreatContext) async -> ThreatAwarenessResult
}

/// The one entry point. Every path through it asks the gate first, and a denial
/// returns a result rather than throwing - the user gets told why nothing ran.
public struct DefensiveAntibodySystem: IDefensiveAntibodySystem {

    private static let fileJustification =
        "Warn the user before they open a file implicated by a defined threat."
    private static let networkJustification =
        "Warn the user before they connect to a location implicated by a defined threat."
    private static let identityJustification =
        "Warn the user if their own identity is exposed, under a defined threat."

    private let gate: any IAuthorizedUseGate
    private let file: any IFileThreatAwareness
    private let network: any INetworkThreatAwareness
    private let breach: any IBreachExposureAwareness
    private let clock: @Sendable () -> Date

    public init(gate: any IAuthorizedUseGate,
                file: any IFileThreatAwareness,
                network: any INetworkThreatAwareness,
                breach: any IBreachExposureAwareness,
                clock: @escaping @Sendable () -> Date = { Date() }) {
        self.gate = gate
        self.file = file
        self.network = network
        self.breach = breach
        self.clock = clock
    }

    /// A system that can never grant anything: no gate, no corpus. This is what
    /// a build that has not opted in looks like, and it is a valid build.
    public static func createDenyByDefault(clock: @escaping @Sendable () -> Date = { Date() })
        -> DefensiveAntibodySystem {
        let corpus = EmptyIndicatorCorpus.instance
        return DefensiveAntibodySystem(
            gate: NullAuthorizedUseGate.instance,
            file: FileThreatAwarenessAssessor(corpus: corpus, clock: clock),
            network: NetworkThreatAwarenessAssessor(corpus: corpus, clock: clock),
            breach: BreachExposureAssessor(corpus: corpus, clock: clock),
            clock: clock)
    }

    public static func create(gate: any IAuthorizedUseGate, corpus: any ILocalIndicatorCorpus,
                              clock: @escaping @Sendable () -> Date = { Date() }) -> DefensiveAntibodySystem {
        DefensiveAntibodySystem(
            gate: gate,
            file: FileThreatAwarenessAssessor(corpus: corpus, clock: clock),
            network: NetworkThreatAwarenessAssessor(corpus: corpus, clock: clock),
            breach: BreachExposureAssessor(corpus: corpus, clock: clock),
            clock: clock)
    }

    public func assessFile(_ artifact: FileArtifact,
                           threat: DefensiveThreatContext) async -> ThreatAwarenessResult {
        let decision = await authorize(.fileReputationAwareness, threat, Self.fileJustification)
        guard decision.granted else {
            return .notAuthorized(.fileHashSha256, gateReason: decision.reason, now: clock())
        }
        return await file.inspect(artifact)
    }

    public func assessNetworkIndicator(_ indicator: NetworkIndicator,
                                       threat: DefensiveThreatContext) async -> ThreatAwarenessResult {
        let decision = await authorize(.networkIndicatorAwareness, threat, Self.networkJustification)
        guard decision.granted else {
            return .notAuthorized(indicator.kind, gateReason: decision.reason, now: clock())
        }
        return await network.inspect(indicator)
    }

    public func assessOwnIdentityExposure(_ identity: IdentityIndicator,
                                          threat: DefensiveThreatContext) async -> ThreatAwarenessResult {
        let decision = await authorize(.breachExposureAwareness, threat, Self.identityJustification)
        guard decision.granted else {
            return .notAuthorized(identity.kind, gateReason: decision.reason, now: clock())
        }
        return await breach.inspect(identity)
    }

    private func authorize(_ capability: AntibodyCapability, _ threat: DefensiveThreatContext,
                           _ justification: String) async -> AuthorizationDecision {
        let request = AuthorizedUseRequest(requestId: UUID(), capability: capability, threat: threat,
                                           justification: justification, requestedAtUtc: clock())
        return await gate.requestAuthorization(request)
    }
}
