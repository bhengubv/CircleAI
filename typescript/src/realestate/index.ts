// realestate/index.ts
// Full-parity port of CircleAI.RealEstate (C#). C# is the exact spec.
//
// Domain types + in-memory store for the RealEstate vertical: properties,
// listings, valuations, viewings, and a suburb-average comparable over the
// active listings. Plus the static RealEstateDomainContext.
//
// NOTE: The C# RealEstateCompanionAdapter (an ICompanionSession LLM-prompt
// wrapper) is intentionally NOT ported — consistent with the sibling
// domain-board ports (healthcare/education/legal/commerce).
//
// Type mappings (C# → TS):
//   enum PropertyKind                → string-literal union + frozen value object
//   record                          → readonly interface (+ positional factory)
//   decimal AskingPrice / value      → number
//   DateTimeOffset ...Utc            → Date
//   decimal? (SuburbAverage)         → number | null
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   ActiveInSuburb  — active listings whose property is in `suburb`
//                     (case-insensitive), ordered by ListedUtc descending.
//   SuburbAverage   — mean AskingPrice of the active-in-suburb listings, or null
//                     when there are none.

/** The kind of a property. Mirrors C# `enum PropertyKind`. */
export type PropertyKind = "Apartment" | "House" | "Townhouse" | "Commercial" | "Land";
/** Frozen value object for {@link PropertyKind} members. */
export const PropertyKind = Object.freeze({
  Apartment: "Apartment",
  House: "House",
  Townhouse: "Townhouse",
  Commercial: "Commercial",
  Land: "Land",
} as const) satisfies Record<string, PropertyKind>;

/** A property. Mirrors C# `Property` record. */
export interface Property {
  readonly propertyId: string;
  readonly suburb: string;
  readonly kind: PropertyKind;
  readonly beds: number;
  readonly baths: number;
  readonly floorAreaM2: number;
}

/** Constructs a {@link Property}. */
export function property(
  propertyId: string,
  suburb: string,
  kind: PropertyKind,
  beds: number,
  baths: number,
  floorAreaM2: number,
): Property {
  return { propertyId, suburb, kind, beds, baths, floorAreaM2 };
}

/** A listing of a property for sale. Mirrors C# `Listing` record. */
export interface Listing {
  readonly listingId: string;
  readonly propertyId: string;
  readonly askingPrice: number;
  readonly currency: string;
  /** UTC instant the listing went live (C# `DateTimeOffset ListedUtc`). */
  readonly listedUtc: Date;
  readonly isActive: boolean;
}

/** Constructs a {@link Listing}. */
export function listing(
  listingId: string,
  propertyId: string,
  askingPrice: number,
  currency: string,
  listedUtc: Date,
  isActive: boolean,
): Listing {
  return { listingId, propertyId, askingPrice, currency, listedUtc, isActive };
}

/** A property valuation. Mirrors C# `Valuation` record. */
export interface Valuation {
  readonly propertyId: string;
  readonly estimatedValue: number;
  readonly source: string;
  /** UTC instant of the valuation (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link Valuation}. */
export function valuation(propertyId: string, estimatedValue: number, source: string, atUtc: Date): Valuation {
  return { propertyId, estimatedValue, source, atUtc };
}

/** A scheduled property viewing. Mirrors C# `Viewing` record. */
export interface Viewing {
  readonly viewingId: string;
  readonly listingId: string;
  readonly attendeeName: string;
  /** UTC instant of the viewing (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link Viewing}. */
export function viewing(viewingId: string, listingId: string, attendeeName: string, atUtc: Date): Viewing {
  return { viewingId, listingId, attendeeName, atUtc };
}

/** The real-estate board contract. Mirrors C# `IRealEstateBoard`. */
export interface IRealEstateBoard {
  registerProperty(p: Property): void;
  list(l: Listing): void;
  close(listingId: string): void;
  value(v: Valuation): void;
  scheduleViewing(v: Viewing): void;
  activeInSuburb(suburb: string): readonly Listing[];
  suburbAverage(suburb: string): number | null;
}

/** Deterministic in-memory {@link IRealEstateBoard}. */
export class InMemoryRealEstateBoard implements IRealEstateBoard {
  private readonly props = new Map<string, Property>();
  private readonly listings = new Map<string, Listing>();
  private readonly vals: Valuation[] = [];
  private readonly viewings: Viewing[] = [];

  registerProperty(p: Property): void {
    if (p == null) throw new Error("p required");
    this.props.set(p.propertyId, p);
  }

  list(l: Listing): void {
    if (l == null) throw new Error("l required");
    this.listings.set(l.listingId, l);
  }

  close(listingId: string): void {
    const l = this.listings.get(listingId);
    if (l === undefined) throw new Error(`Unknown listing ${listingId}`);
    this.listings.set(listingId, { ...l, isActive: false });
  }

  value(v: Valuation): void {
    if (v == null) throw new Error("v required");
    this.vals.push(v);
  }

  scheduleViewing(v: Viewing): void {
    if (v == null) throw new Error("v required");
    this.viewings.push(v);
  }

  activeInSuburb(suburb: string): readonly Listing[] {
    if (suburb == null || suburb.trim() === "") throw new Error("suburb required");
    const target = suburb.toUpperCase();
    return [...this.listings.values()]
      .filter((l) => {
        if (!l.isActive) return false;
        const p = this.props.get(l.propertyId);
        return p !== undefined && p.suburb.toUpperCase() === target;
      })
      .sort((a, b) => b.listedUtc.getTime() - a.listedUtc.getTime());
  }

  suburbAverage(suburb: string): number | null {
    const rows = this.activeInSuburb(suburb);
    if (rows.length === 0) return null;
    return rows.reduce((sum, l) => sum + l.askingPrice, 0) / rows.length;
  }
}

/**
 * Static domain context for the RealEstate vertical. Mirrors C#
 * `RealEstateDomainContext`.
 */
export const RealEstateDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: RealEstate] Expert real estate assistant. Help with property market analysis, valuation frameworks, lease and sale agreement review, conveyancing timelines, sectional title rules, and rental management. Ground advice in current market data. Compliance: Alienation of Land Act, Rental Housing Act, PPRA, FICA, POPIA.",
  complianceFlags: ["Alienation_of_Land_Act", "Rental_Housing_Act", "PPRA", "FICA", "POPIA"] as readonly string[],
  suggestedTools: ["property_listings", "document_editor", "map", "analytics"] as readonly string[],
} as const;
