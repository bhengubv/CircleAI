// banking_board.test.ts
// Verifies the CircleAI.Banking port: InMemoryBank double-entry payments +
// balance moves + ledger ordering, the reader/ledger/payment adapters, and the
// fail-closed Null* defaults.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryBank,
  InMemoryAccountReader,
  InMemoryLedgerWriter,
  InMemoryPaymentProcessor,
  NullAccountReader,
  NullLedgerWriter,
  NullPaymentProcessor,
  account,
  ledgerEntry,
  paymentRequest,
} from "../src/banking/index";

describe("InMemoryBank", () => {
  it("append moves the account balance and records the entry", () => {
    const bank = new InMemoryBank();
    bank.seedAccount(account("acc", "owner", "ZAR", 100));
    bank.append(ledgerEntry("t1", "acc", 50, "deposit", new Date("2026-01-01T00:00:00Z")));
    assert.equal(bank.get("acc")?.balance, 150);
    bank.append(ledgerEntry("t2", "acc", -30, "withdrawal", new Date("2026-01-02T00:00:00Z")));
    assert.equal(bank.get("acc")?.balance, 120);
  });

  it("append to an unknown account throws", () => {
    const bank = new InMemoryBank();
    assert.throws(() => bank.append(ledgerEntry("t", "ghost", 1, "m", new Date())), /Unknown account ghost/);
  });

  it("read returns entries newest-first, limited", () => {
    const bank = new InMemoryBank();
    bank.seedAccount(account("acc", "owner", "ZAR", 0));
    bank.append(ledgerEntry("t1", "acc", 1, "a", new Date("2026-01-01T00:00:00Z")));
    bank.append(ledgerEntry("t2", "acc", 1, "b", new Date("2026-03-01T00:00:00Z")));
    bank.append(ledgerEntry("t3", "acc", 1, "c", new Date("2026-02-01T00:00:00Z")));
    assert.deepEqual(
      bank.read("acc", 2).map((e) => e.txId),
      ["t2", "t3"],
    );
    assert.deepEqual(bank.read("unknown", 10), []);
  });

  it("processPayment settles with two entries (debit source, credit dest)", () => {
    const bank = new InMemoryBank();
    bank.seedAccount(account("src", "o1", "ZAR", 500));
    bank.seedAccount(account("dst", "o2", "ZAR", 0));
    const r = bank.processPayment(paymentRequest("src", "dst", 200, "ZAR", "rent"));
    assert.equal(r.accepted, true);
    assert.equal(r.failureReason, null);
    assert.equal(bank.get("src")?.balance, 300);
    assert.equal(bank.get("dst")?.balance, 200);
    // Both ledger entries share the transaction id.
    assert.equal(bank.read("src", 1)[0].txId, r.txId);
    assert.equal(bank.read("dst", 1)[0].txId, r.txId);
  });

  it("processPayment rejects: non-positive, unknown accounts, currency mismatch, insufficient", () => {
    const bank = new InMemoryBank();
    bank.seedAccount(account("src", "o1", "ZAR", 100));
    bank.seedAccount(account("dst", "o2", "ZAR", 0));
    bank.seedAccount(account("usd", "o3", "USD", 0));

    assert.equal(bank.processPayment(paymentRequest("src", "dst", 0, "ZAR", "m")).failureReason, "Amount must be positive");
    assert.equal(bank.processPayment(paymentRequest("ghost", "dst", 1, "ZAR", "m")).failureReason, "Unknown source account");
    assert.equal(bank.processPayment(paymentRequest("src", "ghost", 1, "ZAR", "m")).failureReason, "Unknown destination account");
    assert.equal(bank.processPayment(paymentRequest("src", "usd", 1, "ZAR", "m")).failureReason, "Currency mismatch");
    assert.equal(bank.processPayment(paymentRequest("src", "dst", 999, "ZAR", "m")).failureReason, "Insufficient funds");
    // A failed payment leaves balances untouched.
    assert.equal(bank.get("src")?.balance, 100);
  });

  it("currency comparison is case-insensitive", () => {
    const bank = new InMemoryBank();
    bank.seedAccount(account("src", "o1", "zar", 100));
    bank.seedAccount(account("dst", "o2", "ZAR", 0));
    const r = bank.processPayment(paymentRequest("src", "dst", 10, "Zar", "m"));
    assert.equal(r.accepted, true);
  });

  it("listForOwner filters by owner", () => {
    const bank = new InMemoryBank();
    bank.seedAccount(account("a", "o1", "ZAR", 0));
    bank.seedAccount(account("b", "o1", "ZAR", 0));
    bank.seedAccount(account("c", "o2", "ZAR", 0));
    assert.deepEqual(
      bank.listForOwner("o1").map((a) => a.accountId).sort(),
      ["a", "b"],
    );
  });
});

describe("Banking adapters over InMemoryBank", () => {
  it("reader/ledger/payment expose backendId and delegate", async () => {
    const bank = new InMemoryBank();
    bank.seedAccount(account("src", "o", "ZAR", 100));
    bank.seedAccount(account("dst", "o", "ZAR", 0));
    const reader = new InMemoryAccountReader(bank);
    const writer = new InMemoryLedgerWriter(bank);
    const proc = new InMemoryPaymentProcessor(bank);

    assert.equal(reader.backendId, "in-memory");
    assert.equal(writer.backendId, "in-memory");
    assert.equal(proc.backendId, "in-memory");

    assert.equal((await reader.getAccountAsync("src"))?.balance, 100);
    assert.equal(await reader.getAccountAsync("ghost"), null);
    assert.equal((await reader.listForOwnerAsync("o")).length, 2);

    const r = await proc.processAsync(paymentRequest("src", "dst", 40, "ZAR", "m"));
    assert.equal(r.accepted, true);
    assert.equal((await writer.readAsync("src"))[0].amount, -40);
  });

  it("constructing an adapter with a null bank throws", () => {
    assert.throws(() => new InMemoryAccountReader(null as never));
    assert.throws(() => new InMemoryLedgerWriter(null as never));
    assert.throws(() => new InMemoryPaymentProcessor(null as never));
  });
});

describe("Banking Null* fail-closed defaults", () => {
  it("null reader returns nothing", async () => {
    assert.equal(NullAccountReader.instance.backendId, "null");
    assert.equal(await NullAccountReader.instance.getAccountAsync("x"), null);
    assert.deepEqual(await NullAccountReader.instance.listForOwnerAsync("x"), []);
  });

  it("null ledger echoes append but reads nothing", async () => {
    const e = ledgerEntry("t", "a", 1, "m", new Date());
    assert.equal(await NullLedgerWriter.instance.appendAsync(e), e);
    assert.deepEqual(await NullLedgerWriter.instance.readAsync("a"), []);
  });

  it("null processor rejects every payment with the empty guid", async () => {
    const r = await NullPaymentProcessor.instance.processAsync(paymentRequest("a", "b", 1, "ZAR", "m"));
    assert.equal(r.accepted, false);
    assert.equal(r.txId, "00000000-0000-0000-0000-000000000000");
    assert.equal(r.failureReason, "NullPaymentProcessor.");
  });
});
