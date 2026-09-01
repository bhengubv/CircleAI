/**
 * hosting/memory_pressure
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hosting/memory_pressure.ts
//
// Port of CircleAI.Hosting.IMemoryPressureSource + MemoryPressureLevel +
// NullMemoryPressureSource + ManualMemoryPressureSource. (RT-04) A
// platform-published memory-pressure signal that AIService listens to for its
// fallback-chain brownout swap.
//
// C# Subscribe returns an IDisposable; the TS port returns an unsubscribe
// function. Handlers receive (oldLevel, newLevel) and may be async.

/** Coarse memory-pressure level. Mirrors CircleAI.Hosting.MemoryPressureLevel. */
export enum MemoryPressureLevel {
  /** Plenty of headroom; no action. */
  Normal = 0,
  /** OS asked apps to release optional caches. Drop prefix cache. */
  Trim = 1,
  /** OS is about to kill the process. Drop everything; consider downshift. */
  Critical = 2,
}

/** A pressure-transition handler receiving (oldLevel, newLevel). */
export type MemoryPressureHandler = (
  oldLevel: MemoryPressureLevel,
  newLevel: MemoryPressureLevel,
) => Promise<void>;

/**
 * A platform-published memory-pressure signal. Mirrors
 * CircleAI.Hosting.IMemoryPressureSource.
 */
export interface IMemoryPressureSource {
  /** Current pressure level as last observed. */
  readonly current: MemoryPressureLevel;
  /**
   * Subscribe to pressure-level transitions. Returns an unsubscribe function
   * (C# returns an IDisposable).
   */
  subscribe(handler: MemoryPressureHandler): () => void;
}

/**
 * Default source that always reports Normal and never raises events. Mirrors
 * CircleAI.Hosting.NullMemoryPressureSource.
 */
export class NullMemoryPressureSource implements IMemoryPressureSource {
  static readonly instance = new NullMemoryPressureSource();

  get current(): MemoryPressureLevel {
    return MemoryPressureLevel.Normal;
  }

  subscribe(_handler: MemoryPressureHandler): () => void {
    return () => {};
  }
}

/**
 * Manually-driven source. Hosts (or tests) construct one and call raise() when
 * the platform publishes a pressure event. Mirrors
 * CircleAI.Hosting.ManualMemoryPressureSource.
 */
export class ManualMemoryPressureSource implements IMemoryPressureSource {
  private _current: MemoryPressureLevel = MemoryPressureLevel.Normal;
  private readonly handlers: MemoryPressureHandler[] = [];

  get current(): MemoryPressureLevel {
    return this._current;
  }

  subscribe(handler: MemoryPressureHandler): () => void {
    if (!handler) throw new Error("handler required");
    this.handlers.push(handler);
    return () => {
      const idx = this.handlers.indexOf(handler);
      if (idx >= 0) this.handlers.splice(idx, 1);
    };
  }

  /**
   * Publish a new pressure level. Idempotent for the same level — only
   * transitions fire handlers.
   */
  async raise(level: MemoryPressureLevel): Promise<void> {
    if (this._current === level) return;
    const previous = this._current;
    this._current = level;
    const snapshot = [...this.handlers];
    for (const h of snapshot) {
      try {
        await h(previous, level);
      } catch {
        /* error-isolated; pressure handlers must not break the source */
      }
    }
  }
}
