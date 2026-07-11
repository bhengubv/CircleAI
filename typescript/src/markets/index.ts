// markets/index.ts
// Full-parity port of CircleAI.Markets (C#). C# is the exact spec.
//
// Markets contracts (2.8.0) + real in-memory primitives (3.3.0): an instrument
// catalog with case-insensitive symbol lookup + substring search, a market-data
// feed with publish/subscribe quote pushes, and an order router that accepts or
// rejects on simple rules (positive quantity, known instrument, valid limit
// price for limit orders). Plus the fail-closed Null* defaults (2.8.0).
//
// Type mappings (C# → TS):
//   enum                   → string-literal union + frozen value object
//   record                 → readonly interface (+ positional factory)
//   decimal                → number
//   DateTimeOffset         → Date
//   ValueTask<T>           → Promise<T> (impls complete synchronously via
//                            ValueTask.FromResult; already-resolved promises)
//   IDisposable            → { dispose(): void } (the SubscribeQuotes handle)
//   Func<Quote, ValueTask> → (q: Quote) => Promise<void>  (quote handler)
//   CancellationToken ct   → signal?: AbortSignal (present for contract parity)
//   ConcurrentDictionary (OrdinalIgnoreCase) → Map keyed by lower-cased symbol,
//                            storing the last-written value (matching the .NET
//                            OrdinalIgnoreCase dictionary indexer semantics)
//
// CONCURRENCY: the C# feed snapshots the subscriber list under a lock before
// invoking handlers, so a handler that unsubscribes (or another publish) cannot
// mutate the list mid-iteration. JS is single-threaded; we still snapshot the
// list (spread copy) before iterating so an unsubscribe/subscribe during a
// handler dispatch is safe. Handler exceptions are swallowed (fire-and-forget),
// matching the C# `try { _ = s(q); } catch { … }`.

/** Buy/sell side of an order. Mirrors C# `enum OrderSide`. */
export type OrderSide = "Buy" | "Sell";
/** Frozen value object for {@link OrderSide} members. */
export const OrderSide = Object.freeze({
  Buy: "Buy",
  Sell: "Sell",
} as const) satisfies Record<string, OrderSide>;

/** Market/limit order type. Mirrors C# `enum OrderType`. */
export type OrderType = "Market" | "Limit";
/** Frozen value object for {@link OrderType} members. */
export const OrderType = Object.freeze({
  Market: "Market",
  Limit: "Limit",
} as const) satisfies Record<string, OrderType>;

/** A tradable instrument. Mirrors C# `Instrument` record. */
export interface Instrument {
  readonly symbol: string;
  readonly exchange: string;
  readonly currency: string;
  readonly assetClass: string;
}

/** Constructs an {@link Instrument}. */
export function instrument(symbol: string, exchange: string, currency: string, assetClass: string): Instrument {
  return { symbol, exchange, currency, assetClass };
}

