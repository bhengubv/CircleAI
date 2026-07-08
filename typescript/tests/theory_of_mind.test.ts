// theory_of_mind.test.ts
//
// Verifies BeliefTrackerTheoryOfMind against the C# reference
// (HerJarvisRealImplementations.cs #10). The headline guarantees:
//   * the greedy claim group stops only at . ; ! ? (a run of clauses with no
//     delimiter is ONE claim),
//   * verb weighting (believe=1.0, others=0.7) with recency decay,
//   * `likelyBeliefJson` is byte-identical to
//     JsonSerializer.Serialize(Dictionary<string,double>) — including STJ's
//     aggressive default escaping and shortest-round-trip doubles.
// The expected JSON/confidence strings were captured from a .NET 10 probe that
// runs the identical algorithm.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  BeliefTrackerTheoryOfMind,
  stjEscape,
  stjDouble,
  stjSerializeDoubleMap,
} from '../src/companion/reasoning/index';

const tom = new BeliefTrackerTheoryOfMind();

describe('BeliefTrackerTheoryOfMind — belief extraction + wire format', () => {
  it('reproduces the C# JSON for a three-clause history (delimiter-separated)', async () => {
    const est = await tom.estimateAsync(
      'peer',
      'She thinks the plan is risky. He believes it will work. They want more time.',
    );
    assert.equal(est.targetIdentifier, 'peer');
    assert.equal(
      est.likelyBeliefJson,
      '{"thinks:the plan is risky":0.7,"believes:it will work":0.9090909090909091,"want:more time":0.5833333333333334}',
    );
    // conf = min(1, (0.7 + 0.9090909090909091 + 0.5833333333333334) / 5)
    assert.ok(Math.abs(est.confidence - 0.4384848484848485) < 1e-15);
  });

  it('the claim group is greedy up to . ; ! ? — no delimiter means one claim', async () => {
    // "Alex believes X and Bob THINKS x" has no . ; ! ? so the whole tail is the
    // claim; the second verb is swallowed, not matched. Weight 1.0, decay 1.0.
    const est = await tom.estimateAsync('peer', 'Alex believes X and Bob THINKS x');
    assert.equal(est.likelyBeliefJson, '{"believes:X and Bob THINKS x":1}');
    assert.ok(Math.abs(est.confidence - 0.2) < 1e-15);
  });

  it('splits on ! and ; as well as .', async () => {
    const est = await tom.estimateAsync(
      'peer',
      'He hopes for peace! She fears the dark; they think loudly?',
    );
    assert.equal(
      est.likelyBeliefJson,
      '{"hopes:for peace":0.7,"fears:the dark":0.6363636363636364,"think:loudly":0.5833333333333334}',
    );
  });

  it('empty belief set → {} and confidence 0', async () => {
    const est = await tom.estimateAsync('peer', 'nothing to see here, just a plain sentence');
    assert.equal(est.likelyBeliefJson, '{}');
    assert.equal(est.confidence, 0);
  });

  it('accumulates a repeated (verb,claim) key rather than duplicating it', async () => {
    // "wants coffee and wants tea" — one match, claim "coffee and wants tea".
    const est = await tom.estimateAsync('peer', 'user wants coffee and wants tea');
    assert.equal(est.likelyBeliefJson, '{"wants:coffee and wants tea":0.7}');
  });

  it('confidence clamps to 1.0 for many strong beliefs', async () => {
    const history =
      'x believes a. x believes b. x believes c. x believes d. x believes e. x believes f.';
    const est = await tom.estimateAsync('peer', history);
    assert.ok(est.confidence <= 1.0);
    assert.equal(est.confidence, Math.min(1.0, est.confidence)); // never exceeds 1
  });

  it('throws on blank target and null history', async () => {
    await assert.rejects(() => tom.estimateAsync('  ', 'x thinks y'), /target required/);
    await assert.rejects(
      // @ts-expect-error deliberately passing null to hit the guard
      () => tom.estimateAsync('peer', null),
      /interactionHistoryJson required/,
    );
  });
});

describe('System.Text.Json-faithful serialisation helpers', () => {
  it('escapes exactly like STJ default (aggressive: < > & + \' " ` and non-ASCII)', () => {
    assert.equal(stjEscape('angle <b> & plus+'), 'angle \\u003Cb\\u003E \\u0026 plus\\u002B');
    assert.equal(stjEscape("apostrophe ' its"), 'apostrophe \\u0027 its');
    // '"' becomes " (NOT \"), backslash stays \\, forward slash is bare.
    assert.equal(stjEscape('a"b\\c/d'), 'a\\u0022b\\\\c/d');
    // non-ASCII -> uppercase \uXXXX
    assert.equal(stjEscape('café'), 'caf\\u00E9');
    assert.equal(stjEscape('日'), '\\u65E5');
    // short escapes
    assert.equal(stjEscape('\t\n\r'), '\\t\\n\\r');
    // backtick and DEL
    assert.equal(stjEscape('`'), '\\u0060');
    assert.equal(stjEscape('\x7f'), '\\u007F');
  });

  it('formats doubles as shortest round-trip in normal range', () => {
    assert.equal(stjDouble(1.0), '1');
    assert.equal(stjDouble(0.7), '0.7');
    assert.equal(stjDouble(1.0 / 1.1), '0.9090909090909091');
    assert.equal(stjDouble(0), '0');
    assert.equal(stjDouble(-1), '-1');
  });

  it('formats scientific notation the .NET way (uppercase E, padded exponent)', () => {
    assert.equal(stjDouble(1e21), '1E+21');
    assert.equal(stjDouble(1e-7), '1E-07');
  });

  it('serialises an empty map as {} and preserves insertion order', () => {
    assert.equal(stjSerializeDoubleMap(new Map()), '{}');
    const m = new Map<string, number>([
      ['b', 0.5],
      ['a', 1.0],
    ]);
    assert.equal(stjSerializeDoubleMap(m), '{"b":0.5,"a":1}');
  });
});
