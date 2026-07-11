// wearable/index.ts
// Full-parity port of CircleAI.Wearable (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Wearable vertical: device descriptors,
// telemetry samples, latest/average rollups, and the WearableContext biometric
// snapshot record.
//
// NOTE: The C# WearableCompanionAdapter (an ICompanionSession LLM-prompt wrapper)
// is intentionally NOT ported — consistent with the sibling domain-board ports.
//
// Type mappings (C# → TS):
//   enum WearableKind                → const enum-like (Smartwatch=0..Headset=4)
//   enum WearableTelemetryKind       → const enum-like (HeartRate=0..OxygenPct=6)
//   record                           → readonly interface (+ positional factory)
//   double Value / BatteryPct        → number
//   double? LatestValue (return)     → number | undefined
//   double AverageValue (return)     → number (NaN when empty)
//   int? StepCountToday              → number | null
//   DateTimeOffset AtUtc/CapturedAt  → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   Devices      — all devices, Vendor ascending.
//   Record       — throws on unknown device; appends the sample.
//   ReadSince    — (device,kind) samples with AtUtc >= since, AtUtc ascending.
//   LatestValue  — most-recent (device,kind) Value, or undefined.
//   AverageValue — mean of ReadSince values, or NaN when empty.

/** A wearable device class. Mirrors C# `WearableKind` (Smartwatch = 0). */
export type WearableKind = 0 | 1 | 2 | 3 | 4;
/** Frozen value object for {@link WearableKind} members. */
export const WearableKind = Object.freeze({
  Smartwatch: 0,
  FitnessBand: 1,
  ChestStrap: 2,
  Patch: 3,
  Headset: 4,
} as const) satisfies Record<string, WearableKind>;

/** A wearable telemetry channel. Mirrors C# `WearableTelemetryKind` (HeartRate = 0). */
export type WearableTelemetryKind = 0 | 1 | 2 | 3 | 4 | 5 | 6;
/** Frozen value object for {@link WearableTelemetryKind} members. */
export const WearableTelemetryKind = Object.freeze({
  HeartRate: 0,
  Steps: 1,
  Calories: 2,
  SleepStage: 3,
  SkinTempC: 4,
  Stress: 5,
  OxygenPct: 6,
} as const) satisfies Record<string, WearableTelemetryKind>;

/** A wearable device descriptor. Mirrors C# `WearableDevice` record. */
export interface WearableDevice {
  readonly deviceId: string;
  readonly kind: WearableKind;
  readonly vendor: string;
  readonly firmwareVersion: string;
  readonly batteryPct: number;
}

/** Constructs a {@link WearableDevice}. */
export function wearableDevice(
  deviceId: string,
  kind: WearableKind,
  vendor: string,
  firmwareVersion: string,
  batteryPct: number,
): WearableDevice {
  return { deviceId, kind, vendor, firmwareVersion, batteryPct };
}

/** A telemetry sample. Mirrors C# `WearableSample` record. */
export interface WearableSample {
  readonly deviceId: string;
  readonly kind: WearableTelemetryKind;
  readonly value: number;
  /** UTC instant of the sample (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link WearableSample}. */
export function wearableSample(
  deviceId: string,
  kind: WearableTelemetryKind,
  value: number,
  atUtc: Date,
): WearableSample {
  return { deviceId, kind, value, atUtc };
}

/**
 * Biometric snapshot injected into the Companion context on wearable surfaces.
 * Values are optional — only populated when the sensor is available and
 * consented. Mirrors C# `WearableContext` record.
 */
export interface WearableContext {
  readonly heartRateBpm: number | null;
  readonly stepCountToday: number | null;
  readonly spO2Percent: number | null;
  readonly skinTempCelsius: number | null;
  readonly isWorkoutActive: boolean;
  /** UTC instant the snapshot was captured (C# `DateTimeOffset CapturedAt`). */
  readonly capturedAt: Date;
}

/** Constructs a {@link WearableContext}. */
export function wearableContext(
  heartRateBpm: number | null,
  stepCountToday: number | null,
  spO2Percent: number | null,
  skinTempCelsius: number | null,
  isWorkoutActive: boolean,
  capturedAt: Date,
): WearableContext {
  return { heartRateBpm, stepCountToday, spO2Percent, skinTempCelsius, isWorkoutActive, capturedAt };
}

/** The wearable board contract. Mirrors C# `IWearableBoard`. */
export interface IWearableBoard {
  add(d: WearableDevice): void;
  getDevice(id: string): WearableDevice | undefined;
  readonly devices: readonly WearableDevice[];
  record(s: WearableSample): void;
  readSince(deviceId: string, kind: WearableTelemetryKind, since: Date): readonly WearableSample[];
  latestValue(deviceId: string, kind: WearableTelemetryKind): number | undefined;
  averageValue(deviceId: string, kind: WearableTelemetryKind, since: Date): number;
}

/** Ordinal (code-unit) string comparison, matching C# StringComparer.Ordinal. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** Deterministic in-memory {@link IWearableBoard}. */
export class InMemoryWearableBoard implements IWearableBoard {
  private readonly devicesById = new Map<string, WearableDevice>();
  private readonly samples: WearableSample[] = [];

  add(d: WearableDevice): void {
    if (d == null) throw new Error("d required");
    this.devicesById.set(d.deviceId, d);
  }

  getDevice(id: string): WearableDevice | undefined {
    return this.devicesById.get(id);
  }

  get devices(): readonly WearableDevice[] {
    return [...this.devicesById.values()].sort((a, b) => ordinalCompare(a.vendor, b.vendor));
  }

  record(s: WearableSample): void {
    if (s == null) throw new Error("s required");
    if (!this.devicesById.has(s.deviceId)) throw new Error(`Unknown device ${s.deviceId}`);
    this.samples.push(s);
  }

  readSince(deviceId: string, kind: WearableTelemetryKind, since: Date): readonly WearableSample[] {
    const sinceMs = since.getTime();
    return this.samples
      .filter((s) => s.deviceId === deviceId && s.kind === kind && s.atUtc.getTime() >= sinceMs)
      .sort((a, b) => a.atUtc.getTime() - b.atUtc.getTime());
  }

  latestValue(deviceId: string, kind: WearableTelemetryKind): number | undefined {
    const hit = this.samples
      .filter((s) => s.deviceId === deviceId && s.kind === kind)
      .sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime())[0];
    return hit?.value;
  }

  averageValue(deviceId: string, kind: WearableTelemetryKind, since: Date): number {
    const items = this.readSince(deviceId, kind, since);
    if (items.length === 0) return Number.NaN; // C# double.NaN
    return items.reduce((sum, s) => sum + s.value, 0) / items.length;
  }
}
