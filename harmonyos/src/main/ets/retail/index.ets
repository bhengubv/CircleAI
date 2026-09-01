/**
 * retail/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// retail/index.ts
// Full-parity port of CircleAI.Retail (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Retail vertical: products, stock
// levels, sales, a same-day revenue rollup, and a top-sellers-since ranking.
// Plus the static RetailDomainContext.
//
// NOTE: The C# RetailCompanionAdapter (an ICompanionSession LLM-prompt wrapper)
// is intentionally NOT ported — consistent with the sibling domain-board ports
// (healthcare/education/legal/commerce), which port only the board +
// DomainContext.
//
// Type mappings (C# → TS):
//   record                          → readonly interface (+ positional factory)
//   decimal Price / UnitPrice        → number
//   int Quantity                     → number
//   DateTimeOffset AtUtc            → Date
//   (string Sku, int Sold) tuple     → { readonly sku: string; readonly sold: number }
//                                      (named-tuple element names preserved)
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   RecordSale       — throws on unknown SKU; decrements stock by quantity.
//   RevenueToday     — sum of UnitPrice*Quantity for sales whose UTC calendar
//                      date equals `now`'s UTC calendar date (C# AtUtc.Date ==
//                      now.Date). NOTE: C# missing keys read Stock() as 0, so a
//                      sale can drive stock negative — preserved here.
//   TopSellersSince  — sales with AtUtc >= since, grouped by SKU, summed, ordered
//                      by units sold descending, take topK.

/** A retail product. Mirrors C# `Product` record. */
export interface Product {
  readonly sku: string;
  readonly name: string;
  readonly price: number;
  readonly currency: string;
  readonly category: string | null;
}

/** Constructs a {@link Product}. */
export function product(sku: string, name: string, price: number, currency: string, category: string | null): Product {
  return { sku, name, price, currency, category };
}

/** A per-SKU stock level. Mirrors C# `StockLevel` record. */
export interface StockLevel {
  readonly sku: string;
  readonly quantity: number;
}

/** Constructs a {@link StockLevel}. */
export function stockLevel(sku: string, quantity: number): StockLevel {
  return { sku, quantity };
}

/** A recorded sale. Mirrors C# `Sale` record. */
export interface Sale {
  readonly saleId: string;
  readonly sku: string;
  readonly quantity: number;
  readonly unitPrice: number;
  /** UTC instant of the sale (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link Sale}. */
export function sale(saleId: string, sku: string, quantity: number, unitPrice: number, atUtc: Date): Sale {
  return { saleId, sku, quantity, unitPrice, atUtc };
}

/** A top-seller entry. Mirrors C# named tuple `(string Sku, int Sold)`. */
export interface TopSeller {
  readonly sku: string;
  readonly sold: number;
}

/** The retail board contract. Mirrors C# `IRetailBoard`. */
export interface IRetailBoard {
  addProduct(p: Product): void;
  getProduct(sku: string): Product | undefined;
  setStock(l: StockLevel): void;
  stock(sku: string): number;
  recordSale(s: Sale): void;
  revenueToday(now: Date): number;
  topSellersSince(since: Date, topK?: number): readonly TopSeller[];
}

/** True when two instants fall on the same UTC calendar date (C# `.Date` eq). */
function sameUtcDate(a: Date, b: Date): boolean {
  return (
    a.getUTCFullYear() === b.getUTCFullYear() &&
    a.getUTCMonth() === b.getUTCMonth() &&
    a.getUTCDate() === b.getUTCDate()
  );
}

/** Deterministic in-memory {@link IRetailBoard}. */
export class InMemoryRetailBoard implements IRetailBoard {
  private readonly products = new Map<string, Product>();
  private readonly stockBySku = new Map<string, number>();
  private readonly sales: Sale[] = [];

  addProduct(p: Product): void {
    if (p == null) throw new Error("p required");
    this.products.set(p.sku, p);
  }

  getProduct(sku: string): Product | undefined {
    return this.products.get(sku);
  }

  setStock(l: StockLevel): void {
    if (l == null) throw new Error("l required");
    this.stockBySku.set(l.sku, l.quantity);
  }

  stock(sku: string): number {
    const q = this.stockBySku.get(sku);
    return q !== undefined ? q : 0;
  }

  recordSale(s: Sale): void {
    if (s == null) throw new Error("s required");
    if (!this.products.has(s.sku)) throw new Error(`Unknown SKU ${s.sku}`);
    this.sales.push(s);
    this.stockBySku.set(s.sku, this.stock(s.sku) - s.quantity);
  }

  revenueToday(now: Date): number {
    return this.sales.filter((s) => sameUtcDate(s.atUtc, now)).reduce((sum, s) => sum + s.unitPrice * s.quantity, 0);
  }

  topSellersSince(since: Date, topK = 5): readonly TopSeller[] {
    if (topK <= 0) throw new Error("topK");
    const bySku = new Map<string, number>();
    for (const s of this.sales) {
      if (s.atUtc.getTime() >= since.getTime()) {
        bySku.set(s.sku, (bySku.get(s.sku) ?? 0) + s.quantity);
      }
    }
    return [...bySku.entries()]
      .map(([sku, sold]) => ({ sku, sold }))
      .sort((a, b) => b.sold - a.sold)
      .slice(0, topK);
  }
}

/**
 * Static domain context for the Retail vertical. Mirrors C#
 * `RetailDomainContext`.
 */
export const RetailDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Retail] Expert retail operations assistant. Help with stock replenishment, planogram optimisation, shrinkage reduction, seasonal promotions, customer loyalty, and sales floor management. Ground advice in margin and sell-through rates. Compliance: Consumer Protection Act, POPIA.",
  complianceFlags: ["Consumer_Protection_Act", "POPIA", "Labour_Relations_Act"] as readonly string[],
  suggestedTools: ["pos_system", "inventory", "analytics", "promotions_engine"] as readonly string[],
} as const;
