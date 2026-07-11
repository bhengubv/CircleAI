// Pipelines.swift
//
// Port of CircleAI.Pipelines/ — data-pipeline source/sink/executor + a tiny
// in-memory SQL-ish query tool.
//   • Contracts.cs           — PipelineRecord, PipelineRun, IPipelineSource,
//                              IPipelineSink, IPipelineExecutor,
//                              DatabaseQueryResult, IDatabaseQueryTool
//   • InMemoryPipelines.cs   — InMemoryPipelineSource (channel-backed streams),
//                              InMemoryPipelineSink, InMemoryPipelineExecutor
//                              (run state machine), InMemoryDatabaseQueryTool
//   • NullImplementations.cs — Null* fail-closed backends
//
// Porting notes:
//   • `IReadOnlyDictionary<string, object?>` value bags → `[String: AnyCodable]`
//     (reusing the module's existing `AnyCodable`).
//   • `IAsyncEnumerable<PipelineRecord>` → `AsyncStream<PipelineRecord>`. The C#
//     source is an unbounded channel per stream that yields buffered records
//     and completes when the writer is completed. The Swift `InMemoryPipelineSource`
//     mirrors this: `push` buffers into the stream, `complete` finishes it.
//     `read` returns the stream synchronously (so a consumer can subscribe
//     before producing), and buffering is unbounded so fan-in never drops.
//   • `IPipelineExecutor.RunAsync` runs a registered runner returning a row
//     count, capturing any thrown error into `PipelineRun.failureReason` — the
//     run never throws. `runSeq` is a monotonic counter under the lock.
//   • `InMemoryDatabaseQueryTool` ports the deliberately-tiny
//     "SELECT * FROM <table>" parser verbatim, including case-insensitive table
//     names and the SELECT-only / FROM-required guards.

import Foundation

// MARK: - Records

/// A single pipeline record on a named stream. (C# `PipelineRecord`.)
///
/// Carries a `[String: AnyCodable]` value bag (C# `IReadOnlyDictionary<string,
/// object?>`). `AnyCodable` boxes `Any?`, so the record is not `Equatable`;
/// tests compare `stream` + individual boxed values via `stringValue(_:)`.
public struct PipelineRecord: Sendable, Codable {
    /// Stream name.
    public let stream: String
    /// Arbitrary typed values.
    public let values: [String: AnyCodable]

    public init(stream: String, values: [String: AnyCodable]) {
        self.stream = stream
        self.values = values
    }

    /// Convenience typed accessor for a string-valued field.
    public func stringValue(_ key: String) -> String? { values[key]?.value as? String }
    /// Convenience typed accessor for an int-valued field.
    public func intValue(_ key: String) -> Int? { values[key]?.value as? Int }
}

/// A single pipeline run record. (C# `PipelineRun`.)
public struct PipelineRun: Sendable, Equatable, Codable {
    /// Run identifier.
    public let runId: String
    /// Pipeline identifier.
    public let pipelineId: String
    /// UTC start time.
    public let startUtc: Date
    /// UTC end time, or `nil` while running.
    public let endUtc: Date?
    /// Rows processed by this run.
    public let rowsProcessed: Int64
    /// Failure reason, or `nil` on success.
    public let failureReason: String?

    public init(runId: String, pipelineId: String, startUtc: Date, endUtc: Date?,
                rowsProcessed: Int64, failureReason: String?) {
        self.runId = runId
        self.pipelineId = pipelineId
        self.startUtc = startUtc
        self.endUtc = endUtc
        self.rowsProcessed = rowsProcessed
        self.failureReason = failureReason
    }
}

/// Result of a database query. (C# `DatabaseQueryResult`.) Rows are `Any?`
/// value bags, so the result is not `Equatable`.
public struct DatabaseQueryResult: Sendable {
    /// Rows, each a value bag.
    public let rows: [[String: AnyCodable]]
    /// Row count (== `rows.count`).
    public let rowCount: Int

    public init(rows: [[String: AnyCodable]], rowCount: Int) {
        self.rows = rows
        self.rowCount = rowCount
    }
}

// MARK: - Errors

/// Errors raised by the pipeline executor / query tool.
public enum PipelineError: Error, Equatable, CustomStringConvertible {
    case unknownPipeline(String)
    case onlySelectSupported
    case selectRequiresFrom

    public var description: String {
        switch self {
        case .unknownPipeline(let id): return "Unknown pipeline '\(id)'."
        case .onlySelectSupported: return "Only SELECT queries are supported by InMemoryDatabaseQueryTool."
        case .selectRequiresFrom: return "SELECT requires a FROM clause."
        }
    }
}

// MARK: - Contracts

/// A source of pipeline records on named streams. (C# `IPipelineSource`.)
public protocol IPipelineSource: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Reads records from `stream` as an async sequence.
    func read(_ stream: String) -> AsyncStream<PipelineRecord>
}

