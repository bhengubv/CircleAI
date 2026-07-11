// crm/index.ts
// Full-parity port of CircleAI.CRM (C#). C# is the exact spec.
//
// CRM contracts (2.8.0) + real in-memory primitives (3.3.0): a contact store
// with name/email substring search, a deal pipeline indexed by stage, and a
// per-contact activity log. Plus the fail-closed Null* defaults (2.8.0).
//
// Type mappings (C# → TS):
//   record                 → readonly interface (+ positional factory)
//   decimal                → number
//   DateTimeOffset         → Date
//   ValueTask<T>           → Promise<T> (the C# impls complete synchronously via
//                            ValueTask.FromResult / ValueTask.CompletedTask; we
//                            preserve that with already-resolved promises)
//   CancellationToken ct   → signal?: AbortSignal (unused by these deterministic
//                            in-memory impls, present for contract parity)
//   ConcurrentDictionary<string,T> (Ordinal) → Map<string,T> (JS string keys are
//                            ordinal, matching StringComparer.Ordinal)
//
// ORDERING PARITY:
//   IContactStore.SearchAsync  — name/email substring (case-insensitive),
//                                ordered by FullName ascending (OrdinalIgnoreCase),
//                                take topK.
//   IDealPipeline.ListByStage  — stage match (case-insensitive), ordered by Value
//                                descending.
//   IActivityLog.ReadForContact— ordered by AtUtc descending, take limit.

/** A CRM contact. Mirrors C# `Contact` record. */
export interface Contact {
  readonly contactId: string;
  readonly fullName: string;
  readonly email: string | null;
  readonly phone: string | null;
  readonly companyId: string | null;
}

/** Constructs a {@link Contact}. */
export function contact(
  contactId: string,
  fullName: string,
  email: string | null,
  phone: string | null,
  companyId: string | null,
): Contact {
  return { contactId, fullName, email, phone, companyId };
}

/** A company. Mirrors C# `Company` record. */
export interface Company {
  readonly companyId: string;
  readonly name: string;
  readonly industry: string | null;
}

/** Constructs a {@link Company}. */
export function company(companyId: string, name: string, industry: string | null): Company {
  return { companyId, name, industry };
}

/** A sales deal. Mirrors C# `Deal` record. */
export interface Deal {
  readonly dealId: string;
  readonly companyId: string;
  readonly name: string;
  readonly value: number;
  readonly currency: string;
  readonly stage: string;
}

/** Constructs a {@link Deal}. */
export function deal(
  dealId: string,
  companyId: string,
  name: string,
  value: number,
  currency: string,
  stage: string,
): Deal {
  return { dealId, companyId, name, value, currency, stage };
}

/** A logged CRM activity. Mirrors C# `Activity` record. */
export interface Activity {
  readonly activityId: string;
  readonly contactId: string;
  readonly kind: string;
  readonly body: string;
  /** UTC instant of the activity (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs an {@link Activity}. */
export function activity(
  activityId: string,
  contactId: string,
  kind: string,
  body: string,
  atUtc: Date,
): Activity {
  return { activityId, contactId, kind, body, atUtc };
}

/** Stores and searches contacts. Mirrors C# `IContactStore`. */
export interface IContactStore {
  readonly backendId: string;
  upsertAsync(c: Contact, signal?: AbortSignal): Promise<void>;
  getAsync(id: string, signal?: AbortSignal): Promise<Contact | null>;
  searchAsync(query: string, topK?: number, signal?: AbortSignal): Promise<readonly Contact[]>;
}

/** A stage-indexed deal pipeline. Mirrors C# `IDealPipeline`. */
export interface IDealPipeline {
  readonly backendId: string;
  upsertAsync(d: Deal, signal?: AbortSignal): Promise<void>;
  getAsync(id: string, signal?: AbortSignal): Promise<Deal | null>;
  listByStageAsync(stage: string, signal?: AbortSignal): Promise<readonly Deal[]>;
}

/** A per-contact activity log. Mirrors C# `IActivityLog`. */
export interface IActivityLog {
  readonly backendId: string;
  appendAsync(a: Activity, signal?: AbortSignal): Promise<void>;
  readForContactAsync(contactId: string, limit?: number, signal?: AbortSignal): Promise<readonly Activity[]>;
}

/** Ordinal (code-unit) string comparison, matching C# StringComparer.Ordinal. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/**
 * Ordinal-case-insensitive compare, matching C# StringComparer.OrdinalIgnoreCase
 * ordering used by `SearchAsync`'s `OrderBy(c => c.FullName, OrdinalIgnoreCase)`.
 * Ties (equal under case-fold) fall back to the ordinal comparison of the
 * originals to keep the sort deterministic, matching .NET's tie-break behaviour
 * for OrdinalIgnoreCase.
 */
function ordinalIgnoreCaseCompare(a: string, b: string): number {
  const la = a.toUpperCase();
  const lb = b.toUpperCase();
  if (la < lb) return -1;
  if (la > lb) return 1;
  return ordinalCompare(a, b);
}

/** Case-insensitive substring test, matching C# `Contains(q, OrdinalIgnoreCase)`. */
function containsIgnoreCase(haystack: string, needle: string): boolean {
  return haystack.toUpperCase().includes(needle.toUpperCase());
}

/**
 * Real in-memory {@link IContactStore}: substring search on name/email
 * (case-insensitive), ordered by full name ascending.
 */
export class InMemoryContactStore implements IContactStore {
  private readonly items = new Map<string, Contact>();
  get backendId(): string {
    return "in-memory";
  }

