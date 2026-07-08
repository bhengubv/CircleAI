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
} from "./contracts.js";

// World models.
export { FrequencyWorldModel, BayesianWorldModel } from "./world_model.js";

// Predictive engines.
export { HistogramPredictiveEngine, SequencePredictiveEngine } from "./predictive_engine.js";

// Inner monologue.
export { TemplateInnerMonologue, ReasoningLoopInnerMonologue } from "./inner_monologue.js";

// Theory of mind.
export { BeliefTrackerTheoryOfMind } from "./theory_of_mind.js";

// Shared helpers, exported for callers that want the same scenario→observation
// extraction or STJ-faithful serialisation the models use internally.
export { extractObservations, jsonElementToString } from "./json_observations.js";
export { stjSerializeDoubleMap, stjEscape, stjDouble } from "./stj_json.js";
