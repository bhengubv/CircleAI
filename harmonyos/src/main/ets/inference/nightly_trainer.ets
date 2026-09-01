/**
 * inference/nightly_trainer
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// inference/nightly_trainer.ts
//
// Port of CircleAI.Inference.NightlyAdapterTrainer (Phase D3) + the
// LoRAAdapterManager training surface it drives (from MnnInteropRtFeatures.cs).
// The trainer periodically drains the FeedbackTrainingQueue, runs LoRA
// gradient steps, saves the adapter, and applies it. Idle-and-charging gating
// is host-supplied via shouldFireNow.
//
// The native P/Invoke LoRAAdapterManager (mnn_llm_train_lora_step / save / apply)
// is expressed as an injected ILoRAAdapterManager interface with a
// deterministic in-memory implementation. Its TrainStep/SaveAdapter/Apply
// argument validation is byte-faithful; TrainingNotSupportedError stands in for
// the native NotSupportedException so the trainer's re-queue path is testable.

import type { IFeedbackTrainingQueue, TrainingSample } from "./feedback_training";

/**
 * Raised when the underlying engine was compiled without training support.
 * Stand-in for the C# NotSupportedException the native LoRAAdapterManager
 * throws (mnnbridge MNNBRIDGE_ERR_TRAINING_DISABLED / -12). The trainer catches
 * this exact type to re-queue and bail.
 */
export class TrainingNotSupportedError extends Error {
  constructor(message = "Training is not enabled in this build.") {
    super(message);
    this.name = "TrainingNotSupportedError";
  }
}

/** LoRA adapter training + persistence surface. Ported from LoRAAdapterManager. */
export interface ILoRAAdapterManager {
  /**
   * Run one gradient-descent step on the LoRA adapter weights. Returns the
   * scalar loss. Throws TrainingNotSupportedError when training is disabled.
   */
  trainStep(
    inputTokens: readonly number[],
    targetTokens: readonly number[],
    learningRate?: number,
    loraRank?: number,
  ): number;

  /** Persist the current LoRA adapter weights to `adapterPath`. */
  saveAdapter(adapterPath: string): void;

  /** Apply an adapter from `adapterPath` to the loaded model. */
  apply(adapterPath: string): void;
}

/**
 * Deterministic in-memory LoRA adapter manager. Computes a stable, decreasing
 * loss as a function of the accumulated step count so training runs are
 * assertable. Argument validation mirrors LoRAAdapterManager.TrainStep /
 * SaveAdapter.
 */
export class InMemoryLoRAAdapterManager implements ILoRAAdapterManager {
  private steps = 0;
  private savedPaths = new Set<string>();
  private appliedPath: string | null = null;

  /** When true, trainStep throws TrainingNotSupportedError (native-disabled parity). */
  trainingSupported = true;

  /** Number of successful training steps run. Diagnostics. */
  get stepCount(): number {
    return this.steps;
  }

  /** Last adapter path applied, or null. Diagnostics. */
  get currentAdapter(): string | null {
    return this.appliedPath;
  }

  trainStep(
    inputTokens: readonly number[],
    targetTokens: readonly number[],
    learningRate = 1e-4,
    loraRank = 8,
  ): number {
    if (!inputTokens || inputTokens.length === 0) throw new Error("inputTokens required");
    if (!targetTokens || targetTokens.length === 0) throw new Error("targetTokens required");
    if (learningRate <= 0) throw new RangeError("learningRate must be > 0");
    if (loraRank <= 0) throw new RangeError("loraRank must be > 0");
    if (!this.trainingSupported) throw new TrainingNotSupportedError();

    this.steps++;
    // Deterministic monotonically-decreasing loss.
    return Math.fround(1.0 / (1.0 + this.steps));
  }

  saveAdapter(adapterPath: string): void {
    if (!adapterPath || adapterPath.trim().length === 0) throw new Error("adapterPath required");
    this.savedPaths.add(adapterPath);
  }

  apply(adapterPath: string): void {
    if (!adapterPath || adapterPath.trim().length === 0) throw new Error("adapterPath required");
    this.appliedPath = adapterPath;
  }
}

/** Ported from CircleAI.Inference.NightlyAdapterTrainerOptions. */
export interface NightlyAdapterTrainerOptions {
  /** Minimum samples to bother training. Skip otherwise. Default 16. */
  readonly minBatchSize?: number;
  /** Cap per nightly run so a backlog can't lock the device. Default 256. */
  readonly maxSamplesPerRun?: number;
  /** Adam-style LR for the LoRA adapter parameters. Default 1e-4. */
  readonly learningRate?: number;
  /** Rank of the LoRA decomposition; lower = smaller adapter. Default 8. */
  readonly loraRank?: number;
  /** Where to persist the trained adapter. Default "circleai-lora.mnn". */
  readonly adapterPath?: string;
  /** How often to check whether to train, in ms. Default 6 hours. */
  readonly intervalMs?: number;
  /** Optional gate (battery, charging, idle) — defaults to "always". */
  readonly shouldFireNow?: () => boolean;
  /** Tokeniser text -> int IDs. Falls back to char-level mapping if omitted. */
  readonly tokenizer?: (text: string) => number[];
}