  upsertAsync(c: Contact, _signal?: AbortSignal): Promise<void> {
    if (c == null) throw new Error("c required");
    if (c.contactId == null || c.contactId.trim() === "") throw new Error("ContactId required");
    this.items.set(c.contactId, c);
    return Promise.resolve();
  }

  getAsync(id: string, _signal?: AbortSignal): Promise<Contact | null> {
    if (id == null || id.trim() === "") throw new Error("id required");
    return Promise.resolve(this.items.get(id) ?? null);
  }

  searchAsync(query: string, topK = 20, _signal?: AbortSignal): Promise<readonly Contact[]> {
    if (query == null) throw new Error("query required");
    if (topK <= 0) throw new Error("topK");
    const hits = [...this.items.values()]
      .filter((c) => containsIgnoreCase(c.fullName, query) || (c.email != null && containsIgnoreCase(c.email, query)))
      .sort((a, b) => ordinalIgnoreCaseCompare(a.fullName, b.fullName))
      .slice(0, topK);
    return Promise.resolve(hits);
  }
}

/**
 * Real in-memory {@link IDealPipeline}: deals indexed by id, listed by stage
 * (case-insensitive) ordered by value descending.
 */
export class InMemoryDealPipeline implements IDealPipeline {
  private readonly items = new Map<string, Deal>();
  get backendId(): string {
    return "in-memory";
  }

  upsertAsync(d: Deal, _signal?: AbortSignal): Promise<void> {
    if (d == null) throw new Error("d required");
    if (d.dealId == null || d.dealId.trim() === "") throw new Error("DealId required");
    this.items.set(d.dealId, d);
    return Promise.resolve();
  }

  getAsync(id: string, _signal?: AbortSignal): Promise<Deal | null> {
    return Promise.resolve(this.items.get(id) ?? null);
  }

  listByStageAsync(stage: string, _signal?: AbortSignal): Promise<readonly Deal[]> {
    if (stage == null || stage.trim() === "") throw new Error("stage required");
    const target = stage.toUpperCase();
    const hits = [...this.items.values()]
      .filter((d) => d.stage.toUpperCase() === target)
      .sort((a, b) => b.value - a.value);
    return Promise.resolve(hits);
  }
}

/**
 * Real in-memory {@link IActivityLog}: append-only, one list per contact,
 * read newest-first (AtUtc descending) up to `limit`.
 */
export class InMemoryActivityLog implements IActivityLog {
  private readonly byContact = new Map<string, Activity[]>();
  get backendId(): string {
    return "in-memory";
  }

  appendAsync(a: Activity, _signal?: AbortSignal): Promise<void> {
    if (a == null) throw new Error("a required");
    if (a.contactId == null || a.contactId.trim() === "") throw new Error("ContactId required");
    let list = this.byContact.get(a.contactId);
    if (list === undefined) {
      list = [];
      this.byContact.set(a.contactId, list);
    }
    list.push(a);
    return Promise.resolve();
  }

  readForContactAsync(contactId: string, limit = 100, _signal?: AbortSignal): Promise<readonly Activity[]> {
    if (contactId == null || contactId.trim() === "") throw new Error("contactId required");
    const list = this.byContact.get(contactId);
    if (list === undefined) return Promise.resolve([]);
    const hits = [...list].sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime()).slice(0, limit);
    return Promise.resolve(hits);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Fail-closed Null* defaults (2.8.0)
// ─────────────────────────────────────────────────────────────────────────────

/** Fail-closed {@link IContactStore}: stores nothing, returns nothing. */
export class NullContactStore implements IContactStore {
  static readonly instance = new NullContactStore();
  get backendId(): string {
    return "null";
  }
  upsertAsync(_c: Contact, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
  getAsync(_id: string, _signal?: AbortSignal): Promise<Contact | null> {
    return Promise.resolve(null);
  }
  searchAsync(_query: string, _topK = 20, _signal?: AbortSignal): Promise<readonly Contact[]> {
    return Promise.resolve([]);
  }
}

/** Fail-closed {@link IDealPipeline}: stores nothing, returns nothing. */
export class NullDealPipeline implements IDealPipeline {
  static readonly instance = new NullDealPipeline();
  get backendId(): string {
    return "null";
  }
  upsertAsync(_d: Deal, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
  getAsync(_id: string, _signal?: AbortSignal): Promise<Deal | null> {
    return Promise.resolve(null);
  }
  listByStageAsync(_stage: string, _signal?: AbortSignal): Promise<readonly Deal[]> {
    return Promise.resolve([]);
  }
}

/** Fail-closed {@link IActivityLog}: appends nowhere, reads nothing. */
export class NullActivityLog implements IActivityLog {
  static readonly instance = new NullActivityLog();
  get backendId(): string {
    return "null";
  }
  appendAsync(_a: Activity, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
  readForContactAsync(_contactId: string, _limit = 100, _signal?: AbortSignal): Promise<readonly Activity[]> {
    return Promise.resolve([]);
  }
}
