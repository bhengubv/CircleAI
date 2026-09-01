/**
 * selector/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// selector/index.ts
//
// Device-aware model selector — port of
// CircleAI.Inference.DeviceAwareModelSelector.

import type { ModelEntry } from "../catalog/index";
import { classify, type DeviceProbe } from "../device/index";
import {
  ChatCapability,
  capabilityHas,
  type IModelSelector,
  type ModelSelection,
} from "../inference/index";
import type { ModelRegistryService } from "../registry/index";

export class DeviceAwareModelSelector implements IModelSelector {
  constructor(private readonly registry: ModelRegistryService) {
    if (!registry) throw new Error("registry is required");
  }

  bestFit(probe: DeviceProbe, required: number = ChatCapability.Default): ModelSelection {
    const entries = [...this.registry.allModels];
    if (entries.length === 0) {
      throw new Error("Model registry is empty. Cannot select a model.");
    }

    const ramGb = probe.ramAvailableBytes / 1024 ** 3;
    const storageGb = probe.storageFreeBytes / 1024 ** 3;

    // 1. Filter by capability.
    const capabilityOk = entries.filter((e) =>
      satisfiesCapability(e, required),
    );
    if (capabilityOk.length === 0) {
      throw new Error(
        `No model in the registry satisfies required capabilities '${required}'. ` +
          "Refresh the registry or relax the requirement.",
      );
    }

    // 2. Filter by device fit — advisory.
    const deviceOk = capabilityOk.filter(
      (e) =>
        (e.minRamGb ?? 0) <= ramGb + 1e-4 &&
        (storageGb <= 0 || (e.minStorageGb ?? 0) <= storageGb + 1e-4),
    );
    const candidates = deviceOk.length > 0 ? deviceOk : capabilityOk;

    // Higher quality_rank wins, smaller min_ram_gb breaks ties.
    candidates.sort((a, b) => {
      const qa = a.qualityRank ?? 0;
      const qb = b.qualityRank ?? 0;
      if (qa !== qb) return qb - qa;
      return (a.minRamGb ?? 0) - (b.minRamGb ?? 0);
    });

    const winner = candidates[0];
    return {
      modelId: winner.name,
      requiresDownload: true,
      estimatedBytes: winner.totalBytes ?? 0,
      tier: classify(probe),
    };
  }

  allCandidates(probe: DeviceProbe): readonly ModelSelection[] {
    const tier = classify(probe);
    const sorted = [...this.registry.allModels].sort(
      (a, b) => (b.qualityRank ?? 0) - (a.qualityRank ?? 0),
    );
    return sorted.map((e) => ({
      modelId: e.name,
      requiresDownload: true,
      estimatedBytes: e.totalBytes ?? 0,
      tier,
    }));
  }
}

function satisfiesCapability(entry: ModelEntry, required: number): boolean {
  if (required === ChatCapability.None) return true;
  const declared = parseCapabilities(entry.capabilities ?? null);
  return capabilityHas(declared, required);
}

export function parseCapabilities(
  labels: readonly string[] | null | undefined,
): number {
  if (!labels || labels.length === 0) return ChatCapability.Default;
  let result = ChatCapability.None;
  for (const label of labels) {
    if (!label || !label.trim()) continue;
    const key = label.trim().toUpperCase().replace(/\s+/g, "_");
    switch (key) {
      case "DEFAULT":
        result |= ChatCapability.Default;
        break;
      case "TOOLS":
        result |= ChatCapability.Tools;
        break;
      case "VISION":
        result |= ChatCapability.Vision;
        break;
      case "LONGCONTEXT":
      case "LONG_CONTEXT":
        result |= ChatCapability.LongContext;
        break;
      case "REASONING":
        result |= ChatCapability.Reasoning;
        break;
    }
  }
  return result === ChatCapability.None ? ChatCapability.Default : result;
}
