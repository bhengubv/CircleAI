/**
 * hr/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hr/index.ts
// Full-parity port of CircleAI.HR (C#). C# is the exact spec.
//
// Domain types + in-memory store for the HR vertical: employee registry, leave
// requests with decisions, and performance reviews with a per-employee average
// rating. Plus the static HRDomainContext.
//
// NOTE: The C# HRCompanionAdapter (an ICompanionSession LLM-prompt wrapper) is
// intentionally NOT ported — the sibling domain-board ports (healthcare,
// education, legal, commerce) likewise port only the board + DomainContext.
//
// Type mappings (C# → TS):
//   record            → readonly interface (+ positional factory)
//   decimal Salary    → number
//   DateTime HiredOn / From / To / ReviewedOn → Date
//   int RatingOutOf5  → number
//   ConcurrentDictionary (Ordinal) → Map<string,T>
//
// ORDERING / ROLLUP PARITY:
//   Employees      — ordered by Name ascending (default comparer / ordinal).
//   PendingLeaves  — Status == "Pending" (case-insensitive), enumeration order.
//   AvgRatingFor   — arithmetic mean of RatingOutOf5, or 0 when no reviews
//                    (C# DefaultIfEmpty(0).Average()).

/** An employee. Mirrors C# `Employee` record. */
export interface Employee {
  readonly employeeId: string;
  readonly name: string;
  readonly role: string;
  readonly hiredOn: Date;
  readonly salary: number;
  readonly currency: string;
}

/** Constructs an {@link Employee}. */
export function employee(
  employeeId: string,
  name: string,
  role: string,
  hiredOn: Date,
  salary: number,
  currency: string,
): Employee {
  return { employeeId, name, role, hiredOn, salary, currency };
}

/** A leave request. Mirrors C# `LeaveRequest` record. */
export interface LeaveRequest {
  readonly requestId: string;
  readonly employeeId: string;
  readonly kind: string;
  readonly from: Date;
  readonly to: Date;
  readonly status: string;
}

/** Constructs a {@link LeaveRequest}. */
export function leaveRequest(
  requestId: string,
  employeeId: string,
  kind: string,
  from: Date,
  to: Date,
  status: string,
): LeaveRequest {
  return { requestId, employeeId, kind, from, to, status };
}

/** A performance review. Mirrors C# `PerformanceReview` record. */
export interface PerformanceReview {
  readonly reviewId: string;
  readonly employeeId: string;
  readonly reviewedOn: Date;
  readonly ratingOutOf5: number;
  readonly notes: string;
}

/** Constructs a {@link PerformanceReview}. */
export function performanceReview(
  reviewId: string,
  employeeId: string,
  reviewedOn: Date,
  ratingOutOf5: number,
  notes: string,
): PerformanceReview {
  return { reviewId, employeeId, reviewedOn, ratingOutOf5, notes };
}

/** The HR board contract. Mirrors C# `IHRBoard`. */
export interface IHRBoard {
  hire(e: Employee): void;
  getEmployee(id: string): Employee | undefined;
  readonly employees: readonly Employee[];
  request(r: LeaveRequest): void;
  decideLeave(requestId: string, decision: string): void;
  pendingLeaves(): readonly LeaveRequest[];
  review(r: PerformanceReview): void;
  avgRatingFor(employeeId: string): number;
}

/** Ordinal (code-unit) string comparison, matching C# StringComparer.Ordinal. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** Deterministic in-memory {@link IHRBoard}. */
export class InMemoryHRBoard implements IHRBoard {
  private readonly employeesById = new Map<string, Employee>();
  private readonly leaves = new Map<string, LeaveRequest>();
  private readonly reviews: PerformanceReview[] = [];

  hire(e: Employee): void {
    if (e == null) throw new Error("e required");
    this.employeesById.set(e.employeeId, e);
  }

  getEmployee(id: string): Employee | undefined {
    return this.employeesById.get(id);
  }

  get employees(): readonly Employee[] {
    return [...this.employeesById.values()].sort((a, b) => ordinalCompare(a.name, b.name));
  }

  request(r: LeaveRequest): void {
    if (r == null) throw new Error("r required");
    this.leaves.set(r.requestId, r);
  }

  decideLeave(requestId: string, decision: string): void {
    const r = this.leaves.get(requestId);
    if (r === undefined) throw new Error(`Unknown leave request ${requestId}`);
    this.leaves.set(requestId, { ...r, status: decision });
  }

  pendingLeaves(): readonly LeaveRequest[] {
    return [...this.leaves.values()].filter((r) => r.status.toUpperCase() === "PENDING");
  }

  review(r: PerformanceReview): void {
    if (r == null) throw new Error("r required");
    this.reviews.push(r);
  }

  avgRatingFor(employeeId: string): number {
    const ratings = this.reviews.filter((r) => r.employeeId === employeeId).map((r) => r.ratingOutOf5);
    if (ratings.length === 0) return 0; // C# DefaultIfEmpty(0).Average()
    return ratings.reduce((sum, v) => sum + v, 0) / ratings.length;
  }
}

/**
 * Static domain context for the HR vertical. Mirrors C# `HRDomainContext`.
 */
export const HRDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: HR] You are a human resources expert. Help with job description drafting, interview frameworks, performance review templates, disciplinary procedures, leave management, and people analytics. Apply South African labour law principles. Compliance: Labour Relations Act 66/1995, BCEA, EEA, Skills Development Act, POPIA.",
  complianceFlags: ["LRA_66_1995", "BCEA", "EEA", "Skills_Development_Act", "POPIA"] as readonly string[],
  suggestedTools: ["hris", "document_editor", "analytics", "job_boards"] as readonly string[],
} as const;
