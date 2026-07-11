// autonomousbiz/index.ts
// Full-parity port of CircleAI.AutonomousBiz (C#). C# is the exact spec.
//
// Autonomous-business primitives: TreasurySnapshot / RevenueEvent /
// AutonomousDecision records, the ITreasury / IRevenueLoop / IDecisionLog
// contracts, deterministic in-memory implementations (a revenue loop that is a
// fan-out pub/sub with kept history, a treasury that derives a running balance
// from currency-matched revenue events, an append-only decision log), and
// fail-closed Null* defaults.
//
// Type mappings (C# → TS):
//   decimal Balance / Amount            → number
//   DateTimeOffset AtUtc                → Date
//   Func<RevenueEvent, ValueTask>       → (e: RevenueEvent) => Promise<void>
//   IDisposable Subscribe(...)          → returns { dispose(): void }

/** A disposable subscription handle. Mirrors C# `IDisposable`. */
export interface Disposable {
  dispose(): void;
}

/** A point-in-time treasury balance. Mirrors C# `TreasurySnapshot` record. */
export interface TreasurySnapshot {
  readonly balance: number;
  readonly currency: string;
  readonly atUtc: Date;
}

/** Constructs a {@link TreasurySnapshot}. */
export function treasurySnapshot(balance: number, currency: string, atUtc: Date): TreasurySnapshot {
  return { balance, currency, atUtc };
}

/** A single revenue event. Mirrors C# `RevenueEvent` record. */
export interface RevenueEvent {
  readonly eventId: string;
  readonly amount: number;
  readonly currency: string;
  readonly source: string;
  readonly atUtc: Date;
}

/** Constructs a {@link RevenueEvent}. */
export function revenueEvent(
  eventId: string,
  amount: number,
  currency: string,
  source: string,
  atUtc: Date,
): RevenueEvent {
  return { eventId, amount, currency, source, atUtc };
}

/** A logged autonomous decision. Mirrors C# `AutonomousDecision` record. */
export interface AutonomousDecision {
  readonly decisionId: string;
  readonly rationale: string;
  readonly chosenAction: string;
  readonly atUtc: Date;
}

/** Constructs an {@link AutonomousDecision}. */
export function autonomousDecision(
  decisionId: string,
  rationale: string,
  chosenAction: string,
  atUtc: Date,
): AutonomousDecision {
  return { decisionId, rationale, chosenAction, atUtc };
}

/** Reads the treasury balance. Mirrors C# `ITreasury`. */
export interface ITreasury {
  readonly backendId: string;
  getSnapshotAsync(signal?: AbortSignal): Promise<TreasurySnapshot>;
}

/** Fan-out revenue pub/sub + history. Mirrors C# `IRevenueLoop`. */
export interface IRevenueLoop {
  readonly backendId: string;
  subscribe(handler: (e: RevenueEvent) => Promise<void>): Disposable;
  readAsync(since: Date, signal?: AbortSignal): Promise<readonly RevenueEvent[]>;
}

/** Append-only decision log. Mirrors C# `IDecisionLog`. */
export interface IDecisionLog {
  readonly backendId: string;
  appendAsync(d: AutonomousDecision, signal?: AbortSignal): Promise<void>;
  readAsync(limit?: number, signal?: AbortSignal): Promise<readonly AutonomousDecision[]>;
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory implementations
// ─────────────────────────────────────────────────────────────────────────────

/**
 * In-memory {@link IRevenueLoop} — a fan-out pub/sub with kept history. Mirrors
 * C# `InMemoryRevenueLoop`. Subscribers are snapshotted before dispatch and
 * fired fire-and-forget (a throwing subscriber does not break publish).
 */
export class InMemoryRevenueLoop implements IRevenueLoop {
  private readonly history: RevenueEvent[] = [];
  private readonly subs: Array<(e: RevenueEvent) => Promise<void>> = [];
  readonly backendId = "in-memory";