interface ResolvedOptions {
  minBatchSize: number;
  maxSamplesPerRun: number;
  learningRate: number;
  loraRank: number;
  adapterPath: string;
  intervalMs: number;
  shouldFireNow?: () => boolean;
  tokenizer?: (text: string) => number[];
}

const SIX_HOURS_MS = 6 * 60 * 60 * 1000;

/**
 * Periodically drains the FeedbackTrainingQueue, runs LoRA gradient steps,
 * saves + applies the adapter. Ported from
 * CircleAI.Inference.NightlyAdapterTrainer.
 */
export class NightlyAdapterTrainer {
  private readonly queue: IFeedbackTrainingQueue;
  private readonly adapter: ILoRAAdapterManager;
  private readonly opts: ResolvedOptions;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private running = false;

  constructor(
    queue: IFeedbackTrainingQueue,
    adapter: ILoRAAdapterManager,
    options: NightlyAdapterTrainerOptions = {},
  ) {
    if (!queue) throw new Error("queue required");
    if (!adapter) throw new Error("adapter required");
    this.queue = queue;
    this.adapter = adapter;
    this.opts = {
      minBatchSize: options.minBatchSize ?? 16,
      maxSamplesPerRun: options.maxSamplesPerRun ?? 256,
      learningRate: options.learningRate ?? 1e-4,
      loraRank: options.loraRank ?? 8,
      adapterPath: options.adapterPath ?? "circleai-lora.mnn",
      intervalMs: options.intervalMs ?? SIX_HOURS_MS,
      shouldFireNow: options.shouldFireNow,
      tokenizer: options.tokenizer,
    };
  }

  /** Start the periodic loop. Idempotent. Mirrors StartAsync. */
  start(): void {
    if (this.running) return;
    this.running = true;
    this.scheduleNext();
  }

  /** Stop the loop. Mirrors StopAsync / DisposeAsync. */
  stop(): void {
    this.running = false;
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }

  private scheduleNext(): void {
    if (!this.running) return;
    this.timer = setTimeout(async () => {
      try {
        if (!this.opts.shouldFireNow || this.opts.shouldFireNow()) {
          await this.runOnce();
        }
      } catch {
        // matches C# LogWarning on run failure — swallow, keep looping.
      }
      this.scheduleNext();
    }, this.opts.intervalMs);
    // Do not keep the event loop alive purely for this timer.
    (this.timer as { unref?: () => void }).unref?.();
  }

  /**
   * Drain + train in one pass. Public so a host can trigger manually. Ported
   * verbatim from RunOnceAsync.
   */
  async runOnce(signal?: AbortSignal): Promise<void> {
    if (this.queue.pending < this.opts.minBatchSize) {
      return; // skipping; pending < min
    }

    const samples = await this.queue.drain(this.opts.maxSamplesPerRun, signal);
    if (samples.length === 0) return;

    const tokenizer = this.opts.tokenizer ?? charTokenizer;
    let stepCount = 0;
    for (const sample of samples) {
      signal?.throwIfAborted();
      try {
        const input = tokenizer(sample.userText);
        const target = tokenizer(sample.polarity >= 0 ? sample.preferredText : sample.assistantText);
        if (input.length === 0 || target.length === 0) continue;

        this.adapter.trainStep(input, target, this.opts.learningRate, this.opts.loraRank);
        stepCount++;
      } catch (e) {
        if (e instanceof TrainingNotSupportedError) {
          // Native training not built — re-queue and bail out.
          for (const s of samples) await this.queue.enqueue(s, signal);
          return;
        }
        // step failed for this sample — matches C# LogWarning branch, continue.
      }
    }

    if (stepCount > 0) {
      try {
        this.adapter.saveAdapter(this.opts.adapterPath);
        this.adapter.apply(this.opts.adapterPath);
      } catch {
        // adapter save/apply failed — matches C# LogWarning branch.
      }
    }
  }
}

/**
 * Char-level tokenizer fallback — every char becomes its UTF-16 code-unit
 * value. Ported verbatim from NightlyAdapterTrainer.CharTokenizer.
 */
export function charTokenizer(text: string): number[] {
  if (!text) return [];
  const arr = new Array<number>(text.length);
  for (let i = 0; i < text.length; i++) arr[i] = text.charCodeAt(i);
  return arr;
}
