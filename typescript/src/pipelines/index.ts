// pipelines/index.ts
// Full-parity port of CircleAI.Pipelines (C#). C# is the exact spec.
//
// Data-pipeline contracts: PipelineRecord / PipelineRun / DatabaseQueryResult
// records, the IPipelineSource / IPipelineSink / IPipelineExecutor /
// IDatabaseQueryTool contracts, deterministic in-memory implementations
// (unbounded per-stream channels for the source, a run-tracking executor, a
// tiny SELECT-only in-memory database), and fail-closed Null* defaults.
//
// Type mappings (C# → TS):
//   IReadOnlyDictionary<string, object?>  → ReadonlyMap<string, unknown>
//   ReadOnlyMemory<byte> / long RowsProcessed → number
//   IAsyncEnumerable<PipelineRecord>      → AsyncGenerator<PipelineRecord>
//   Channel.CreateUnbounded<T>()          → AsyncQueue<T>
//   Interlocked.Increment(ref _runSeq)    → ++this.runSeq

import { AsyncQueue } from "../companion/herjarvis/async_queue.js";

/** A single record flowing through a pipeline stream. Mirrors C# `PipelineRecord`. */
export interface PipelineRecord {
  readonly stream: string;
  readonly values: ReadonlyMap<string, unknown>;
}

/** Constructs a {@link PipelineRecord}. */
export function pipelineRecord(
  stream: string,
  values: ReadonlyMap<string, unknown>,
): PipelineRecord {
  return { stream, values };
}

/** The outcome of one pipeline run. Mirrors C# `PipelineRun` record. */
export interface PipelineRun {
  readonly runId: string;
  readonly pipelineId: string;
  readonly startUtc: Date;
  readonly endUtc: Date | null;
  readonly rowsProcessed: number;
  readonly failureReason: string | null;
}

/** Constructs a {@link PipelineRun}. */
export function pipelineRun(
  runId: string,
  pipelineId: string,
  startUtc: Date,
  endUtc: Date | null,
  rowsProcessed: number,
  failureReason: string | null,
): PipelineRun {
  return { runId, pipelineId, startUtc, endUtc, rowsProcessed, failureReason };
}

/** Result of a database query. Mirrors C# `DatabaseQueryResult` record. */
export interface DatabaseQueryResult {
  readonly rows: readonly ReadonlyMap<string, unknown>[];
  readonly rowCount: number;
}

/** Constructs a {@link DatabaseQueryResult}. */
export function databaseQueryResult(
  rows: readonly ReadonlyMap<string, unknown>[],
  rowCount: number,
): DatabaseQueryResult {
  return { rows, rowCount };
}

/** Reads records from a named stream. Mirrors C# `IPipelineSource`. */
export interface IPipelineSource {
  readonly backendId: string;
  readAsync(stream: string, signal?: AbortSignal): AsyncGenerator<PipelineRecord>;
}

/** Writes records to a sink. Mirrors C# `IPipelineSink`. */
export interface IPipelineSink {
  readonly backendId: string;
  writeAsync(record: PipelineRecord, signal?: AbortSignal): Promise<void>;
  flushAsync(signal?: AbortSignal): Promise<void>;
}

/** Runs registered pipelines and tracks runs. Mirrors C# `IPipelineExecutor`. */
export interface IPipelineExecutor {
  readonly backendId: string;
  runAsync(pipelineId: string, signal?: AbortSignal): Promise<PipelineRun>;
  getRunAsync(runId: string, signal?: AbortSignal): Promise<PipelineRun | null>;
}

/** Executes SQL against a data store. Mirrors C# `IDatabaseQueryTool`. */
export interface IDatabaseQueryTool {
  readonly backendId: string;
  queryAsync(
    sql: string,
    parameters?: ReadonlyMap<string, unknown> | null,
    signal?: AbortSignal,
  ): Promise<DatabaseQueryResult>;
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory implementations
// ─────────────────────────────────────────────────────────────────────────────

/**
 * In-memory {@link IPipelineSource} — one unbounded channel per stream. Mirrors
 * C# `InMemoryPipelineSource`.
 */
export class InMemoryPipelineSource implements IPipelineSource {
  private readonly streams = new Map<string, AsyncQueue<PipelineRecord>>();
  readonly backendId = "in-memory";

