// elderly/index.ts
// Full-parity port of CircleAI.Elderly (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Elderly-care vertical: per-resident
// care plans, medication reminders (activate/deactivate), and check-ins with a
// latest lookup and a missed-check-in test. Plus the static
// ElderlyDomainContext.
//
// NOTE: The C# ElderlyCompanionAdapter (an ICompanionSession LLM-prompt wrapper)
// is intentionally NOT ported — consistent with the sibling domain-board ports
// (healthcare/education/legal/commerce).
//
// Type mappings (C# → TS):
//   record                          → readonly interface (+ positional factory)
//   IReadOnlyList<string>            → readonly string[]
//   TimeSpan DailyAt                 → number of milliseconds (a TimeSpan is a
//                                      duration; ms is a faithful carrier)
//   bool Active                      → boolean
//   DateTimeOffset AtUtc            → Date
//   string? Note                     → string | null
//   ConcurrentDictionary (Ordinal)   → Map<string,T> (residents keyed by name)
//
// SEMANTICS PARITY:
//   ActiveRemindersFor  — reminders for resident that are Active (enum order).
//   DeactivateReminder  — throws on unknown reminder id.
//   LatestCheckIn       — resident's newest check-in (AtUtc descending) or null.
//   MissedCheckIn       — true when there is no check-in, or the latest is before
//                         `since`.

/** A resident's care plan. Mirrors C# `CarePlan` record. */
export interface CarePlan {
  readonly planId: string;
  readonly residentName: string;
  readonly medicalConditions: readonly string[];
  readonly allergies: readonly string[];
  readonly carerNotes: string;
}

/** Constructs a {@link CarePlan}. */
export function carePlan(
  planId: string,
  residentName: string,
  medicalConditions: readonly string[],
  allergies: readonly string[],
  carerNotes: string,
): CarePlan {
  return { planId, residentName, medicalConditions, allergies, carerNotes };
}

/** A daily medication reminder. Mirrors C# `MedReminder` record. */
export interface MedReminder {
  readonly reminderId: string;
  readonly residentName: string;
  readonly medication: string;
  /** Time-of-day the reminder fires, as a duration in ms (C# `TimeSpan DailyAt`). */
  readonly dailyAt: number;
  readonly active: boolean;
}

/** Constructs a {@link MedReminder}. */
export function medReminder(
  reminderId: string,
  residentName: string,
  medication: string,
  dailyAt: number,
  active: boolean,
): MedReminder {
  return { reminderId, residentName, medication, dailyAt, active };
}

/** A resident check-in. Mirrors C# `CheckIn` record. */
export interface CheckIn {
  readonly checkInId: string;
  readonly residentName: string;
  /** UTC instant of the check-in (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
  readonly status: string;
  readonly note: string | null;
}

/** Constructs a {@link CheckIn}. */
export function checkIn(
  checkInId: string,
  residentName: string,
  atUtc: Date,
  status: string,
  note: string | null,
): CheckIn {
  return { checkInId, residentName, atUtc, status, note };
}

/** The elderly-care board contract. Mirrors C# `IElderlyCareBoard`. */
export interface IElderlyCareBoard {
  setPlan(p: CarePlan): void;
  getPlan(resident: string): CarePlan | undefined;
  addReminder(r: MedReminder): void;
  deactivateReminder(reminderId: string): void;
  activeRemindersFor(resident: string): readonly MedReminder[];
  recordCheckIn(c: CheckIn): void;
  latestCheckIn(resident: string): CheckIn | undefined;
  missedCheckIn(resident: string, since: Date): boolean;
}

/** Deterministic in-memory {@link IElderlyCareBoard}. */
export class InMemoryElderlyCareBoard implements IElderlyCareBoard {
  /** Care plans keyed by resident name (C# uses ResidentName as the key). */
  private readonly plans = new Map<string, CarePlan>();
  private readonly reminders = new Map<string, MedReminder>();
  private readonly checkIns: CheckIn[] = [];

  setPlan(p: CarePlan): void {
    if (p == null) throw new Error("p required");
    this.plans.set(p.residentName, p);
  }

  getPlan(resident: string): CarePlan | undefined {
    return this.plans.get(resident);
  }

  addReminder(r: MedReminder): void {
    if (r == null) throw new Error("r required");
    this.reminders.set(r.reminderId, r);
  }

  deactivateReminder(reminderId: string): void {
    const r = this.reminders.get(reminderId);
    if (r === undefined) throw new Error(`Unknown reminder ${reminderId}`);
    this.reminders.set(reminderId, { ...r, active: false });
  }

  activeRemindersFor(resident: string): readonly MedReminder[] {
    return [...this.reminders.values()].filter((r) => r.residentName === resident && r.active);
  }

  recordCheckIn(c: CheckIn): void {
    if (c == null) throw new Error("c required");
    this.checkIns.push(c);
  }

  latestCheckIn(resident: string): CheckIn | undefined {
    const matches = this.checkIns
      .filter((c) => c.residentName === resident)
      .sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime());
    return matches.length > 0 ? matches[0] : undefined;
  }

  missedCheckIn(resident: string, since: Date): boolean {
    const latest = this.latestCheckIn(resident);
    return latest === undefined || latest.atUtc.getTime() < since.getTime();
  }
}

/**
 * Static domain context for the Elderly vertical. Mirrors C#
 * `ElderlyDomainContext`.
 */
export const ElderlyDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Elderly] Compassionate care assistant for elderly persons and their caregivers. Help with medication reminders, appointment management, benefit and pension queries, carer communication, and social activity suggestions. Use clear, patient language. Compliance: Older Persons Act 13/2006, POPIA, Social Assistance Act.",
  complianceFlags: ["Older_Persons_Act_13_2006", "Social_Assistance_Act", "POPIA"] as readonly string[],
  suggestedTools: ["medication_reminder", "calendar", "web_search", "document_editor"] as readonly string[],
} as const;
