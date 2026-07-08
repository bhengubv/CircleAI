// inference/server/bridge_factory.ts
//
// Port of CircleAI.Inference.Server.Endpoints.IBridgeFactory (+
// UnconfiguredBridgeFactory) and a deterministic stand-in for
// MnnInferenceBridgeFactory. The production factory composes registry +
// download + native-runtime + MNN generator into an IInferenceBridge; the
// stand-in composes the same shape over a DeterministicChatGenerator (which
// runs without libmnnbridge), preserving the tier -> approx-memory mapping and
// the ModelDescriptor construction (4096 ctx, Qwen 3 vocab).

import { DeterministicChatGenerator } from "../generator.js";
import {
  BackendKind,
  CapabilityTier,
  LocalProcessInferenceBridge,
  ModelFormat,
  type IInferenceBridge,
  type ModelDescriptor,
} from "./bridge.js";

/**
 * DI factory contract — materialise an IInferenceBridge for a given model id +
 * backend + tier. Ported from IBridgeFactory.
 */
export interface IBridgeFactory {
  create(
    modelId: string,
    backend: BackendKind,
    tier: CapabilityTier,
    signal?: AbortSignal,
  ): Promise<IInferenceBridge>;
}

/**
 * Default implementation — refuses every load with a clear error. Ported from
 * UnconfiguredBridgeFactory.
 */
export class UnconfiguredBridgeFactory implements IBridgeFactory {
  create(
    _modelId: string,
    _backend: BackendKind,
    _tier: CapabilityTier,
    _signal?: AbortSignal,
  ): Promise<IInferenceBridge> {
    throw new Error(
      "No IBridgeFactory is configured. Register one before calling /v1/admin/models/load.",
    );
  }
}

/**
 * Options for the deterministic bridge factory. `resolveMemoryBytes` overrides
 * the default tier -> approx-memory mapping; `visionCapableModelIds` marks which
 * models get a vision-capable generator.
 */
export interface InMemoryBridgeFactoryOptions {
  readonly resolveMemoryBytes?: (tier: CapabilityTier) => number;
  readonly visionCapableModelIds?: readonly string[];
  /** When true, generators emit a <think> reasoning block. */
  readonly emitReasoning?: boolean;
}

/**
 * Deterministic IBridgeFactory. Stand-in for MnnInferenceBridgeFactory: builds a
 * LocalProcessInferenceBridge over a DeterministicChatGenerator. The tier ->
 * approx-memory mapping is byte-faithful to ApproxMemoryFromTier.
 */
export class InMemoryBridgeFactory implements IBridgeFactory {
  private readonly opts: InMemoryBridgeFactoryOptions;

  constructor(options: InMemoryBridgeFactoryOptions = {}) {
    this.opts = options;
  }

  async create(
    modelId: string,
    _backend: BackendKind,
    tier: CapabilityTier,
    _signal?: AbortSignal,
  ): Promise<IInferenceBridge> {
    if (!modelId || modelId.trim().length === 0) throw new Error("modelId required");

    const vision = this.opts.visionCapableModelIds?.includes(modelId) ?? false;
    const generator = new DeterministicChatGenerator({
      modelPath: `deterministic://${modelId}`,
      contextSize: 4096,
      visionCapable: vision,
      emitReasoning: this.opts.emitReasoning ?? false,
    });

    const descriptor: ModelDescriptor = {
      modelId,
      version: "0.0.0",
      format: ModelFormat.Gguf,
      contextWindowTokens: 4096,
      vocabSize: 151_936, // Qwen 3 family default
      parameterCount: 0,
      quantisationLabel: null,
      approximateMemoryBytes: (this.opts.resolveMemoryBytes ?? approxMemoryFromTier)(tier),
    };

    return new LocalProcessInferenceBridge(generator, descriptor);
  }
}

/** Ported verbatim from MnnInferenceBridgeFactory.ApproxMemoryFromTier. */
export function approxMemoryFromTier(tier: CapabilityTier): number {
  const GiB = 1024 * 1024 * 1024;
  switch (tier) {
    case CapabilityTier.Tier0_Tiny:
      return 1 * GiB;
    case CapabilityTier.Tier1_Small:
      return 2 * GiB;
    case CapabilityTier.Tier2_Medium:
      return 6 * GiB;
    case CapabilityTier.Tier3_Large:
      return 12 * GiB;
    case CapabilityTier.Tier4_Frontier:
      return 24 * GiB;
    default:
      return 1 * GiB;
  }
}
