// healthcare/index.ts
// Full-parity port of CircleAI.Healthcare (C#). C# is the exact spec.
//
// A tiny domain "board" for the healthcare vertical: patient registry,
// appointment scheduling with status updates, and prescriptions. Plus the
// static HealthcareDomainContext (system-prompt snippet + compliance/tool
// hints). Deterministic in-memory implementation — no stubs.
//
// Type mappings (C# → TS):
//   record          → readonly interface
//   DateTime        → Date (DateOfBirth is a plain calendar instant)
//   DateTimeOffset  → Date (AtUtc / PrescribedUtc are UTC instants)
//   ConcurrentDictionary<string,T> (Ordinal) → Map<string,T> (JS string keys
//                     are ordinal, matching StringComparer.Ordinal)
//
// ORDERING: AppointmentsFor sorts ascending by AtUtc; PrescriptionsFor sorts
// descending by PrescribedUtc — both mirror the C# LINQ exactly.

/** A registered patient. Mirrors C# `Patient` record. */
export interface Patient {
  readonly patientId: string;
  readonly name: string;
  readonly dateOfBirth: Date;
}

/** Constructs a {@link Patient} (positional, mirroring the C# record ctor). */
export function patient(patientId: string, name: string, dateOfBirth: Date): Patient {
  return { patientId, name, dateOfBirth };
}

/** A scheduled healthcare appointment. Mirrors C# `HealthAppointment` record. */
export interface HealthAppointment {
  readonly apptId: string;
  readonly patientId: string;
  readonly provider: string;
  /** UTC instant of the appointment (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
  readonly status: string;
}

/** Constructs a {@link HealthAppointment}. */
export function healthAppointment(
  apptId: string,
  patientId: string,
  provider: string,
  atUtc: Date,
  status: string,
): HealthAppointment {
  return { apptId, patientId, provider, atUtc, status };
}

/** A prescription. Mirrors C# `Prescription` record. */
export interface Prescription {
  readonly rxId: string;
  readonly patientId: string;
  readonly medicationName: string;
  readonly dose: string;
  readonly frequency: string;
  readonly prescribedUtc: Date;
}

/** Constructs a {@link Prescription}. */
export function prescription(
  rxId: string,
  patientId: string,
  medicationName: string,
  dose: string,
  frequency: string,
  prescribedUtc: Date,
): Prescription {
  return { rxId, patientId, medicationName, dose, frequency, prescribedUtc };
}

/**
 * The healthcare board contract. Register patients, schedule and re-status
 * appointments, and manage prescriptions.
 */
export interface IHealthcareBoard {
  register(p: Patient): void;
  getPatient(id: string): Patient | undefined;
  schedule(a: HealthAppointment): void;
  updateStatus(apptId: string, status: string): void;
  appointmentsFor(patientId: string): readonly HealthAppointment[];
  prescribe(r: Prescription): void;
  prescriptionsFor(patientId: string): readonly Prescription[];
}

/**
 * Deterministic in-memory {@link IHealthcareBoard}. Three ordinal-keyed maps
 * back patients, appointments, and prescriptions.
 */
export class InMemoryHealthcareBoard implements IHealthcareBoard {
  private readonly patients = new Map<string, Patient>();
  private readonly appts = new Map<string, HealthAppointment>();
  private readonly rx = new Map<string, Prescription>();

  register(p: Patient): void {
    if (p == null) throw new Error("p required");
    this.patients.set(p.patientId, p);
  }

  getPatient(id: string): Patient | undefined {
    return this.patients.get(id);
  }

  schedule(a: HealthAppointment): void {
    if (a == null) throw new Error("a required");
    this.appts.set(a.apptId, a);
  }

  updateStatus(apptId: string, status: string): void {
    const a = this.appts.get(apptId);
    if (a === undefined) throw new Error(`Unknown appointment ${apptId}`);
    this.appts.set(apptId, { ...a, status });
  }

  appointmentsFor(patientId: string): readonly HealthAppointment[] {
    return [...this.appts.values()]
      .filter((a) => a.patientId === patientId)
      .sort((x, y) => x.atUtc.getTime() - y.atUtc.getTime());
  }

  prescribe(r: Prescription): void {
    if (r == null) throw new Error("r required");
    this.rx.set(r.rxId, r);
  }

  prescriptionsFor(patientId: string): readonly Prescription[] {
    return [...this.rx.values()]
      .filter((p) => p.patientId === patientId)
      .sort((x, y) => y.prescribedUtc.getTime() - x.prescribedUtc.getTime());
  }
}

/**
 * Static domain context for the Healthcare vertical: the system-prompt snippet
 * plus compliance and suggested-tool hints. Mirrors C# `HealthcareDomainContext`.
 */
export const HealthcareDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Healthcare] You are a healthcare operations and clinical knowledge assistant. Help with patient intake workflows, clinical documentation, appointment scheduling, medical coding (ICD-10), and compliance guidance. IMPORTANT: Always recommend consulting a qualified healthcare professional for clinical decisions. This is a support tool, not a diagnostic system. Compliance: HIPAA, POPIA, Health Professions Act, NHA.",
  complianceFlags: ["HIPAA", "POPIA", "Health_Professions_Act_56_1974", "NHA_61_2003", "ICD10"] as readonly string[],
  suggestedTools: ["ehr_system", "appointment_scheduler", "document_editor", "icd10_lookup"] as readonly string[],
} as const;
