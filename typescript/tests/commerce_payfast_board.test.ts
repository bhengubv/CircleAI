// commerce_payfast_board.test.ts
// Verifies the CircleAI.Commerce.Integration.PayFast port. The signature MD5s
// and the WebUtility.UrlEncode output are pinned to values captured from the
// real .NET 10 runtime (see src/commerce/integration/payfast/index.ts header).

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryPayFastBoard,
  CommerceIntegrationPayFastDomainContext,
  webUtilityUrlEncode,
  payFastConfig,
  payFastItnPayload,
} from "../src/commerce/integration/payfast/index";

describe("webUtilityUrlEncode (.NET parity)", () => {
  it("matches .NET WebUtility.UrlEncode byte-for-byte on the probe string", () => {
    const input = "AB az 09 -_.!*()'~ +&=%@#$/:,;? \"<>{}| é";
    const expected = "AB+az+09+-_.!*()%27%7E+%2B%26%3D%25%40%23%24%2F%3A%2C%3B%3F+%22%3C%3E%7B%7D%7C+%C3%A9";
    assert.equal(webUtilityUrlEncode(input), expected);
  });

  it("space becomes '+', unreserved -_.!*() pass through, ' and ~ are encoded", () => {
    assert.equal(webUtilityUrlEncode("a b"), "a+b");
    assert.equal(webUtilityUrlEncode("-_.!*()"), "-_.!*()");
    assert.equal(webUtilityUrlEncode("'"), "%27");
    assert.equal(webUtilityUrlEncode("~"), "%7E");
  });
});

describe("InMemoryPayFastBoard.signatureFor (.NET parity)", () => {
  function board(passphrase: string) {
    return new InMemoryPayFastBoard(payFastConfig("10000100", "key", passphrase, true));
  }

  it("no passphrase — pinned MD5", () => {
    const fields = new Map<string, string>([
      ["merchant_id", "10000100"],
      ["item_name", "Test Product"],
      ["amount", "100.00"],
    ]);
    assert.equal(board("").signatureFor(fields), "a928072455fff91c7bb4238393006983");
  });

  it("with passphrase — appended and hashed, pinned MD5", () => {
    const fields = new Map<string, string>([
      ["merchant_id", "10000100"],
      ["item_name", "Test Product"],
      ["amount", "100.00"],
    ]);
    assert.equal(board("my secret pass").signatureFor(fields), "974e3c4c1f4d3d21a8e357f799e2896c");
  });

  it("values containing & and = are encoded before hashing — pinned MD5", () => {
    const fields = new Map<string, string>([
      ["a", "x&y"],
      ["b", "c=d"],
      ["e", "space here"],
    ]);
    assert.equal(board("").signatureFor(fields), "0b8fc27bd19fa06043bbf42d30da18eb");
  });

  it("single empty value, no passphrase — pinned MD5 (trailing & trimmed)", () => {
    const fields = new Map<string, string>([["k", ""]]);
    assert.equal(board("").signatureFor(fields), "7de211558c5b2e4d2d6d255f028a1e1a");
  });

  it("null orderedFields throws", () => {
    assert.throws(() => board("").signatureFor(null as never), /orderedFields required/);
  });
});

describe("InMemoryPayFastBoard — ITN + webhooks", () => {
  it("verifyItn matches on merchant id", () => {
    const b = new InMemoryPayFastBoard(payFastConfig("MID", "key", "", false));
    assert.equal(b.verifyItn(payFastItnPayload("MID", "pid", "COMPLETE", 100, "m1", "sig")), true);
    assert.equal(b.verifyItn(payFastItnPayload("OTHER", "pid", "COMPLETE", 100, "m1", "sig")), false);
  });

  it("recentWebhooks returns most-recent-first, limited", () => {
    const b = new InMemoryPayFastBoard(payFastConfig("MID", "key", "", false));
    for (let i = 1; i <= 5; i++) {
      b.recordWebhook(payFastItnPayload("MID", `p${i}`, "COMPLETE", i, `m${i}`, "sig"));
    }
    assert.deepEqual(
      b.recentWebhooks(3).map((w) => w.paymentId),
      ["p5", "p4", "p3"],
    );
    assert.equal(b.recentWebhooks().length, 5);
  });

  it("constructing with a null config throws", () => {
    assert.throws(() => new InMemoryPayFastBoard(null as never), /cfg required/);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(CommerceIntegrationPayFastDomainContext.systemPromptSnippet.includes("[DOMAIN: Commerce.Integration.PayFast]"));
    assert.deepEqual(CommerceIntegrationPayFastDomainContext.complianceFlags, [
      "PCI_DSS",
      "POPIA",
      "PASA",
      "Consumer_Protection_Act",
    ]);
    assert.deepEqual(CommerceIntegrationPayFastDomainContext.suggestedTools, [
      "payfast_api",
      "webhook_debugger",
      "document_editor",
    ]);
  });
});
