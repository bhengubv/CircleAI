/**
 * companion/reasoning/contracts
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/reasoning/contracts.ts
//
// The four companion reasoning-core contracts + their supporting records,
// ported verbatim from CircleAI.Companion.HerJarvis (HerJarvisContracts.cs):
//
//   5.  IWorldModel      + CausalPrediction
//   14. IPredictiveEngine + AnticipatedNeed
//   13. IInnerMonologue   + SelfReflection
//   10. ITheoryOfMind     + OtherMindEstimate
//
// C# uses ValueTask/CancellationToken; TypeScript models both as a Promise —
// cancellation is cooperative and, in these in-memory deterministic
// implementations, never actually observed, so there is no CancellationToken
// parameter to carry. Interface names keep the C# `I` prefix.

// =====================================================================
// 5. World model + causal reasoning.
// =====================================================================

/**
 * A causal prediction: the most-likely outcome for a scenario, its
 * probability, and the observed factors that supported it.
 * Mirrors CircleAI.Companion.HerJarvis.CausalPrediction.
 */
export interface CausalPrediction {
  readonly outcome: string;
  readonly probability: number;
  readonly supportingFactors: readonly string[];
}

/** Predicts outcomes for a scenario from learned evidence. */
export interface IWorldModel {
  predictAsync(scenarioJson: string): Promise<CausalPrediction>;
}

// =====================================================================
// 14. Predictive engine.
// =====================================================================

/**
 * A need the companion anticipates before it is voiced, with the time it
 * is expected by and the probability it materialises.
 * Mirrors CircleAI.Companion.HerJarvis.AnticipatedNeed.
 */
export interface AnticipatedNeed {
  readonly description: string;
  readonly expectedByUtc: Date;
  readonly probability: number;
}

/** Anticipates upcoming needs within a time horizon. */
export interface IPredictiveEngine {
  anticipateAsync(horizonMinutes: number): Promise<readonly AnticipatedNeed[]>;
}

// =====================================================================
// 13. Self-reflection / inner monologue.
// =====================================================================

/**
 * A single reflective thought and the moment it was formed.
 * Mirrors CircleAI.Companion.HerJarvis.SelfReflection.
 */
export interface SelfReflection {
  readonly thought: string;
  readonly at: Date;
}

/** Produces an inner-monologue reflection over a context. */
export interface IInnerMonologue {
  reflectAsync(contextJson: string): Promise<SelfReflection>;
}

// =====================================================================
// 10. Theory of mind.
// =====================================================================

/**
 * An estimate of another mind: the target, its likely beliefs (JSON), and
 * the confidence of the estimate.
 * Mirrors CircleAI.Companion.HerJarvis.OtherMindEstimate.
 */
export interface OtherMindEstimate {
  readonly targetIdentifier: string;
  readonly likelyBeliefJson: string;
  readonly confidence: number;
}

/** Estimates the beliefs of another party from an interaction history. */
export interface ITheoryOfMind {
  estimateAsync(target: string, interactionHistoryJson: string): Promise<OtherMindEstimate>;
}
