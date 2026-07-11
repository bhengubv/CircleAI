// tourism/index.ts
// Full-parity port of CircleAI.Tourism (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Tourism vertical: attractions,
// itineraries, bookings. Plus the static TourismDomainContext.
//
// NOTE: The C# TourismCompanionAdapter (an ICompanionSession LLM-prompt wrapper)
// is intentionally NOT ported — consistent with the sibling domain-board ports.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   double Lat / Lon                 → number
//   TimeSpan StartLocal / EndLocal   → number of milliseconds (a TimeSpan is a
//                                      time-of-day offset; ms is a faithful carrier).
//   IReadOnlyList<T>                 → readonly T[]
//   string? Note                     → string | null
//   int DayIndex / Travelers         → number
//   decimal TotalPrice               → number
//   DateTime StartDate               → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   AttractionsInCity — throws on blank; City ordinal case-insensitive; Name asc.
//   ByTag             — throws on blank; any Tag ordinal case-insensitive; Name asc.
//   Bookings          — snapshot copy (insertion order).

/** A tourist attraction. Mirrors C# `Attraction` record. */
export interface Attraction {
  readonly attractionId: string;
  readonly name: string;
  readonly city: string;
  readonly country: string;
  readonly lat: number;
  readonly lon: number;
  readonly tags: readonly string[];
}

/** Constructs an {@link Attraction}. */
export function attraction(
  attractionId: string,
  name: string,
  city: string,
  country: string,
  lat: number,
  lon: number,
  tags: readonly string[],
): Attraction {
  return { attractionId, name, city, country, lat, lon, tags };
}

/** A single itinerary item. Mirrors C# `ItineraryItem` record. */
export interface ItineraryItem {
  readonly dayIndex: number;
  /** Start time-of-day as a value in milliseconds (C# `TimeSpan StartLocal`). */
  readonly startLocalMs: number;
  /** End time-of-day as a value in milliseconds (C# `TimeSpan EndLocal`). */
  readonly endLocalMs: number;
  readonly attractionId: string;
  readonly note: string | null;
}

/** Constructs an {@link ItineraryItem}. */
export function itineraryItem(
  dayIndex: number,
  startLocalMs: number,
  endLocalMs: number,
  attractionId: string,
  note: string | null,
): ItineraryItem {
  return { dayIndex, startLocalMs, endLocalMs, attractionId, note };
}

/** A planned itinerary. Mirrors C# `Itinerary` record. */
export interface Itinerary {
  readonly itineraryId: string;
  readonly title: string;
  readonly items: readonly ItineraryItem[];
}

/** Constructs an {@link Itinerary}. */
export function itinerary(itineraryId: string, title: string, items: readonly ItineraryItem[]): Itinerary {
  return { itineraryId, title, items };
}

/** A tourism booking. Mirrors C# `TourismBooking` record. */
export interface TourismBooking {
  readonly bookingId: string;
  readonly itineraryId: string;
  /** Start date (C# `DateTime StartDate`). */
  readonly startDate: Date;
  readonly travelers: number;
  readonly totalPrice: number;
  readonly currency: string;
}

/** Constructs a {@link TourismBooking}. */
export function tourismBooking(
  bookingId: string,
  itineraryId: string,
  startDate: Date,
  travelers: number,
  totalPrice: number,
  currency: string,
): TourismBooking {
  return { bookingId, itineraryId, startDate, travelers, totalPrice, currency };
}

/** The tourism board contract. Mirrors C# `ITourismBoard`. */
export interface ITourismBoard {
  add(a: Attraction): void;
  attractionsInCity(city: string): readonly Attraction[];
  byTag(tag: string): readonly Attraction[];
  plan(i: Itinerary): void;
  getItinerary(id: string): Itinerary | undefined;
  book(b: TourismBooking): void;
  readonly bookings: readonly TourismBooking[];
}

/** Ordinal (code-unit) string comparison, matching C# StringComparer.Ordinal. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** Deterministic in-memory {@link ITourismBoard}. */
export class InMemoryTourismBoard implements ITourismBoard {
  private readonly attractions = new Map<string, Attraction>();
  private readonly itineraries = new Map<string, Itinerary>();
  private readonly bookingList: TourismBooking[] = [];

  add(a: Attraction): void {
    if (a == null) throw new Error("a required");
    this.attractions.set(a.attractionId, a);
  }

  attractionsInCity(city: string): readonly Attraction[] {
    if (city == null || city.trim() === "") throw new Error("city required");
    const c = city.toLowerCase();
    return [...this.attractions.values()]
      .filter((a) => a.city.toLowerCase() === c)
      .sort((x, y) => ordinalCompare(x.name, y.name));
  }

  byTag(tag: string): readonly Attraction[] {
    if (tag == null || tag.trim() === "") throw new Error("tag required");
    const needle = tag.toLowerCase();
    return [...this.attractions.values()]
      .filter((a) => a.tags.some((t) => t.toLowerCase() === needle))
      .sort((x, y) => ordinalCompare(x.name, y.name));
  }

  plan(i: Itinerary): void {
    if (i == null) throw new Error("i required");
    this.itineraries.set(i.itineraryId, i);
  }

  getItinerary(id: string): Itinerary | undefined {
    return this.itineraries.get(id);
  }

  book(b: TourismBooking): void {
    if (b == null) throw new Error("b required");
    this.bookingList.push(b);
  }

  get bookings(): readonly TourismBooking[] {
    return [...this.bookingList];
  }
}

/**
 * Static domain context for the Tourism vertical. Mirrors C#
 * `TourismDomainContext`.
 */
export const TourismDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Tourism] Expert tourism and travel operations assistant. Help with itinerary design, tour package costing, guide briefing notes, destination marketing, and safety management plans. Apply experiential travel principles. Compliance: Tourism Act 3/2014, SABS tour operator standards, SATSA, POPIA.",
  complianceFlags: ["Tourism_Act_3_2014", "SABS_Tour_Ops", "SATSA", "POPIA"] as readonly string[],
  suggestedTools: ["mapping", "booking_system", "document_editor", "weather_api"] as readonly string[],
} as const;
