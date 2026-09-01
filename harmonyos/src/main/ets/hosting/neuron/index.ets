/**
 * hosting/neuron/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hosting/neuron/index.ts — barrel for the CircleAI Neuron.
//
// The concierge (router + gate) + the two-slot residency manager + the
// host-neutral NeuronNode facade. Assembled over the existing AIService brain.

export {
  Organ,
  NeuronGate,
  HeuristicNeuronRouter,
  generalistDecision,
  specialistDecision,
} from "./router";
export type { INeuronRouter, RouteContext, RouteDecision } from "./router";

export { ResidentSlotManager, SlotOutcome } from "./resident_slot_manager";
export type {
  SlotAdmission,
  SpecialistBuilder,
} from "./resident_slot_manager";

export { NeuronNode } from "./neuron_node";
export type { INeuronBrain } from "./neuron_node";
