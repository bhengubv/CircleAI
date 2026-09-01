/**
 * travel/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// travel/index.ts
// Full-parity port of CircleAI.Travel (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Travel vertical: flights, hotel stays,
// trips, trip-cost + upcoming rollups. Plus the static TravelDomainContext.
//
// NOTE: The C# TravelCompanionAdapter (an ICompanionSession LLM-prompt wrapper)
// is intentionally NOT ported — consistent with the sibling domain-board ports.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   decimal Price / NightlyRate      → number
//   IReadOnlyList<string>            → readonly string[]
//   DateTimeOffset DepartUtc/ArriveUtc → Date
//   DateTime CheckIn/CheckOut/*Date  → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//   overloaded Add(Flight)/Add(HotelStay) → TS overload signatures on `add`
//
// SEMANTICS PARITY:
//   TripCost      — throws on unknown trip; sum of resolved flight Prices +
//                   (NightlyRate × max(1, (CheckOut − CheckIn) whole days)) for
//                   resolved stays. Missing ids are skipped. C# TimeSpan.Days
//                   truncates toward zero.
//   UpcomingTrips — trips with StartDate >= now, StartDate ascending.

/** A flight. Mirrors C# `Flight` record. */
export interface Flight {
  readonly flightId: string;
  readonly from: string;
  readonly to: string;
  /** UTC departure instant (C# `DateTimeOffset DepartUtc`). */
  readonly departUtc: Date;
  /** UTC arrival instant (C# `DateTimeOffset ArriveUtc`). */
  readonly arriveUtc: Date;
  readonly carrier: string;
  readonly cabin: string;
  readonly price: number;
  readonly currency: string;
}

/** Constructs a {@link Flight}. */
export function flight(
  flightId: string,
  from: string,
  to: string,
  departUtc: Date,
  arriveUtc: Date,
  carrier: string,
  cabin: string,
  price: number,
  currency: string,
): Flight {
  return { flightId, from, to, departUtc, arriveUtc, carrier, cabin, price, currency };
}

/** A hotel stay. Mirrors C# `HotelStay` record. */
export interface HotelStay {
  readonly stayId: string;
  readonly hotel: string;
  readonly city: string;
  /** Check-in date (C# `DateTime CheckIn`). */
  readonly checkIn: Date;
  /** Check-out date (C# `DateTime CheckOut`). */
  readonly checkOut: Date;
  readonly nightlyRate: number;
  readonly currency: string;
}

/** Constructs a {@link HotelStay}. */
export function hotelStay(
  stayId: string,
  hotel: string,
  city: string,
  checkIn: Date,
  checkOut: Date,
  nightlyRate: number,
  currency: string,
): HotelStay {
  return { stayId, hotel, city, checkIn, checkOut, nightlyRate, currency };
}

/** A planned trip. Mirrors C# `TravelTrip` record. */
export interface TravelTrip {
  readonly tripId: string;
  readonly name: string;
  /** Start date (C# `DateTime StartDate`). */
  readonly startDate: Date;
  /** End date (C# `DateTime EndDate`). */
  readonly endDate: Date;
  readonly flightIds: readonly string[];
  readonly stayIds: readonly string[];
}

/** Constructs a {@link TravelTrip}. */
export function travelTrip(
  tripId: string,
  name: string,
  startDate: Date,
  endDate: Date,
  flightIds: readonly string[],
  stayIds: readonly string[],
): TravelTrip {
  return { tripId, name, startDate, endDate, flightIds, stayIds };
}

/** Discriminates a {@link Flight} from a {@link HotelStay} for the `add` overload. */
function isFlight(x: Flight | HotelStay): x is Flight {
  return (x as Flight).flightId !== undefined;
}

/** The travel board contract. Mirrors C# `ITravelBoard`. */
export interface ITravelBoard {
  add(f: Flight): void;
  add(s: HotelStay): void;
  plan(t: TravelTrip): void;
  getTrip(id: string): TravelTrip | undefined;
  getFlight(id: string): Flight | undefined;
  getStay(id: string): HotelStay | undefined;
  tripCost(tripId: string): number;
  upcomingTrips(now: Date): readonly TravelTrip[];
}

/** Whole days between two dates, truncated toward zero (C# `TimeSpan.Days`). */
function wholeDays(from: Date, to: Date): number {
  return Math.trunc((to.getTime() - from.getTime()) / 86_400_000);
}

/** Deterministic in-memory {@link ITravelBoard}. */
export class InMemoryTravelBoard implements ITravelBoard {
  private readonly flights = new Map<string, Flight>();
  private readonly stays = new Map<string, HotelStay>();
  private readonly trips = new Map<string, TravelTrip>();

  add(f: Flight): void;
  add(s: HotelStay): void;
  add(x: Flight | HotelStay): void {
    if (x == null) throw new Error("value required");
    if (isFlight(x)) this.flights.set(x.flightId, x);
    else this.stays.set(x.stayId, x);
  }

  plan(t: TravelTrip): void {
    if (t == null) throw new Error("t required");
    this.trips.set(t.tripId, t);
  }

  getTrip(id: string): TravelTrip | undefined {
    return this.trips.get(id);
  }

  getFlight(id: string): Flight | undefined {
    return this.flights.get(id);
  }

  getStay(id: string): HotelStay | undefined {
    return this.stays.get(id);
  }

  tripCost(tripId: string): number {
    const t = this.trips.get(tripId);
    if (t === undefined) throw new Error(`Unknown trip ${tripId}`);
    let total = 0;
    for (const fid of t.flightIds) {
      const f = this.flights.get(fid);
      if (f !== undefined) total += f.price;
    }
    for (const sid of t.stayIds) {
      const s = this.stays.get(sid);
      if (s !== undefined) {
        const nights = Math.max(1, wholeDays(s.checkIn, s.checkOut));
        total += s.nightlyRate * nights;
      }
    }
    return total;
  }

  upcomingTrips(now: Date): readonly TravelTrip[] {
    const nowMs = now.getTime();
    return [...this.trips.values()]
      .filter((t) => t.startDate.getTime() >= nowMs)
      .sort((a, b) => a.startDate.getTime() - b.startDate.getTime());
  }
}

/**
 * Static domain context for the Travel vertical. Mirrors C#
 * `TravelDomainContext`.
 */
export const TravelDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Travel] Expert travel planning companion. Help with trip itinerary building, visa and entry requirements, budget travel strategies, packing lists, travel insurance guidance, and safety advisories. Personalise to the traveller profile. Compliance: POPIA, Consumer Protection Act (travel packages).",
  complianceFlags: ["POPIA", "Consumer_Protection_Act", "IATA_aware"] as readonly string[],
  suggestedTools: ["flight_search", "mapping", "currency_converter", "web_search"] as readonly string[],
} as const;