/** A market quote. Mirrors C# `Quote` record. */
export interface Quote {
  readonly symbol: string;
  readonly bid: number;
  readonly ask: number;
  readonly last: number;
  /** UTC instant of the quote (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link Quote}. */
export function quote(symbol: string, bid: number, ask: number, last: number, atUtc: Date): Quote {
  return { symbol, bid, ask, last, atUtc };
}

/** An order submission request. Mirrors C# `OrderRequest` record. */
export interface OrderRequest {
  readonly symbol: string;
  readonly side: OrderSide;
  readonly type: OrderType;
  readonly quantity: number;
  readonly limitPrice: number | null;
}

/** Constructs an {@link OrderRequest}. */
export function orderRequest(
  symbol: string,
  side: OrderSide,
  type: OrderType,
  quantity: number,
  limitPrice: number | null,
): OrderRequest {
  return { symbol, side, type, quantity, limitPrice };
}

/** The result of submitting an order. Mirrors C# `OrderResult` record. */
export interface OrderResult {
  readonly orderId: string;
  readonly accepted: boolean;
  readonly failureReason: string | null;
}

/** Constructs an {@link OrderResult}. */
export function orderResult(orderId: string, accepted: boolean, failureReason: string | null): OrderResult {
  return { orderId, accepted, failureReason };
}

/** A handler invoked with each published {@link Quote}. */
export type QuoteHandler = (q: Quote) => Promise<void>;

/** A disposable subscription handle. Mirrors C# `IDisposable`. */
export interface IDisposable {
  dispose(): void;
}

/** Streams and reads quotes. Mirrors C# `IMarketDataFeed`. */
export interface IMarketDataFeed {
  readonly backendId: string;
  getQuoteAsync(symbol: string, signal?: AbortSignal): Promise<Quote | null>;
  subscribeQuotes(symbol: string, handler: QuoteHandler): IDisposable;
}

/** Looks up and searches instruments. Mirrors C# `IInstrumentCatalog`. */
export interface IInstrumentCatalog {
  readonly backendId: string;
  getAsync(symbol: string, signal?: AbortSignal): Promise<Instrument | null>;
  searchAsync(query: string, topK?: number, signal?: AbortSignal): Promise<readonly Instrument[]>;
}

/** Routes order submissions. Mirrors C# `IOrderRouter`. */
export interface IOrderRouter {
  readonly backendId: string;
  submitAsync(req: OrderRequest, signal?: AbortSignal): Promise<OrderResult>;
}

/** The all-zero GUID rendered as C# `Guid.Empty.ToString()`. */
const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";

/** Ordinal (code-unit) string comparison, matching C# StringComparer.Ordinal. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** Case-insensitive substring test, matching C# `Contains(q, OrdinalIgnoreCase)`. */
function containsIgnoreCase(haystack: string, needle: string): boolean {
  return haystack.toUpperCase().includes(needle.toUpperCase());
}

/**
 * Real in-memory {@link IInstrumentCatalog}. Keyed case-insensitively by symbol
 * (matching the C# ConcurrentDictionary(OrdinalIgnoreCase)); the stored value is
 * the last write for that case-folded symbol. Search does a case-insensitive
 * substring match ordered by symbol ascending (ordinal on the original casing,
 * matching C# `OrderBy(i => i.Symbol)` with the default comparer).
 */
export class InMemoryInstrumentCatalog implements IInstrumentCatalog {
  /** symbol.toLowerCase() → instrument (OrdinalIgnoreCase indexer parity). */
  private readonly items = new Map<string, Instrument>();
  get backendId(): string {
    return "in-memory";
  }

  add(item: Instrument): void {
    if (item == null) throw new Error("item required");
    this.items.set(item.symbol.toLowerCase(), item);
  }

  getAsync(symbol: string, _signal?: AbortSignal): Promise<Instrument | null> {
    if (symbol == null || symbol.trim() === "") throw new Error("symbol required");
    return Promise.resolve(this.items.get(symbol.toLowerCase()) ?? null);
  }

  searchAsync(query: string, topK = 20, _signal?: AbortSignal): Promise<readonly Instrument[]> {
    if (query == null) throw new Error("query required");
    if (topK <= 0) throw new Error("topK");
    const hits = [...this.items.values()]
      .filter((i) => containsIgnoreCase(i.symbol, query))
      .sort((a, b) => ordinalCompare(a.symbol, b.symbol))
      .slice(0, topK);
    return Promise.resolve(hits);
  }
}

/**
 * Real in-memory {@link IMarketDataFeed}. Publish stores the latest quote and
 * pushes it to all subscribers of that symbol; the subscriber list is snapshot
 * before dispatch so unsubscribing (or re-entrant publishing) mid-dispatch is
 * safe. Subscriber exceptions are swallowed (fire-and-forget parity).
 */
export class InMemoryMarketDataFeed implements IMarketDataFeed {
  /** symbol.toLowerCase() → latest quote. */
  private readonly quotes = new Map<string, Quote>();
  /** symbol.toLowerCase() → subscriber handlers. */
  private readonly subs = new Map<string, QuoteHandler[]>();
  get backendId(): string {
    return "in-memory";
  }

  publish(q: Quote): void {
    if (q == null) throw new Error("q required");
    const key = q.symbol.toLowerCase();
    this.quotes.set(key, q);
    const list = this.subs.get(key);
    if (list !== undefined) {
      const snap = [...list];
      for (const s of snap) {
        try {
          void s(q);
        } catch {
          // [CircleAI.Markets] quote subscriber threw — swallow, matching C#.
        }
      }
    }
  }

  getQuoteAsync(symbol: string, _signal?: AbortSignal): Promise<Quote | null> {
    if (symbol == null || symbol.trim() === "") throw new Error("symbol required");
    return Promise.resolve(this.quotes.get(symbol.toLowerCase()) ?? null);
  }