  private channel(stream: string): AsyncQueue<PipelineRecord> {
    let ch = this.streams.get(stream);
    if (ch === undefined) {
      ch = new AsyncQueue<PipelineRecord>();
      this.streams.set(stream, ch);
    }
    return ch;
  }

  /** Push a record onto a stream. Mirrors C# `Push`. */
  push(stream: string, record: PipelineRecord): void {
    if (isBlank(stream)) throw new Error("stream required");
    if (record == null) throw new Error("record required");
    this.channel(stream).enqueue(record);
  }

  /** Complete a stream so readers finish once drained. Mirrors C# `Complete`. */
  complete(stream: string): void {
    const ch = this.streams.get(stream);
    if (ch !== undefined) ch.complete();
  }

  async *readAsync(stream: string, signal?: AbortSignal): AsyncGenerator<PipelineRecord> {
    if (isBlank(stream)) throw new Error("stream required");
    // Subscribe to the channel synchronously (create it before iterating) so a
    // producer that pushes after readAsync starts is not missed.
    const ch = this.channel(stream);
    yield* ch.drain(signal);
  }
}

/** In-memory {@link IPipelineSink} — appends every record. Mirrors C# `InMemoryPipelineSink`. */
export class InMemoryPipelineSink implements IPipelineSink {
  private readonly recordList: PipelineRecord[] = [];
  readonly backendId = "in-memory";

  writeAsync(record: PipelineRecord, _signal?: AbortSignal): Promise<void> {
    if (record == null) throw new Error("record required");
    this.recordList.push(record);
    return Promise.resolve();
  }

  flushAsync(_signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }

  /** Snapshot of every written record. Mirrors C# `Records`. */
  get records(): readonly PipelineRecord[] {
    return [...this.recordList];
  }
}

/** A registered pipeline runner delegate. Mirrors C# `Func<CancellationToken, Task<long>>`. */
export type PipelineRunner = (signal?: AbortSignal) => Promise<number>;

/**
 * In-memory {@link IPipelineExecutor} — runs registered pipelines and tracks runs.
 * Mirrors C# `InMemoryPipelineExecutor`.
 */
export class InMemoryPipelineExecutor implements IPipelineExecutor {
  private readonly pipelines = new Map<string, PipelineRunner>();
  private readonly runs = new Map<string, PipelineRun>();
  private runSeq = 0;
  readonly backendId = "in-memory";

  /** Register (replacing) a runner for a pipeline id. Mirrors C# `Register`. */
  register(pipelineId: string, runner: PipelineRunner): void {
    if (isBlank(pipelineId)) throw new Error("pipelineId required");
    if (runner == null) throw new Error("runner required");
    this.pipelines.set(pipelineId, runner);
  }

  async runAsync(pipelineId: string, signal?: AbortSignal): Promise<PipelineRun> {
    if (isBlank(pipelineId)) throw new Error("pipelineId required");
    const runner = this.pipelines.get(pipelineId);
    if (runner === undefined) throw new Error(`Unknown pipeline '${pipelineId}'.`);

    const runId = `run-${++this.runSeq}`;
    const start = new Date();
    let rows = 0;
    let err: string | null = null;
    try {
      rows = await runner(signal);
    } catch (ex) {
      err = (ex as Error)?.message ?? String(ex);
    }
    const run = pipelineRun(runId, pipelineId, start, new Date(), rows, err);
    this.runs.set(runId, run);
    return run;
  }

  getRunAsync(runId: string, _signal?: AbortSignal): Promise<PipelineRun | null> {
    if (isBlank(runId)) throw new Error("runId required");
    return Promise.resolve(this.runs.get(runId) ?? null);
  }
}

/**
 * Tiny in-memory database — supports simple `SELECT * FROM <table>` against
 * registered tables. Mirrors C# `InMemoryDatabaseQueryTool`. Table names are
 * matched case-insensitively.
 */
export class InMemoryDatabaseQueryTool implements IDatabaseQueryTool {
  private readonly tables = new Map<string, Array<Map<string, unknown>>>();
  readonly backendId = "in-memory";

