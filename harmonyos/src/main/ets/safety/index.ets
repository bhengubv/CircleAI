/**
 * safety/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// safety/index.ts
// Full-parity port of CircleAI.Safety (SafetyPrimitives.cs). C# is the exact spec.
//
// Real domain types + in-memory store for the Safety vertical: incidents,
// hazards, emergency contacts, severity-routing.
//
// NOTE: This module had no TypeScript counterpart before the StubGuard parity
// pass; it is ported here in full (base types + interface + in-memory impl,
// including the StubGuard additions IncidentCount / RemoveContact /
// HazardsByCategory / ContactsByRelationship) so the port stays at parity.
//
// Type mappings (C# → TS):
//   enum IncidentSeverity            → const enum-like (Info=0 .. Emergency=3)
//   record                           → readonly interface (+ positional factory)
//   double? Latitude/Longitude       → number | null
//   DateTimeOffset AtUtc/NotedUtc    → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>  (hazards)
//   List<T>                          → T[]             (incidents, contacts)
//
// SEMANTICS PARITY:
//   Active            — all incidents, newest-first (AtUtc descending).
//   AtOrAboveSeverity — incidents with (int)Severity >= minimum, newest-first.
//   Hazards           — all hazards, newest-first (NotedUtc descending).
//   HazardsByCategory — category match (OrdinalIgnoreCase), newest-first; empty → [].
//   RemoveContact     — removes the first contact with a matching id (ordinal).
//   ContactsByRelationship — relationship match (OrdinalIgnoreCase), insertion order.

/** Severity of a logged incident. Mirrors C# `IncidentSeverity` (Info = 0). */
export type IncidentSeverity = 0 | 1 | 2 | 3;
/** Frozen value object for {@link IncidentSeverity} members. */
export const IncidentSeverity = Object.freeze({
  Info: 0,
  Warning: 1,
  Critical: 2,
  Emergency: 3,
} as const) satisfies Record<string, IncidentSeverity>;

/** A logged safety incident. Mirrors C# `Incident` record. */
export interface Incident {
  readonly incidentId: string;
  readonly severity: IncidentSeverity;
  readonly description: string;
  /** Optional latitude (C# `double? Latitude`). */
  readonly latitude: number | null;
  /** Optional longitude (C# `double? Longitude`). */
  readonly longitude: number | null;
  /** UTC instant of the incident (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs an {@link Incident}. */
export function incident(
  incidentId: string,
  severity: IncidentSeverity,
  description: string,
  latitude: number | null,
  longitude: number | null,
  atUtc: Date,
): Incident {
  return { incidentId, severity, description, latitude, longitude, atUtc };
}

/** A noted hazard. Mirrors C# `Hazard` record. */
export interface Hazard {
  readonly hazardId: string;
  readonly description: string;
  readonly category: string;
  /** UTC instant the hazard was noted (C# `DateTimeOffset NotedUtc`). */
  readonly notedUtc: Date;
}

/** Constructs a {@link Hazard}. */
export function hazard(hazardId: string, description: string, category: string, notedUtc: Date): Hazard {
  return { hazardId, description, category, notedUtc };
}

/** An emergency contact. Mirrors C# `EmergencyContact` record. */
export interface EmergencyContact {
  readonly contactId: string;
  readonly name: string;
  readonly phone: string;
  readonly relationship: string;
}

/** Constructs an {@link EmergencyContact}. */
export function emergencyContact(
  contactId: string,
  name: string,
  phone: string,
  relationship: string,
): EmergencyContact {
  return { contactId, name, phone, relationship };
}

/** The safety board contract. Mirrors C# `ISafetyBoard`. */
export interface ISafetyBoard {
  log(i: Incident): void;
  /** All incidents, newest-first. */
  readonly active: readonly Incident[];
  /** Total number of incidents logged since this board was created. */
  readonly incidentCount: number;
  atOrAboveSeverity(minimum: IncidentSeverity): readonly Incident[];
  noteHazard(h: Hazard): void;
  /** All hazards, newest-first. */
  readonly hazards: readonly Hazard[];
  /** Hazards filed under a given category (case-insensitive), newest-first. */
  hazardsByCategory(category: string): readonly Hazard[];
  addContact(c: EmergencyContact): void;
  /** Remove the first emergency contact matching an id. Returns whether one was removed. */
  removeContact(contactId: string): boolean;
  /** The first emergency contact, or undefined. */
  readonly firstContact: EmergencyContact | undefined;
  readonly contacts: readonly EmergencyContact[];
  /** Emergency contacts with a given relationship (case-insensitive). */
  contactsByRelationship(relationship: string): readonly EmergencyContact[];
}

/** Deterministic in-memory {@link ISafetyBoard}. */
export class InMemorySafetyBoard implements ISafetyBoard {
  private readonly incidents: Incident[] = [];
  private readonly hazardsById = new Map<string, Hazard>();
  private readonly contactsList: EmergencyContact[] = [];

  log(i: Incident): void {
    if (i == null) throw new Error("i required");
    this.incidents.push(i);
  }

  /** All incidents, newest-first. Mirrors C# `Active`. */
  get active(): readonly Incident[] {
    return [...this.incidents].sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime());
  }

  /** Total number of incidents logged since this board was created. Mirrors C# `IncidentCount`. */
  get incidentCount(): number {
    return this.incidents.length;
  }

  atOrAboveSeverity(minimum: IncidentSeverity): readonly Incident[] {
    return this.incidents
      .filter((i) => i.severity >= minimum)
      .sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime());
  }

  noteHazard(h: Hazard): void {
    if (h == null) throw new Error("h required");
    this.hazardsById.set(h.hazardId, h);
  }

  /** All hazards, newest-first. Mirrors C# `Hazards`. */
  get hazards(): readonly Hazard[] {
    return [...this.hazardsById.values()].sort((a, b) => b.notedUtc.getTime() - a.notedUtc.getTime());
  }

  /** Hazards filed under a given category (case-insensitive), newest-first. Mirrors C# `HazardsByCategory`. */
  hazardsByCategory(category: string): readonly Hazard[] {
    if (category == null || category.length === 0) return [];
    const target = category.toLowerCase();
    return [...this.hazardsById.values()]
      .filter((h) => h.category.toLowerCase() === target)
      .sort((a, b) => b.notedUtc.getTime() - a.notedUtc.getTime());
  }

  addContact(c: EmergencyContact): void {
    if (c == null) throw new Error("c required");
    this.contactsList.push(c);
  }

  /**
   * Remove the first emergency contact matching an id (ordinal, case-sensitive).
   * Returns whether one was removed. Mirrors C# `RemoveContact`.
   */
  removeContact(contactId: string): boolean {
    if (contactId == null || contactId.length === 0) return false;
    const idx = this.contactsList.findIndex((c) => c.contactId === contactId);
    if (idx < 0) return false;
    this.contactsList.splice(idx, 1);
    return true;
  }

  /** The first emergency contact, or undefined. Mirrors C# `FirstContact`. */
  get firstContact(): EmergencyContact | undefined {
    return this.contactsList[0];
  }

  get contacts(): readonly EmergencyContact[] {
    return [...this.contactsList];
  }

  /**
   * Emergency contacts with a given relationship (e.g. "spouse", "doctor"),
   * matched case-insensitively, in insertion order. Mirrors C#
   * `ContactsByRelationship`.
   */
  contactsByRelationship(relationship: string): readonly EmergencyContact[] {
    if (relationship == null || relationship.length === 0) return [];
    const target = relationship.toLowerCase();
    return this.contactsList.filter((c) => c.relationship.toLowerCase() === target);
  }
}
