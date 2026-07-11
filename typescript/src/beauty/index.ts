// beauty/index.ts
// Full-parity port of CircleAI.Beauty (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Beauty vertical: treatments,
// appointments, skin profiles, and a concern-based treatment recommender. Plus
// the static BeautyDomainContext.
//
// NOTE: The C# BeautyCompanionAdapter (an ICompanionSession LLM-prompt wrapper)
// is intentionally NOT ported — consistent with the sibling domain-board ports.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   int DurationMinutes              → number
//   decimal Price                    → number
//   string? Notes                    → string | null
//   IReadOnlyList<string> Concerns   → readonly string[]
//   DateTimeOffset AtUtc             → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   AppointmentsBetween — appts with start <= AtUtc <= end, AtUtc ascending.
//   RecommendFor        — [] when no profile; else treatments whose Name contains
//                         any of the profile's concerns (ordinal case-insensitive).

/** A bookable treatment. Mirrors C# `Treatment` record. */
export interface Treatment {
  readonly treatmentId: string;
  readonly name: string;
  readonly durationMinutes: number;
  readonly price: number;
  readonly currency: string;
}

/** Constructs a {@link Treatment}. */
export function treatment(
  treatmentId: string,
  name: string,
  durationMinutes: number,
  price: number,
  currency: string,
): Treatment {
  return { treatmentId, name, durationMinutes, price, currency };
}

/** A booked appointment. Mirrors C# `Appointment` record. */
export interface Appointment {
  readonly apptId: string;
  readonly clientName: string;
  readonly treatmentId: string;
  /** UTC instant of the appointment (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
  readonly notes: string | null;
}

/** Constructs an {@link Appointment}. */
export function appointment(
  apptId: string,
  clientName: string,
  treatmentId: string,
  atUtc: Date,
  notes: string | null,
): Appointment {
  return { apptId, clientName, treatmentId, atUtc, notes };
}

/** A client's skin profile. Mirrors C# `SkinProfile` record. */
export interface SkinProfile {
  readonly clientName: string;
  readonly skinType: string;
  readonly concerns: readonly string[];
}

/** Constructs a {@link SkinProfile}. */
export function skinProfile(clientName: string, skinType: string, concerns: readonly string[]): SkinProfile {
  return { clientName, skinType, concerns };
}

/** The beauty board contract. Mirrors C# `IBeautyBoard`. */
export interface IBeautyBoard {
  addTreatment(t: Treatment): void;
  getTreatment(id: string): Treatment | undefined;
  book(a: Appointment): void;
  appointmentsBetween(start: Date, end: Date): readonly Appointment[];
  saveProfile(p: SkinProfile): void;
  getProfile(clientName: string): SkinProfile | undefined;
  recommendFor(clientName: string): readonly Treatment[];
}

/** Deterministic in-memory {@link IBeautyBoard}. */
export class InMemoryBeautyBoard implements IBeautyBoard {
  private readonly treatments = new Map<string, Treatment>();
  private readonly appts: Appointment[] = [];
  private readonly profiles = new Map<string, SkinProfile>();

  addTreatment(t: Treatment): void {
    if (t == null) throw new Error("t required");
    this.treatments.set(t.treatmentId, t);
  }

  getTreatment(id: string): Treatment | undefined {
    return this.treatments.get(id);
  }

  book(a: Appointment): void {
    if (a == null) throw new Error("a required");
    this.appts.push(a);
  }

  appointmentsBetween(start: Date, end: Date): readonly Appointment[] {
    const s = start.getTime();
    const e = end.getTime();
    return this.appts
      .filter((a) => a.atUtc.getTime() >= s && a.atUtc.getTime() <= e)
      .sort((x, y) => x.atUtc.getTime() - y.atUtc.getTime());
  }

  saveProfile(p: SkinProfile): void {
    if (p == null) throw new Error("p required");
    this.profiles.set(p.clientName, p);
  }

  getProfile(clientName: string): SkinProfile | undefined {
    return this.profiles.get(clientName);
  }

  recommendFor(clientName: string): readonly Treatment[] {
    const p = this.profiles.get(clientName);
    if (p === undefined) return [];
    return [...this.treatments.values()].filter((t) => {
      const name = t.name.toLowerCase();
      return p.concerns.some((c) => name.includes(c.toLowerCase()));
    });
  }
}

/**
 * Static domain context for the Beauty vertical. Mirrors C#
 * `BeautyDomainContext`.
 */
export const BeautyDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Beauty] Expert beauty and personal care companion. Help with skincare routine building, ingredient education, product recommendations (without brand bias), hair care, makeup guidance, and wellness rituals. Celebrate all skin tones, types, and expressions. Compliance: POPIA, Medicines and Related Substances Act (cosmetic claims).",
  complianceFlags: ["POPIA", "Medicines_Act_cosmetic_claims"] as readonly string[],
  suggestedTools: ["product_db", "ingredient_checker", "web_search"] as readonly string[],
} as const;
