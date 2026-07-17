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
} from "./router.js";
export type { INeuronRouter, RouteContext, RouteDecision } from "./router.js";

export { ResidentSlotManager, SlotOutcome } from "./resident_slot_manager.js";
export type {
  SlotAdmission,
  SpecialistBuilder,
} from "./resident_slot_manager.js";

export { NeuronNode } from "./neuron_node.js";
export type { INeuronBrain } from "./neuron_node.js";
