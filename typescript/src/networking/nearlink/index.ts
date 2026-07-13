// networking/nearlink/index.ts
// Full-parity port of CircleAI.Networking.NearLink (NearLinkTransportCommons.cs).
// C# is the exact spec.
//
// Shared types + helpers for the NearLink network transport: device pairing
// record, pairing/power state enums, session record, throughput sample, and an
// in-memory registry.
//
// NOTE: This module had no TypeScript counterpart before the StubGuard parity
// pass; it is ported here in full (base types + registry, including the
// StubGuard additions AvgKbpsRead / AvgKbpsWrite / Unregister /
// SessionsForDevice) so the port stays at parity with the C# reference.
//
// Type mappings (C# → TS):
//   enum NearLinkPairingState        → const enum-like (Unpaired=0 .. PairingFailed=3)
//   enum NearLinkPowerProfile        → const enum-like (LowEnergy=0 .. HighThroughput=2)
//   record                           → readonly interface (+ positional factory)
//   int RssiDbm                      → number
//   DateTimeOffset StartedUtc/AtUtc  → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//   List<throughput sample>          → T[]

/** Pairing state of a NearLink device. Mirrors C# `NearLinkPairingState` (Unpaired = 0). */
export type NearLinkPairingState = 0 | 1 | 2 | 3;
/** Frozen value object for {@link NearLinkPairingState} members. */
export const NearLinkPairingState = Object.freeze({
  Unpaired: 0,
  Pairing: 1,
  Paired: 2,
  PairingFailed: 3,
} as const) satisfies Record<string, NearLinkPairingState>;

/** Power profile for a NearLink session. Mirrors C# `NearLinkPowerProfile` (LowEnergy = 0). */
export type NearLinkPowerProfile = 0 | 1 | 2;
/** Frozen value object for {@link NearLinkPowerProfile} members. */
export const NearLinkPowerProfile = Object.freeze({
  LowEnergy: 0,
  Balanced: 1,
  HighThroughput: 2,
} as const) satisfies Record<string, NearLinkPowerProfile>;

/** A NearLink device. Mirrors C# `NearLinkDevice` record. */
export interface NearLinkDevice {
  readonly deviceId: string;
  readonly friendlyName: string;
  readonly manufacturerId: string;
  readonly firmwareVersion: string;
}

/** Constructs a {@link NearLinkDevice}. */
export function nearLinkDevice(
  deviceId: string,
  friendlyName: string,
  manufacturerId: string,
  firmwareVersion: string,
): NearLinkDevice {
  return { deviceId, friendlyName, manufacturerId, firmwareVersion };
}

/** An open NearLink session. Mirrors C# `NearLinkSession` record. */
export interface NearLinkSession {
  readonly sessionId: string;
  readonly deviceId: string;
  readonly powerProfile: NearLinkPowerProfile;
  /** UTC instant the session started (C# `DateTimeOffset StartedUtc`). */
  readonly startedUtc: Date;
}

/** Constructs a {@link NearLinkSession}. */
export function nearLinkSession(
  sessionId: string,
  deviceId: string,
  powerProfile: NearLinkPowerProfile,
  startedUtc: Date,
): NearLinkSession {
  return { sessionId, deviceId, powerProfile, startedUtc };
}

/** A NearLink throughput observation. Mirrors C# `NearLinkThroughputSample` record. */
export interface NearLinkThroughputSample {
  readonly deviceId: string;
  readonly kbpsRead: number;
  readonly kbpsWrite: number;
  readonly rssiDbm: number;
  /** UTC instant of the sample (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link NearLinkThroughputSample}. */
export function nearLinkThroughputSample(
  deviceId: string,
  kbpsRead: number,
  kbpsWrite: number,
  rssiDbm: number,
  atUtc: Date,
): NearLinkThroughputSample {
  return { deviceId, kbpsRead, kbpsWrite, rssiDbm, atUtc };
}

/** Ordinal (case-sensitive) string compare, matching C# `StringComparer.Ordinal`. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** In-memory NearLink registry. Mirrors C# `InMemoryNearLinkRegistry`. */
export class InMemoryNearLinkRegistry {
  private readonly devices = new Map<string, NearLinkDevice>();
  private readonly states = new Map<string, NearLinkPairingState>();
  private readonly sessions = new Map<string, NearLinkSession>();
  private readonly throughput: NearLinkThroughputSample[] = [];