  subscribeQuotes(symbol: string, handler: QuoteHandler): IDisposable {
    if (symbol == null || symbol.trim() === "") throw new Error("symbol required");
    if (handler == null) throw new Error("handler required");
    const key = symbol.toLowerCase();
    let list = this.subs.get(key);
    if (list === undefined) {
      list = [];
      this.subs.set(key, list);
    }
    list.push(handler);
    return new Subscription(this, key, handler);
  }

  /** @internal Removes a handler from its symbol list on dispose. */
  _remove(key: string, handler: QuoteHandler): void {
    const list = this.subs.get(key);
    if (list !== undefined) {
      const idx = list.indexOf(handler);
      if (idx >= 0) list.splice(idx, 1);
    }
  }
}

/** Subscription handle for {@link InMemoryMarketDataFeed.subscribeQuotes}. */
class Subscription implements IDisposable {
  constructor(
    private readonly owner: InMemoryMarketDataFeed,
    private readonly key: string,
    private readonly handler: QuoteHandler,
  ) {}
  dispose(): void {
    this.owner._remove(this.key, this.handler);
  }
}

/**
 * Real in-memory {@link IOrderRouter}. Rejects non-positive quantities, limit
 * orders without a positive limit price, and unknown symbols (checked against
 * the injected catalog); otherwise accepts with a fresh sequential order id.
 */
export class InMemoryOrderRouter implements IOrderRouter {
  private readonly catalog: IInstrumentCatalog;
  private seq = 0;

  constructor(catalog: IInstrumentCatalog) {
    if (catalog == null) throw new Error("catalog required");
    this.catalog = catalog;
  }

  get backendId(): string {
    return "in-memory";
  }

  async submitAsync(req: OrderRequest, signal?: AbortSignal): Promise<OrderResult> {
    if (req == null) throw new Error("req required");
    if (req.quantity <= 0) return { orderId: this.nextId(), accepted: false, failureReason: "Quantity must be positive" };
    if (req.type === OrderType.Limit && (req.limitPrice == null || req.limitPrice <= 0)) {
      return { orderId: this.nextId(), accepted: false, failureReason: "Limit order requires positive LimitPrice" };
    }

    const inst = await this.catalog.getAsync(req.symbol, signal);
    if (inst == null) return { orderId: this.nextId(), accepted: false, failureReason: "Unknown symbol" };

    return { orderId: this.nextId(), accepted: true, failureReason: null };
  }

  private nextId(): string {
    return `ord-${++this.seq}`;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Fail-closed Null* defaults (2.8.0)
// ─────────────────────────────────────────────────────────────────────────────

/** Fail-closed {@link IMarketDataFeed}: no quotes, no-op subscriptions. */
export class NullMarketDataFeed implements IMarketDataFeed {
  static readonly instance = new NullMarketDataFeed();
  get backendId(): string {
    return "null";
  }
  getQuoteAsync(_symbol: string, _signal?: AbortSignal): Promise<Quote | null> {
    return Promise.resolve(null);
  }
  subscribeQuotes(_symbol: string, _handler: QuoteHandler): IDisposable {
    return EMPTY_DISPOSABLE;
  }
}

/** A no-op {@link IDisposable}, matching C# `EmptyDisposable.Instance`. */
const EMPTY_DISPOSABLE: IDisposable = Object.freeze({ dispose() {} });

/** Fail-closed {@link IInstrumentCatalog}: no instruments, ever. */
export class NullInstrumentCatalog implements IInstrumentCatalog {
  static readonly instance = new NullInstrumentCatalog();
  get backendId(): string {
    return "null";
  }
  getAsync(_symbol: string, _signal?: AbortSignal): Promise<Instrument | null> {
    return Promise.resolve(null);
  }
  searchAsync(_query: string, _topK = 20, _signal?: AbortSignal): Promise<readonly Instrument[]> {
    return Promise.resolve([]);
  }
}

/** Fail-closed {@link IOrderRouter}: rejects every order. */
export class NullOrderRouter implements IOrderRouter {
  static readonly instance = new NullOrderRouter();
  get backendId(): string {
    return "null";
  }
  submitAsync(_req: OrderRequest, _signal?: AbortSignal): Promise<OrderResult> {
    return Promise.resolve({ orderId: EMPTY_GUID, accepted: false, failureReason: "NullOrderRouter — fail-closed." });
  }
}
