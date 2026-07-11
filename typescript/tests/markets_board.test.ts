// markets_board.test.ts
// Verifies the CircleAI.Markets port: instrument catalog (case-insensitive
// lookup + substring search), pub/sub market-data feed with unsubscribe, the
// rule-based order router, the OrderSide/OrderType enums, and Null* defaults.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryInstrumentCatalog,
  InMemoryMarketDataFeed,
  InMemoryOrderRouter,
  NullMarketDataFeed,
  NullInstrumentCatalog,
  NullOrderRouter,
  OrderSide,
  OrderType,
  instrument,
  quote,
  orderRequest,
} from "../src/markets/index";

describe("OrderSide / OrderType enums", () => {
  it("expose the C# member values", () => {
    assert.equal(OrderSide.Buy, "Buy");
    assert.equal(OrderSide.Sell, "Sell");
    assert.equal(OrderType.Market, "Market");
    assert.equal(OrderType.Limit, "Limit");
  });
});

describe("InMemoryInstrumentCatalog", () => {
  it("looks up case-insensitively and searches by symbol substring ordered ascending", async () => {
    const cat = new InMemoryInstrumentCatalog();
    assert.equal(cat.backendId, "in-memory");
    cat.add(instrument("NPN", "JSE", "ZAR", "Equity"));
    cat.add(instrument("AGL", "JSE", "ZAR", "Equity"));
    assert.equal((await cat.getAsync("npn"))?.exchange, "JSE"); // case-insensitive key
    assert.equal(await cat.getAsync("ZZZ"), null);
    const hits = await cat.searchAsync("l"); // AGL matches
    assert.deepEqual(
      hits.map((i) => i.symbol),
      ["AGL"],
    );
    await assert.rejects(async () => cat.searchAsync("x", 0));
    await assert.rejects(async () => cat.getAsync(" "));
  });
});

describe("InMemoryMarketDataFeed", () => {
  it("publishes latest quote and pushes to subscribers; unsubscribe stops delivery", async () => {
    const feed = new InMemoryMarketDataFeed();
    const seen: number[] = [];
    const sub = feed.subscribeQuotes("NPN", async (q) => {
      seen.push(q.last);
    });
    feed.publish(quote("NPN", 1, 2, 1.5, new Date("2026-01-01T00:00:00Z")));
    feed.publish(quote("npn", 1, 2, 2.5, new Date("2026-01-02T00:00:00Z"))); // case-insensitive symbol
    assert.deepEqual(seen, [1.5, 2.5]);
    assert.equal((await feed.getQuoteAsync("NPN"))?.last, 2.5);

    sub.dispose();
    feed.publish(quote("NPN", 1, 2, 9, new Date("2026-01-03T00:00:00Z")));
    assert.deepEqual(seen, [1.5, 2.5]); // no further delivery
  });

  it("swallows subscriber exceptions and keeps delivering to others", () => {
    const feed = new InMemoryMarketDataFeed();
    const seen: string[] = [];
    feed.subscribeQuotes("X", async () => {
      throw new Error("boom");
    });
    feed.subscribeQuotes("X", async () => {
      seen.push("ok");
    });
    assert.doesNotThrow(() => feed.publish(quote("X", 1, 1, 1, new Date())));
    assert.deepEqual(seen, ["ok"]);
  });
});

describe("InMemoryOrderRouter", () => {
  it("accepts valid orders and rejects on rules; ids are sequential", async () => {
    const cat = new InMemoryInstrumentCatalog();
    cat.add(instrument("NPN", "JSE", "ZAR", "Equity"));
    const router = new InMemoryOrderRouter(cat);

    const ok = await router.submitAsync(orderRequest("NPN", OrderSide.Buy, OrderType.Market, 10, null));
    assert.equal(ok.accepted, true);
    assert.equal(ok.orderId, "ord-1");

    const badQty = await router.submitAsync(orderRequest("NPN", OrderSide.Buy, OrderType.Market, 0, null));
    assert.equal(badQty.accepted, false);
    assert.match(badQty.failureReason ?? "", /Quantity must be positive/);

    const badLimit = await router.submitAsync(orderRequest("NPN", OrderSide.Sell, OrderType.Limit, 5, null));
    assert.match(badLimit.failureReason ?? "", /positive LimitPrice/);

    const unknown = await router.submitAsync(orderRequest("ZZZ", OrderSide.Buy, OrderType.Market, 1, null));
    assert.match(unknown.failureReason ?? "", /Unknown symbol/);

    const ok2 = await router.submitAsync(orderRequest("NPN", OrderSide.Buy, OrderType.Limit, 1, 5));
    assert.equal(ok2.accepted, true);
    assert.equal(ok2.orderId, "ord-5"); // seq increments on every attempt
  });

  it("throws when constructed without a catalog", () => {
    assert.throws(() => new InMemoryOrderRouter(null as never));
  });
});

describe("Markets Null* defaults", () => {
  it("fail closed", async () => {
    assert.equal(await NullMarketDataFeed.instance.getQuoteAsync("X"), null);
    assert.doesNotThrow(() => NullMarketDataFeed.instance.subscribeQuotes("X", async () => {}).dispose());
    assert.equal(await NullInstrumentCatalog.instance.getAsync("X"), null);
    assert.deepEqual(await NullInstrumentCatalog.instance.searchAsync("X"), []);
    const r = await NullOrderRouter.instance.submitAsync(orderRequest("X", OrderSide.Buy, OrderType.Market, 1, null));
    assert.equal(r.accepted, false);
    assert.equal(r.orderId, "00000000-0000-0000-0000-000000000000");
  });
});
