// predictive_engine.test.ts
//
// Verifies the two IPredictiveEngine implementations against the C# reference:
//   HistogramPredictiveEngine (HerJarvisRealImplementations.cs #14) and
//   SequencePredictiveEngine  (SequencePredictiveEngine.cs).
//
// Both engines read the wall clock (DateTimeOffset.UtcNow) for "now", so the
// tests are written to be clock-independent: histogram tests anchor observed
// events to the CURRENT slot (which the m=0 horizon sample always hits), and
// sequence tests assert count-derived probabilities exactly while allowing a
// tolerance on the forecast timestamp.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  HistogramPredictiveEngine,
  SequencePredictiveEngine,
} from '../src/companion/reasoning/index';

describe('HistogramPredictiveEngine', () => {
  it('anticipates a need whose slot falls inside the horizon (prob = share)', async () => {
    const eng = new HistogramPredictiveEngine();
    const now = new Date();
    // Two hits at the current slot, one at a far slot (3.5 days out).
    eng.observe('coffee', now);
    eng.observe('coffee', now);
    eng.observe('coffee', new Date(now.getTime() + 84 * 3600 * 1000)); // ~3.5 days
    // Sub-30-min horizon → the sampling loop runs only m=0 (the current slot).
    // A 60-min horizon samples m=0 AND m=30, which land in the SAME hour slot
    // whenever the wall-clock minute is < 30, double-counting the current slot
    // (2→4) and making the C#-faithful result clock-minute-dependent (2/3 when
    // minute≥30, 4/3 when minute<30). The engine matches C# exactly; the
    // reference algorithm carries that latent clock-dependence, so the test
    // pins the deterministic sub-30 case.
    const needs = await eng.anticipateAsync(20); // only m=0 samples the current slot
    const coffee = needs.find((n) => n.description === 'coffee');
    assert.ok(coffee, 'coffee should be anticipated');
    // total = 3; upcoming = the 2 current-slot hits (far one not sampled) → 2/3.
    assert.equal(coffee!.probability, 2 / 3);
  });

  it('skips a need with zero occurrences inside the horizon', async () => {
    const eng = new HistogramPredictiveEngine();
    const now = new Date();
    // Put the only occurrence ~3.5 days out, far from any 30-min sample of a
    // 60-min horizon → not anticipated.
    eng.observe('gym', new Date(now.getTime() + 84 * 3600 * 1000));
    const needs = await eng.anticipateAsync(60);
    assert.ok(!needs.some((n) => n.description === 'gym'));
  });

  it('preserves the original description surface form (case-insensitive key)', async () => {
    const eng = new HistogramPredictiveEngine();
    const now = new Date();
    eng.observe('Coffee', now);
    eng.observe('coffee', now); // merges onto the first surface form
    const needs = await eng.anticipateAsync(60);
    const hit = needs.find((n) => n.description.toLowerCase() === 'coffee');
    assert.ok(hit);
    assert.equal(hit!.description, 'Coffee'); // first-seen form wins
  });

  it('orders results by descending probability', async () => {
    const eng = new HistogramPredictiveEngine();
    const now = new Date();
    // 'a' present once at current slot (prob 1). 'b' present at current slot
    // once plus far once (prob 0.5). So a should sort before b.
    eng.observe('a', now);
    eng.observe('b', now);
    eng.observe('b', new Date(now.getTime() + 84 * 3600 * 1000));
    const needs = await eng.anticipateAsync(60);
    const probs = needs.map((n) => n.probability);
    for (let i = 1; i < probs.length; i++) assert.ok(probs[i - 1] >= probs[i]);
    assert.equal(needs[0].description, 'a');
  });

  it('rejects a non-positive horizon and a blank description', async () => {
    const eng = new HistogramPredictiveEngine();
    await assert.rejects(() => eng.anticipateAsync(0), /horizonMinutes out of range/);
    assert.throws(() => eng.observe('  ', new Date()), /description required/);
  });
});

describe('SequencePredictiveEngine', () => {
  it('predicts the next event from the n-gram context with back-off weighting', async () => {
    const eng = new SequencePredictiveEngine(3);
    const base = new Date('2026-01-01T00:00:00Z').getTime();
    // Deterministic sequence: wake -> coffee -> email, wake -> coffee -> email.
    // After the second "coffee", the context {wake,coffee} strongly predicts
    // "email".
    const seq = ['wake', 'coffee', 'email', 'wake', 'coffee'];
    seq.forEach((e, i) => eng.observe(e, new Date(base + i * 3600 * 1000)));
    const needs = await eng.anticipateAsync(24 * 60);
    assert.ok(needs.length > 0);
    assert.equal(needs[0].description, 'email');
    // Probabilities are a normalised distribution → sum to ~1 over the emitted
    // (in-horizon) events; each is in (0,1].
    for (const n of needs) assert.ok(n.probability > 0 && n.probability <= 1);
  });

  it('forecasts arrival from the mean inter-arrival interval', async () => {
    const eng = new SequencePredictiveEngine(3);
    const base = new Date('2026-01-01T00:00:00Z').getTime();
    // Same event repeated at a fixed 10-minute cadence → mean interval 600s.
    // (Inter-arrival is only tracked when the immediately preceding event is
    // the same event, so a pure repeat sequence exercises it.)
    for (let i = 0; i < 4; i++) eng.observe('ping', new Date(base + i * 10 * 60 * 1000));
    const before = Date.now();
    const needs = await eng.anticipateAsync(60); // horizon 3600s > 600s interval
    const after = Date.now();
    const ping = needs.find((n) => n.description === 'ping');
    assert.ok(ping, 'ping should be anticipated within the horizon');
    // expectedByUtc ≈ now + 600s. Allow the wall-clock window between our two
    // Date.now() reads plus a small slack.
    const expectedLo = before + 600 * 1000 - 50;
    const expectedHi = after + 600 * 1000 + 50;
    const got = ping!.expectedByUtc.getTime();
    assert.ok(got >= expectedLo && got <= expectedHi, `forecast ${got} not in [${expectedLo},${expectedHi}]`);
  });

  it('drops events whose mean interval exceeds the horizon', async () => {
    const eng = new SequencePredictiveEngine(3);
    const base = new Date('2026-01-01T00:00:00Z').getTime();
    // 'slow' repeats every 2 hours (7200s) → exceeds a 60-min (3600s) horizon.
    for (let i = 0; i < 3; i++) eng.observe('slow', new Date(base + i * 2 * 3600 * 1000));
    const needs = await eng.anticipateAsync(60);
    assert.ok(!needs.some((n) => n.description === 'slow'));
  });

  it('returns [] before any events are observed', async () => {
    const eng = new SequencePredictiveEngine();
    assert.deepEqual(await eng.anticipateAsync(60), []);
  });

  it('rejects an out-of-range order and a non-positive horizon', async () => {
    assert.throws(() => new SequencePredictiveEngine(0), /order out of range/);
    assert.throws(() => new SequencePredictiveEngine(7), /order out of range/);
    const eng = new SequencePredictiveEngine();
    await assert.rejects(() => eng.anticipateAsync(-5), /horizonMinutes out of range/);
    assert.throws(() => eng.observe('', new Date()), /event required/);
  });
});
