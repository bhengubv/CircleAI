// selector.ts — ChatCapability flags + DeviceAwareModelSelector.

import { DeviceSnapshot, DeviceTier, IDeviceContext } from './device';

/** Bitwise-composable capability flags. */
export const ChatCapability = {
  None:      0,
  Text:      1 << 0,
  Tools:     1 << 1,
  Vision:    1 << 2,
  Audio:     1 << 3,
  LongCtx:   1 << 4,
  Reasoning: 1 << 5,
  Streaming: 1 << 6,
  Default:   (1 << 0) | (1 << 6),  // Text | Streaming
} as const;

export function parseCapabilities(raw: string | null | undefined): number {
  if (!raw) return 0;
  let out = 0;
  for (const tok of raw.split(/[,\s]+/)) {
    switch (tok.trim().toLowerCase()) {
      case '': break;
      case 'text': out |= ChatCapability.Text; break;
      case 'tools': out |= ChatCapability.Tools; break;
      case 'vision': out |= ChatCapability.Vision; break;
      case 'audio': out |= ChatCapability.Audio; break;
      case 'longctx': case 'long_ctx': case 'long-ctx': out |= ChatCapability.LongCtx; break;
      case 'reasoning': out |= ChatCapability.Reasoning; break;
      case 'streaming': out |= ChatCapability.Streaming; break;
    }
  }
  return out;
}

export interface ModelCandidate {
  readonly name: string;
  readonly totalBytes: number;
  readonly capabilities: string | null;
}

export interface ModelSelection<T extends ModelCandidate = ModelCandidate> {
  readonly entry: T;
  readonly reason: string;
}

export interface IModelSelector<T extends ModelCandidate = ModelCandidate> {
  select(candidates: ReadonlyArray<T>, device: DeviceSnapshot, required: number): ModelSelection<T> | null;
}

function maxBytesForTier(tier: DeviceTier): number {
  switch (tier) {
    case DeviceTier.Wearable:    return 200 * 1024 * 1024;
    case DeviceTier.Embedded:    return 500 * 1024 * 1024;
    case DeviceTier.Phone:       return 2_500_000_000;
    case DeviceTier.Tablet:      return 6_000_000_000;
    case DeviceTier.Laptop:      return 20_000_000_000;
    case DeviceTier.Workstation: return 60_000_000_000;
  }
}

export class DeviceAwareModelSelector<T extends ModelCandidate = ModelCandidate> implements IModelSelector<T> {
  constructor(private readonly deviceContext: IDeviceContext) {}

  select(candidates: ReadonlyArray<T>, device: DeviceSnapshot, required: number = ChatCapability.Default): ModelSelection<T> | null {
    const ceil = maxBytesForTier(device.tier);
    const viable = candidates
      .filter(c => {
        const caps = parseCapabilities(c.capabilities ?? 'Text,Streaming');
        return (caps & required) === required && c.totalBytes <= ceil;
      })
      .sort((a, b) => a.totalBytes - b.totalBytes);
    if (viable.length === 0) return null;
    const chosen = viable[0];
    const reason = `tier=${DeviceTier[device.tier]} ram=${Math.floor(device.ramBytes / (1024 * 1024))}MB required=${required} → ${chosen.name} (${chosen.totalBytes} bytes)`;
    return { entry: chosen, reason };
  }
}
