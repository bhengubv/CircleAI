/**
 * hosting/cron_schedule_parser
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hosting/cron_schedule_parser.ts
//
// Port of CircleAI.Hosting.CronScheduleParser — a minimal 5-field cron parser
// that computes the next occurrence strictly after a reference time. Field
// order: minute(0-59) hour(0-23) dom(1-31) month(1-12) dow(0-6, 0=Sunday).
// Supports  *  N  N,M  */N  N-M  N-M/S.
//
// C# operates on DateTimeOffset with a zero offset (UTC), so all component
// accessors here use getUTC*. The advancement helpers reconstruct instants via
// Date.UTC so month/day/hour rollover matches DateTimeOffset arithmetic
// exactly. Search is capped at 5 years forward to reject impossible
// expressions (e.g. "0 9 31 2 *").

/** Parse/lookup error for cron expressions. */
export class CronScheduleError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "CronScheduleError";
  }
}

/**
 * Computes the earliest UTC timestamp strictly after `after` that satisfies
 * `cronExpression`. Mirrors CronScheduleParser.GetNextOccurrence.
 */
export function getNextOccurrence(
  cronExpression: string,
  after: Date,
): Date {
  if (cronExpression == null || cronExpression.trim().length === 0)
    throw new CronScheduleError("cronExpression required");

  const parts = cronExpression
    .trim()
    .split(" ")
    .filter((p) => p.length > 0);
  if (parts.length !== 5)
    throw new CronScheduleError(
      `Cron expression must have exactly 5 fields, got ${parts.length}: '${cronExpression}'`,
    );

  const minuteSet = parseField(parts[0], 0, 59);
  const hourSet = parseField(parts[1], 0, 23);
  const domSet = parseField(parts[2], 1, 31);
  const monthSet = parseField(parts[3], 1, 12);
  const dowSet = parseField(parts[4], 0, 6);

  // Start searching from the next whole minute after `after` (seconds/ms = 0).
  let candidate = addMinutes(truncateToMinute(after), 1);

  // Cap iteration to 5 years to prevent infinite loops on impossible exprs.
  const limit = addYears(candidate, 5);

  while (candidate.getTime() <= limit.getTime()) {
    // Month check.
    if (!monthSet.has(candidate.getUTCMonth() + 1)) {
      candidate = advanceToNextMonth(candidate, monthSet);
      continue;
    }

    // Day-of-month check.
    if (!domSet.has(candidate.getUTCDate())) {
      candidate = truncateToDate(addDays(candidate, 1));
      continue;
    }

    // Day-of-week check (0=Sunday).
    if (!dowSet.has(candidate.getUTCDay())) {
      candidate = truncateToDate(addDays(candidate, 1));
      continue;
    }

    // Hour check.
    if (!hourSet.has(candidate.getUTCHours())) {
      candidate = advanceToNextHour(candidate, hourSet);
      continue;
    }

    // Minute check.
    if (!minuteSet.has(candidate.getUTCMinutes())) {
      candidate = addMinutes(candidate, 1);
      continue;
    }

    // All fields match.
    return candidate;
  }

  throw new CronScheduleError(
    `No occurrence found within 5 years for cron expression '${cronExpression}'.`,
  );
}

// ── Parsing ──────────────────────────────────────────────────────────────────

function parseField(field: string, min: number, max: number): Set<number> {
  const result = new Set<number>();
  for (const part of field.split(",")) {
    parsePart(part.trim(), min, max, result);
  }
  return result;
}

