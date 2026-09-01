/**
 * hospitality/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hospitality/index.ts
// Full-parity port of CircleAI.Hospitality (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Hospitality vertical: hotel rooms,
// guest reservations, front-desk notes, availability + checkout. Plus the static
// HospitalityDomainContext.
//
// NOTE: The C# HospitalityCompanionAdapter (an ICompanionSession LLM-prompt
// wrapper) is intentionally NOT ported — consistent with the sibling
// domain-board ports.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   decimal NightlyRate              → number
//   bool IsClean                     → boolean
//   DateTime CheckIn / CheckOut      → Date
//   DateTimeOffset AtUtc             → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   AvailableOn — rooms not booked over `date` (CheckIn <= date < CheckOut) AND
//                 IsClean (map insertion order).
//   CheckOut    — throws on unknown reservation; if roomNeedsCleaning and the
//                 room exists, sets that room IsClean=false.
//   NotesFor    — notes for the reservation, AtUtc descending.

/** A hotel room. Mirrors C# `HotelRoom` record. */
export interface HotelRoom {
  readonly roomId: string;
  readonly type: string;
  readonly nightlyRate: number;
  readonly currency: string;
  readonly isClean: boolean;
}

/** Constructs a {@link HotelRoom}. */
export function hotelRoom(
  roomId: string,
  type: string,
  nightlyRate: number,
  currency: string,
  isClean: boolean,
): HotelRoom {
  return { roomId, type, nightlyRate, currency, isClean };
}

/** A guest reservation. Mirrors C# `GuestReservation` record. */
export interface GuestReservation {
  readonly reservationId: string;
  readonly guestName: string;
  readonly roomId: string;
  /** Check-in date (C# `DateTime CheckIn`). */
  readonly checkIn: Date;
  /** Check-out date (C# `DateTime CheckOut`). */
  readonly checkOut: Date;
}

/** Constructs a {@link GuestReservation}. */
export function guestReservation(
  reservationId: string,
  guestName: string,
  roomId: string,
  checkIn: Date,
  checkOut: Date,
): GuestReservation {
  return { reservationId, guestName, roomId, checkIn, checkOut };
}

/** A front-desk note. Mirrors C# `FrontDeskNote` record. */
export interface FrontDeskNote {
  readonly noteId: string;
  readonly reservationId: string;
  readonly body: string;
  /** UTC instant of the note (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link FrontDeskNote}. */
export function frontDeskNote(noteId: string, reservationId: string, body: string, atUtc: Date): FrontDeskNote {
  return { noteId, reservationId, body, atUtc };
}

/** The hospitality board contract. Mirrors C# `IHospitalityBoard`. */
export interface IHospitalityBoard {
  addRoom(r: HotelRoom): void;
  getRoom(id: string): HotelRoom | undefined;
  availableOn(date: Date): readonly HotelRoom[];
  reserve(r: GuestReservation): void;
  checkOut(reservationId: string, roomNeedsCleaning: boolean): void;
  getReservation(id: string): GuestReservation | undefined;
  addNote(n: FrontDeskNote): void;
  notesFor(reservationId: string): readonly FrontDeskNote[];
}

/** Deterministic in-memory {@link IHospitalityBoard}. */
export class InMemoryHospitalityBoard implements IHospitalityBoard {
  private readonly rooms = new Map<string, HotelRoom>();
  private readonly res = new Map<string, GuestReservation>();
  private readonly notes: FrontDeskNote[] = [];

  addRoom(r: HotelRoom): void {
    if (r == null) throw new Error("r required");
    this.rooms.set(r.roomId, r);
  }

  getRoom(id: string): HotelRoom | undefined {
    return this.rooms.get(id);
  }

  availableOn(date: Date): readonly HotelRoom[] {
    const d = date.getTime();
    const booked = new Set<string>();
    for (const r of this.res.values()) {
      if (r.checkIn.getTime() <= d && r.checkOut.getTime() > d) booked.add(r.roomId);
    }
    return [...this.rooms.values()].filter((r) => !booked.has(r.roomId) && r.isClean);
  }

  reserve(r: GuestReservation): void {
    if (r == null) throw new Error("r required");
    this.res.set(r.reservationId, r);
  }

  checkOut(reservationId: string, roomNeedsCleaning: boolean): void {
    const r = this.res.get(reservationId);
    if (r === undefined) throw new Error(`Unknown reservation ${reservationId}`);
    if (roomNeedsCleaning) {
      const room = this.rooms.get(r.roomId);
      if (room !== undefined) this.rooms.set(r.roomId, { ...room, isClean: false });
    }
  }

  getReservation(id: string): GuestReservation | undefined {
    return this.res.get(id);
  }

  addNote(n: FrontDeskNote): void {
    if (n == null) throw new Error("n required");
    this.notes.push(n);
  }

  notesFor(reservationId: string): readonly FrontDeskNote[] {
    return this.notes
      .filter((n) => n.reservationId === reservationId)
      .sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime());
  }
}

/**
 * Static domain context for the Hospitality vertical. Mirrors C#
 * `HospitalityDomainContext`.
 */
export const HospitalityDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Hospitality] Expert hospitality operations assistant. Help with PMS integration, RevPAR optimisation, F&B menu costing, housekeeping scheduling, guest satisfaction recovery, and MICE event coordination. Apply yield management principles. Compliance: Tourism Act, CATHSSETA, Liquor Act, Health regulations, POPIA.",
  complianceFlags: ["Tourism_Act", "CATHSSETA", "Liquor_Act", "Health_Regs", "POPIA"] as readonly string[],
  suggestedTools: ["pms_system", "analytics", "document_editor", "reservation_engine"] as readonly string[],
} as const;
