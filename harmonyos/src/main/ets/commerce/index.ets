/**
 * commerce/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// commerce/index.ts
// Full-parity port of CircleAI.Commerce (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Commerce vertical: customers, orders,
// line items, and a lifetime-value rollup. Plus the static CommerceDomainContext.
//
// Type mappings (C# → TS):
//   record           → readonly interface (+ positional factory)
//   decimal          → number
//   string? Email    → string | null
//   DateTimeOffset   → Date
//   ConcurrentDictionary (Ordinal) → Map<string,T>
//   List<CommerceLineItem> under a lock → plain array (JS is single-threaded)
//
// ORDERING / ROLLUP PARITY:
//   OrdersFor      — sorted AtUtc descending
//   LinesFor       — insertion order preserved (no sort in C#)
//   LifetimeValue  — sum of Order.Total for the customer

/** A commerce customer. Mirrors C# `CommerceCustomer` record. */
export interface CommerceCustomer {
  readonly customerId: string;
  readonly name: string;
  readonly email: string | null;
  readonly createdUtc: Date;
}

/** Constructs a {@link CommerceCustomer}. */
export function commerceCustomer(
  customerId: string,
  name: string,
  email: string | null,
  createdUtc: Date,
): CommerceCustomer {
  return { customerId, name, email, createdUtc };
}

/** A commerce order. Mirrors C# `CommerceOrder` record. */
export interface CommerceOrder {
  readonly orderId: string;
  readonly customerId: string;
  readonly total: number;
  readonly currency: string;
  readonly status: string;
  readonly atUtc: Date;
}

/** Constructs a {@link CommerceOrder}. */
export function commerceOrder(
  orderId: string,
  customerId: string,
  total: number,
  currency: string,
  status: string,
  atUtc: Date,
): CommerceOrder {
  return { orderId, customerId, total, currency, status, atUtc };
}

/** A single line item on an order. Mirrors C# `CommerceLineItem` record. */
export interface CommerceLineItem {
  readonly lineId: string;
  readonly orderId: string;
  readonly sku: string;
  readonly quantity: number;
  readonly unitPrice: number;
}

/** Constructs a {@link CommerceLineItem}. */
export function commerceLineItem(
  lineId: string,
  orderId: string,
  sku: string,
  quantity: number,
  unitPrice: number,
): CommerceLineItem {
  return { lineId, orderId, sku, quantity, unitPrice };
}

/** The commerce board contract. */
export interface ICommerceBoard {
  addCustomer(c: CommerceCustomer): void;
  getCustomer(id: string): CommerceCustomer | undefined;
  place(o: CommerceOrder): void;
  addLine(l: CommerceLineItem): void;
  updateStatus(orderId: string, status: string): void;
  ordersFor(customerId: string): readonly CommerceOrder[];
  linesFor(orderId: string): readonly CommerceLineItem[];
  lifetimeValue(customerId: string): number;
}

/** Deterministic in-memory {@link ICommerceBoard}. */
export class InMemoryCommerceBoard implements ICommerceBoard {
  private readonly customers = new Map<string, CommerceCustomer>();
  private readonly orders = new Map<string, CommerceOrder>();
  private readonly lines: CommerceLineItem[] = [];

  addCustomer(c: CommerceCustomer): void {
    if (c == null) throw new Error("c required");
    this.customers.set(c.customerId, c);
  }

  getCustomer(id: string): CommerceCustomer | undefined {
    return this.customers.get(id);
  }

  place(o: CommerceOrder): void {
    if (o == null) throw new Error("o required");
    this.orders.set(o.orderId, o);
  }

  addLine(l: CommerceLineItem): void {
    if (l == null) throw new Error("l required");
    this.lines.push(l);
  }

  updateStatus(orderId: string, status: string): void {
    const o = this.orders.get(orderId);
    if (o === undefined) throw new Error(`Unknown order ${orderId}`);
    this.orders.set(orderId, { ...o, status });
  }

  ordersFor(customerId: string): readonly CommerceOrder[] {
    return [...this.orders.values()]
      .filter((o) => o.customerId === customerId)
      .sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime());
  }

  linesFor(orderId: string): readonly CommerceLineItem[] {
    return this.lines.filter((l) => l.orderId === orderId);
  }

  lifetimeValue(customerId: string): number {
    return this.ordersFor(customerId).reduce((sum, o) => sum + o.total, 0);
  }
}

/**
 * Static domain context for the Commerce vertical. Mirrors C#
 * `CommerceDomainContext`.
 */
export const CommerceDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Commerce] You are an e-commerce and trading expert. Help with product listings, " +
    "pricing strategy, order management, supplier negotiations, marketplace analytics, and sales " +
    "optimisation. Apply margin-aware thinking to every recommendation. Compliance: Consumer Protection Act, POPIA.",
  complianceFlags: ["POPIA", "Consumer_Protection_Act", "GDPR_aware"] as readonly string[],
  suggestedTools: ["inventory", "pricing_engine", "order_management", "analytics"] as readonly string[],
} as const;
