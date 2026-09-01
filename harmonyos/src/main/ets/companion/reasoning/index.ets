/**
 * companion/reasoning/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/reasoning/index.ts
//
// Barrel for the companion reasoning core — the four HER/Jarvis contracts and
// their in-memory, deterministic implementations, ported from
// CircleAI.Companion.HerJarvis (+ BayesianWorldModel.cs,
// SequencePredictiveEngine.cs, ReasoningLoopInnerMonologue.cs):
//
//   IWorldModel       — FrequencyWorldModel, BayesianWorldModel
//   IPredictiveEngine — HistogramPredictiveEngine, SequencePredictiveEngine
//   IInnerMonologue   — TemplateInnerMonologue, ReasoningLoopInnerMonologue
//   ITheoryOfMind     — BeliefTrackerTheoryOfMind

// Contracts + supporting records.
export type {
  CausalPrediction,
  IWorldModel,
  AnticipatedNeed,
  IPredictiveEngine,
  SelfReflection,
  IInnerMonologue,
  OtherMindEstimate,
  ITheoryOfMind,
} from "./contracts";

// World models.
export { FrequencyWorldModel, BayesianWorldModel } from "./world_model";

// Predictive engines.
export { HistogramPredictiveEngine, SequencePredictiveEngine } from "./predictive_engine";

// Inner monologue.
export { TemplateInnerMonologue, ReasoningLoopInnerMonologue } from "./inner_monologue";

// Theory of mind.
export { BeliefTrackerTheoryOfMind } from "./theory_of_mind";

// Shared helpers, exported for callers that want the same scenario→observation
// extraction or STJ-faithful serialisation the models use internally.
export { extractObservations, jsonElementToString } from "./json_observations";
export { stjSerializeDoubleMap, stjEscape, stjDouble } from "./stj_json";
