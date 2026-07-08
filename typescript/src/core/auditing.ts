// core/auditing.ts
//
// Port of CircleAI.Core.Auditing:
//   • ICircleAIAuditLog          (ICircleAIAuditLog.cs)
//   • CircleAIAuditEntry         (immutable audit entry record)
//   • CircleAIAuditQuery         (query filter record)
//   • LoggerAuditLog             (writes structured entries to a logger)
//   • NoopAuditLog               (default — silently discards)
//   • CircleAIAuditing           (process-wide ambient sink)
//
// Tamper-aware audit surface for the CircleAI SDK. Every state-changing
// operation a CircleAIComponentBase-derived component performs is auto-recorded
// here. Default registration is NoopAuditLog — entries are silently dropped
// until a consumer wires LoggerAuditLog or their own append-only sink.

// ─────────────────────────────────────────────────────────────────────────────
// CircleAIAuditEntry
// ─────────────────────────────────────────────────────────────────────────────

/**
 * An immutable audit entry emitted by the CircleAI SDK.
 * Mirrors the C# `sealed record CircleAIAuditEntry`.
 */
export interface CircleAIAuditEntry {
  /** UTC timestamp of the action (C# `DateTimeOffset At`). */
  readonly at: Date;

  /**
   * Canonical CircleAI component name (e.g. "DefaultSecurityWatchdog",
   * "JsonPersonaProvider", "InMemoryFederationAggregator").
   */
  readonly component: string;

  /**
   * Logical operation name (e.g. "OnAnomalyDetectedAsync", "GetAsync",
   * "TryCommitAsync").
   */
  readonly operation: string;

  /** Outcome — one of {@link CircleAIDiagnostics.Outcomes}. */
  readonly outcome: string;

  /** Tenant id, when running multi-tenant. Null for single-tenant deployments. */
  readonly tenantId?: string | null;

  /**
   * User id (UHID) when the operation was scoped to a specific user.
   * Null for tenant-wide or device-wide operations.
   */
  readonly uhidIdentityId?: string | null;

  /**
   * Optional correlation id (e.g. session id, request id) for joining audit
   * entries with traces.
   */
  readonly correlationId?: string | null;

  /** Operation duration in milliseconds. */
  readonly durationMs: number;

  /**
   * When {@link outcome} is not "success", the exception type that was thrown
   * (e.g. "OperationCanceledException", "InvalidOperationException").
   */
  readonly errorType?: string | null;

  /** Implementation-supplied error code, when applicable. */
  readonly errorCode?: string | null;

  /**
   * Hash of any sensitive payload involved in the operation. Never carries the
   * raw payload — this exists so auditors can correlate without leaking
   * content. Null when no payload was involved.
   */
  readonly payloadSha256Hex?: string | null;
}

// ─────────────────────────────────────────────────────────────────────────────
// CircleAIAuditQuery
// ─────────────────────────────────────────────────────────────────────────────

/** Query filter for {@link ICircleAIAuditLog.queryAsync}. */
export interface CircleAIAuditQuery {
  /** Inclusive lower bound on {@link CircleAIAuditEntry.at}. */
  readonly fromUtc?: Date | null;

  /** Inclusive upper bound on {@link CircleAIAuditEntry.at}. */
  readonly toUtc?: Date | null;

  /** Restrict to a single component. */
  readonly component?: string | null;

  /** Restrict to a single tenant. */
  readonly tenantId?: string | null;

  /** Restrict to a single UHID identity. */
  readonly uhidIdentityId?: string | null;

  /** Restrict to a single outcome. */
  readonly outcome?: string | null;

  /** Maximum entries to return. Defaults to 1000. */
  readonly maxItems?: number;
}

/** Default MaxItems for a query, matching the C# record default. */
export const DEFAULT_AUDIT_QUERY_MAX_ITEMS = 1000;

// ─────────────────────────────────────────────────────────────────────────────
// ICircleAIAuditLog
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Tamper-aware audit surface for the CircleAI SDK.
 *
 * `recordAsync` MUST NOT throw — the caller may be mid-operation and audit-log
 * failure must never bring it down.
 *
 * `queryAsync` returns an async iterator over historical entries for compliance
 * reporting / forensic investigation.
 */
export interface ICircleAIAuditLog {
  /**
   * Record an audit entry. MUST NOT throw — implementations should catch and
   * log internally, failing open.
   */
  recordAsync(entry: CircleAIAuditEntry): Promise<void>;

  /**
   * Query historical entries. Implementations are expected to support
   * tenant-scoped queries when running multi-tenant.
   */
  queryAsync(query: CircleAIAuditQuery): AsyncIterable<CircleAIAuditEntry>;
}