/// A sink that collects pipeline records. (C# `IPipelineSink`.)
public protocol IPipelineSink: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Writes a record.
    func write(_ record: PipelineRecord) async
    /// Flushes any buffered writes.
    func flush() async
}

/// Runs registered pipelines and tracks runs. (C# `IPipelineExecutor`.)
public protocol IPipelineExecutor: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Runs the pipeline `pipelineId`, returning the completed run record.
    func run(_ pipelineId: String) async -> PipelineRun
    /// Returns a run by id, or `nil`.
    func getRun(_ runId: String) async -> PipelineRun?
}

/// Runs SELECT queries against in-memory tables. (C# `IDatabaseQueryTool`.)
public protocol IDatabaseQueryTool: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Runs `sql`, returning matching rows. Throws `PipelineError` for
    /// non-SELECT queries or a missing FROM clause.
    func query(_ sql: String, parameters: [String: AnyCodable]?) async throws -> DatabaseQueryResult
}

public extension IDatabaseQueryTool {
    /// Overload matching the C# default `parameters = null`.
    func query(_ sql: String) async throws -> DatabaseQueryResult {
        try await query(sql, parameters: nil)
    }
}

// MARK: - InMemoryPipelineSource

/// In-memory source: unbounded per-stream buffers. `push` appends a record and
/// `complete` closes the stream. `read` returns the stream synchronously so a
/// consumer can subscribe before records are produced. (C# `InMemoryPipelineSource`.)
public final class InMemoryPipelineSource: IPipelineSource, @unchecked Sendable {
    private let lock = NSLock()
    private var continuations: [String: AsyncStream<PipelineRecord>.Continuation] = [:]
    private var buffers: [String: [PipelineRecord]] = [:]
    private var completed: Set<String> = []

    public init() {}

    public var backendId: String { "in-memory" }

    /// Buffer a record onto `stream`. Delivered to a live consumer or held
    /// until one subscribes. Records pushed after `complete` are ignored.
    public func push(_ stream: String, _ record: PipelineRecord) {
        precondition(!stream.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "stream required")
        lock.lock()
        if completed.contains(stream) { lock.unlock(); return }
        if let cont = continuations[stream] {
            lock.unlock()
            cont.yield(record)
        } else {
            buffers[stream, default: []].append(record)
            lock.unlock()
        }
    }

    /// Complete `stream` — the consumer's sequence ends after buffered records.
    public func complete(_ stream: String) {
        lock.lock()
        completed.insert(stream)
        let cont = continuations[stream]
        continuations[stream] = nil
        lock.unlock()
        cont?.finish()
    }

    public func read(_ stream: String) -> AsyncStream<PipelineRecord> {
        precondition(!stream.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "stream required")
        return AsyncStream(bufferingPolicy: .unbounded) { continuation in
            lock.lock()
            // Drain anything buffered before this consumer subscribed.
            if let pending = buffers[stream] {
                for r in pending { continuation.yield(r) }
                buffers[stream] = nil
            }
            if completed.contains(stream) {
                lock.unlock()
                continuation.finish()
                return
            }
            continuations[stream] = continuation
            lock.unlock()
        }
    }
}

// MARK: - InMemoryPipelineSink

/// In-memory sink — collects records into a list. (C# `InMemoryPipelineSink`.)
public final class InMemoryPipelineSink: IPipelineSink, @unchecked Sendable {
    private let lock = NSLock()
    private var records: [PipelineRecord] = []

    public init() {}

    public var backendId: String { "in-memory" }

    public func write(_ record: PipelineRecord) async {
        lock.lock(); records.append(record); lock.unlock()
    }

    public func flush() async { /* no-op — records held in memory */ }

    /// Snapshot of all written records.
    public var allRecords: [PipelineRecord] {
        lock.lock(); defer { lock.unlock() }
        return records
    }
}

// MARK: - InMemoryPipelineExecutor

/// In-memory executor: runs registered pipeline closures (each returns a row
/// count) and tracks runs. A thrown error becomes `PipelineRun.failureReason`.
/// (C# `InMemoryPipelineExecutor`.)
public final class InMemoryPipelineExecutor: IPipelineExecutor, @unchecked Sendable {
    private let lock = NSLock()
    private var pipelines: [String: @Sendable () async throws -> Int64] = [:]
    private var runs: [String: PipelineRun] = [:]
    private var runSeq: Int64 = 0

    public init() {}

    public var backendId: String { "in-memory" }

    /// Registers a runner for `pipelineId`. Replaces any prior runner.
    public func register(_ pipelineId: String, runner: @escaping @Sendable () async throws -> Int64) {
        precondition(!pipelineId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "pipelineId required")
        lock.lock(); pipelines[pipelineId] = runner; lock.unlock()
    }

