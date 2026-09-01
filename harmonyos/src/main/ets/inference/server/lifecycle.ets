/**
 * inference/server/lifecycle
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// inference/server/lifecycle.ts
//
// Port of the model-lifecycle layer:
//   • ModelLoadDescriptor / ModelLoadState / LoadOutcome / LoadResult /
//     UnloadOutcome  (ModelLifecycleTypes.cs)
//   • IModelLifecycleManager                             (IModelLifecycleManager.cs)
//   • ModelLifecycleManager                              (ModelLifecycleManager.cs)
//
// The manager is the sole authorised writer to IInferenceServerModelRegistry.
// Its admission gate (already-loaded? VRAM headroom? RAM headroom?) runs BEFORE
// invoking the bridge factory, reserving the accounting first to prevent
// overcommit races. The C# ICapabilityProbe is injected here as an
// ICapacityProbe returning a HostCapacity snapshot (deterministic in-memory).

import { BackendKind, CapabilityTier, type IInferenceBridge } from "./bridge";
import type { IInferenceServerModelRegistry } from "./runtime";

/**
 * What the caller wants to load. Ported from ModelLoadDescriptor. The C#
 * BridgeFactory `Func<CancellationToken, Task<IInferenceBridge>>` maps to a
 * factory taking an optional AbortSignal.
 */
export interface ModelLoadDescriptor {
  readonly modelId: string;
  readonly backend: BackendKind;
  readonly requestedTier: CapabilityTier;
  readonly vramRequiredBytes: number;
  readonly ramRequiredBytes: number;
  readonly bridgeFactory: (signal?: AbortSignal) => Promise<IInferenceBridge>;
}

/** Runtime view of one loaded model. Ported from ModelLoadState. */
export interface ModelLoadState {
  readonly modelId: string;
  readonly backend: BackendKind;
  readonly tier: CapabilityTier;
  readonly vramBytes: number;
  readonly ramBytes: number;
  /** ISO 8601 UTC timestamp the load was admitted. */
  readonly loadedAt: string;
}

/** Outcome enum for a load attempt. Ported from LoadOutcome. */
export enum LoadOutcome {
  Loaded = 0,
  AlreadyLoaded = 1,
  InsufficientVram = 2,
  InsufficientRam = 3,
  FactoryFailed = 4,
}

/** Result of a load attempt. Ported from LoadResult. */
export interface LoadResult {
  readonly outcome: LoadOutcome;
  readonly state: ModelLoadState | null;
  readonly rationale: string;
}

/** Outcome enum for an unload attempt. Ported from UnloadOutcome. */
export enum UnloadOutcome {
  Unloaded = 0,
  NotLoaded = 1,
}

/**
 * Admits or rejects model loads based on host capacity and keeps an
 * authoritative ledger. Ported from IModelLifecycleManager.
 */
export interface IModelLifecycleManager {
  load(descriptor: ModelLoadDescriptor, signal?: AbortSignal): Promise<LoadResult>;
  unload(modelId: string, signal?: AbortSignal): Promise<UnloadOutcome>;
  list(): readonly ModelLoadState[];
  readonly totalAllocatedVramBytes: number;
  readonly totalAllocatedRamBytes: number;
}

/** Host capacity snapshot. Projection of the C# HostProfile fields the manager reads. */
export interface HostCapacity {
  readonly totalPhysicalMemoryBytes: number;
  /** VRAM ceiling of the primary GPU, or 0 when none. */
  readonly gpuVramBytes: number;
}

/** Injected capacity probe. Mirrors the C# ICapabilityProbe.ProbeAsync usage. */
export interface ICapacityProbe {
  probe(signal?: AbortSignal): Promise<HostCapacity>;
}

/** Deterministic in-memory capacity probe. Stand-in for CapabilityProbe. */
export class InMemoryCapacityProbe implements ICapacityProbe {
  constructor(private readonly capacity: HostCapacity) {}
  async probe(_signal?: AbortSignal): Promise<HostCapacity> {
    return this.capacity;
  }
}

const MiB = 1024 * 1024;

/** Default IModelLifecycleManager. Ported from ModelLifecycleManager. */
export class ModelLifecycleManager implements IModelLifecycleManager {
  private readonly registry: IInferenceServerModelRegistry;
  private readonly probe: ICapacityProbe;
  private readonly loaded = new Map<string, ModelLoadState>();
  private cachedCapacity: HostCapacity | null = null;

  constructor(registry: IInferenceServerModelRegistry, probe: ICapacityProbe) {
    if (!registry) throw new Error("registry required");
    if (!probe) throw new Error("probe required");
    this.registry = registry;
    this.probe = probe;
  }

  get totalAllocatedVramBytes(): number {
    let sum = 0;
    for (const s of this.loaded.values()) sum += s.vramBytes;
    return sum;
  }

  get totalAllocatedRamBytes(): number {
    let sum = 0;
    for (const s of this.loaded.values()) sum += s.ramBytes;
    return sum;
  }

