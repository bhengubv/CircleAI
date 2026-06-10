// device.ts — DeviceProbe, DeviceTier classification, DefaultDeviceContext.
//
// On HarmonyOS-native ArkTS, callers would back this with @ohos.deviceInfo.
// Here we use the cross-platform fallbacks so the same module is testable
// under Node + tsx.

export enum DeviceTier {
  Phone = 0,
  Wearable = 1,
  Tablet = 2,
  Laptop = 3,
  Workstation = 4,
  Embedded = 5,
}

export enum GpuKind {
  None = 0,
  Integrated = 1,
  Discrete = 2,
  NeuralEngine = 3,
}

export enum ThermalClass {
  Normal = 0,
  Fair = 1,
  Serious = 2,
  Critical = 3,
}

export enum Connectivity {
  Offline = 0,
  Cellular = 1,
  WiFi = 2,
  Ethernet = 3,
}

export interface DeviceSnapshot {
  readonly tier: DeviceTier;
  readonly ramBytes: number;
  readonly freeStorageBytes: number;
  readonly cpuCores: number;
  readonly gpuKind: GpuKind;
  readonly thermal: ThermalClass;
  readonly connectivity: Connectivity;
  readonly os: string;
  readonly arch: string;
}

export interface DeviceTierDefaults {
  readonly contextWindow: number;
  readonly maxConcurrent: number;
  readonly maxAgenticIterations: number;
}

export function deviceTierDefaultsFor(tier: DeviceTier): DeviceTierDefaults {
  switch (tier) {
    case DeviceTier.Wearable:    return { contextWindow: 1024,  maxConcurrent: 1,  maxAgenticIterations: 2 };
    case DeviceTier.Phone:       return { contextWindow: 4096,  maxConcurrent: 2,  maxAgenticIterations: 4 };
    case DeviceTier.Embedded:    return { contextWindow: 2048,  maxConcurrent: 1,  maxAgenticIterations: 2 };
    case DeviceTier.Tablet:      return { contextWindow: 8192,  maxConcurrent: 4,  maxAgenticIterations: 8 };
    case DeviceTier.Laptop:      return { contextWindow: 16384, maxConcurrent: 6,  maxAgenticIterations: 16 };
    case DeviceTier.Workstation: return { contextWindow: 32768, maxConcurrent: 12, maxAgenticIterations: 32 };
  }
}

export interface IDeviceContext {
  snapshot(): DeviceSnapshot;
}

/** No-op context. Returns a deterministic stub useful for tests. */
export class NullDeviceContext implements IDeviceContext {
  snapshot(): DeviceSnapshot {
    return {
      tier: DeviceTier.Phone,
      ramBytes: 4 * 1024 * 1024 * 1024,
      freeStorageBytes: 8 * 1024 * 1024 * 1024,
      cpuCores: 4,
      gpuKind: GpuKind.None,
      thermal: ThermalClass.Normal,
      connectivity: Connectivity.WiFi,
      os: 'unknown',
      arch: 'unknown',
    };
  }
}

function classifyTier(ramBytes: number, cpuCores: number): DeviceTier {
  const gib = Math.floor(ramBytes / (1024 * 1024 * 1024));
  if (gib >= 32 && cpuCores >= 16) return DeviceTier.Workstation;
  if (gib >= 16 && cpuCores >= 8)  return DeviceTier.Laptop;
  if (gib >= 6  && cpuCores >= 4)  return DeviceTier.Tablet;
  if (gib >= 3) return DeviceTier.Phone;
  if (gib >= 1) return DeviceTier.Embedded;
  return DeviceTier.Wearable;
}

export class DeviceProbe {
  static probe(): DeviceSnapshot {
    // Use Node's os module when available; on HarmonyOS-native callers wire
    // in @ohos.deviceInfo + @ohos.app.ability.featureAbility.
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    let osMod: { totalmem: () => number; cpus: () => unknown[]; platform: () => string; arch: () => string } | null = null;
    try {
      // dynamic require so HarmonyOS-only callers don't choke
      // @ts-expect-error — node:os is optional under ArkTS
      // eslint-disable-next-line @typescript-eslint/no-require-imports
      osMod = require('node:os');
    } catch { /* not in Node — degrade gracefully */ }

    const ram = osMod ? osMod.totalmem() : 4 * 1024 * 1024 * 1024;
    const cpu = osMod ? osMod.cpus().length : 4;
    const platform = osMod ? osMod.platform() : 'harmonyos';
    const arch = osMod ? osMod.arch() : 'unknown';
    const tier = classifyTier(ram, cpu);
    return {
      tier,
      ramBytes: ram,
      freeStorageBytes: 0,
      cpuCores: cpu,
      gpuKind: GpuKind.None,
      thermal: ThermalClass.Normal,
      connectivity: Connectivity.WiFi,
      os: platform,
      arch,
    };
  }
}

export class DefaultDeviceContext implements IDeviceContext {
  snapshot(): DeviceSnapshot { return DeviceProbe.probe(); }
}
