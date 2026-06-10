// device/index.ts
//
// Device probe + tier classification — port of CircleAI.Core.DeviceProbe.
// Node-first but browser-tolerant: when `os` / `node:os` is unavailable
// (e.g. running under a non-Node runtime), the probe degrades to zeros
// and DefaultDeviceContext just reports null for the platform-specific
// fields. The structural API stays identical so consumer code is portable.

// Lazy-loaded — keeps the bundle browser-clean when the host doesn't need
// Node-only modules.
type NodeOSModule = typeof import("node:os");
let _nodeOS: NodeOSModule | null = null;
async function nodeOS(): Promise<NodeOSModule | null> {
  if (_nodeOS !== null) return _nodeOS;
  try {
    _nodeOS = await import("node:os");
    return _nodeOS;
  } catch {
    _nodeOS = null as unknown as NodeOSModule;
    return null;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Enums
// ─────────────────────────────────────────────────────────────────────────────

export enum GpuKind {
  None = 0,
  Integrated = 1,
  Discrete = 2,
  Npu = 3,
  Metal = 4,
  Vulkan = 5,
  OpenCl = 6,
}

export enum ThermalClass {
  /** Fan-cooled desktop / workstation. */
  Active = 0,
  /** Tablet / fanless laptop. */
  Passive = 1,
  /** Phone. */
  Constrained = 2,
  /** Wearable. */
  Sealed = 3,
}

export enum Connectivity {
  Unknown = 0,
  Offline = 1,
  MeshOnly = 2,
  Metered = 3,
  Unlimited = 4,
}

export enum DeviceTier {
  Wearable = 0,
  Phone = 1,
  Tablet = 2,
  Desktop = 3,
  Workstation = 4,
}

// ─────────────────────────────────────────────────────────────────────────────
// DeviceProbe
// ─────────────────────────────────────────────────────────────────────────────

/** A point-in-time snapshot of what the device can physically do. */
export interface DeviceProbe {
  readonly ramAvailableBytes: number;
  readonly storageFreeBytes: number;
  readonly cpuCores: number;
  readonly gpuKind: GpuKind;
  readonly thermalClass: ThermalClass;
  readonly connectivity: Connectivity;
}

export interface SnapshotOptions {
  readonly modelCacheDirectory?: string;
  readonly gpuOverride?: GpuKind;
  readonly thermalOverride?: ThermalClass;
}

/** Capture the current device state. */
export async function snapshot(opts: SnapshotOptions = {}): Promise<DeviceProbe> {
  const os = await nodeOS();
  const ramAvailable = os ? os.freemem() : 0;
  const cpuCores = os ? os.cpus().length : 1;
  return {
    ramAvailableBytes: ramAvailable,
    storageFreeBytes: await probeStorageFree(opts.modelCacheDirectory),
    cpuCores,
    gpuKind: opts.gpuOverride ?? GpuKind.None,
    thermalClass: opts.thermalOverride ?? ThermalClass.Active,
    connectivity: Connectivity.Unknown,
  };
}

async function probeStorageFree(path?: string): Promise<number> {
  if (!path) return 0;
  try {
    // statfs is available in Node 19+ — use the async variant.
    const fs = await import("node:fs/promises");
    const fsInst = fs as unknown as {
      statfs?: (p: string) => Promise<{ bavail: bigint; bsize: bigint }>;
    };
    if (typeof fsInst.statfs === "function") {
      const stat = await fsInst.statfs(path);
      return Number(stat.bavail * stat.bsize);
    }
  } catch {
    /* fall through */
  }
  return 0;
}

/** Classify a probe into one of the five tiers. */
export function classify(probe: DeviceProbe): DeviceTier {
  const gb = probe.ramAvailableBytes / 1024 ** 3;
  if (probe.thermalClass === ThermalClass.Sealed) return DeviceTier.Wearable;
  if (gb < 2 || probe.thermalClass === ThermalClass.Constrained)
    return DeviceTier.Phone;
  if (gb < 8 || probe.thermalClass === ThermalClass.Passive)
    return DeviceTier.Tablet;
  if (gb < 32) return DeviceTier.Desktop;
  return DeviceTier.Workstation;
}

// ─────────────────────────────────────────────────────────────────────────────
// DeviceTierDefaults
// ─────────────────────────────────────────────────────────────────────────────

export const DeviceTierDefaults = {
  contextWindow(tier: DeviceTier): number {
    switch (tier) {
      case DeviceTier.Wearable:
        return 2048;
      case DeviceTier.Phone:
        return 4096;
      case DeviceTier.Tablet:
        return 8192;
      case DeviceTier.Desktop:
        return 32_768;
      case DeviceTier.Workstation:
        return 131_072;
    }
  },

  maxConcurrency(tier: DeviceTier, cpuCores: number): number {
    switch (tier) {
      case DeviceTier.Wearable:
        return 1;
      case DeviceTier.Phone:
        return 2;
      case DeviceTier.Tablet:
        return 4;
      case DeviceTier.Desktop:
        return 8;
      case DeviceTier.Workstation:
        return Math.min(16, Math.max(1, cpuCores - 2));
    }
  },

  agenticMaxIterations(tier: DeviceTier): number {
    switch (tier) {
      case DeviceTier.Wearable:
        return 2;
      case DeviceTier.Phone:
        return 3;
      case DeviceTier.Tablet:
        return 5;
      case DeviceTier.Desktop:
      case DeviceTier.Workstation:
        return 10;
    }
  },
} as const;

// ─────────────────────────────────────────────────────────────────────────────
// IDeviceContext + Null / Default
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Sensorium contract — anything platform-specific the SDK queries.
 * Returning `null` is always valid; the SDK degrades gracefully.
 * Mirrors CircleAI.Core.IDeviceContext.
 */
export interface IDeviceContext {
  readonly activeAppId: string | null;
  readonly locale: string | null;
  readonly timeZoneId: string | null;
  readonly localTime: Date | null;

  readonly latitude: number | null;
  readonly longitude: number | null;
  readonly locationHint: string | null;

  readonly batteryLevel: number | null;
  readonly isCharging: boolean | null;

  readonly networkType: string | null;
  readonly cpuUsagePercent: number | null;
  readonly availableMemoryBytes: number | null;
  readonly thermalState: string | null;
  readonly storageFreeBytes: number | null;
  readonly lastActiveUtc: Date | null;
}

/** No-op IDeviceContext. Use in tests. */
export class NullDeviceContext implements IDeviceContext {
  readonly activeAppId = null;
  readonly locale = null;
  readonly timeZoneId = null;
  readonly localTime = null;
  readonly latitude = null;
  readonly longitude = null;
  readonly locationHint = null;
  readonly batteryLevel = null;
  readonly isCharging = null;
  readonly networkType = null;
  readonly cpuUsagePercent = null;
  readonly availableMemoryBytes = null;
  readonly thermalState = null;
  readonly storageFreeBytes = null;
  readonly lastActiveUtc = null;
}

/**
 * Node-aware IDeviceContext that probes locale + timezone via Intl
 * (works under any modern JS runtime). RAM + storage are populated
 * asynchronously via `refreshAsync()` — synchronous getters reflect the
 * last refresh result. Platform-specific sensors (GPS, battery, active
 * app) stay null; platforms with those sensors should ship their own
 * IDeviceContext.
 */
export class DefaultDeviceContext implements IDeviceContext {
  constructor(
    private readonly modelCacheDir: string = "",
    private readonly thermalHint: ThermalClass = ThermalClass.Active,
  ) {}

  private _ramBytes: number | null = null;
  private _storageBytes: number | null = null;

  /** Refresh the async-only fields (RAM, storage). Best-effort. */
  async refreshAsync(): Promise<void> {
    const os = await nodeOS();
    this._ramBytes = os ? os.freemem() : null;
    this._storageBytes = this.modelCacheDir
      ? (await probeStorageFree(this.modelCacheDir)) || null
      : null;
  }

  get activeAppId(): string | null {
    return null;
  }

  get locale(): string | null {
    try {
      return Intl.DateTimeFormat().resolvedOptions().locale ?? null;
    } catch {
      return null;
    }
  }

  get timeZoneId(): string | null {
    try {
      return Intl.DateTimeFormat().resolvedOptions().timeZone ?? null;
    } catch {
      return null;
    }
  }

  get localTime(): Date | null {
    return new Date();
  }

  get latitude(): number | null {
    return null;
  }
  get longitude(): number | null {
    return null;
  }
  get locationHint(): string | null {
    return null;
  }
  get batteryLevel(): number | null {
    return null;
  }
  get isCharging(): boolean | null {
    return null;
  }

  get networkType(): string | null {
    return null;
  }

  get cpuUsagePercent(): number | null {
    return null;
  }

  get availableMemoryBytes(): number | null {
    return this._ramBytes;
  }

  get thermalState(): string | null {
    return "normal";
  }

  get storageFreeBytes(): number | null {
    return this._storageBytes;
  }

  get lastActiveUtc(): Date | null {
    return null;
  }

  /** Build a DeviceProbe using this context's modelCacheDir + thermalHint. */
  async buildProbe(gpuOverride?: GpuKind): Promise<DeviceProbe> {
    return snapshot({
      modelCacheDirectory: this.modelCacheDir || undefined,
      gpuOverride,
      thermalOverride: this.thermalHint,
    });
  }
}

/** Singleton-equivalent for callers who want stdlib defaults. */
export const DEFAULT_DEVICE_CONTEXT = new DefaultDeviceContext();
