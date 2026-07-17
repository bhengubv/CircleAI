// hosting/neuron/router.ts
//
// The concierge router — TS port of CircleAI.Hosting.Neuron's INeuronRouter +
// HeuristicNeuronRouter + NeuronGate. Per turn it decides, with cheap
// keyword/length heuristics (never a model), whether the always-warm generalist
// answers or a capability-matched specialist should be hot-loaded. This is the
// "gear system": load and run only what the turn needs.

import { ChatCapability } from "../../inference/index.js";

/** Which resident organ should serve a turn. Mirrors the Organ enum. */
export enum Organ {
  Generalist = "generalist",
  Specialist = "specialist",
}

/** What the router sees for one turn. Mirrors RouteContext. */
export interface RouteContext {
  /** The user's text for this turn. */
  readonly query: string;
  /** True when the turn carries image bytes (vision). */
  readonly hasImage?: boolean;
  /** Total prompt length in chars; defaults to query.length. */
  readonly promptChars?: number;
}

/** The router's per-turn verdict. Mirrors RouteDecision. */
export interface RouteDecision {
  readonly organ: Organ;
  /** Capability the specialist must satisfy (ignored for the generalist). */
  readonly capability: ChatCapability | number;
  /** Short human-readable reason, for observability. */
  readonly reason: string;
}

/** The plain-generalist decision. */
export function generalistDecision(reason = "generalist"): RouteDecision {
  return { organ: Organ.Generalist, capability: ChatCapability.Default, reason };
}

/** A specialist decision for a capability. */
export function specialistDecision(
  capability: ChatCapability | number,
  reason: string,
): RouteDecision {
  return { organ: Organ.Specialist, capability, reason };
}

/** Per-turn router contract. Mirrors INeuronRouter. */
export interface INeuronRouter {
  route(context: RouteContext): RouteDecision;
}

/**
 * Safety/capability veto over a specialist pick. Mirrors NeuronGate: when the
 * predicate rejects a decision the router demotes it to the generalist, so a
 * turn is never blocked — the floor always answers.
 */
export class NeuronGate {
  constructor(
    private readonly allowSpecialist: (decision: RouteDecision) => boolean = () =>
      true,
  ) {}

  /** True if the specialist decision is permitted. */
  allows(decision: RouteDecision): boolean {
    return this.allowSpecialist(decision);
  }
}

/** Reasoning cue substrings → route to a Reasoning specialist. */
const REASONING_CUES: readonly string[] = [
  "debug",
  "stack trace",
  "solve",
  "prove",
  "reason",
  "analy",
  "calculate",
  "equation",
  "step by step",
  "algorithm",
  "why does",
  "derive",
  "diagnose",
];

/**
 * The default concierge router. Cheap heuristics, in priority order:
 *   image bytes     → Specialist(Vision)
 *   long prompt     → Specialist(LongContext)   (≥ longContextChars)
 *   reasoning cues  → Specialist(Reasoning)
 *   otherwise       → Generalist(Default)
 * A NeuronGate veto demotes any specialist pick back to the generalist.
 * Mirrors HeuristicNeuronRouter.
 */
export class HeuristicNeuronRouter implements INeuronRouter {
  private readonly longContextChars: number;
  private readonly gate: NeuronGate;

  constructor(opts: { longContextChars?: number; gate?: NeuronGate } = {}) {
    this.longContextChars = opts.longContextChars ?? 4000;
    this.gate = opts.gate ?? new NeuronGate();
  }

  route(context: RouteContext): RouteDecision {
    const decision = this.classify(context);
    if (decision.organ === Organ.Specialist && !this.gate.allows(decision)) {
      return generalistDecision("gate-vetoed → generalist");
    }
    return decision;
  }

  private classify(context: RouteContext): RouteDecision {
    if (context.hasImage) {
      return specialistDecision(ChatCapability.Vision, "image present");
    }
    const chars = context.promptChars ?? context.query.length;
    if (chars >= this.longContextChars) {
      return specialistDecision(
        ChatCapability.LongContext,
        `long prompt (${chars} chars)`,
      );
    }
    const q = context.query.toLowerCase();
    if (REASONING_CUES.some((c) => q.includes(c))) {
      return specialistDecision(ChatCapability.Reasoning, "reasoning cue");
    }
    return generalistDecision("default");
  }
}