// ─────────────────────────────────────────────────────────────────────────────
// A logger sink, matching Microsoft.Extensions.Logging.ILogger usage here.
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Minimal structured-logging sink. Stands in for the C# `ILogger<LoggerAuditLog>`
 * dependency — an injectable so LoggerAuditLog stays free of a concrete logging
 * framework. `console.info` is the natural default.
 */
export interface IAuditLogger {
  logInformation(message: string): void;
}

/** IAuditLogger backed by `console.info`. */
export const ConsoleAuditLogger: IAuditLogger = {
  logInformation(message: string): void {
    // eslint-disable-next-line no-console
    console.info(message);
  },
};

// ─────────────────────────────────────────────────────────────────────────────
// LoggerAuditLog
// ─────────────────────────────────────────────────────────────────────────────

/** Formats a Date the way C# `{At:O}` (round-trip ISO 8601) does. */
function formatRoundTrip(d: Date): string {
  return d.toISOString();
}

/**
 * {@link ICircleAIAuditLog} implementation that writes structured entries to an
 * {@link IAuditLogger} at Information level.
 *
 * `queryAsync` always yields nothing — reading back from a log sink isn't
 * possible at the SDK layer, matching the C# implementation.
 */
export class LoggerAuditLog implements ICircleAIAuditLog {
  private readonly logger: IAuditLogger;

  /** Construct with a logger. Defaults to a console-backed logger. */
  constructor(logger: IAuditLogger = ConsoleAuditLogger) {
    if (logger === null || logger === undefined)
      throw new Error("logger is required");
    this.logger = logger;
  }

  recordAsync(entry: CircleAIAuditEntry): Promise<void> {
    if (entry === null || entry === undefined)
      throw new Error("entry is required");
    // Structured logging — every field would be a queryable property in
    // Seq / Loki / OpenSearch. Rendered here into a single line, mirroring the
    // named-property template from the C# LoggerAuditLog.
    this.logger.logInformation(
      `CircleAI audit ${entry.component}.${entry.operation} ${entry.outcome} ` +
        `tenant=${entry.tenantId ?? "-"} uhid=${entry.uhidIdentityId ?? "-"} ` +
        `corr=${entry.correlationId ?? "-"} ` +
        `duration_ms=${entry.durationMs} ` +
        `error=${entry.errorType ?? "-"}(${entry.errorCode ?? "-"}) ` +
        `payload_sha256=${entry.payloadSha256Hex ?? "-"} at=${formatRoundTrip(entry.at)}`,
    );
    return Promise.resolve();
  }

  // eslint-disable-next-line @typescript-eslint/require-await
  async *queryAsync(
    _query: CircleAIAuditQuery,
  ): AsyncIterable<CircleAIAuditEntry> {
    // Query support is a sink-specific feature; a logger sink can't read back.
    return;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// NoopAuditLog
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Default {@link ICircleAIAuditLog} — silently discards every entry and returns
 * an empty query result. This is what a consumer gets if they wire no audit
 * sink. Exists so the component wrappers can emit unconditionally.
 */
export class NoopAuditLog implements ICircleAIAuditLog {
  /** Shared singleton instance. */
  static readonly instance: NoopAuditLog = new NoopAuditLog();

  recordAsync(_entry: CircleAIAuditEntry): Promise<void> {
    return Promise.resolve();
  }

  // eslint-disable-next-line @typescript-eslint/require-await
  async *queryAsync(
    _query: CircleAIAuditQuery,
  ): AsyncIterable<CircleAIAuditEntry> {
    return;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// CircleAIAuditing — process-wide ambient access point
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Process-wide ambient access point for the audit sink. Component Run wrappers
 * emit through {@link CircleAIAuditing.default} without depending on a DI
 * container.
 *
 * Initial value is {@link NoopAuditLog.instance}. Hosts wire the real sink via
 * {@link CircleAIAuditing.setDefault} during startup.
 */
export class CircleAIAuditing {
  private static _default: ICircleAIAuditLog = NoopAuditLog.instance;

  /**
   * The current ambient audit sink. Defaults to {@link NoopAuditLog} — replace
   * via {@link setDefault} during host startup.
   */
  static get default(): ICircleAIAuditLog {
    return CircleAIAuditing._default;
  }

  /**
   * Replace the ambient audit sink. Idempotent — calling repeatedly with the
   * same instance is safe.
   */
  static setDefault(audit: ICircleAIAuditLog): void {
    if (audit === null || audit === undefined)
      throw new Error("audit is required");
    CircleAIAuditing._default = audit;
  }

  /** Restore the default to {@link NoopAuditLog}. Test-helper. */
  static resetToNoop(): void {
    CircleAIAuditing._default = NoopAuditLog.instance;
  }

  private constructor() {
    /* static-only */
  }
}
