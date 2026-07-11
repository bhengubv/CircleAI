// banking/index.ts
// Full-parity port of CircleAI.Banking (C#). C# is the exact spec.
//
// Banking contracts (2.8.0) + real in-memory primitives (3.3.0): an account
// store, ledger writer, and payment processor with balance checks and
// double-entry bookkeeping (debit source, credit destination). Plus the
// fail-closed Null* defaults (2.8.0).
//
// Type mappings (C# → TS):
//   record                 → readonly interface (+ positional factory)
//   decimal                → number
//   DateTimeOffset         → Date
//   ValueTask<T>           → Promise<T> (the C# impls all complete synchronously
//                            via ValueTask.FromResult; we preserve that by
//                            returning already-resolved promises)
//   CancellationToken ct   → signal?: AbortSignal (unused by these deterministic
//                            in-memory impls, present for contract parity)
//   Guid.NewGuid("n")      → newGuidN(); Guid.Empty.ToString() → all-zero guid
//   ConcurrentDictionary<string,T> (Ordinal) → Map<string,T>
//
// CONCURRENCY: the C# `InMemoryBank` guards ledger mutation + payment with a
// single `_txLock`. JS is single-threaded so no lock object is needed; the
// method bodies are already atomic with respect to other synchronous calls.

import { newGuidN } from "../companion/herjarvis/guid.js";

/** The all-zero GUID rendered as C# `Guid.Empty.ToString()`. */
const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";

/** A bank account. Mirrors C# `Account` record. */
export interface Account {
  readonly accountId: string;
  readonly ownerId: string;
  readonly currency: string;
  readonly balance: number;
}

/** Constructs an {@link Account}. */
export function account(accountId: string, ownerId: string, currency: string, balance: number): Account {
  return { accountId, ownerId, currency, balance };
}

/** A ledger entry. Mirrors C# `LedgerEntry` record. */
export interface LedgerEntry {
  readonly txId: string;
  readonly accountId: string;
  readonly amount: number;
  readonly memo: string;
  readonly atUtc: Date;
}

/** Constructs a {@link LedgerEntry}. */
export function ledgerEntry(
  txId: string,
  accountId: string,
  amount: number,
  memo: string,
  atUtc: Date,
): LedgerEntry {
  return { txId, accountId, amount, memo, atUtc };
}

/** A payment request. Mirrors C# `PaymentRequest` record. */
export interface PaymentRequest {
  readonly fromAccount: string;
  readonly toAccount: string;
  readonly amount: number;
  readonly currency: string;
  readonly memo: string;
}

/** Constructs a {@link PaymentRequest}. */
export function paymentRequest(
  fromAccount: string,
  toAccount: string,
  amount: number,
  currency: string,
  memo: string,
): PaymentRequest {
  return { fromAccount, toAccount, amount, currency, memo };
}

/** The result of a payment attempt. Mirrors C# `PaymentResult` record. */
export interface PaymentResult {
  readonly txId: string;
  readonly accepted: boolean;
  readonly failureReason: string | null;
}

/** Constructs a {@link PaymentResult}. */
export function paymentResult(txId: string, accepted: boolean, failureReason: string | null): PaymentResult {
  return { txId, accepted, failureReason };
}

/** Reads account state. Backends inject a concrete implementation. */
export interface IAccountReader {
  readonly backendId: string;
  getAccountAsync(accountId: string, signal?: AbortSignal): Promise<Account | null>;
  listForOwnerAsync(ownerId: string, signal?: AbortSignal): Promise<readonly Account[]>;
}

/** Appends to and reads the ledger. */
export interface ILedgerWriter {
  readonly backendId: string;
  appendAsync(entry: LedgerEntry, signal?: AbortSignal): Promise<LedgerEntry>;
  readAsync(accountId: string, limit?: number, signal?: AbortSignal): Promise<readonly LedgerEntry[]>;
}

/** Processes a payment request. */
export interface IPaymentProcessor {
  readonly backendId: string;
  processAsync(req: PaymentRequest, signal?: AbortSignal): Promise<PaymentResult>;
}

/**
 * Concurrent in-memory bank shared by reader / ledger / payment. Mirrors C#
 * `InMemoryBank`: balance is updated on every ledger append, and payments are
 * settled with two ledger entries (debit source, credit destination).
 */
export class InMemoryBank {
  private readonly accounts = new Map<string, Account>();
  private readonly ledger = new Map<string, LedgerEntry[]>();

  seedAccount(acct: Account): void {
    if (acct == null) throw new Error("account required");
    this.accounts.set(acct.accountId, acct);
  }

  get(id: string): Account | null {
    return this.accounts.get(id) ?? null;
  }

  listForOwner(ownerId: string): readonly Account[] {
    return [...this.accounts.values()].filter((a) => a.ownerId === ownerId);
  }

  append(entry: LedgerEntry): LedgerEntry {
    if (entry == null) throw new Error("entry required");
    const acct = this.accounts.get(entry.accountId);
    if (acct === undefined) throw new Error(`Unknown account ${entry.accountId}`);

    this.accounts.set(entry.accountId, { ...acct, balance: acct.balance + entry.amount });
    let list = this.ledger.get(entry.accountId);
    if (list === undefined) {
      list = [];
      this.ledger.set(entry.accountId, list);
    }
    list.push(entry);
    return entry;
  }

