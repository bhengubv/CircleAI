// hosting/neuron/resident_slot_manager.ts
//
// The RAM admission gate for the second (specialist) slot — TS port of
// CircleAI.Hosting.Neuron.ResidentSlotManager (modeled on the server's
// ModelLifecycleManager). The generalist floor is reserved; a specialist is
// admitted only if it fits beside it, and is evicted first under pressure so
// the generalist never drops.

import type { IChatGenerator, ModelSelection } from "../../inference/index.js";

/** Outcome of an admission attempt. Mirrors SlotOutcome. */
export enum SlotOutcome {
  /** Newly built + resident. */
  Admitted = "admitted",
  /** The requested model was already the resident specialist. */
  AlreadyResident = "already-resident",
  /** Reserved + estimated bytes exceed the RAM ceiling. */
  InsufficientRam = "insufficient-ram",
  /** The build callback returned null. */
  BuildFailed = "build-failed",
}

/** Result of ensureSpecialist. Mirrors SlotAdmission. */
export interface SlotAdmission {
  readonly outcome: SlotOutcome;
  readonly generator: IChatGenerator | null;
}

/** Build callback: resolve + load a generator for a model id (may be async). */
export type SpecialistBuilder = (
  modelId: string,
) => IChatGenerator | null | Promise<IChatGenerator | null>;

/**
 * Owns the single hot-swappable specialist slot beside the always-warm
 * generalist floor. Admission = generalistReserved + estimatedBytes ≤ ceiling.
 * A different pick evicts the incumbent first (one specialist at a time).
 * Mirrors ResidentSlotManager.
 */
export class ResidentSlotManager {
  private specialist: IChatGenerator | null = null;
  private specialistModelId: string | null = null;

  constructor(
    private readonly generalistReservedBytes: number,
    private readonly ramAvailableBytes: () => number,
  ) {}

  /** The resident specialist's model id, or null if the slot is empty. */
  get residentSpecialistModelId(): string | null {
    return this.specialistModelId;
  }

  /** The resident specialist generator, or null. */
  get residentSpecialist(): IChatGenerator | null {
    return this.specialist;
  }

  /**
   * Ensure `selection` is the resident specialist, building it via `build` if
   * needed. Admission-gated on RAM; a different pick evicts the incumbent.
   * Never throws on denial — returns the outcome so the caller can fall back to
   * the generalist floor.
   */
  async ensureSpecialist(
    selection: ModelSelection,
    build: SpecialistBuilder,
  ): Promise<SlotAdmission> {
    const id = selection.modelId;
    if (
      this.specialistModelId !== null &&
      this.specialistModelId.toLowerCase() === id.toLowerCase()
    ) {
      return {
        outcome: SlotOutcome.AlreadyResident,
        generator: this.specialist,
      };
    }

    // RAM admission gate: reserve the floor, then check the specialist fits.
    const needed =
      this.generalistReservedBytes + Math.max(0, selection.estimatedBytes);
    if (needed > this.ramAvailableBytes()) {
      return { outcome: SlotOutcome.InsufficientRam, generator: null };
    }

    // Evict the incumbent (one specialist at a time) before building the new.
    this.evictSpecialist();

    const built = await build(id);
    if (built == null) {
      return { outcome: SlotOutcome.BuildFailed, generator: null };
    }

    this.specialist = built;
    this.specialistModelId = id;
    return { outcome: SlotOutcome.Admitted, generator: built };
  }

  /** Drop the resident specialist (the generalist floor is untouched). */
  evictSpecialist(): void {
    const g = this.specialist;
    this.specialist = null;
    this.specialistModelId = null;
    if (g != null) {
      try {
        g.dispose();
      } catch {
        /* dispose is best-effort */
      }
    }
  }
}