    public func run(_ pipelineId: String) async -> PipelineRun {
        lock.lock()
        let runner = pipelines[pipelineId]
        lock.unlock()
        guard let runner = runner else {
            // Match the C# InvalidOperationException as a failed run — the
            // async protocol method is non-throwing, so surface it in the run.
            let now = Date()
            let run = PipelineRun(runId: nextRunId(), pipelineId: pipelineId, startUtc: now,
                                  endUtc: now, rowsProcessed: 0,
                                  failureReason: PipelineError.unknownPipeline(pipelineId).description)
            record(run)
            return run
        }
        let runId = nextRunId()
        let start = Date()
        var rows: Int64 = 0
        var err: String? = nil
        do { rows = try await runner() } catch { err = "\(error)" }
        let run = PipelineRun(runId: runId, pipelineId: pipelineId, startUtc: start,
                              endUtc: Date(), rowsProcessed: rows, failureReason: err)
        record(run)
        return run
    }

    public func getRun(_ runId: String) async -> PipelineRun? {
        precondition(!runId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "runId required")
        lock.lock(); defer { lock.unlock() }
        return runs[runId]
    }

    private func nextRunId() -> String {
        lock.lock(); runSeq += 1; let n = runSeq; lock.unlock()
        return "run-\(n)"
    }

    private func record(_ run: PipelineRun) {
        lock.lock(); runs[run.runId] = run; lock.unlock()
    }
}

// MARK: - InMemoryDatabaseQueryTool

/// Tiny in-memory database — supports "SELECT * FROM <table>" against
/// registered tables. Ports the C# parser verbatim. (C# `InMemoryDatabaseQueryTool`.)
public final class InMemoryDatabaseQueryTool: IDatabaseQueryTool, @unchecked Sendable {
    private let lock = NSLock()
    /// Table name (lower-cased key) → rows. Case-insensitive lookup like the
    /// C# `StringComparer.OrdinalIgnoreCase` dictionary.
    private var tables: [String: [[String: AnyCodable]]] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    /// Inserts `row` into `tableName`.
    public func insert(_ tableName: String, row: [String: AnyCodable]) {
        precondition(!tableName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "tableName required")
        lock.lock(); tables[tableName.lowercased(), default: []].append(row); lock.unlock()
    }

    public func query(_ sql: String, parameters: [String: AnyCodable]?) async throws -> DatabaseQueryResult {
        precondition(!sql.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "sql required")
        let trimmed = sql.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.uppercased().hasPrefix("SELECT ") else {
            throw PipelineError.onlySelectSupported
        }
        // Find "FROM " (case-insensitive).
        guard let fromRange = trimmed.range(of: "FROM ", options: .caseInsensitive) else {
            throw PipelineError.selectRequiresFrom
        }
        let rest = String(trimmed[fromRange.upperBound...]).trimmingCharacters(in: .whitespacesAndNewlines)
        // Table name = up to the next space or ';'.
        var tableName = rest
        if let idx = rest.firstIndex(where: { $0 == " " || $0 == ";" }) {
            tableName = String(rest[..<idx])
        }
        lock.lock(); let rows = tables[tableName.lowercased()]; lock.unlock()
        guard let rows = rows else {
            return DatabaseQueryResult(rows: [], rowCount: 0)
        }
        return DatabaseQueryResult(rows: rows, rowCount: rows.count)
    }
}

// MARK: - Null implementations

/// Fail-closed source — yields nothing. (C# `NullPipelineSource`.)
public final class NullPipelineSource: IPipelineSource, @unchecked Sendable {
    public static let instance = NullPipelineSource()
    public init() {}
    public var backendId: String { "null" }
    public func read(_ stream: String) -> AsyncStream<PipelineRecord> {
        AsyncStream { $0.finish() }
    }
}

/// Fail-closed sink — discards writes. (C# `NullPipelineSink`.)
public final class NullPipelineSink: IPipelineSink, @unchecked Sendable {
    public static let instance = NullPipelineSink()
    public init() {}
    public var backendId: String { "null" }
    public func write(_ record: PipelineRecord) async {}
    public func flush() async {}
}

/// Fail-closed executor — every run fails with the empty GUID. (C# `NullPipelineExecutor`.)
public final class NullPipelineExecutor: IPipelineExecutor, @unchecked Sendable {
    public static let instance = NullPipelineExecutor()
    public init() {}
    public var backendId: String { "null" }
    public func run(_ pipelineId: String) async -> PipelineRun {
        PipelineRun(runId: "00000000-0000-0000-0000-000000000000", pipelineId: pipelineId,
                    startUtc: IntegrationDates.minValue, endUtc: IntegrationDates.minValue,
                    rowsProcessed: 0, failureReason: "NullPipelineExecutor")
    }
    public func getRun(_ runId: String) async -> PipelineRun? { nil }
}

/// Fail-closed query tool — always empty. (C# `NullDatabaseQueryTool`.)
public final class NullDatabaseQueryTool: IDatabaseQueryTool, @unchecked Sendable {
    public static let instance = NullDatabaseQueryTool()
    public init() {}
    public var backendId: String { "null" }
    public func query(_ sql: String, parameters: [String: AnyCodable]?) async throws -> DatabaseQueryResult {
        DatabaseQueryResult(rows: [], rowCount: 0)
    }
}
