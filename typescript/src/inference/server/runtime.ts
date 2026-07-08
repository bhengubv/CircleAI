// inference/server/runtime.ts
//
// Server-wide runtime primitives: ServerCounters, AdmissionControl,
// IInferenceServerModelRegistry (+ impl), and INativeRuntimeStatus (+ impl,
// with the NativeRuntimePaths record). Ported from
// CircleAI.Inference.Server.Models.ServerCounters / Hosting.AdmissionControl /
// Models.ModelRegistry / Lifecycle.INativeRuntimeStatus. Single-threaded JS
// makes the C# Interlocked/lock machinery unnecessary — the observable
// semantics are preserved.

import type { ITextEmbedder } from "../../embeddings/index.js";
import type { IInferenceBridge } from "./bridge.js";
import type { InferenceServerOptions } from "./options.js";

// ── ServerCounters ────────────────────────────────────────────────────────────

/** Coarse-grain server-wide counters. Ported from ServerCounters. */
export class ServerCounters {
  private _total = 0;
  private _rejected = 0;
  private _failed = 0;
  private _active = 0;

  /** UTC time the server process started (ISO 8601). */
  readonly startedAt: string = new Date().toISOString();

  /** Total requests accepted (including those that subsequently failed). */
  get totalRequests(): number {
    return this._total;
  }

  /** Requests rejected at admission (concurrency cap, auth fail). */
  get rejectedRequests(): number {
    return this._rejected;
  }

  /** Requests that admitted but failed downstream. */
  get failedRequests(): number {
    return this._failed;
  }

  /** Requests currently in flight. */
  get activeRequests(): number {
    return this._active;
  }

  /** Mark a request as accepted (admission passed). */
  accountAdmitted(): void {
    this._total++;
    this._active++;
  }

  /** Mark a request as completed. */
  accountCompleted(): void {
    this._active--;
  }

  /** Mark a request as rejected at admission (not counted in total). */
  accountRejected(): void {
    this._rejected++;
  }

  /** Mark a request as failed downstream. */
  accountFailed(): void {
    this._failed++;
  }
}

// ── AdmissionControl ──────────────────────────────────────────────────────────

/** A held admission slot. Release exactly once (via `release`). */
export interface AdmissionSlot {
  release(): void;
}

/**
 * Bounded admission gate — at most maxConcurrentRequests in flight at once;
 * excess requests are rejected immediately (no queueing). Ported from
 * CircleAI.Inference.Server.Hosting.AdmissionControl.
 */
export class AdmissionControl {
  readonly maxConcurrentRequests: number;
  private readonly counters: ServerCounters;
  private inFlight = 0;

  constructor(options: InferenceServerOptions, counters: ServerCounters) {
    if (!options) throw new Error("options required");
    if (!counters) throw new Error("counters required");
    this.maxConcurrentRequests = Math.max(1, options.maxConcurrentRequests);
    this.counters = counters;
  }

  /**
   * Attempt to acquire one slot. Returns a slot the caller MUST release, or
   * null when the gate is saturated. Ported from TryEnter.
   */
  tryEnter(): AdmissionSlot | null {
    if (this.inFlight < this.maxConcurrentRequests) {
      this.inFlight++;
      this.counters.accountAdmitted();
      let disposed = false;
      const self = this;
      return {
        release(): void {
          if (disposed) return;
          disposed = true;
          self.inFlight--;
          self.counters.accountCompleted();
        },
      };
    }
    this.counters.accountRejected();
    return null;
  }
}

// ── IInferenceServerModelRegistry ─────────────────────────────────────────────

/**
 * In-process registry of bridge instances keyed by logical model ID (the value
 * clients pass in the `model` field of an OpenAI request). Ported from
 * CircleAI.Inference.Server.Models.IInferenceServerModelRegistry.
 */
export interface IInferenceServerModelRegistry {
  register(modelId: string, bridge: IInferenceBridge): void;
  registerEmbedder(modelId: string, embedder: ITextEmbedder): void;
  deregister(modelId: string): boolean;
  resolve(modelId: string): IInferenceBridge | null;
  resolveEmbedder(modelId: string): ITextEmbedder | null;
  allModelIds(): readonly string[];
  chatModelIds(): readonly string[];
}

/** Default implementation. Ported from InferenceServerModelRegistry. */
export class InferenceServerModelRegistry implements IInferenceServerModelRegistry {
  private readonly chat = new Map<string, IInferenceBridge>();
  private readonly embed = new Map<string, ITextEmbedder>();

  register(modelId: string, bridge: IInferenceBridge): void {
    if (!modelId || modelId.trim().length === 0) throw new Error("modelId required");
    if (!bridge) throw new Error("bridge required");
    this.chat.set(modelId, bridge);
  }

  registerEmbedder(modelId: string, embedder: ITextEmbedder): void {
    if (!modelId || modelId.trim().length === 0) throw new Error("modelId required");
    if (!embedder) throw new Error("embedder required");
    this.embed.set(modelId, embedder);
  }

  deregister(modelId: string): boolean {
    return this.chat.delete(modelId);
  }

  resolve(modelId: string): IInferenceBridge | null {
    return this.chat.get(modelId) ?? null;
  }

  resolveEmbedder(modelId: string): ITextEmbedder | null {
    return this.embed.get(modelId) ?? null;
  }

  allModelIds(): readonly string[] {
    const set = new Set<string>();
    for (const k of this.chat.keys()) set.add(k);
    for (const k of this.embed.keys()) set.add(k);
    return [...set];
  }

  chatModelIds(): readonly string[] {
    return [...this.chat.keys()];
  }
}

// ── INativeRuntimeStatus ──────────────────────────────────────────────────────

/**
 * Last-known native-runtime paths. Ported from
 * CircleAI.Inference.NativeRuntimePrep.NativeRuntimePaths — surfaced through the
 * diagnostics endpoint so DLL-not-found failures are debuggable from the wire.
 */
export interface NativeRuntimePaths {
  readonly rid: string;
  readonly expectedNativeDir: string;
  readonly mnnBridgePath: string;
  readonly mnnBridgeLoaded: boolean;
  readonly mnnCoreFetchedPath: string;
  readonly mnnCoreFlattenedPath: string;
  readonly mnnCorePreloaded: boolean;
  readonly flattenError: string | null;
  readonly preloadError: string | null;
}

/**
 * Singleton holder of the last-known NativeRuntimePaths. Ported from
 * CircleAI.Inference.Server.Lifecycle.INativeRuntimeStatus.
 */
export interface INativeRuntimeStatus {
  /** Most recent prep result, or null before the first model load. */
  readonly latest: NativeRuntimePaths | null;
  /** Record the result of a successful prep run. */
  update(paths: NativeRuntimePaths): void;
}

/** Default implementation. Ported from NativeRuntimeStatus. */
export class NativeRuntimeStatus implements INativeRuntimeStatus {
  private _latest: NativeRuntimePaths | null = null;

  get latest(): NativeRuntimePaths | null {
    return this._latest;
  }

  update(paths: NativeRuntimePaths): void {
    if (!paths) throw new Error("paths required");
    this._latest = paths;
  }
}
