// retail_board.test.ts
// Verifies the CircleAI.Retail port: products + stock, sale recording with
// stock decrement + unknown-SKU guard, same-day revenue, top-sellers ranking.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import { InMemoryRetailBoard, RetailDomainContext, product, stockLevel, sale } from "../src/retail/index";

const D = (s: string) => new Date(s);

describe("InMemoryRetailBoard", () => {
  it("adds products, sets and reads stock (missing = 0)", () => {
    const b = new InMemoryRetailBoard();
    b.addProduct(product("A", "Apple", 5, "ZAR", "Fruit"));
    b.setStock(stockLevel("A", 20));
    assert.equal(b.getProduct("A")?.name, "Apple");
    assert.equal(b.stock("A"), 20);
    assert.equal(b.stock("missing"), 0);
  });

  it("records sales, decrements stock, and rejects unknown SKUs", () => {
    const b = new InMemoryRetailBoard();
    b.addProduct(product("A", "Apple", 5, "ZAR", "Fruit"));
    b.setStock(stockLevel("A", 20));
    b.recordSale(sale("s1", "A", 3, 5, D("2026-05-01T10:00:00Z")));
    assert.equal(b.stock("A"), 17);
    assert.throws(() => b.recordSale(sale("s2", "ZZZ", 1, 1, D("2026-05-01T10:00:00Z"))), /Unknown SKU ZZZ/);
  });

  it("sums revenue for the given UTC day only", () => {
    const b = new InMemoryRetailBoard();
    b.addProduct(product("A", "Apple", 5, "ZAR", "Fruit"));
    b.recordSale(sale("s1", "A", 2, 5, D("2026-05-01T08:00:00Z"))); // today
    b.recordSale(sale("s2", "A", 1, 5, D("2026-05-01T23:00:00Z"))); // today
    b.recordSale(sale("s3", "A", 4, 5, D("2026-04-30T23:00:00Z"))); // other day
    assert.equal(b.revenueToday(D("2026-05-01T12:00:00Z")), 15); // (2+1)*5
  });

  it("ranks top sellers since a cutoff, take topK", () => {
    const b = new InMemoryRetailBoard();
    b.addProduct(product("A", "Apple", 5, "ZAR", null));
    b.addProduct(product("B", "Banana", 3, "ZAR", null));
    b.recordSale(sale("s1", "A", 2, 5, D("2026-05-01T10:00:00Z")));
    b.recordSale(sale("s2", "B", 9, 3, D("2026-05-02T10:00:00Z")));
    b.recordSale(sale("s3", "A", 4, 5, D("2026-05-03T10:00:00Z")));
    b.recordSale(sale("s4", "B", 1, 3, D("2026-04-01T10:00:00Z"))); // before cutoff
    const top = b.topSellersSince(D("2026-05-01T00:00:00Z"), 5);
    assert.deepEqual(top, [
      { sku: "B", sold: 9 },
      { sku: "A", sold: 6 },
    ]);
    assert.equal(b.topSellersSince(D("2026-05-01T00:00:00Z"), 1).length, 1);
    assert.throws(() => b.topSellersSince(D("2026-05-01T00:00:00Z"), 0));
  });

  it("rejects null arguments", () => {
    const b = new InMemoryRetailBoard();
    assert.throws(() => b.addProduct(null as never));
    assert.throws(() => b.setStock(null as never));
    assert.throws(() => b.recordSale(null as never));
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(RetailDomainContext.systemPromptSnippet.includes("[DOMAIN: Retail]"));
    assert.deepEqual(RetailDomainContext.complianceFlags, ["Consumer_Protection_Act", "POPIA", "Labour_Relations_Act"]);
    assert.deepEqual(RetailDomainContext.suggestedTools, ["pos_system", "inventory", "analytics", "promotions_engine"]);
  });
});
