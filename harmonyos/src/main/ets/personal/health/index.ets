/**
 * personal/health/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// personal/health/index.ts
// Full-parity port of CircleAI.Personal.Health (C#). C# is the exact spec.
//
// Domain types + in-memory store for personal health: vitals (BP, glucose,
// weight, etc.), allergies, medications, and last-reading helpers. Plus the
// static PersonalHealthDomainContext. Privacy: instances are user-scoped.
//
// Type mappings (C# → TS):
//   enum VitalKind        → TS enum (ordinals are the wire contract)
//   record                → readonly interface (+ positional factory)
//   double Value          → number
//   DateTimeOffset        → Date; DateTimeOffset? EndedAtUtc → Date | null
//   string? Note          → string | null
//   ConcurrentDictionary (Ordinal) + List under a lock → Map + array
//
// PARITY NOTES:
//   ReadSince(kind, since)  — kind match AND AtUtc >= since, sorted AtUtc asc
//   Latest(kind)            — most recent reading of that kind, or undefined
//   ActiveMedications()     — EndedAtUtc is null, sorted by Name (ordinal) asc

/** The kind of vital being recorded. Ordinals mirror the C# enum. */
export enum VitalKind {
  BloodPressureSystolic = 0,
  BloodPressureDiastolic = 1,
  GlucoseMgDl = 2,
  WeightKg = 3,
  HeartRateBpm = 4,
  TemperatureC = 5,
  OxygenPct = 6,
  StepsCount = 7,
}

/** A single vital reading. Mirrors C# `VitalReading` record. */
export interface VitalReading {
  readonly kind: VitalKind;
  readonly value: number;
  readonly atUtc: Date;
  readonly note: string | null;
}

/** Constructs a {@link VitalReading}. */
export function vitalReading(kind: VitalKind, value: number, atUtc: Date, note: string | null): VitalReading {
  return { kind, value, atUtc, note };
}

/** A recorded allergy. Mirrors C# `Allergy` record. */
export interface Allergy {
  readonly allergyId: string;
  readonly substance: string;
  readonly severity: string;
}

/** Constructs an {@link Allergy}. */
export function allergy(allergyId: string, substance: string, severity: string): Allergy {
  return { allergyId, substance, severity };
}

/** A medication, optionally ended. Mirrors C# `Medication` record. */
export interface Medication {
  readonly medId: string;
  readonly name: string;
  readonly dose: string;
  readonly frequency: string;
  readonly startedAtUtc: Date;
  readonly endedAtUtc: Date | null;
}

/** Constructs a {@link Medication}. */
export function medication(
  medId: string,
  name: string,
  dose: string,
  frequency: string,
  startedAtUtc: Date,
  endedAtUtc: Date | null,
): Medication {
  return { medId, name, dose, frequency, startedAtUtc, endedAtUtc };
}

/** The personal-health board contract. */
export interface IPersonalHealthBoard {
  record(v: VitalReading): void;
  readSince(kind: VitalKind, since: Date): readonly VitalReading[];
  latest(kind: VitalKind): VitalReading | undefined;
  addAllergy(a: Allergy): void;
  readonly allergies: readonly Allergy[];
  addMedication(m: Medication): void;
  endMedication(medId: string, endedAtUtc: Date): void;
  activeMedications(): readonly Medication[];
}

/** Deterministic in-memory {@link IPersonalHealthBoard}. */
export class InMemoryPersonalHealthBoard implements IPersonalHealthBoard {
  private readonly vitals: VitalReading[] = [];
  private readonly allergyMap = new Map<string, Allergy>();
  private readonly meds = new Map<string, Medication>();

  record(v: VitalReading): void {
    if (v == null) throw new Error("v required");
    this.vitals.push(v);
  }

  readSince(kind: VitalKind, since: Date): readonly VitalReading[] {
    const from = since.getTime();
    return this.vitals
      .filter((v) => v.kind === kind && v.atUtc.getTime() >= from)
      .sort((a, b) => a.atUtc.getTime() - b.atUtc.getTime());
  }

  latest(kind: VitalKind): VitalReading | undefined {
    return this.vitals
      .filter((v) => v.kind === kind)
      .sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime())[0];
  }

  addAllergy(a: Allergy): void {
    if (a == null) throw new Error("a required");
    this.allergyMap.set(a.allergyId, a);
  }

  get allergies(): readonly Allergy[] {
    return [...this.allergyMap.values()];
  }

  addMedication(m: Medication): void {
    if (m == null) throw new Error("m required");
    this.meds.set(m.medId, m);
  }

  endMedication(medId: string, endedAtUtc: Date): void {
    const m = this.meds.get(medId);
    if (m === undefined) throw new Error(`Unknown medication ${medId}`);
    this.meds.set(medId, { ...m, endedAtUtc });
  }

  activeMedications(): readonly Medication[] {
    return [...this.meds.values()]
      .filter((m) => m.endedAtUtc === null)
      .sort((a, b) => ordinalCompare(a.name, b.name));
  }
}

/** Ordinal (code-unit) string comparison, matching C# StringComparer.Ordinal. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/**
 * Static domain context for the Personal.Health vertical. Mirrors C#
 * `PersonalHealthDomainContext`.
 */
export const PersonalHealthDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Personal.Health] Personal health and wellness assistant. Help with symptom tracking, appointment preparation, medication reminders, health goal setting, nutrition basics, and health literacy. IMPORTANT: Always recommend consulting a qualified healthcare professional for medical decisions. This is not medical advice. Compliance: POPIA, Health Professions Act.",
  complianceFlags: ["POPIA", "Health_Professions_Act", "Not_Medical_Advice"] as readonly string[],
  suggestedTools: ["health_tracker", "symptom_checker_ref", "calendar", "document_editor"] as readonly string[],
} as const;
