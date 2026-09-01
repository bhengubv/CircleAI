/**
 * pets/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// pets/index.ts
// Full-parity port of CircleAI.Pets (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Pets vertical: pets, vaccinations,
// weight history, and vet appointments (with an upcoming query). Plus the
// static PetsDomainContext.
//
// NOTE: The C# PetsCompanionAdapter (an ICompanionSession LLM-prompt wrapper) is
// intentionally NOT ported — consistent with the sibling domain-board ports
// (healthcare/education/legal/commerce).
//
// Type mappings (C# → TS):
//   record                          → readonly interface (+ positional factory)
//   double WeightKg                  → number
//   DateTime DateOfBirth             → Date
//   DateTimeOffset ...Utc            → Date
//   DateTimeOffset? BoosterDueUtc    → Date | null
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   Pets                  — ordered by Name ascending (default comparer/ordinal).
//   VaccinationsFor       — pet's vaccinations, AdministeredUtc descending.
//   WeightHistory         — pet's weight samples, AtUtc ascending.
//   UpcomingAppointments  — appointments with AtUtc >= now, AtUtc ascending.
//                           `now` defaults to the current instant (C# uses
//                           DateTimeOffset.UtcNow); an override is accepted for
//                           deterministic testing without changing the default
//                           behaviour.

/** A pet. Mirrors C# `Pet` record. */
export interface Pet {
  readonly petId: string;
  readonly name: string;
  readonly species: string;
  readonly breed: string | null;
  readonly dateOfBirth: Date;
}

/** Constructs a {@link Pet}. */
export function pet(petId: string, name: string, species: string, breed: string | null, dateOfBirth: Date): Pet {
  return { petId, name, species, breed, dateOfBirth };
}

/** A vaccination record. Mirrors C# `Vaccination` record. */
export interface Vaccination {
  readonly petId: string;
  readonly vaccine: string;
  /** UTC instant the vaccine was administered (C# `DateTimeOffset AdministeredUtc`). */
  readonly administeredUtc: Date;
  /** UTC instant a booster is due, if any (C# `DateTimeOffset? BoosterDueUtc`). */
  readonly boosterDueUtc: Date | null;
}

/** Constructs a {@link Vaccination}. */
export function vaccination(
  petId: string,
  vaccine: string,
  administeredUtc: Date,
  boosterDueUtc: Date | null,
): Vaccination {
  return { petId, vaccine, administeredUtc, boosterDueUtc };
}

/** A weight measurement. Mirrors C# `WeightSample` record. */
export interface WeightSample {
  readonly petId: string;
  readonly weightKg: number;
  /** UTC instant of the measurement (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link WeightSample}. */
export function weightSample(petId: string, weightKg: number, atUtc: Date): WeightSample {
  return { petId, weightKg, atUtc };
}

/** A vet appointment. Mirrors C# `VetAppointment` record. */
export interface VetAppointment {
  readonly apptId: string;
  readonly petId: string;
  readonly reason: string;
  /** UTC instant of the appointment (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
  readonly vet: string;
}

/** Constructs a {@link VetAppointment}. */
export function vetAppointment(apptId: string, petId: string, reason: string, atUtc: Date, vet: string): VetAppointment {
  return { apptId, petId, reason, atUtc, vet };
}

/** The pets board contract. Mirrors C# `IPetsBoard`. */
export interface IPetsBoard {
  add(p: Pet): void;
  getPet(id: string): Pet | undefined;
  readonly pets: readonly Pet[];
  recordVaccination(v: Vaccination): void;
  vaccinationsFor(petId: string): readonly Vaccination[];
  recordWeight(s: WeightSample): void;
  weightHistory(petId: string): readonly WeightSample[];
  schedule(a: VetAppointment): void;
  upcomingAppointments(now?: Date): readonly VetAppointment[];
}

/** Ordinal (code-unit) string comparison, matching C# StringComparer.Ordinal. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** Deterministic in-memory {@link IPetsBoard}. */
export class InMemoryPetsBoard implements IPetsBoard {
  private readonly petsById = new Map<string, Pet>();
  private readonly vax: Vaccination[] = [];
  private readonly weights: WeightSample[] = [];
  private readonly appts = new Map<string, VetAppointment>();

  add(p: Pet): void {
    if (p == null) throw new Error("p required");
    this.petsById.set(p.petId, p);
  }

  getPet(id: string): Pet | undefined {
    return this.petsById.get(id);
  }

  get pets(): readonly Pet[] {
    return [...this.petsById.values()].sort((a, b) => ordinalCompare(a.name, b.name));
  }

  recordVaccination(v: Vaccination): void {
    if (v == null) throw new Error("v required");
    this.vax.push(v);
  }

  vaccinationsFor(petId: string): readonly Vaccination[] {
    return this.vax
      .filter((v) => v.petId === petId)
      .sort((a, b) => b.administeredUtc.getTime() - a.administeredUtc.getTime());
  }

  recordWeight(s: WeightSample): void {
    if (s == null) throw new Error("s required");
    this.weights.push(s);
  }

  weightHistory(petId: string): readonly WeightSample[] {
    return this.weights.filter((w) => w.petId === petId).sort((a, b) => a.atUtc.getTime() - b.atUtc.getTime());
  }

  schedule(a: VetAppointment): void {
    if (a == null) throw new Error("a required");
    this.appts.set(a.apptId, a);
  }

  upcomingAppointments(now: Date = new Date()): readonly VetAppointment[] {
    const cutoff = now.getTime();
    return [...this.appts.values()]
      .filter((a) => a.atUtc.getTime() >= cutoff)
      .sort((a, b) => a.atUtc.getTime() - b.atUtc.getTime());
  }
}

/**
 * Static domain context for the Pets vertical. Mirrors C# `PetsDomainContext`.
 */
export const PetsDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Pets] Expert pet care companion. Help with nutrition advice, training techniques (positive reinforcement), health symptom triage (recommend vet for medical decisions), breed-specific care, and emergency first aid basics. Compliance: Animals Protection Act 71/1962, POPIA.",
  complianceFlags: ["Animals_Protection_Act_71_1962", "POPIA", "Vet_Referral_Required"] as readonly string[],
  suggestedTools: ["vet_finder", "pet_health_db", "training_tools", "calendar"] as readonly string[],
} as const;
