// commerce_xero_board.test.ts
// Verifies the CircleAI.Commerce.Integration.Xero port: token storage/expiry,
// tenant de-dup, and webhook recording (recent-first).

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryXeroBoard,
  CommerceIntegrationXeroDomainContext,
  xeroTokens,
  xeroTenant,
  xeroWebhookEvent,
} from "../src/commerce/integration/xero/index";

describe("InMemoryXeroBoard — tokens", () => {
  it("stores/retrieves tokens and reports expiry vs now", () => {
    const b = new InMemoryXeroBoard();
    const exp = new Date("2026-07-10T12:00:00Z");
    b.storeTokens("u1", xeroTokens("acc", "ref", exp, "id"));
    assert.equal(b.getTokens("u1")?.accessToken, "acc");
    assert.equal(b.tokensExpired("u1", new Date("2026-07-10T11:59:59Z")), false);
    assert.equal(b.tokensExpired("u1", new Date("2026-07-10T12:00:00Z")), true); // now >= expiry
    assert.equal(b.tokensExpired("u1", new Date("2026-07-10T12:00:01Z")), true);
  });

  it("unknown user is treated as expired and has no tokens", () => {
    const b = new InMemoryXeroBoard();
    assert.equal(b.getTokens("ghost"), undefined);
    assert.equal(b.tokensExpired("ghost", new Date()), true);
  });
});

describe("InMemoryXeroBoard — tenants", () => {
  it("adds tenants per user and de-duplicates by TenantId", () => {
    const b = new InMemoryXeroBoard();
    b.addTenant("u1", xeroTenant("t1", "Org1", "ORGANISATION"));
    b.addTenant("u1", xeroTenant("t1", "Org1-dup", "ORGANISATION")); // same id → ignored
    b.addTenant("u1", xeroTenant("t2", "Org2", "ORGANISATION"));
    assert.deepEqual(
      b.tenantsFor("u1").map((t) => t.tenantId),
      ["t1", "t2"],
    );
    assert.deepEqual(b.tenantsFor("other"), []);
  });
});

describe("InMemoryXeroBoard — webhooks", () => {
  it("records events and returns them most-recent-first (by AtUtc), limited", () => {
    const b = new InMemoryXeroBoard();
    b.recordWebhook(xeroWebhookEvent("t1", "Invoice", "r1", new Date("2026-01-01T00:00:00Z")));
    b.recordWebhook(xeroWebhookEvent("t1", "Invoice", "r3", new Date("2026-03-01T00:00:00Z")));
    b.recordWebhook(xeroWebhookEvent("t1", "Invoice", "r2", new Date("2026-02-01T00:00:00Z")));
    assert.deepEqual(
      b.recentEvents(2).map((e) => e.resourceId),
      ["r3", "r2"],
    );
    assert.equal(b.recentEvents().length, 3);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(CommerceIntegrationXeroDomainContext.systemPromptSnippet.includes("[DOMAIN: Commerce.Integration.Xero]"));
    assert.deepEqual(CommerceIntegrationXeroDomainContext.complianceFlags, ["SARS", "IFRS", "Xero_Data_Standards", "POPIA"]);
    assert.deepEqual(CommerceIntegrationXeroDomainContext.suggestedTools, ["xero_api", "spreadsheet", "document_editor"]);
  });
});
