/**
 * networking/bluetooth/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// networking/bluetooth/index.ts
// Full-parity port of CircleAI.Networking.Bluetooth (BluetoothTransportCommons.cs).
// C# is the exact spec.
//
// Shared metadata + helpers for the Bluetooth network transport: connection-state
// enum, endpoint + capability descriptors, throughput sample, and a simple
// in-memory transport registry.
//
// NOTE: This module had no TypeScript counterpart before the StubGuard parity
// pass; it is ported here in full (base types + registry, including the
// StubGuard additions AvgKbpsWrite / Unregister / EndpointsWithService /
// ConnectedCount) so the port stays at parity with the C# reference.
//
// Type mappings (C# → TS):
//   enum BluetoothConnectionState    → const enum-like (Disconnected=0 .. Failed=4)
//   record                           → readonly interface (+ positional factory)
//   IReadOnlyList<string>            → readonly string[]
//   DateTimeOffset AtUtc             → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//   List<throughput sample>          → T[]

/** Lifecycle state of a Bluetooth connection. Mirrors C# `BluetoothConnectionState` (Disconnected = 0). */
export type BluetoothConnectionState = 0 | 1 | 2 | 3 | 4;
/** Frozen value object for {@link BluetoothConnectionState} members. */
export const BluetoothConnectionState = Object.freeze({
  Disconnected: 0,
  Discovering: 1,
  Connecting: 2,
  Connected: 3,
  Failed: 4,
} as const) satisfies Record<string, BluetoothConnectionState>;

/** A discovered Bluetooth endpoint. Mirrors C# `BluetoothEndpointDescriptor` record. */
export interface BluetoothEndpointDescriptor {
  readonly deviceId: string;
  readonly name: string;
  readonly macAddress: string;
  readonly advertisedServices: readonly string[];
}

/** Constructs a {@link BluetoothEndpointDescriptor}. */
export function bluetoothEndpointDescriptor(
  deviceId: string,
  name: string,
  macAddress: string,
  advertisedServices: readonly string[],
): BluetoothEndpointDescriptor {
  return { deviceId, name, macAddress, advertisedServices };
}

/** A Bluetooth capability profile. Mirrors C# `BluetoothCapabilityProfile` record. */
export interface BluetoothCapabilityProfile {
  readonly maxMtuBytes: number;
  readonly supportsSecureConnections: boolean;
  readonly supportsHighSpeed: boolean;
  readonly compatibleProfiles: readonly string[];
}

/** Constructs a {@link BluetoothCapabilityProfile}. */
export function bluetoothCapabilityProfile(
  maxMtuBytes: number,
  supportsSecureConnections: boolean,
  supportsHighSpeed: boolean,
  compatibleProfiles: readonly string[],
): BluetoothCapabilityProfile {
  return { maxMtuBytes, supportsSecureConnections, supportsHighSpeed, compatibleProfiles };
}

/** A Bluetooth throughput observation. Mirrors C# `BluetoothThroughputSample` record. */
export interface BluetoothThroughputSample {
  readonly deviceId: string;
  readonly kbpsRead: number;
  readonly kbpsWrite: number;
  /** UTC instant of the sample (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link BluetoothThroughputSample}. */
export function bluetoothThroughputSample(
  deviceId: string,
  kbpsRead: number,
  kbpsWrite: number,
  atUtc: Date,
): BluetoothThroughputSample {
  return { deviceId, kbpsRead, kbpsWrite, atUtc };
}

/** Well-known capability profiles. Mirrors C# `BluetoothCapabilityProfiles`. */
export const BluetoothCapabilityProfiles = {
  Le5: bluetoothCapabilityProfile(247, true, true, ["GATT", "L2CAP"]),
  Le4: bluetoothCapabilityProfile(23, true, false, ["GATT"]),
  Classic: bluetoothCapabilityProfile(1024, true, false, ["SPP", "RFCOMM"]),
} as const;

/** Ordinal (case-sensitive) string compare, matching C# `StringComparer.Ordinal`. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** In-memory Bluetooth transport registry. Mirrors C# `InMemoryBluetoothTransportRegistry`. */
export class InMemoryBluetoothTransportRegistry {
  private readonly endpoints = new Map<string, BluetoothEndpointDescriptor>();
  private readonly states = new Map<string, BluetoothConnectionState>();
  private readonly throughput: BluetoothThroughputSample[] = [];

  register(e: BluetoothEndpointDescriptor): void {
    if (e == null) throw new Error("e required");
    this.endpoints.set(e.deviceId, e);
  }

  getEndpoint(deviceId: string): BluetoothEndpointDescriptor | undefined {
    return this.endpoints.get(deviceId);
  }

  /** All endpoints, ordered by name. Mirrors C# `AllEndpoints`. */
  get allEndpoints(): readonly BluetoothEndpointDescriptor[] {
    return [...this.endpoints.values()].sort((a, b) => ordinalCompare(a.name, b.name));
  }

  setState(deviceId: string, s: BluetoothConnectionState): void {
    this.states.set(deviceId, s);
  }

  state(deviceId: string): BluetoothConnectionState {
    return this.states.get(deviceId) ?? BluetoothConnectionState.Disconnected;
  }

  recordThroughput(s: BluetoothThroughputSample): void {
    if (s == null) throw new Error("s required");
    this.throughput.push(s);
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
   * Drop a device from the registry: removes its endpoint descriptor and any
   * tracked connection state. Returns whether an endpoint was actually removed.
   * Mirrors C# `Unregister`.
   */
  unregister(deviceId: string): boolean {
    if (deviceId == null || deviceId.length === 0) return false;
    const removed = this.endpoints.delete(deviceId);
    this.states.delete(deviceId);
    return removed;
  }

  /**
   * Endpoints advertising a given GATT/SPP service, matched case-insensitively
   * and ordered by device name (ordinal). Empty service → []. Mirrors C#
   * `EndpointsWithService`.
   */
  endpointsWithService(service: string): readonly BluetoothEndpointDescriptor[] {
    if (service == null || service.length === 0) return [];
    const target = service.toLowerCase();
    return [...this.endpoints.values()]
      .filter((e) => e.advertisedServices.some((s) => s.toLowerCase() === target))
      .sort((a, b) => ordinalCompare(a.name, b.name));
  }

  /** Number of devices currently in the Connected state. Mirrors C# `ConnectedCount`. */
  get connectedCount(): number {
    let n = 0;
    for (const s of this.states.values()) if (s === BluetoothConnectionState.Connected) n++;
    return n;
  }
}
