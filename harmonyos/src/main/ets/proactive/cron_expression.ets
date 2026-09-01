/**
 * proactive/cron_expression
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// proactive/cron_expression.ts
//
// CronExpression.cs — a minimal 5-field cron parser: `minute hour day-of-month
// month day-of-week`. Supports `*`, integers, ranges (`1-5`), lists
// (`1,15,30`), and step values (`*/15`). Day-of-week uses 0=Sunday through
// 6=Saturday.
//
// The C# operates on DateTimeOffset with a zero offset, so `.Minute/.Hour/.Day/
// .Month/.DayOfWeek` are UTC components. We use the getUTC* accessors so a Date
// is interpreted identically. GetNextOccurrence walks minute-by-minute with a
// one-year upper bound, throwing if nothing matches (a dead expression).

class CronParseError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "CronParseError";
  }
}

export class CronExpression {
  private readonly minutes: Set<number>;
  private readonly hours: Set<number>;
  private readonly daysOfMonth: Set<number>;
  private readonly months: Set<number>;
  private readonly daysOfWeek: Set<number>;

  private constructor(
    minutes: Set<number>,
    hours: Set<number>,
    daysOfMonth: Set<number>,
    months: Set<number>,
    daysOfWeek: Set<number>,
  ) {
    this.minutes = minutes;
    this.hours = hours;
    this.daysOfMonth = daysOfMonth;
    this.months = months;
    this.daysOfWeek = daysOfWeek;
  }

  static parse(expression: string): CronExpression {
    if (expression == null) throw new CronParseError("expression required");
    // Split on runs of whitespace, dropping empties + trimming (C#
    // RemoveEmptyEntries | TrimEntries).
    const fields = expression.split(/\s+/).filter((f) => f.length > 0);
    if (fields.length !== 5) {
      throw new CronParseError(
        `Cron expression must have 5 fields, got ${fields.length}: '${expression}'`,
      );
    }
    return new CronExpression(
      parseField(fields[0], 0, 59),
      parseField(fields[1], 0, 23),
      parseField(fields[2], 1, 31),
      parseField(fields[3], 1, 12),
      parseField(fields[4], 0, 6),
    );
  }

  /**
   * Next UTC time at or after `after` when the expression matches. Hard upper
   * bound of one year forward — if nothing matches in 365 days the expression is
   * effectively dead and we throw rather than spin.
   */
  getNextOccurrence(after: Date): Date {
    // after.AddMinutes(1), then truncate to the minute (seconds/ms = 0).
    let t = new Date(after.getTime() + 60_000);
    t = new Date(
      Date.UTC(t.getUTCFullYear(), t.getUTCMonth(), t.getUTCDate(), t.getUTCHours(), t.getUTCMinutes(), 0, 0),
    );
    const limit = addUtcYears(t, 1);
    while (t.getTime() <= limit.getTime()) {
      if (this.matches(t)) return t;
      t = new Date(t.getTime() + 60_000);
    }
    throw new Error("Cron expression does not match any time in the next year.");
  }

  matches(moment: Date): boolean {
    if (!this.minutes.has(moment.getUTCMinutes())) return false;
    if (!this.hours.has(moment.getUTCHours())) return false;
    if (!this.daysOfMonth.has(moment.getUTCDate())) return false;
    if (!this.months.has(moment.getUTCMonth() + 1)) return false;
    // Day-of-month AND day-of-week must both match (C# settles on AND).
    if (!this.daysOfWeek.has(moment.getUTCDay())) return false;
    return true;
  }
}

function parseField(field: string, min: number, max: number): Set<number> {
  const values = new Set<number>();
  for (const part of field.split(",")) {
    expandPart(part.trim(), min, max, values);
  }
  if (values.size === 0) {
    throw new CronParseError(`Cron field '${field}' resolved to no values.`);
  }
  return values;
}

function expandPart(part: string, min: number, max: number, sink: Set<number>): void {
  let step = 1;
  const slash = part.indexOf("/");
  if (slash >= 0) {
    const stepStr = part.substring(slash + 1);
    const parsed = parseStrictInt(stepStr);
    if (parsed === null || parsed <= 0) {
      throw new CronParseError(`Cron step '${part}' is not a positive integer.`);
    }
    step = parsed;
    part = part.substring(0, slash);
  }

  let rangeStart: number;
  let rangeEnd: number;
  if (part === "*") {
    rangeStart = min;
    rangeEnd = max;
  } else if (part.includes("-")) {
    const dash = part.indexOf("-");
    rangeStart = intOrThrow(part.substring(0, dash));
    rangeEnd = intOrThrow(part.substring(dash + 1));
  } else {
    rangeStart = intOrThrow(part);
    rangeEnd = rangeStart;
  }

  if (rangeStart < min || rangeEnd > max || rangeStart > rangeEnd) {
    throw new CronParseError(`Cron part '${part}' out of range [${min},${max}].`);
  }

  for (let v = rangeStart; v <= rangeEnd; v += step) {
    sink.add(v);
  }
}

/** C# int.TryParse — strict base-10 integer, no trailing junk. */
function parseStrictInt(s: string): number | null {
  if (!/^[+-]?\d+$/.test(s.trim())) return null;
  const n = Number.parseInt(s, 10);
  return Number.isNaN(n) ? null : n;
}

/** C# int.Parse — throws FormatException on non-integer. */
function intOrThrow(s: string): number {
  const n = parseStrictInt(s);
  if (n === null) throw new CronParseError(`'${s}' is not a valid integer.`);
  return n;
}

/**
 * Add whole years to a UTC instant the way DateTimeOffset.AddYears does:
 * same month/day/time, year + n, clamping Feb-29 to Feb-28 on non-leap years.
 */
function addUtcYears(d: Date, years: number): Date {
  const y = d.getUTCFullYear() + years;
  const m = d.getUTCMonth();
  let day = d.getUTCDate();
  const daysInTarget = new Date(Date.UTC(y, m + 1, 0)).getUTCDate();
  if (day > daysInTarget) day = daysInTarget;
  return new Date(
    Date.UTC(y, m, day, d.getUTCHours(), d.getUTCMinutes(), d.getUTCSeconds(), d.getUTCMilliseconds()),
  );
}
