/**
 * inference/layer_streaming
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// inference/layer_streaming.ts
//
// Port of CircleAI.Inference.LayerStreamingInference (3.3.0) — layer-by-layer
// streaming inference (AirLLM pattern): load one transformer layer's weights,
// run forward, evict, load the next. The MNN/CUDA glue is host-supplied via
// ILayerStreamingRunner; this file defines the contract + a null default +
// the orchestrator + on-disk shard discovery.
//
// ReadOnlyMemory<float> maps to Float32Array. The one filesystem touch —
// LayerShardDiscovery.Discover — is expressed over an ILayerShardListing seam
// (with a deterministic in-memory listing) but the layer_NNN parse + sort is
// byte-faithful.

/** One layer's weights packed for streaming. Ported from LayerWeightShard. */
export interface LayerWeightShard {
  /** 0-based transformer layer index. */
  readonly layerIndex: number;
  /** Path on disk to this layer's tensor shard. */
  readonly weightShardPath: string;
  /** Size of the shard, for memory accounting. */
  readonly approxBytes: number;
}

/** Layer-streaming model plan. Ported from LayerStreamingPlan. */
export interface LayerStreamingPlan {
  readonly modelId: string;
  readonly totalLayers: number;
  readonly shards: readonly LayerWeightShard[];
  readonly approxParameterBytes: number;
}

/** One layer's hidden-state output after forward. Ported from LayerActivations. */
export interface LayerActivations {
  readonly layerIndex: number;
  readonly hidden: Float32Array;
}

/** Host-supplied per-layer runner (load + forward + evict). Ported from ILayerStreamingRunner. */
export interface ILayerStreamingRunner {
  readonly backendId: string;
  readonly isAvailable: boolean;

  /** Forward one layer; returns hidden states. */
  runLayer(
    shard: LayerWeightShard,
    inputHidden: Float32Array,
    signal?: AbortSignal,
  ): Promise<LayerActivations>;

  /** Drop the layer from RAM after forward. */
  evict(layerIndex: number, signal?: AbortSignal): Promise<void>;
}

/**
 * Null runner that throws on use — drop-in default. Ported from
 * NullLayerStreamingRunner.
 */
export class NullLayerStreamingRunner implements ILayerStreamingRunner {
  static readonly instance = new NullLayerStreamingRunner();
  readonly backendId = "null";
  readonly isAvailable = false;

  async runLayer(
    _shard: LayerWeightShard,
    _inputHidden: Float32Array,
    _signal?: AbortSignal,
  ): Promise<LayerActivations> {
    throw new Error(
      "No ILayerStreamingRunner is wired. Register one (CircleAI.Inference.Native.AirLlm) to enable layer-streaming.",
    );
  }

  async evict(_layerIndex: number, _signal?: AbortSignal): Promise<void> {
    // no-op
  }
}

/**
 * Drives a full forward pass layer by layer. Ported from
 * LayerStreamingOrchestrator.
 */
export class LayerStreamingOrchestrator {
  private readonly runner: ILayerStreamingRunner;

  constructor(runner: ILayerStreamingRunner) {
    if (!runner) throw new Error("runner required");
    this.runner = runner;
  }

  /**
   * Stream every layer in `plan`, evicting after each. Returns the final hidden
   * state. `onLayerComplete` fires after each layer. Ported from ForwardAsync.
   */
  async forward(
    plan: LayerStreamingPlan,
    initialHidden: Float32Array,
    onLayerComplete?: (activations: LayerActivations) => void,
    signal?: AbortSignal,
  ): Promise<LayerActivations> {
    if (!plan) throw new Error("plan required");
    if (plan.shards.length === 0) throw new Error("Plan has no layer shards.");

    let hidden = initialHidden;
    let last: LayerActivations | null = null;
    for (const shard of plan.shards) {
      signal?.throwIfAborted();
      last = await this.runner.runLayer(shard, hidden, signal);
      hidden = last.hidden;
      onLayerComplete?.(last);
      await this.runner.evict(shard.layerIndex, signal);
    }
    return last!;
  }
}

/**
 * Directory listing seam for LayerShardDiscovery — abstracts
 * Directory.EnumerateFiles so discovery is testable without real disk.
 */
export interface ILayerShardListing {
  /** true when `dir` exists. */
  dirExists(dir: string): boolean;
  /** Files under `dir` matching the `layer_*.*` glob, with their sizes. */
  listLayerFiles(dir: string): Array<{ path: string; sizeBytes: number }>;
}

/** Deterministic in-memory listing. Register (path -> size) entries. */
export class InMemoryLayerShardListing implements ILayerShardListing {
  private readonly dirs = new Set<string>();
  private readonly files = new Map<string, number>();

  addDir(dir: string): this {
    this.dirs.add(dir);
    return this;
  }

  /** Register a shard file. Auto-registers its parent directory. */
  addFile(path: string, sizeBytes: number): this {
    this.files.set(path, sizeBytes);
    const idx = Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\"));
    if (idx >= 0) this.dirs.add(path.substring(0, idx));
    return this;
  }

  dirExists(dir: string): boolean {
    return this.dirs.has(dir);
  }

  listLayerFiles(dir: string): Array<{ path: string; sizeBytes: number }> {
    const out: Array<{ path: string; sizeBytes: number }> = [];
    for (const [path, size] of this.files) {
      const parent = path.substring(0, Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\")));
      if (parent !== dir) continue;
      const base = fileName(path);
      if (base.startsWith("layer_")) out.push({ path, sizeBytes: size });
    }
    return out;
  }
}

/**
 * Discover layer shards on disk from a manifest directory. Ported from
 * LayerShardDiscovery.Discover. Scans for `layer_NNN.*` files, parses NNN, and
 * builds a plan sorted by layer index.
 */
export function discoverLayerShards(
  modelId: string,
  modelDirectory: string,
  listing: ILayerShardListing,
): LayerStreamingPlan {
  if (!modelId || modelId.trim().length === 0) throw new Error("modelId required");
  if (!listing.dirExists(modelDirectory)) {
    throw new Error(`Model directory not found: ${modelDirectory}`);
  }

  const shards: LayerWeightShard[] = [];
  let total = 0;
  for (const entry of listing.listLayerFiles(modelDirectory)) {
    const name = fileNameWithoutExtension(entry.path);
    // "layer_NNN" -> NNN
    const underscoreIdx = name.indexOf("_");
    if (underscoreIdx < 0) continue;
    const idxStr = name.substring(underscoreIdx + 1);
    const index = parseIntStrict(idxStr);
    if (index === null) continue;
    shards.push({ layerIndex: index, weightShardPath: entry.path, approxBytes: entry.sizeBytes });
    total += entry.sizeBytes;
  }

  shards.sort((a, b) => a.layerIndex - b.layerIndex);
  return { modelId, totalLayers: shards.length, shards, approxParameterBytes: total };
}

// ── helpers ──────────────────────────────────────────────────────────────────

function fileName(path: string): string {
  const idx = Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\"));
  return idx < 0 ? path : path.substring(idx + 1);
}

function fileNameWithoutExtension(path: string): string {
  const base = fileName(path);
  const dot = base.lastIndexOf(".");
  return dot < 0 ? base : base.substring(0, dot);
}

/** int.TryParse equivalent — full-string base-10 parse, else null. */
function parseIntStrict(s: string): number | null {
  if (!/^[+-]?\d+$/.test(s)) return null;
  const n = Number(s);
  return Number.isInteger(n) ? n : null;
}