  async load(descriptor: ModelLoadDescriptor, signal?: AbortSignal): Promise<LoadResult> {
    if (!descriptor) throw new Error("descriptor required");
    if (!descriptor.modelId || descriptor.modelId.trim().length === 0) throw new Error("modelId required");
    if (!descriptor.bridgeFactory) throw new Error("bridgeFactory required");

    // Idempotent fast path.
    const existing = this.loaded.get(descriptor.modelId);
    if (existing) {
      return {
        outcome: LoadOutcome.AlreadyLoaded,
        state: existing,
        rationale: `Model '${descriptor.modelId}' is already loaded (${BackendKind[existing.backend]}, ${CapabilityTier[existing.tier]}).`,
      };
    }

    const capacity = await this.getOrProbe(signal);

    // VRAM admission — only enforced on GPU-class backends.
    if (
      descriptor.backend === BackendKind.Cuda ||
      descriptor.backend === BackendKind.Vulkan ||
      descriptor.backend === BackendKind.Metal ||
      descriptor.backend === BackendKind.OpenCL
    ) {
      const vramCeiling = capacity.gpuVramBytes;
      const vramFree = vramCeiling - this.totalAllocatedVramBytes;
      if (vramFree < descriptor.vramRequiredBytes) {
        return {
          outcome: LoadOutcome.InsufficientVram,
          state: null,
          rationale:
            `Need ${Math.floor(descriptor.vramRequiredBytes / MiB)} MiB VRAM, ` +
            `have ${Math.floor(Math.max(0, vramFree) / MiB)} MiB free ` +
            `(${Math.floor(this.totalAllocatedVramBytes / MiB)} MiB of ${Math.floor(vramCeiling / MiB)} MiB in use).`,
        };
      }
    }

    // RAM admission — always enforced.
    const ramFree = capacity.totalPhysicalMemoryBytes - this.totalAllocatedRamBytes;
    if (ramFree < descriptor.ramRequiredBytes) {
      return {
        outcome: LoadOutcome.InsufficientRam,
        state: null,
        rationale:
          `Need ${Math.floor(descriptor.ramRequiredBytes / MiB)} MiB RAM, ` +
          `have ${Math.floor(Math.max(0, ramFree) / MiB)} MiB free ` +
          `(${Math.floor(this.totalAllocatedRamBytes / MiB)} MiB of ${Math.floor(capacity.totalPhysicalMemoryBytes / MiB)} MiB in use).`,
      };
    }

    // Reserve before invoking the factory so concurrent loads see the accounting.
    const reserveState: ModelLoadState = {
      modelId: descriptor.modelId,
      backend: descriptor.backend,
      tier: descriptor.requestedTier,
      vramBytes: descriptor.vramRequiredBytes,
      ramBytes: descriptor.ramRequiredBytes,
      loadedAt: new Date().toISOString(),
    };

    // Single-threaded JS: the "race winner" check + reserve is atomic here.
    if (this.loaded.has(descriptor.modelId)) {
      const raceWinner = this.loaded.get(descriptor.modelId)!;
      return {
        outcome: LoadOutcome.AlreadyLoaded,
        state: raceWinner,
        rationale: `Model '${descriptor.modelId}' was loaded by a concurrent request.`,
      };
    }
    this.loaded.set(descriptor.modelId, reserveState);

    try {
      const bridge = await descriptor.bridgeFactory(signal);
      if (!bridge) throw new Error(`BridgeFactory for '${descriptor.modelId}' returned null.`);

      this.registry.register(descriptor.modelId, bridge);
      return {
        outcome: LoadOutcome.Loaded,
        state: reserveState,
        rationale: `Loaded '${descriptor.modelId}' on ${BackendKind[descriptor.backend]} at ${CapabilityTier[descriptor.requestedTier]}.`,
      };
    } catch (e) {
      // Roll the reservation back.
      this.loaded.delete(descriptor.modelId);
      const message = e instanceof Error ? e.message : String(e);
      return {
        outcome: LoadOutcome.FactoryFailed,
        state: null,
        rationale: `Bridge factory for '${descriptor.modelId}' failed: ${message}`,
      };
    }
  }

  async unload(modelId: string, _signal?: AbortSignal): Promise<UnloadOutcome> {
    if (!modelId || modelId.trim().length === 0) throw new Error("modelId required");
    if (!this.loaded.delete(modelId)) return UnloadOutcome.NotLoaded;

    const bridge = this.registry.resolve(modelId);
    if (bridge?.dispose) bridge.dispose();
    this.registry.deregister(modelId);
    return UnloadOutcome.Unloaded;
  }

  list(): readonly ModelLoadState[] {
    return [...this.loaded.values()];
  }

  private async getOrProbe(signal?: AbortSignal): Promise<HostCapacity> {
    if (this.cachedCapacity !== null) return this.cachedCapacity;
    const c = await this.probe.probe(signal);
    if (this.cachedCapacity === null) this.cachedCapacity = c;
    return this.cachedCapacity;
  }
}
