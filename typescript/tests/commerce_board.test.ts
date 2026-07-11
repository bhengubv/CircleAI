// commerce_board.test.ts
// Verifies the CircleAI.Commerce port: customers, orders (newest-first), line
// items (insertion order), status update, and lifetime value.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryCommerceBoard,
  CommerceDomainContext,
  commerceCustomer,
  commerceOrder,
  commerceLineItem,
} from "../src/commerce/index";

describe("InMemoryCommerceBoard", () => {
  it("adds and retrieves customers (email may be null)", () => {
    const b = new InMemoryCommerceBoard();
    b.addCustomer(commerceCustomer("c1", "Ada", null, new Date("2026-01-01T00:00:00Z")));
    assert.equal(b.getCustomer("c1")?.email, null);
    assert.equal(b.getCustomer("nope"), undefined);
  });

  it("lists orders newest-first and computes lifetime value", () => {
    const b = new InMemoryCommerceBoard();
    b.place(commerceOrder("o1", "c1", 100, "ZAR", "Paid", new Date("2026-01-01T00:00:00Z")));
    b.place(commerceOrder("o2", "c1", 250, "ZAR", "Paid", new Date("2026-06-01T00:00:00Z")));
    b.place(commerceOrder("o3", "c2", 999, "ZAR", "Paid", new Date("2026-05-01T00:00:00Z")));
    assert.deepEqual(
      b.ordersFor("c1").map((o) => o.orderId),
      ["o2", "o1"],
    );
    assert.equal(b.lifetimeValue("c1"), 350);
    assert.equal(b.lifetimeValue("unknown"), 0);
  });

  it("line items preserve insertion order and filter by order", () => {
    const b = new InMemoryCommerceBoard();
    b.addLine(commerceLineItem("L1", "o1", "SKU-A", 2, 50));
    b.addLine(commerceLineItem("L2", "o2", "SKU-B", 1, 10));
    b.addLine(commerceLineItem("L3", "o1", "SKU-C", 3, 5));
    assert.deepEqual(
      b.linesFor("o1").map((l) => l.lineId),
      ["L1", "L3"],
    );
  });

  it("updateStatus mutates the order; unknown throws", () => {
    const b = new InMemoryCommerceBoard();
    b.place(commerceOrder("o1", "c1", 100, "ZAR", "Pending", new Date("2026-01-01T00:00:00Z")));
    b.updateStatus("o1", "Shipped");
    assert.equal(b.ordersFor("c1")[0].status, "Shipped");
    assert.throws(() => b.updateStatus("ghost", "X"), /Unknown order ghost/);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(CommerceDomainContext.systemPromptSnippet.includes("[DOMAIN: Commerce]"));
    assert.deepEqual(CommerceDomainContext.complianceFlags, ["POPIA", "Consumer_Protection_Act", "GDPR_aware"]);
    assert.deepEqual(CommerceDomainContext.suggestedTools, ["inventory", "pricing_engine", "order_management", "analytics"]);
  });
});
