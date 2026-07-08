// cron_expression.test.ts
//
// Verifies the ported CronExpression (CronExpression.cs): 5-field parsing with
// *, integers, ranges, lists, and steps; day-of-week 0=Sunday..6=Saturday;
// AND semantics between day-of-month and day-of-week; next-occurrence walking;
// and the parse-error cases.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { CronExpression } from '../src/proactive/index';

describe('CronExpression.parse — field grammar', () => {
  it('requires exactly 5 fields', () => {
    assert.throws(() => CronExpression.parse('* * * *'), /must have 5 fields/);
    assert.throws(() => CronExpression.parse('* * * * * *'), /must have 5 fields/);
  });

  it('matches a specific minute/hour', () => {
    const expr = CronExpression.parse('30 6 * * *'); // 06:30 every day
    assert.equal(expr.matches(new Date(Date.UTC(2026, 6, 8, 6, 30, 0))), true);
    assert.equal(expr.matches(new Date(Date.UTC(2026, 6, 8, 6, 31, 0))), false);
    assert.equal(expr.matches(new Date(Date.UTC(2026, 6, 8, 7, 30, 0))), false);
  });

  it('supports ranges and lists', () => {
    const expr = CronExpression.parse('0 9-17 * * 1,3,5'); // top of hour, 9-17, Mon/Wed/Fri
    // 2026-07-08 is a Wednesday (getUTCDay 3).
    assert.equal(new Date(Date.UTC(2026, 6, 8)).getUTCDay(), 3);
    assert.equal(expr.matches(new Date(Date.UTC(2026, 6, 8, 9, 0, 0))), true);
    assert.equal(expr.matches(new Date(Date.UTC(2026, 6, 8, 17, 0, 0))), true);
    assert.equal(expr.matches(new Date(Date.UTC(2026, 6, 8, 18, 0, 0))), false); // out of hour range
    // 2026-07-09 is a Thursday (day 4) → excluded.
    assert.equal(expr.matches(new Date(Date.UTC(2026, 6, 9, 9, 0, 0))), false);
  });

  it('supports step values', () => {
    const expr = CronExpression.parse('*/15 * * * *'); // every 15 minutes
    for (const m of [0, 15, 30, 45]) assert.equal(expr.matches(new Date(Date.UTC(2026, 6, 8, 1, m, 0))), true);
    assert.equal(expr.matches(new Date(Date.UTC(2026, 6, 8, 1, 7, 0))), false);
  });

  it('day 0 and day 7? — Sunday is 0 (day 7 is out of range and rejected)', () => {
    const sunday = CronExpression.parse('0 0 * * 0');
    // 2026-07-12 is a Sunday.
    assert.equal(new Date(Date.UTC(2026, 6, 12)).getUTCDay(), 0);
    assert.equal(sunday.matches(new Date(Date.UTC(2026, 6, 12, 0, 0, 0))), true);
    assert.throws(() => CronExpression.parse('0 0 * * 7'), /out of range/);
  });

  it('applies day-of-month AND day-of-week (both must match)', () => {
    // "at 00:00 on the 8th, only if it is also a Monday". 2026-07-08 is Wed →
    // no match on the 8th; but a later 8th that is a Monday would match.
    const expr = CronExpression.parse('0 0 8 * 1');
    assert.equal(expr.matches(new Date(Date.UTC(2026, 6, 8, 0, 0, 0))), false); // Wed
    // 2026-06-08 is a Monday.
    assert.equal(new Date(Date.UTC(2026, 5, 8)).getUTCDay(), 1);
    assert.equal(expr.matches(new Date(Date.UTC(2026, 5, 8, 0, 0, 0))), true);
  });
});

describe('CronExpression.getNextOccurrence', () => {
  it('finds the next matching minute strictly after `after`', () => {
    const expr = CronExpression.parse('30 6 * * *');
    const after = new Date(Date.UTC(2026, 6, 8, 6, 0, 0));
    const next = expr.getNextOccurrence(after);
    assert.equal(next.getTime(), Date.UTC(2026, 6, 8, 6, 30, 0));
  });

  it('rolls to the next day when today is already past', () => {
    const expr = CronExpression.parse('30 6 * * *');
    const after = new Date(Date.UTC(2026, 6, 8, 7, 0, 0));
    const next = expr.getNextOccurrence(after);
    assert.equal(next.getTime(), Date.UTC(2026, 6, 9, 6, 30, 0));
  });

  it('starts one minute after `after` (never returns `after` itself)', () => {
    const expr = CronExpression.parse('* * * * *'); // every minute
    const after = new Date(Date.UTC(2026, 6, 8, 6, 30, 30)); // 06:30:30
    const next = expr.getNextOccurrence(after);
    // after+1min truncated to the minute = 06:31:00.
    assert.equal(next.getTime(), Date.UTC(2026, 6, 8, 6, 31, 0));
  });
});

describe('CronExpression parse errors', () => {
  it('rejects a non-positive step, out-of-range values, and inverted ranges', () => {
    assert.throws(() => CronExpression.parse('*/0 * * * *'), /positive integer/);
    assert.throws(() => CronExpression.parse('99 * * * *'), /out of range/);
    assert.throws(() => CronExpression.parse('5-1 * * * *'), /out of range/);
  });
});