function parsePart(
  part: string,
  min: number,
  max: number,
  result: Set<number>,
): void {
  let step: number | null = null;
  let core = part;

  const slashIdx = part.indexOf("/");
  if (slashIdx >= 0) {
    const s = tryParseInt(part.substring(slashIdx + 1));
    if (s === null || s < 1)
      throw new CronScheduleError(`Invalid step in cron field part '${part}'.`);
    step = s;
    core = part.substring(0, slashIdx);
  }

  let rangeMin: number;
  let rangeMax: number;

  if (core === "*") {
    rangeMin = min;
    rangeMax = max;
  } else {
    const dashIdx = core.indexOf("-");
    if (dashIdx >= 0) {
      const lo = tryParseInt(core.substring(0, dashIdx));
      const hi = tryParseInt(core.substring(dashIdx + 1));
      if (lo === null || hi === null)
        throw new CronScheduleError(`Invalid range in cron field part '${part}'.`);
      rangeMin = lo;
      rangeMax = hi;
    } else {
      const v = tryParseInt(core);
      if (v === null)
        throw new CronScheduleError(`Invalid value in cron field part '${part}'.`);
      rangeMin = v;
      rangeMax = v;
    }
  }

  if (rangeMin < min || rangeMax > max || rangeMin > rangeMax)
    throw new CronScheduleError(
      `Cron field value ${rangeMin}-${rangeMax} out of range [${min},${max}].`,
    );

  const effectiveStep = step ?? 1;
  for (let v = rangeMin; v <= rangeMax; v += effectiveStep) result.add(v);
}

/** C# int.TryParse — strict base-10 integer, no trailing junk. */
function tryParseInt(s: string): number | null {
  if (!/^[+-]?\d+$/.test(s.trim())) return null;
  const n = Number.parseInt(s, 10);
  return Number.isNaN(n) ? null : n;
}

// ── Advancement ──────────────────────────────────────────────────────────────

function advanceToNextMonth(dt: Date, monthSet: Set<number>): Date {
  let year = dt.getUTCFullYear();
  let month = dt.getUTCMonth() + 1 + 1; // C#: dt.Month + 1 (1-based)

  if (month > 12) {
    month = 1;
    year++;
  }

  const startYear = dt.getUTCFullYear();
  while (year < startYear + 6) {
    if (monthSet.has(month)) {
      return new Date(Date.UTC(year, month - 1, 1, 0, 0, 0, 0));
    }
    month++;
    if (month > 12) {
      month = 1;
      year++;
    }
  }

  throw new CronScheduleError("No valid month found in cron expression.");
}

function advanceToNextHour(dt: Date, hourSet: Set<number>): Date {
  // Try subsequent hours today.
  for (let h = dt.getUTCHours() + 1; h <= 23; h++) {
    if (hourSet.has(h)) {
      return new Date(
        Date.UTC(dt.getUTCFullYear(), dt.getUTCMonth(), dt.getUTCDate(), h, 0, 0, 0),
      );
    }
  }
  // No valid hour today — move to next day, first valid hour.
  const nextDay = truncateToDate(addDays(dt, 1));
  const minHour = Math.min(...hourSet);
  return new Date(
    Date.UTC(
      nextDay.getUTCFullYear(),
      nextDay.getUTCMonth(),
      nextDay.getUTCDate(),
      minHour,
      0,
      0,
      0,
    ),
  );
}

// ── Date arithmetic (UTC, matching DateTimeOffset semantics) ─────────────────

function truncateToMinute(d: Date): Date {
  return new Date(
    Date.UTC(
      d.getUTCFullYear(),
      d.getUTCMonth(),
      d.getUTCDate(),
      d.getUTCHours(),
      d.getUTCMinutes(),
      0,
      0,
    ),
  );
}

function truncateToDate(d: Date): Date {
  return new Date(
    Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate(), 0, 0, 0, 0),
  );
}

function addMinutes(d: Date, minutes: number): Date {
  return new Date(d.getTime() + minutes * 60_000);
}

function addDays(d: Date, days: number): Date {
  return new Date(d.getTime() + days * 86_400_000);
}

/** DateTimeOffset.AddYears — same month/day/time, clamping Feb-29. */
function addYears(d: Date, years: number): Date {
  const y = d.getUTCFullYear() + years;
  const m = d.getUTCMonth();
  let day = d.getUTCDate();
  const daysInTarget = new Date(Date.UTC(y, m + 1, 0)).getUTCDate();
  if (day > daysInTarget) day = daysInTarget;
  return new Date(
    Date.UTC(
      y,
      m,
      day,
      d.getUTCHours(),
      d.getUTCMinutes(),
      d.getUTCSeconds(),
      d.getUTCMilliseconds(),
    ),
  );
}
