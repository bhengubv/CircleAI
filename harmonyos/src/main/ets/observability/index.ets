/**
 * observability/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// observability/index.ts
//
// Full-parity port of CircleAI.Observability (C#). C# is the exact spec.
//
// Metric sink, trace sink, dashboard publisher: contracts + records +
// deterministic in-memory implementations + drop-all Null* defaults.
//
// Type mappings (C# → TS):
//   record                                → readonly interface (+ positional factory)
//   double Value                          → number
//   IReadOnlyDictionary<string,string>?   → Readonly<Map<string, string>> | null
//   TimeSpan Duration                     → number (milliseconds)
//   DateTimeOffset StartUtc               → Date
//   ValueTask<T>                          → Promise<T>

// ─────────────────────────────────────────────────────────────────────────────
// Records
// ─────────────────────────────────────────────────────────────────────────────

/** One metric sample. Mirrors C# `MetricSample`. */
export interface MetricSample {
  readonly name: string;
  readonly value: number;
  readonly tags: Readonly<Map<string, string>> | null;
}

/** Constructs a {@link MetricSample}. */
export function metricSample(name: string, value: number, tags: Readonly<Map<string, string>> | null = null): MetricSample {
  return { name, value, tags };
}

/** One trace span. Mirrors C# `TraceSpan`. */
export interface TraceSpan {
  readonly traceId: string;
  readonly spanId: string;
  readonly parentSpanId: string | null;
  readonly name: string;
  /** UTC start instant (C# `DateTimeOffset StartUtc`). */
  readonly startUtc: Date;
  /** Span duration in milliseconds (C# `TimeSpan Duration`). */
  readonly durationMs: number;
  readonly attributes: Readonly<Map<string, string>> | null;
}

/** Constructs a {@link TraceSpan}. */
export function traceSpan(
  traceId: string,
  spanId: string,
  parentSpanId: string | null,
  name: string,
  startUtc: Date,
  durationMs: number,
  attributes: Readonly<Map<string, string>> | null = null,
): TraceSpan {
  return { traceId, spanId, parentSpanId, name, startUtc, durationMs, attributes };
}

/** One dashboard spec. Mirrors C# `DashboardSpec`. */
export interface DashboardSpec {
  readonly dashboardId: string;
  readonly title: string;
  readonly jsonBlob: string;
}

/** Constructs a {@link DashboardSpec}. */
export function dashboardSpec(dashboardId: string, title: string, jsonBlob: string): DashboardSpec {
  return { dashboardId, title, jsonBlob };
}

// ─────────────────────────────────────────────────────────────────────────────
// Contracts
// ─────────────────────────────────────────────────────────────────────────────

/** Metric sink — Prometheus / OTel. Mirrors C# `IMetricSink`. */
export interface IMetricSink {
  readonly backendId: string;
  emitAsync(sample: MetricSample): Promise<void>;
}

/** Trace sink — OTel. Mirrors C# `ITraceSink`. */
export interface ITraceSink {
  readonly backendId: string;
  emitAsync(span: TraceSpan): Promise<void>;
}

/** Dashboard publisher — Grafana. Mirrors C# `IDashboardPublisher`. */
export interface IDashboardPublisher {
  readonly backendId: string;
  publishAsync(spec: DashboardSpec): Promise<void>;
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory implementations
// ─────────────────────────────────────────────────────────────────────────────

/** In-memory metric sink (per-name buckets). Mirrors C# `InMemoryMetricSink`. */
export class InMemoryMetricSink implements IMetricSink {
  private readonly byName = new Map<string, MetricSample[]>();

  get backendId(): string {
    return "in-memory";
  }

  async emitAsync(sample: MetricSample): Promise<void> {
    if (sample == null) throw new Error("sample required");
    if (sample.name == null || sample.name.trim().length === 0) throw new Error("Name required");
    const list = this.byName.get(sample.name) ?? [];
    list.push(sample);
    this.byName.set(sample.name, list);
  }

  read(name: string): readonly MetricSample[] {
    const list = this.byName.get(name);
    return list === undefined ? [] : [...list];
  }

  get metricNames(): readonly string[] {
    return [...this.byName.keys()].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
  }
}

/** In-memory trace sink (per-trace spans). Mirrors C# `InMemoryTraceSink`. */
export class InMemoryTraceSink implements ITraceSink {
  private readonly byTrace = new Map<string, TraceSpan[]>();

  get backendId(): string {
    return "in-memory";
  }

  async emitAsync(span: TraceSpan): Promise<void> {
    if (span == null) throw new Error("span required");
    if (span.traceId == null || span.traceId.trim().length === 0) throw new Error("TraceId required");
    const list = this.byTrace.get(span.traceId) ?? [];
    list.push(span);
    this.byTrace.set(span.traceId, list);
  }

  read(traceId: string): readonly TraceSpan[] {
    const list = this.byTrace.get(traceId);
    if (list === undefined) return [];
    return [...list].sort((a, b) => a.startUtc.getTime() - b.startUtc.getTime());
  }
}

/** In-memory dashboard publisher. Mirrors C# `InMemoryDashboardPublisher`. */
export class InMemoryDashboardPublisher implements IDashboardPublisher {
  private readonly specs = new Map<string, DashboardSpec>();

  get backendId(): string {
    return "in-memory";
  }

  async publishAsync(spec: DashboardSpec): Promise<void> {
    if (spec == null) throw new Error("spec required");
    if (spec.dashboardId == null || spec.dashboardId.trim().length === 0) throw new Error("DashboardId required");
    this.specs.set(spec.dashboardId, spec);
  }

  get(dashboardId: string): DashboardSpec | null {
    return this.specs.get(dashboardId) ?? null;
  }

  get all(): readonly DashboardSpec[] {
    return [...this.specs.values()].sort((a, b) => (a.dashboardId < b.dashboardId ? -1 : a.dashboardId > b.dashboardId ? 1 : 0));
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null* defaults — signals go nowhere
// ─────────────────────────────────────────────────────────────────────────────

/** Drop-all {@link IMetricSink}. */
export class NullMetricSink implements IMetricSink {
  static readonly instance = new NullMetricSink();
  get backendId(): string {
    return "null";
  }
  async emitAsync(): Promise<void> {
    /* no-op */
  }
}

/** Drop-all {@link ITraceSink}. */
export class NullTraceSink implements ITraceSink {
  static readonly instance = new NullTraceSink();
  get backendId(): string {
    return "null";
  }
  async emitAsync(): Promise<void> {
    /* no-op */
  }
}

/** Drop-all {@link IDashboardPublisher}. */
export class NullDashboardPublisher implements IDashboardPublisher {
  static readonly instance = new NullDashboardPublisher();
  get backendId(): string {
    return "null";
  }
  async publishAsync(): Promise<void> {
    /* no-op */
  }
}
