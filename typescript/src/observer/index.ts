// observer/index.ts
//
// Full-parity port of CircleAI.Observer (C#). C# is the exact spec.
//
// The perceive-reason-act observation loop: ISensor / IObservationToolbox /
// IObservationLoop contracts, SensorReading / ObservationTool / ObservationTick
// records, the SensorRecorder + ObserverDecision helpers, the real
// InMemoryObservationLoop runtime, the InMemoryObservationToolbox, and the
// Null* defaults.
//
// Type mappings (C# → TS):
//   record                                → readonly interface (+ positional factory)
//   IReadOnlyDictionary<string,string>    → Readonly<Record<string,string>>
//   ReadOnlyMemory<byte>?                 → Uint8Array | null
//   DateTimeOffset                        → Date
//   IDisposable Subscribe(...)            → { dispose(): void }
//   IAsyncDisposable                      → { disposeAsync(): Promise<void> }
//   Func<SensorReading, ValueTask>        → (r: SensorReading) => Promise<void>
//   TimeSpan tickInterval                 → number (milliseconds)
//   CancellationToken                     → dropped (loop lifetime is start/stop)
//
// CONCURRENCY SAFETY: subscribers are snapshotted before fan-out; the loop
// fires each tick after the reason() call, tolerating handler/tool exceptions.

/** A disposable subscription handle (mirrors C# `IDisposable`). */
export interface Disposable {
  dispose(): void;
}

/** One snapshot from one sensor. Mirrors C# `SensorReading`. */
export interface SensorReading {
  readonly sensorId: string;
  readonly kind: string;
  /** UTC capture instant (C# `DateTimeOffset CapturedAtUtc`). */
  readonly capturedAtUtc: Date;
  readonly values: Readonly<Record<string, string>>;
  readonly payload: Uint8Array | null;
}

/** Constructs a {@link SensorReading}. */
export function sensorReading(
  sensorId: string,
  kind: string,
  capturedAtUtc: Date,
  values: Readonly<Record<string, string>>,
  payload: Uint8Array | null = null,
): SensorReading {
  return { sensorId, kind, capturedAtUtc, values, payload };
}

/** A single perception source. Mirrors C# `ISensor` (IAsyncDisposable). */
export interface ISensor {
  readonly sensorId: string;
  readonly kind: string;
  readonly backendId: string;
  startAsync(): Promise<void>;
  stopAsync(): Promise<void>;
  subscribe(handler: (r: SensorReading) => Promise<void>): Disposable;
  disposeAsync(): Promise<void>;
}

/** One tool the observer can invoke during its act tick. Mirrors C# `ObservationTool`. */
export interface ObservationTool {
  readonly toolId: string;
  readonly description: string;
  readonly tags: readonly string[];
  readonly invoke: (args: Readonly<Record<string, string>>) => Promise<string>;
}

/** Constructs an {@link ObservationTool}. */
export function observationTool(
  toolId: string,
  description: string,
  tags: readonly string[],
  invoke: (args: Readonly<Record<string, string>>) => Promise<string>,
): ObservationTool {
  return { toolId, description, tags, invoke };
}

/** Registry of tools available to the observation loop. Mirrors C# `IObservationToolbox`. */
export interface IObservationToolbox {
  readonly backendId: string;
  registerTool(tool: ObservationTool): void;
  /** Returns the tool for `toolId`, or null when absent (C# `TryGet` out-param). */
  tryGet(toolId: string): ObservationTool | null;
  listTools(): readonly ObservationTool[];
}

/** One loop tick — perceived / reasoning / tools-invoked. Mirrors C# `ObservationTick`. */
export interface ObservationTick {
  /** UTC instant of the tick (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
  readonly perceived: readonly SensorReading[];
  readonly reasoning: string;
  readonly toolsInvoked: readonly string[];
}

/** Constructs an {@link ObservationTick}. */
export function observationTick(
  atUtc: Date,
  perceived: readonly SensorReading[],
  reasoning: string,
  toolsInvoked: readonly string[],
): ObservationTick {
  return { atUtc, perceived, reasoning, toolsInvoked };
}

/** The perceive-reason-act loop. Mirrors C# `IObservationLoop` (IAsyncDisposable). */
export interface IObservationLoop {
  readonly backendId: string;
  /** Starts ticking every `tickIntervalMs` milliseconds. */
  startAsync(tickIntervalMs: number): Promise<void>;
  stopAsync(): Promise<void>;
  subscribe(handler: (t: ObservationTick) => Promise<void>): Disposable;
  disposeAsync(): Promise<void>;
}

/** Decision shape returned by the reasoner. Mirrors C# `ObserverDecision`. */
export interface ObserverDecision {
  readonly reasoning: string;
  readonly toolsToInvoke: readonly string[];
  readonly toolArgs: Readonly<Record<string, string>> | null;
}

/** Constructs an {@link ObserverDecision}. */
export function observerDecision(
  reasoning: string,
  toolsToInvoke: readonly string[],
  toolArgs: Readonly<Record<string, string>> | null = null,
): ObserverDecision {
  return { reasoning, toolsToInvoke, toolArgs };
}

// ─────────────────────────────────────────────────────────────────────────────
// SensorRecorder — captures the latest reading from a sensor.
// ─────────────────────────────────────────────────────────────────────────────

/** Captures the latest reading from a sensor. Mirrors C# `SensorRecorder`. */
export class SensorRecorder implements Disposable {
  private readonly sub: Disposable;
  private latestReading: SensorReading | null = null;

