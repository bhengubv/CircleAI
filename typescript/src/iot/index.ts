// iot/index.ts
// Full-parity port of CircleAI.IoT board primitives (C#). C# is the exact spec.
//
// Domain types + in-memory store for the IoT vertical: a device registry,
// telemetry with a latest-value lookup and bounded history, and per-device
// command log.
//
// NOTE: CircleAI.IoT has no *DomainContext (unlike the other domain boards).
// The C# IoTCompanionPipeline (a voice-in → Companion → voice-out wiring over
// CircleAI.Voice + CircleAI.Companion) is intentionally NOT ported — it depends
// on the out-of-scope Voice pipeline, and the work unit scopes IoT to the board
// primitives (IIoTBoard / IoTDevice / IoTTelemetry / IoTCommand).
//
// Type mappings (C# → TS):
//   record                          → readonly interface (+ positional factory)
//   double Value                     → number
//   DateTimeOffset ...Utc            → Date
//   double.NaN                       → Number.NaN
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   Devices      — ordered by Name ascending (default comparer / ordinal).
//   LatestValue  — newest telemetry (AtUtc descending) for device+metric, or NaN.
//   History      — device+metric telemetry, AtUtc descending, take limit.
//   CommandsFor  — device commands, SentUtc descending.

/** An IoT device. Mirrors C# `IoTDevice` record. */
export interface IoTDevice {
  readonly deviceId: string;
  readonly name: string;
  readonly kind: string;
  readonly firmwareVersion: string;
  /** UTC instant the device was last seen (C# `DateTimeOffset LastSeenUtc`). */
  readonly lastSeenUtc: Date;
}

/** Constructs an {@link IoTDevice}. */
export function ioTDevice(
  deviceId: string,
  name: string,
  kind: string,
  firmwareVersion: string,
  lastSeenUtc: Date,
): IoTDevice {
  return { deviceId, name, kind, firmwareVersion, lastSeenUtc };
}

/** A telemetry sample. Mirrors C# `IoTTelemetry` record. */
export interface IoTTelemetry {
  readonly deviceId: string;
  readonly metric: string;
  readonly value: number;
  /** UTC instant of the reading (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs an {@link IoTTelemetry}. */
export function ioTTelemetry(deviceId: string, metric: string, value: number, atUtc: Date): IoTTelemetry {
  return { deviceId, metric, value, atUtc };
}

/** A command sent to a device. Mirrors C# `IoTCommand` record. */
export interface IoTCommand {
  readonly commandId: string;
  readonly deviceId: string;
  readonly action: string;
  readonly argumentsJson: string;
  /** UTC instant the command was sent (C# `DateTimeOffset SentUtc`). */
  readonly sentUtc: Date;
}

/** Constructs an {@link IoTCommand}. */
export function ioTCommand(
  commandId: string,
  deviceId: string,
  action: string,
  argumentsJson: string,
  sentUtc: Date,
): IoTCommand {
  return { commandId, deviceId, action, argumentsJson, sentUtc };
}

/** The IoT board contract. Mirrors C# `IIoTBoard`. */
export interface IIoTBoard {
  register(d: IoTDevice): void;
  getDevice(id: string): IoTDevice | undefined;
  readonly devices: readonly IoTDevice[];
  recordTelemetry(t: IoTTelemetry): void;
  latestValue(deviceId: string, metric: string): number;
  history(deviceId: string, metric: string, limit?: number): readonly IoTTelemetry[];
  sendCommand(c: IoTCommand): void;
  commandsFor(deviceId: string): readonly IoTCommand[];
}

/** Ordinal (code-unit) string comparison, matching C# StringComparer.Ordinal. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** Deterministic in-memory {@link IIoTBoard}. */
export class InMemoryIoTBoard implements IIoTBoard {
  private readonly devicesById = new Map<string, IoTDevice>();
  private readonly telemetry: IoTTelemetry[] = [];
  private readonly commands: IoTCommand[] = [];

  register(d: IoTDevice): void {
    if (d == null) throw new Error("d required");
    this.devicesById.set(d.deviceId, d);
  }

  getDevice(id: string): IoTDevice | undefined {
    return this.devicesById.get(id);
  }

  get devices(): readonly IoTDevice[] {
    return [...this.devicesById.values()].sort((a, b) => ordinalCompare(a.name, b.name));
  }

  recordTelemetry(t: IoTTelemetry): void {
    if (t == null) throw new Error("t required");
    this.telemetry.push(t);
  }

  latestValue(deviceId: string, metric: string): number {
    const matches = this.telemetry
      .filter((t) => t.deviceId === deviceId && t.metric === metric)
      .sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime());
    return matches.length > 0 ? matches[0].value : Number.NaN;
  }

  history(deviceId: string, metric: string, limit = 100): readonly IoTTelemetry[] {
    if (limit <= 0) throw new Error("limit");
    return this.telemetry
      .filter((t) => t.deviceId === deviceId && t.metric === metric)
      .sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime())
      .slice(0, limit);
  }

  sendCommand(c: IoTCommand): void {
    if (c == null) throw new Error("c required");
    this.commands.push(c);
  }

  commandsFor(deviceId: string): readonly IoTCommand[] {
    return this.commands.filter((c) => c.deviceId === deviceId).sort((a, b) => b.sentUtc.getTime() - a.sentUtc.getTime());
  }
}
