// Auditing.swift
//
// Port of CircleAI.Core.Auditing — ICircleAIAuditLog, CircleAIAuditEntry,
// CircleAIAuditQuery, NoopAuditLog, LoggerAuditLog, CircleAIAuditing.
//
// Tamper-aware audit surface for the CircleAI SDK. Every state-changing
// operation a component performs is auto-recorded here (component + operation
// + outcome + duration + tenant + UHID + error info, with hash-only references
// to any payload).
//
// Default registration is NoopAuditLog — entries are silently dropped until a
// consumer wires LoggerAuditLog or their own append-only sink.

import Foundation

// MARK: - CircleAIAuditEntry — CircleAI.Core.Auditing.CircleAIAuditEntry

/// An immutable audit entry emitted by the CircleAI SDK.
public struct CircleAIAuditEntry: Sendable, Equatable {
    /// UTC timestamp of the action.
    public let at: Date
    /// Canonical CircleAI component name.
    public let component: String
    /// Logical operation name.
    public let operation: String
    /// Outcome — one of CircleAIDiagnostics.Outcomes.
    public let outcome: String
    /// Tenant id, when running multi-tenant. Nil for single-tenant deployments.
    public let tenantId: String?
    /// User id (UHID) when the operation was scoped to a specific user.
    public let uhidIdentityId: String?
    /// Optional correlation id (e.g. session id, request id).
    public let correlationId: String?
    /// Operation duration in milliseconds.
    public let durationMs: Double
    /// CLR/Swift error type when the outcome is not "success".
    public let errorType: String?
    /// Implementation-supplied error code, when applicable.
    public let errorCode: String?
    /// Hash of any sensitive payload involved. Never carries the raw payload.
    public let payloadSha256Hex: String?

    public init(
        at: Date,
        component: String,
        operation: String,
        outcome: String,
        tenantId: String? = nil,
        uhidIdentityId: String? = nil,
        correlationId: String? = nil,
        durationMs: Double = 0,
        errorType: String? = nil,
        errorCode: String? = nil,
        payloadSha256Hex: String? = nil
    ) {
        self.at = at
        self.component = component
        self.operation = operation
        self.outcome = outcome
        self.tenantId = tenantId
        self.uhidIdentityId = uhidIdentityId
        self.correlationId = correlationId
        self.durationMs = durationMs
        self.errorType = errorType
        self.errorCode = errorCode
        self.payloadSha256Hex = payloadSha256Hex
    }
}

// MARK: - CircleAIAuditQuery — CircleAI.Core.Auditing.CircleAIAuditQuery

/// Query filter for `ICircleAIAuditLog.query`.
public struct CircleAIAuditQuery: Sendable, Equatable {
    /// Inclusive lower bound on `CircleAIAuditEntry.at`.
    public let fromUtc: Date?
    /// Inclusive upper bound on `CircleAIAuditEntry.at`.
    public let toUtc: Date?
    /// Restrict to a single component.
    public let component: String?
    /// Restrict to a single tenant.
    public let tenantId: String?
    /// Restrict to a single UHID identity.
    public let uhidIdentityId: String?
    /// Restrict to a single outcome.
    public let outcome: String?
    /// Maximum entries to return.
    public let maxItems: Int

    public init(
        fromUtc: Date? = nil,
        toUtc: Date? = nil,
        component: String? = nil,
        tenantId: String? = nil,
        uhidIdentityId: String? = nil,
        outcome: String? = nil,
        maxItems: Int = 1000
    ) {
        self.fromUtc = fromUtc
        self.toUtc = toUtc
        self.component = component
        self.tenantId = tenantId
        self.uhidIdentityId = uhidIdentityId
        self.outcome = outcome
        self.maxItems = maxItems
    }
}

// MARK: - ICircleAIAuditLog — CircleAI.Core.Auditing.ICircleAIAuditLog

/// Tamper-aware audit sink.
public protocol ICircleAIAuditLog: Sendable {
    /// Record an audit entry. MUST NOT throw — the caller may be mid-operation
    /// and audit-log failure must never bring it down. Implementations should
    /// catch and log internally, failing open.
    func record(_ entry: CircleAIAuditEntry) async