  register(d: NearLinkDevice): void {
    if (d == null) throw new Error("d required");
    this.devices.set(d.deviceId, d);
  }

  getDevice(id: string): NearLinkDevice | undefined {
    return this.devices.get(id);
  }

  /** All devices, ordered by friendly name. Mirrors C# `Devices`. */
  get devicesList(): readonly NearLinkDevice[] {
    return [...this.devices.values()].sort((a, b) => ordinalCompare(a.friendlyName, b.friendlyName));
  }

  setPairingState(deviceId: string, s: NearLinkPairingState): void {
    this.states.set(deviceId, s);
  }

  pairingState(deviceId: string): NearLinkPairingState {
    return this.states.get(deviceId) ?? NearLinkPairingState.Unpaired;
  }

  openSession(s: NearLinkSession): void {
    if (s == null) throw new Error("s required");
    this.sessions.set(s.sessionId, s);
  }

  getSession(id: string): NearLinkSession | undefined {
    return this.sessions.get(id);
  }

  closeSession(id: string): void {
    this.sessions.delete(id);
  }

  /** All open sessions, in insertion order. Mirrors C# `ActiveSessions`. */
  get activeSessions(): readonly NearLinkSession[] {
    return [...this.sessions.values()];
  }

  recordThroughput(s: NearLinkThroughputSample): void {
    if (s == null) throw new Error("s required");
    this.throughput.push(s);
  }

  /** Average observed RSSI (dBm) for a device, or -127 when unsampled. Mirrors C# `AvgRssi`. */
  avgRssi(deviceId: string): number {
    const samples = this.throughput.filter((t) => t.deviceId === deviceId).map((t) => t.rssiDbm);
    if (samples.length === 0) return -127;
    return samples.reduce((sum, v) => sum + v, 0) / samples.length;
  }

  /** Average observed read throughput (kbps) for a device, or 0 when unsampled. Mirrors C# `AvgKbpsRead`. */
  avgKbpsRead(deviceId: string): number {
    const samples = this.throughput.filter((t) => t.deviceId === deviceId).map((t) => t.kbpsRead);
    if (samples.length === 0) return 0.0;
    return samples.reduce((sum, v) => sum + v, 0) / samples.length;
  }

  /** Average observed write throughput (kbps) for a device, or 0 when unsampled. Mirrors C# `AvgKbpsWrite`. */
  avgKbpsWrite(deviceId: string): number {
    const samples = this.throughput.filter((t) => t.deviceId === deviceId).map((t) => t.kbpsWrite);
    if (samples.length === 0) return 0.0;
    return samples.reduce((sum, v) => sum + v, 0) / samples.length;
  }

  /**
   * Remove a paired device: drops its device record and cached pairing state.
   * Open sessions are left untouched (close them explicitly via
   * {@link closeSession}). Returns whether a device record was actually removed.
   * Mirrors C# `Unregister`.
   */
  unregister(deviceId: string): boolean {
    if (deviceId == null || deviceId.length === 0) return false;
    const removed = this.devices.delete(deviceId);
    this.states.delete(deviceId);
    return removed;
  }

  /** Active sessions belonging to a device (ordinal id match), oldest-first by start time. Mirrors C# `SessionsForDevice`. */
  sessionsForDevice(deviceId: string): readonly NearLinkSession[] {
    if (deviceId == null || deviceId.length === 0) return [];
    return [...this.sessions.values()]
      .filter((s) => s.deviceId === deviceId)
      .sort((a, b) => a.startedUtc.getTime() - b.startedUtc.getTime());
  }
}
