// CoreDiagnostics.swift
//
// Counters, timings and outcomes, and the marker that says how far a component
// has actually been proven.
//
// WHY IT IS A SEAM AND NOT A LIBRARY. The C# side is System.Diagnostics.Metrics,
// which an OpenTelemetry exporter picks up with no code at all. Swift has no
// equivalent in the standard library, and pulling swift-metrics in would put a
// dependency into a package that has none — on a phone the exporter is usually
// not there anyway. So the SHAPE is ported (the names, the units, the outcome
// vocabulary, the operation span) behind a sink a host can point anywhere, and
// nothing is recorded until somebody asks for it.
//
// Ported from src/CircleAI.Core/Diagnostics/CircleAIDiagnostics.cs and
// Validation/CircleAIVerificationStatusAttribute.cs.

import Foundation

// MARK: - How far this has actually been proven

/// How far a component has been proven, which is NOT the same as whether it
/// compiles.
///
/// The distinction is the point. A type marked `reference` may be complete,
/// tested and green and still have never had a byte cross a wire; one marked
/// `productionDeployed` has run against the real thing. Recording it is what
/// stops a green test suite reading as a shipped feature.
public enum VerificationLevel: Int, Sendable, Equatable, Codable, CaseIterable {
    /// Written against the specification. Nothing has been exchanged with a
    /// real counterpart.
    case reference = 0
    /// Proven against a real peer on a real link.
    case wireProven = 1
    /// Running in production.
    case productionDeployed = 2
}

/// C# carries this as an attribute; Swift has no attributes, so a conforming
/// type states the same fact as a static property. Same information, same name,
/// and still discoverable — a caller asks the type rather than its metadata.
public protocol CircleAIVerificationStatus {
    static var verificationStatus: VerificationLevel { get }
    static var verificationNotes: String? { get }
}

public extension CircleAIVerificationStatus {
    static var verificationNotes: String? { nil }
}

// MARK: - Metrics

/// Where a measurement goes. A host wires this to whatever it actually has;
/// unset, nothing is recorded and nothing is allocated.
public protocol ICircleAIMetricSink: Sendable {
    func count(_ name: String, by amount: Int64, tags: [String: String])
    func record(_ name: String, milliseconds: Double, tags: [String: String])
}

/// The names and the vocabulary, in one place so two components cannot report
/// the same thing under two spellings.
public enum CircleAIDiagnostics {

    public static let activitySourceName = "CircleAI"
    public static let meterName = "CircleAI"
    public static let version = "1.1.0"

    // The instrument names are the C# ones EXACTLY. A dashboard is built on
    // these strings, so renaming one here silently splits a metric in two.
    public static let operationsTotal = "circleai.operations.total"
    public static let operationDurationMs = "circleai.operation.duration"
    public static let anomalySignalsTotal = "circleai.anomaly.signals.total"
    public static let inferenceRequestsTotal = "circleai.inference.requests.total"

    /// How an operation ended. A closed vocabulary on purpose: "failed",
    /// "error" and "err" in three components make a chart that cannot be read.
    public enum Outcomes {
        public static let success = "success"
        public static let cancelled = "cancelled"
        public static let unavailable = "unavailable"
        public static let rateLimited = "rate_limited"
        public static let invalid = "invalid"
        public static let error = "error"

        public static let all = [success, cancelled, unavailable, rateLimited, invalid, error]
    }

    private static let lock = NSLock()
    nonisolated(unsafe) private static var sink: (any ICircleAIMetricSink)?

    /// Nil by default. Nothing is measured until a host says where to put it.
    public static var metricSink: (any ICircleAIMetricSink)? {
        get { lock.lock(); defer { lock.unlock() }; return sink }
        set { lock.lock(); sink = newValue; lock.unlock() }
    }

    public static func count(_ name: String, by amount: Int64 = 1,
                             tags: [String: String] = [:]) {
        metricSink?.count(name, by: amount, tags: tags)
    }

    public static func record(_ name: String, milliseconds: Double,
                              tags: [String: String] = [:]) {
        metricSink?.record(name, milliseconds: milliseconds, tags: tags)
    }

    /// Start measuring one operation. The returned value records its duration
    /// and its outcome when it is finished.
    public static func startOperation(component: String, operation: String) -> CircleAIOperation {
        CircleAIOperation(component: component, operation: operation)
    }
}

/// One operation being measured.
///
/// The outcome is recorded when `finish` is called, not when this is dropped: a
/// span that ends silently on deinit reports every abandoned operation as a
/// success, which is exactly backwards.
public final class CircleAIOperation: @unchecked Sendable {

    public let component: String
    public let operation: String

    private let started = Date()
    private let lock = NSLock()
    private var done = false

    init(component: String, operation: String) {
        self.component = component
        self.operation = operation
    }

    public var elapsedMs: Double { Date().timeIntervalSince(started) * 1000 }

    /// Records the duration and the outcome. Idempotent — a caller that
    /// finishes in both a success path and a defer must not double-count.
    public func finish(outcome: String = CircleAIDiagnostics.Outcomes.success) {
        lock.lock()
        if done { lock.unlock(); return }
        done = true
        lock.unlock()

        let tags = [
            "circleai.component": component,
            "circleai.operation": operation,
            "circleai.outcome": outcome,
        ]
        CircleAIDiagnostics.record(CircleAIDiagnostics.operationDurationMs,
                                   milliseconds: elapsedMs, tags: tags)
        CircleAIDiagnostics.count(CircleAIDiagnostics.operationsTotal, by: 1, tags: tags)
    }

    /// Whether this operation has already reported. Exposed so a caller can
    /// tell an abandoned span from a finished one.
    public var isFinished: Bool {
        lock.lock(); defer { lock.unlock() }
        return done
    }
}