  read(accountId: string, limit: number): readonly LedgerEntry[] {
    const list = this.ledger.get(accountId);
    if (list === undefined) return [];
    return [...list].sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime()).slice(0, limit);
  }

  processPayment(req: PaymentRequest): PaymentResult {
    if (req == null) throw new Error("req required");
    if (req.amount <= 0) return { txId: newGuidN(), accepted: false, failureReason: "Amount must be positive" };

    const src = this.accounts.get(req.fromAccount);
    if (src === undefined) return { txId: newGuidN(), accepted: false, failureReason: "Unknown source account" };
    const dst = this.accounts.get(req.toAccount);
    if (dst === undefined) return { txId: newGuidN(), accepted: false, failureReason: "Unknown destination account" };
    if (
      src.currency.toLowerCase() !== req.currency.toLowerCase() ||
      dst.currency.toLowerCase() !== req.currency.toLowerCase()
    ) {
      return { txId: newGuidN(), accepted: false, failureReason: "Currency mismatch" };
    }
    if (src.balance < req.amount) return { txId: newGuidN(), accepted: false, failureReason: "Insufficient funds" };

    const txId = newGuidN();
    const now = new Date();
    this.append({ txId, accountId: req.fromAccount, amount: -req.amount, memo: `To ${req.toAccount}: ${req.memo}`, atUtc: now });
    this.append({ txId, accountId: req.toAccount, amount: req.amount, memo: `From ${req.fromAccount}: ${req.memo}`, atUtc: now });
    return { txId, accepted: true, failureReason: null };
  }
}

/** In-memory {@link IAccountReader} over an {@link InMemoryBank}. */
export class InMemoryAccountReader implements IAccountReader {
  private readonly bank: InMemoryBank;
  constructor(bank: InMemoryBank) {
    if (bank == null) throw new Error("bank required");
    this.bank = bank;
  }
  get backendId(): string {
    return "in-memory";
  }
  getAccountAsync(id: string, _signal?: AbortSignal): Promise<Account | null> {
    return Promise.resolve(this.bank.get(id));
  }
  listForOwnerAsync(owner: string, _signal?: AbortSignal): Promise<readonly Account[]> {
    return Promise.resolve(this.bank.listForOwner(owner));
  }
}

/** In-memory {@link ILedgerWriter} over an {@link InMemoryBank}. */
export class InMemoryLedgerWriter implements ILedgerWriter {
  private readonly bank: InMemoryBank;
  constructor(bank: InMemoryBank) {
    if (bank == null) throw new Error("bank required");
    this.bank = bank;
  }
  get backendId(): string {
    return "in-memory";
  }
  appendAsync(e: LedgerEntry, _signal?: AbortSignal): Promise<LedgerEntry> {
    return Promise.resolve(this.bank.append(e));
  }
  readAsync(acc: string, limit = 100, _signal?: AbortSignal): Promise<readonly LedgerEntry[]> {
    return Promise.resolve(this.bank.read(acc, limit));
  }
}

/** In-memory {@link IPaymentProcessor} over an {@link InMemoryBank}. */
export class InMemoryPaymentProcessor implements IPaymentProcessor {
  private readonly bank: InMemoryBank;
  constructor(bank: InMemoryBank) {
    if (bank == null) throw new Error("bank required");
    this.bank = bank;
  }
  get backendId(): string {
    return "in-memory";
  }
  processAsync(req: PaymentRequest, _signal?: AbortSignal): Promise<PaymentResult> {
    return Promise.resolve(this.bank.processPayment(req));
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Fail-closed Null* defaults (2.8.0)
// ─────────────────────────────────────────────────────────────────────────────

/** Fail-closed {@link IAccountReader}: no accounts, ever. */
export class NullAccountReader implements IAccountReader {
  static readonly instance = new NullAccountReader();
  get backendId(): string {
    return "null";
  }
  getAccountAsync(_id: string, _signal?: AbortSignal): Promise<Account | null> {
    return Promise.resolve(null);
  }
  listForOwnerAsync(_owner: string, _signal?: AbortSignal): Promise<readonly Account[]> {
    return Promise.resolve([]);
  }
}

/** Fail-closed {@link ILedgerWriter}: echoes the entry, reads nothing. */
export class NullLedgerWriter implements ILedgerWriter {
  static readonly instance = new NullLedgerWriter();
  get backendId(): string {
    return "null";
  }
  appendAsync(e: LedgerEntry, _signal?: AbortSignal): Promise<LedgerEntry> {
    return Promise.resolve(e);
  }
  readAsync(_acc: string, _limit = 100, _signal?: AbortSignal): Promise<readonly LedgerEntry[]> {
    return Promise.resolve([]);
  }
}

/** Fail-closed {@link IPaymentProcessor}: rejects every payment. */
export class NullPaymentProcessor implements IPaymentProcessor {
  static readonly instance = new NullPaymentProcessor();
  get backendId(): string {
    return "null";
  }
  processAsync(_req: PaymentRequest, _signal?: AbortSignal): Promise<PaymentResult> {
    return Promise.resolve({ txId: EMPTY_GUID, accepted: false, failureReason: "NullPaymentProcessor." });
  }
}