    /// Query historical entries — for compliance reporting, forensic
    /// investigation, debugging.
    func query(_ query: CircleAIAuditQuery) -> AsyncStream<CircleAIAuditEntry>
}

// MARK: - ICircleAILogger — injection seam for LoggerAuditLog

/// Minimal structured-logging seam. C#'s LoggerAuditLog depends on
/// `Microsoft.Extensions.Logging.ILogger`; the Swift port injects this
/// interface so the SDK stays free of a concrete logging dependency.
public protocol ICircleAILogger: Sendable {
    /// Emit an informational, already-formatted message.
    func logInformation(_ message: String)
}

/// Default logger that prints to standard output. Handy for development and
/// tests; production hosts inject their own sink.
public struct ConsoleCircleAILogger: ICircleAILogger {
    public init() {}
    public func logInformation(_ message: String) {
        print(message)
    }
}

// MARK: - NoopAuditLog — CircleAI.Core.Auditing.NoopAuditLog

/// Default `ICircleAIAuditLog` — silently discards every entry and returns an
/// empty query result.
public struct NoopAuditLog: ICircleAIAuditLog {
    /// Shared singleton instance.
    public static let instance = NoopAuditLog()

    public init() {}

    public func record(_ entry: CircleAIAuditEntry) async { /* dropped */ }

    public func query(_ query: CircleAIAuditQuery) -> AsyncStream<CircleAIAuditEntry> {
        AsyncStream { $0.finish() }
    }
}

// MARK: - LoggerAuditLog — CircleAI.Core.Auditing.LoggerAuditLog

/// `ICircleAIAuditLog` implementation that writes structured entries to an
/// injected `ICircleAILogger` at information level.
///
/// The `query` implementation always returns empty — query support is a
/// sink-specific feature and reading back from a logger isn't possible at the
/// SDK layer.
public struct LoggerAuditLog: ICircleAIAuditLog {
    private let logger: any ICircleAILogger

    /// Construct with a logger.
    public init(logger: any ICircleAILogger) {
        self.logger = logger
    }

    public func record(_ entry: CircleAIAuditEntry) async {
        // Structured logging — mirrors the C# named-property template.
        let at = ISO8601DateFormatter().string(from: entry.at)
        let message =
            "CircleAI audit \(entry.component).\(entry.operation) \(entry.outcome) " +
            "tenant=\(entry.tenantId ?? "-") uhid=\(entry.uhidIdentityId ?? "-") " +
            "corr=\(entry.correlationId ?? "-") duration_ms=\(entry.durationMs) " +
            "error=\(entry.errorType ?? "-")(\(entry.errorCode ?? "-")) " +
            "payload_sha256=\(entry.payloadSha256Hex ?? "-") at=\(at)"
        logger.logInformation(message)
    }

    public func query(_ query: CircleAIAuditQuery) -> AsyncStream<CircleAIAuditEntry> {
        AsyncStream { $0.finish() }
    }
}

// MARK: - CircleAIAuditing — CircleAI.Core.Auditing.CircleAIAuditing

/// Process-wide ambient access point for the audit sink. Components emit
/// through `default` without depending on a DI container.
///
/// Initial value is `NoopAuditLog.instance`. Hosts wire the real sink by
/// calling `setDefault` during startup.
public enum CircleAIAuditing {
    private static let lock = NSLock()
    private static var _default: any ICircleAIAuditLog = NoopAuditLog.instance

    /// The current ambient audit sink. Defaults to `NoopAuditLog`.
    public static var `default`: any ICircleAIAuditLog {
        lock.lock(); defer { lock.unlock() }
        return _default
    }

    /// Replace the ambient audit sink. Idempotent.
    public static func setDefault(_ audit: any ICircleAIAuditLog) {
        lock.lock(); _default = audit; lock.unlock()
    }

    /// Restore the default to `NoopAuditLog`. Test-helper.
    public static func resetToNoop() {
        lock.lock(); _default = NoopAuditLog.instance; lock.unlock()
    }
}
