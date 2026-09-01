/**
 * ambient/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// ambient/index.ts
// Full-parity port of CircleAI.Ambient (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Ambient vertical: environmental
// readings, comfort preferences, and a comfort test.
//
// NOTE: The C# AmbientCompanionMonitor (an ICompanionSession LLM/telemetry
// wrapper) is intentionally NOT ported — consistent with the sibling
// domain-board ports; only the board + records are ported.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   double TemperatureC / Humidity / LuxLight / DbNoise / Target* / Max* → number
//   DateTimeOffset AtUtc             → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//   List<AmbientReading> (+lock)     → array (single-threaded; lock is a no-op)
//
// SEMANTICS PARITY:
//   Latest        — most-recent reading for the device (AtUtc descending), or undefined.
//   History       — device's readings, AtUtc descending, take limit.
//   IsComfortable — false when no pref or no reading; else |temp − target| <= 2 AND
//                   |humidity − target| <= 10 AND dbNoise <= maxNoise.

/** An ambient environmental reading. Mirrors C# `AmbientReading` record. */
export interface AmbientReading {
  readonly deviceId: string;
  readonly temperatureC: number;
  readonly humidity: number;
  readonly luxLight: number;
  readonly dbNoise: number;
  /** UTC instant of the reading (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs an {@link AmbientReading}. */
export function ambientReading(
  deviceId: string,
  temperatureC: number,
  humidity: number,
  luxLight: number,
  dbNoise: number,
  atUtc: Date,
): AmbientReading {
  return { deviceId, temperatureC, humidity, luxLight, dbNoise, atUtc };
}

/** A location's comfort preference. Mirrors C# `AmbientPreference` record. */
export interface AmbientPreference {
  readonly location: string;
  readonly targetTempC: number;
  readonly targetHumidity: number;
  readonly maxNoiseDb: number;
}

/** Constructs an {@link AmbientPreference}. */
export function ambientPreference(
  location: string,
  targetTempC: number,
  targetHumidity: number,
  maxNoiseDb: number,
): AmbientPreference {
  return { location, targetTempC, targetHumidity, maxNoiseDb };
}

/** The ambient board contract. Mirrors C# `IAmbientBoard`. */
export interface IAmbientBoard {
  record(r: AmbientReading): void;
  latest(deviceId: string): AmbientReading | undefined;
  history(deviceId: string, limit?: number): readonly AmbientReading[];
  setPreference(p: AmbientPreference): void;
  getPreference(location: string): AmbientPreference | undefined;
  isComfortable(deviceId: string, location: string): boolean;
}

/** Deterministic in-memory {@link IAmbientBoard}. */
export class InMemoryAmbientBoard implements IAmbientBoard {
  private readonly readings: AmbientReading[] = [];
  private readonly prefs = new Map<string, AmbientPreference>();

  record(r: AmbientReading): void {
    if (r == null) throw new Error("r required");
    this.readings.push(r);
  }

  latest(deviceId: string): AmbientReading | undefined {
    return this.readings
      .filter((r) => r.deviceId === deviceId)
      .sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime())[0];
  }

  history(deviceId: string, limit = 50): readonly AmbientReading[] {
    return this.readings
      .filter((r) => r.deviceId === deviceId)
      .sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime())
      .slice(0, limit);
  }

  setPreference(p: AmbientPreference): void {
    if (p == null) throw new Error("p required");
    this.prefs.set(p.location, p);
  }

  getPreference(location: string): AmbientPreference | undefined {
    return this.prefs.get(location);
  }

  isComfortable(deviceId: string, location: string): boolean {
    const pref = this.getPreference(location);
    const last = this.latest(deviceId);
    if (pref === undefined || last === undefined) return false;
    return (
      Math.abs(last.temperatureC - pref.targetTempC) <= 2 &&
      Math.abs(last.humidity - pref.targetHumidity) <= 10 &&
      last.dbNoise <= pref.maxNoiseDb
    );
  }
}
