/**
 * inference/power_budget
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// inference/power_budget.ts
//
// KvCompressionMode + PowerBudgetPolicy — ported from CircleAI.Inference
// (PowerBudget.cs + the KvCompressionMode / KvCompressionApplyResult enums in
// MnnInterop.cs). PowerBudget itself is already declared in inference/index.ts;
// this module adds the runtime translation (Resolution) + the KV-compression
// typed apply surface.

import { PowerBudget } from "./index";

// ─────────────────────────────────────────────────────────────────────────────
// KvCompressionMode
// ─────────────────────────────────────────────────────────────────────────────

/**
 * KV cache compression mode. Mirrors the C ABI's integer encoding so the
 * managed and native layers agree without translation tables.
 * Ported from CircleAI.Inference.KvCompressionMode.
 */
export enum KvCompressionMode {
  /** Full FP16 KV cache — default behaviour, always supported. */
  Off = 0,
  /** TurboQuant at 4 bits per channel — ~4x shrink, < 1% accuracy loss. */
  TurboQuant4Bit = 1,
  /** TurboQuant at 3 bits per channel — ~5x shrink, marginal accuracy loss. */
  TurboQuant3Bit = 2,
  /** TurboQuant at 2 bits per channel — ~8x shrink, noticeable accuracy loss. */
  TurboQuant2Bit = 3,
}

/**
 * Outcome of applying a KV-compression mode after being translated into a
 * typed result. Mirrors the C ABI status codes
 * (CircleAI.Inference.KvCompressionApplyResult).
 */
export enum KvCompressionApplyResult {
  /** Native path accepted the mode and will use it. */
  Applied = 0,
  /** The mode value was outside the valid 0..3 range. */
  InvalidMode = 1,
  /** LEGACY (mnnbridge <= 1.1.0) — scaffolding-only response. */
  NotImplemented = 2,
  /** Handle pointer was invalid. */
  HandleInvalid = -1,
}

/**
 * Contract for the native KV-compression seam. Mirrors the two operations of
 * C#'s internal MnnKvCompression (Set/Get) over a model handle. In C# this
 * was internal because MnnModelHandle was internal; here it is injected so the
 * runtime can be exercised deterministically without a native library.
 */
export interface IKvCompressionController {
  /** Applies the requested mode and returns the typed result. */
  set(mode: KvCompressionMode): KvCompressionApplyResult;
  /** Reads the last-set mode (or Off on invalid handle). */
  get(): KvCompressionMode;
}

/**
 * Deterministic in-memory KV-compression controller. Validates the mode range
 * exactly as C#'s MnnKvCompression.Set switch does and remembers the last
 * applied mode. Stand-in for the native ATTENTION_OPTION path — no throw, no
 * native calls.
 */
export class InMemoryKvCompressionController implements IKvCompressionController {
  private mode: KvCompressionMode = KvCompressionMode.Off;

  set(mode: KvCompressionMode): KvCompressionApplyResult {
    // Mirror the native contract: only 0..3 are valid.
    if (mode < 0 || mode > 3) return KvCompressionApplyResult.InvalidMode;
    this.mode = mode;
    return KvCompressionApplyResult.Applied;
  }

  get(): KvCompressionMode {
    return this.mode >= 0 && this.mode <= 3 ? this.mode : KvCompressionMode.Off;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// PowerBudgetPolicy
// ─────────────────────────────────────────────────────────────────────────────

/**
 * The runtime's translation of a PowerBudget into concrete generation knobs.
 * Mirrors CircleAI.Inference.PowerBudgetPolicy.Resolution.
 */
export interface PowerBudgetResolution {
  /** Cap on output tokens for this call. */
  readonly maxTokens: number;
  /** Which KvCompressionMode the runtime prefers for this budget. */
  readonly preferredKvMode: KvCompressionMode;
  /**
   * When a fallback chain is configured, whether to pick a smaller model than
   * the chain head. `true` for PowerBudget.Low.
   */
  readonly preferSmallerModelInChain: boolean;
}

/**
 * Maps a PowerBudget to concrete knobs. Ported from
 * CircleAI.Inference.PowerBudgetPolicy.Resolve. Generators call this with the
 * user's requested max-tokens; the returned resolution caps any over-budget
 * value without altering the caller's options.
 *
 * @param budget The declared budget.
 * @param requestedMaxTokens The caller's requested max-tokens.
 * @param batteryLevelPercent 0..100 if known, null when the platform doesn't
 *   surface it. Used to auto-downgrade Normal on low battery.
 * @param thermalThrottled true when the platform reports an elevated thermal
 *   state. Used to auto-downgrade High.
 */
export function resolvePowerBudget(
  budget: PowerBudget,
  requestedMaxTokens: number,
  batteryLevelPercent: number | null = null,
  thermalThrottled = false,
): PowerBudgetResolution {
  // Auto-downgrade based on device state.
  if (
    budget === PowerBudget.Normal &&
    batteryLevelPercent !== null &&
    batteryLevelPercent < 15
  ) {
    budget = PowerBudget.Low;
  }
  if (budget === PowerBudget.High && thermalThrottled) {
    budget = PowerBudget.Normal;
  }

  switch (budget) {
    case PowerBudget.None:
      return {
        maxTokens: requestedMaxTokens,
        preferredKvMode: KvCompressionMode.TurboQuant4Bit,
        preferSmallerModelInChain: false,
      };
    case PowerBudget.Low:
      return {
        maxTokens: Math.min(requestedMaxTokens, 64),
        preferredKvMode: KvCompressionMode.TurboQuant4Bit,
        preferSmallerModelInChain: true,
      };
    case PowerBudget.Normal:
      return {
        maxTokens: Math.min(requestedMaxTokens, 512),
        preferredKvMode: KvCompressionMode.TurboQuant4Bit,
        preferSmallerModelInChain: false,
      };
    case PowerBudget.High:
      return {
        maxTokens: Math.min(requestedMaxTokens, 2048),
        preferredKvMode: KvCompressionMode.Off,
        preferSmallerModelInChain: false,
      };
    default:
      return {
        maxTokens: requestedMaxTokens,
        preferredKvMode: KvCompressionMode.TurboQuant4Bit,
        preferSmallerModelInChain: false,
      };
  }
}