  constructor(sensor: ISensor) {
    if (sensor == null) throw new Error("sensor required");
    this.sub = sensor.subscribe(async (r) => {
      this.latestReading = r;
    });
  }

  get latest(): SensorReading | null {
    return this.latestReading;
  }

  dispose(): void {
    this.sub.dispose();
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryObservationLoop — the real perceive-reason-act runtime.
// ─────────────────────────────────────────────────────────────────────────────

/** The perceive-reason-act loop. Mirrors C# `InMemoryObservationLoop`. */
export class InMemoryObservationLoop implements IObservationLoop {
  private readonly recorders: readonly SensorRecorder[];
  private readonly toolbox: IObservationToolbox;
  private readonly reason: (readings: readonly SensorReading[]) => Promise<ObserverDecision>;
  private readonly subs: Array<(t: ObservationTick) => Promise<void>> = [];
  private running = false;
  private runPromise: Promise<void> | null = null;

  constructor(
    sensors: Iterable<ISensor>,
    toolbox: IObservationToolbox,
    reason: (readings: readonly SensorReading[]) => Promise<ObserverDecision>,
  ) {
    if (sensors == null) throw new Error("sensors required");
    if (toolbox == null) throw new Error("toolbox required");
    if (reason == null) throw new Error("reason required");
    this.toolbox = toolbox;
    this.reason = reason;
    this.recorders = [...sensors].map((s) => new SensorRecorder(s));
  }

  get backendId(): string {
    return "in-memory";
  }

  async startAsync(tickIntervalMs: number): Promise<void> {
    if (this.running) throw new Error("already started");
    this.running = true;
    this.runPromise = this.run(tickIntervalMs);
  }

  async stopAsync(): Promise<void> {
    if (!this.running) return;
    this.running = false;
    try {
      if (this.runPromise !== null) await this.runPromise;
    } catch {
      /* expected on cancellation */
    }
    this.runPromise = null;
  }

  subscribe(handler: (t: ObservationTick) => Promise<void>): Disposable {
    if (handler == null) throw new Error("handler required");
    this.subs.push(handler);
    const self = this;
    return {
      dispose(): void {
        const idx = self.subs.indexOf(handler);
        if (idx >= 0) self.subs.splice(idx, 1);
      },
    };
  }

  async disposeAsync(): Promise<void> {
    await this.stopAsync();
    for (const r of this.recorders) r.dispose();
  }

  private async run(intervalMs: number): Promise<void> {
    while (this.running) {
      try {
        const readings = this.recorders.map((r) => r.latest).filter((r): r is SensorReading => r !== null);
        const decision = await this.reason(readings);
        const invoked: string[] = [];
        for (const toolId of decision.toolsToInvoke) {
          const tool = this.toolbox.tryGet(toolId);
          if (tool !== null) {
            try {
              await tool.invoke(decision.toolArgs ?? {});
              invoked.push(toolId);
            } catch {
              /* tool threw — skip, mirror C# swallow */
            }
          }
        }
        const tick = observationTick(new Date(), readings, decision.reasoning, invoked);
        // Snapshot subscribers before fan-out (concurrency safety).
        const snap = [...this.subs];
        for (const s of snap) {
          try {
            await s(tick);
          } catch {
            /* subscriber threw — skip */
          }
        }
      } catch {
        /* reasoner threw — skip this tick */
      }
      if (!this.running) break;
      await delay(intervalMs);
    }
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryObservationToolbox
// ─────────────────────────────────────────────────────────────────────────────

/** In-memory tool registry. Mirrors C# `InMemoryObservationToolbox`. */
export class InMemoryObservationToolbox implements IObservationToolbox {
  private readonly tools = new Map<string, ObservationTool>();

  get backendId(): string {
    return "in-memory";
  }

  registerTool(tool: ObservationTool): void {
    this.tools.set(tool.toolId, tool);
  }

  tryGet(toolId: string): ObservationTool | null {
    return this.tools.get(toolId) ?? null;
  }

  listTools(): readonly ObservationTool[] {
    return [...this.tools.values()];
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null* defaults
// ─────────────────────────────────────────────────────────────────────────────

const emptyDisposable: Disposable = { dispose(): void {} };

/** Fail-safe {@link ISensor} — emits nothing. */
export class NullSensor implements ISensor {
  readonly sensorId = "null";
  readonly kind = "null";
  get backendId(): string {
    return "null";
  }
  async startAsync(): Promise<void> {
    /* no-op */
  }
  async stopAsync(): Promise<void> {
    /* no-op */
  }
  subscribe(): Disposable {
    return emptyDisposable;
  }
  async disposeAsync(): Promise<void> {
    /* no-op */
  }
}

/** Fail-safe {@link IObservationLoop} — never ticks. */
export class NullObservationLoop implements IObservationLoop {
  get backendId(): string {
    return "null";
  }
  async startAsync(): Promise<void> {
    /* no-op */
  }
  async stopAsync(): Promise<void> {
    /* no-op */
  }
  subscribe(): Disposable {
    return emptyDisposable;
  }
  async disposeAsync(): Promise<void> {
    /* no-op */
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, Math.max(0, ms)));
}