  /** Publish a revenue event to every subscriber and to history. Mirrors C# `Publish`. */
  publish(e: RevenueEvent): void {
    if (e == null) throw new Error("event required");
    this.history.push(e);
    const snapshot = [...this.subs];
    for (const s of snapshot) {
      try {
        void s(e);
      } catch {
        /* a revenue subscriber must not break publish */
      }
    }
  }

  subscribe(handler: (e: RevenueEvent) => Promise<void>): Disposable {
    if (handler == null) throw new Error("handler required");
    this.subs.push(handler);
    const subs = this.subs;
    return {
      dispose(): void {
        const idx = subs.indexOf(handler);
        if (idx >= 0) subs.splice(idx, 1);
      },
    };
  }

  readAsync(since: Date, _signal?: AbortSignal): Promise<readonly RevenueEvent[]> {
    return Promise.resolve(this.history.filter((e) => e.atUtc.getTime() >= since.getTime()));
  }
}

/**
 * In-memory {@link ITreasury} — derives a running balance from currency-matched
 * revenue events read from an {@link IRevenueLoop}. Mirrors C# `InMemoryTreasury`.
 */
export class InMemoryTreasury implements ITreasury {
  private readonly loop: IRevenueLoop;
  private readonly currency: string;
  readonly backendId = "in-memory";

  constructor(loop: IRevenueLoop, currency = "ZAR") {
    if (loop == null) throw new Error("loop required");
    this.loop = loop;
    this.currency = currency;
  }

  async getSnapshotAsync(signal?: AbortSignal): Promise<TreasurySnapshot> {
    const events = await this.loop.readAsync(MIN_DATE, signal);
    const bal = events
      .filter((e) => e.currency.toLowerCase() === this.currency.toLowerCase())
      .reduce((sum, e) => sum + e.amount, 0);
    return treasurySnapshot(bal, this.currency, new Date());
  }
}

/** In-memory append-only {@link IDecisionLog}. Mirrors C# `InMemoryDecisionLog`. */
export class InMemoryDecisionLog implements IDecisionLog {
  private readonly items: AutonomousDecision[] = [];
  readonly backendId = "in-memory";

  appendAsync(d: AutonomousDecision, _signal?: AbortSignal): Promise<void> {
    if (d == null) throw new Error("decision required");
    this.items.push(d);
    return Promise.resolve();
  }

  readAsync(limit = 100, _signal?: AbortSignal): Promise<readonly AutonomousDecision[]> {
    if (limit <= 0) throw new Error("limit out of range");
    return Promise.resolve(
      [...this.items].sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime()).slice(0, limit),
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null implementations
// ─────────────────────────────────────────────────────────────────────────────

const MIN_DATE = new Date("0001-01-01T00:00:00Z");

/** Fail-closed {@link ITreasury}. Mirrors C# `NullTreasury`. */
export class NullTreasury implements ITreasury {
  static readonly instance = new NullTreasury();
  readonly backendId = "null";
  getSnapshotAsync(_signal?: AbortSignal): Promise<TreasurySnapshot> {
    return Promise.resolve(treasurySnapshot(0, "ZAR", MIN_DATE));
  }
}

/** Fail-closed {@link IRevenueLoop}. Mirrors C# `NullRevenueLoop`. */
export class NullRevenueLoop implements IRevenueLoop {
  static readonly instance = new NullRevenueLoop();
  readonly backendId = "null";
  subscribe(_handler: (e: RevenueEvent) => Promise<void>): Disposable {
    return { dispose(): void {} };
  }
  readAsync(_since: Date, _signal?: AbortSignal): Promise<readonly RevenueEvent[]> {
    return Promise.resolve([]);
  }
}

/** Fail-closed {@link IDecisionLog}. Mirrors C# `NullDecisionLog`. */
export class NullDecisionLog implements IDecisionLog {
  static readonly instance = new NullDecisionLog();
  readonly backendId = "null";
  appendAsync(_d: AutonomousDecision, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
  readAsync(_limit = 100, _signal?: AbortSignal): Promise<readonly AutonomousDecision[]> {
    return Promise.resolve([]);
  }
}
