// Observability.swift
//
// Port of src/CircleAI.Observability/:
//   • Contracts.cs                 — MetricSample, TraceSpan, DashboardSpec;
//                                     IMetricSink, ITraceSink, IDashboardPublisher
//   • InMemoryObservability.cs     — aggregating metric sink, per-trace span
//                                     sink, dashboard-spec publisher (all with
//                                     extra read helpers beyond the contract)
//   • NullImplementations.cs       — drop-all Null* sinks
//
// Porting notes:
//   • `record` → `struct: Sendable, Equatable, Codable`. Optional
//     `IReadOnlyDictionary<string,string>?` → `[String: String]?`.
//   • `DateTimeOffset` → `Date`; `TimeSpan` → `TimeInterval`.
//   • The in-memory sinks expose the same extra read surface the C# class
//     exposes (`read(name:)`, `metricNames`, `read(traceId:)`, `get`, `all`);
//     spans are returned ordered ascending by `startUtc`.
//   • State guarded by a single `NSLock` per sink.

import Foundation

// MARK: - Records

/// One metric emission — a named value with optional tags.
public struct MetricSample: Sendable, Equatable, Codable {
    public let name: String
    public let value: Double
    public let tags: [String: String]?

    public init(name: String, value: Double, tags: [String: String]? = nil) {
        self.name = name
        self.value = value
        self.tags = tags
    }
}

/// One distributed-trace span.
public struct TraceSpan: Sendable, Equatable, Codable {
    public let traceId: String
    public let spanId: String
    public let parentSpanId: String?
    public let name: String
    public let startUtc: Date
    public let duration: TimeInterval
    public let attributes: [String: String]?

    public init(
        traceId: String,
        spanId: String,
        parentSpanId: String?,
        name: String,
        startUtc: Date,
        duration: TimeInterval,
        attributes: [String: String]? = nil
    ) {
        self.traceId = traceId
        self.spanId = spanId
        self.parentSpanId = parentSpanId
        self.name = name
        self.startUtc = startUtc
        self.duration = duration
        self.attributes = attributes
    }
}

/// A dashboard specification — an opaque JSON blob keyed by id + title.
public struct DashboardSpec: Sendable, Equatable, Codable {
    public let dashboardId: String
    public let title: String
    public let jsonBlob: String

    public init(dashboardId: String, title: String, jsonBlob: String) {
        self.dashboardId = dashboardId
        self.title = title
        self.jsonBlob = jsonBlob
    }
}

// MARK: - Errors

public enum ObservabilityError: Error, Equatable, CustomStringConvertible {
    case nameRequired
    case traceIdRequired
    case dashboardIdRequired

    public var description: String {
        switch self {
        case .nameRequired: return "Name required"
        case .traceIdRequired: return "TraceId required"
        case .dashboardIdRequired: return "DashboardId required"
        }
    }
}

// MARK: - Contracts

/// Metric sink — Prometheus / OTel.
public protocol IMetricSink: Sendable {
    var backendId: String { get }
    func emit(_ sample: MetricSample) async throws
}

/// Trace sink — OTel.
public protocol ITraceSink: Sendable {
    var backendId: String { get }
    func emit(_ span: TraceSpan) async throws
}

/// Dashboard publisher — Grafana / claude-team-dashboard.
public protocol IDashboardPublisher: Sendable {
    var backendId: String { get }
    func publish(_ spec: DashboardSpec) async throws
}

// MARK: - In-memory sinks

/// Aggregating metric sink — keeps every sample per metric name.
public final class InMemoryMetricSink: IMetricSink, @unchecked Sendable {
    private let lock = NSLock()
    private var byName: [String: [MetricSample]] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    public func emit(_ sample: MetricSample) async throws {
        if sample.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw ObservabilityError.nameRequired
        }
        lock.lock(); defer { lock.unlock() }
        byName[sample.name, default: []].append(sample)
    }

    /// All samples recorded for `name` (empty when unknown).
    public func read(name: String) -> [MetricSample] {
        lock.lock(); defer { lock.unlock() }
        return byName[name] ?? []
    }

    /// Every known metric name, sorted ascending.
    public var metricNames: [String] {
        lock.lock(); defer { lock.unlock() }
        return byName.keys.sorted()
    }
}

/// Per-trace span sink — keeps every span grouped by trace id.
public final class InMemoryTraceSink: ITraceSink, @unchecked Sendable {
    private let lock = NSLock()
    private var byTrace: [String: [TraceSpan]] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    public func emit(_ span: TraceSpan) async throws {
        if span.traceId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw ObservabilityError.traceIdRequired
        }
        lock.lock(); defer { lock.unlock() }
        byTrace[span.traceId, default: []].append(span)
    }

    /// Spans for `traceId`, ordered ascending by `startUtc` (empty when unknown).
    public func read(traceId: String) -> [TraceSpan] {
        lock.lock(); defer { lock.unlock() }
        guard let list = byTrace[traceId] else { return [] }
        return list.sorted { $0.startUtc < $1.startUtc }
    }
}

/// Dashboard-spec publisher — round-trips specs by id.
public final class InMemoryDashboardPublisher: IDashboardPublisher, @unchecked Sendable {
    private let lock = NSLock()
    private var specs: [String: DashboardSpec] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    public func publish(_ spec: DashboardSpec) async throws {
        if spec.dashboardId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw ObservabilityError.dashboardIdRequired
        }
        lock.lock(); defer { lock.unlock() }
        specs[spec.dashboardId] = spec
    }

    /// The spec previously published under `dashboardId`, or nil.
    public func get(_ dashboardId: String) -> DashboardSpec? {
        lock.lock(); defer { lock.unlock() }
        return specs[dashboardId]
    }

    /// Every published spec, ordered ascending by id.
    public var all: [DashboardSpec] {
        lock.lock(); defer { lock.unlock() }
        return specs.values.sorted { $0.dashboardId < $1.dashboardId }
    }
}

// MARK: - Null sinks

/// Drop-all metric sink.
public final class NullMetricSink: IMetricSink, @unchecked Sendable {
    public static let instance = NullMetricSink()
    public init() {}
    public var backendId: String { "null" }
    public func emit(_ sample: MetricSample) async throws {}
}

/// Drop-all trace sink.
public final class NullTraceSink: ITraceSink, @unchecked Sendable {
    public static let instance = NullTraceSink()
    public init() {}
    public var backendId: String { "null" }
    public func emit(_ span: TraceSpan) async throws {}
}

/// Drop-all dashboard publisher.
public final class NullDashboardPublisher: IDashboardPublisher, @unchecked Sendable {
    public static let instance = NullDashboardPublisher()
    public init() {}
    public var backendId: String { "null" }
    public func publish(_ spec: DashboardSpec) async throws {}
}