  /** Insert a row into a table (created on first insert). Mirrors C# `Insert`. */
  insert(tableName: string, row: ReadonlyMap<string, unknown>): void {
    if (isBlank(tableName)) throw new Error("tableName required");
    if (row == null) throw new Error("row required");
    const lower = tableName.toLowerCase();
    let list = this.tables.get(lower);
    if (list === undefined) {
      list = [];
      this.tables.set(lower, list);
    }
    list.push(new Map(row));
  }

  queryAsync(
    sql: string,
    _parameters?: ReadonlyMap<string, unknown> | null,
    _signal?: AbortSignal,
  ): Promise<DatabaseQueryResult> {
    if (isBlank(sql)) throw new Error("sql required");
    const trimmed = sql.trim();
    if (!trimmed.toUpperCase().startsWith("SELECT ")) {
      throw new Error("Only SELECT queries are supported by InMemoryDatabaseQueryTool.");
    }

    const fromIdx = trimmed.toUpperCase().indexOf("FROM ");
    if (fromIdx < 0) throw new Error("SELECT requires a FROM clause.");
    const rest = trimmed.slice(fromIdx + 5).trim();
    const spaceIdx = indexOfAny(rest, [" ", ";"]);
    const tableName = spaceIdx > 0 ? rest.slice(0, spaceIdx) : rest;

    const list = this.tables.get(tableName.toLowerCase());
    if (list === undefined) return Promise.resolve(databaseQueryResult([], 0));

    const rows: ReadonlyMap<string, unknown>[] = list.map((r) => new Map(r));
    return Promise.resolve(databaseQueryResult(rows, rows.length));
  }
}

function indexOfAny(s: string, chars: readonly string[]): number {
  let min = -1;
  for (const c of chars) {
    const i = s.indexOf(c);
    if (i >= 0 && (min < 0 || i < min)) min = i;
  }
  return min;
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// Null implementations
// ─────────────────────────────────────────────────────────────────────────────

/** Fail-closed {@link IPipelineSource}. Mirrors C# `NullPipelineSource`. */
export class NullPipelineSource implements IPipelineSource {
  static readonly instance = new NullPipelineSource();
  readonly backendId = "null";
  // eslint-disable-next-line require-yield
  async *readAsync(_stream: string, _signal?: AbortSignal): AsyncGenerator<PipelineRecord> {
    return;
  }
}

/** Fail-closed {@link IPipelineSink}. Mirrors C# `NullPipelineSink`. */
export class NullPipelineSink implements IPipelineSink {
  static readonly instance = new NullPipelineSink();
  readonly backendId = "null";
  writeAsync(_record: PipelineRecord, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
  flushAsync(_signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
}

/** Fail-closed {@link IPipelineExecutor}. Mirrors C# `NullPipelineExecutor`. */
export class NullPipelineExecutor implements IPipelineExecutor {
  static readonly instance = new NullPipelineExecutor();
  readonly backendId = "null";
  runAsync(pipelineId: string, _signal?: AbortSignal): Promise<PipelineRun> {
    return Promise.resolve(
      pipelineRun(EMPTY_GUID, pipelineId, MIN_DATE, MIN_DATE, 0, "NullPipelineExecutor"),
    );
  }
  getRunAsync(_runId: string, _signal?: AbortSignal): Promise<PipelineRun | null> {
    return Promise.resolve(null);
  }
}

/** Fail-closed {@link IDatabaseQueryTool}. Mirrors C# `NullDatabaseQueryTool`. */
export class NullDatabaseQueryTool implements IDatabaseQueryTool {
  static readonly instance = new NullDatabaseQueryTool();
  readonly backendId = "null";
  queryAsync(
    _sql: string,
    _parameters?: ReadonlyMap<string, unknown> | null,
    _signal?: AbortSignal,
  ): Promise<DatabaseQueryResult> {
    return Promise.resolve(databaseQueryResult([], 0));
  }
}

/** C# `Guid.Empty.ToString()`. */
const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";
/** C# `DateTimeOffset.MinValue` (0001-01-01T00:00:00Z). */
const MIN_DATE = new Date("0001-01-01T00:00:00Z");
