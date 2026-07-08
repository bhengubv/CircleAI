// feedback_analyser.test.ts
//
// Exercises FeedbackAnalyser (persona-adaptation deltas from a window of
// signals) and the InMemoryFeedbackStore added to stores.ts. Mirrors the C#
// FeedbackAnalyser rules and CircleAI.Tests.FeedbackStoreTests.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { FeedbackAnalyser } from '../src/memory/feedback_analyser';
import { InMemoryFeedbackStore } from '../src/memory/stores';
import { FeedbackPolarity, type FeedbackSignal } from '../src/memory/index';

// FP32-narrowed deltas — must equal the C# `float` literals exactly.
const VERBOSITY_DOWN = Math.fround(-0.1);
const VERBOSITY_UP = Math.fround(0.05);

let seq = 0;
function make(
  polarity: FeedbackPolarity,
  at?: Date,
  user = 'user',
): FeedbackSignal {
  // Monotonic default timestamps so window ordering is deterministic per call.
  return {
    id: crypto.randomUUID(),
    recordedAtUtc: at ?? new Date(1_700_000_000_000 + seq++ * 1000),
    userText: user,
    assistantText: 'response',
    polarity,
  };
}

// ══════════════════════════════════════════════════════════════════════════
// FeedbackAnalyser
// ══════════════════════════════════════════════════════════════════════════

describe('FeedbackAnalyser', () => {
  it('rejects a window size below 1', () => {
    assert.throws(() => new FeedbackAnalyser(0), RangeError);
  });

  it('returns zero deltas for an empty signal set', () => {
    const a = new FeedbackAnalyser().analyse([]);
    assert.equal(a.verbosityDelta, 0);
    assert.equal(a.formalityDelta, 0);
    assert.deepEqual(a.preferredTopics, []);
  });

  it('drops verbosity by -0.1 when > 70% of the window is negative', () => {
    const analyser = new FeedbackAnalyser();
    // 8 negative + 2 positive = 80% negative.
    const signals: FeedbackSignal[] = [];
    for (let i = 0; i < 8; i++) signals.push(make(FeedbackPolarity.Negative));
    for (let i = 0; i < 2; i++) signals.push(make(FeedbackPolarity.Positive));

    const a = analyser.analyse(signals);
    assert.equal(a.verbosityDelta, VERBOSITY_DOWN);
    assert.equal(a.formalityDelta, 0);
    assert.deepEqual(a.preferredTopics, []);
  });

  it('raises verbosity by +0.05 when > 70% of the window is positive', () => {
    const analyser = new FeedbackAnalyser();
    const signals: FeedbackSignal[] = [];
    for (let i = 0; i < 8; i++) signals.push(make(FeedbackPolarity.Positive));
    for (let i = 0; i < 2; i++) signals.push(make(FeedbackPolarity.Negative));

    const a = analyser.analyse(signals);
    assert.equal(a.verbosityDelta, VERBOSITY_UP);
  });

  it('leaves verbosity at 0 for a balanced window', () => {
    const analyser = new FeedbackAnalyser();
    const signals: FeedbackSignal[] = [];
    for (let i = 0; i < 5; i++) signals.push(make(FeedbackPolarity.Positive));
    for (let i = 0; i < 5; i++) signals.push(make(FeedbackPolarity.Negative));

    assert.equal(analyser.analyse(signals).verbosityDelta, 0);
  });

  it('treats exactly 70% as NOT crossing the threshold (strict >)', () => {
    const analyser = new FeedbackAnalyser(10);
    // Exactly 7/10 negative — 0.70 is not > 0.70.
    const signals: FeedbackSignal[] = [];
    for (let i = 0; i < 7; i++) signals.push(make(FeedbackPolarity.Negative));
    for (let i = 0; i < 3; i++) signals.push(make(FeedbackPolarity.Positive));

    assert.equal(analyser.analyse(signals).verbosityDelta, 0);
  });

  it('only considers the most-recent windowSize signals (newest-first)', () => {
    const analyser = new FeedbackAnalyser(3);
    // Older bulk is positive; the 3 newest are negative → window is 100% negative.
    const older: FeedbackSignal[] = [];
    for (let i = 0; i < 10; i++)
      older.push(make(FeedbackPolarity.Positive, new Date(1000 + i)));
    const newest: FeedbackSignal[] = [];
    for (let i = 0; i < 3; i++)
      newest.push(make(FeedbackPolarity.Negative, new Date(9_000_000 + i)));

    const a = analyser.analyse([...older, ...newest]);
    assert.equal(a.verbosityDelta, VERBOSITY_DOWN);
  });

  it('ignores Correction signals in the ratio (neither positive nor negative)', () => {
    const analyser = new FeedbackAnalyser();
    // 8 negative + 2 correction = 8/10 = 80% negative → down.
    const signals: FeedbackSignal[] = [];
    for (let i = 0; i < 8; i++) signals.push(make(FeedbackPolarity.Negative));
    for (let i = 0; i < 2; i++) signals.push(make(FeedbackPolarity.Correction));
    assert.equal(analyser.analyse(signals).verbosityDelta, VERBOSITY_DOWN);
  });

  it('rejects a null signal set', () => {
    assert.throws(() => new FeedbackAnalyser().analyse(null as unknown as FeedbackSignal[]));
  });
});

// ══════════════════════════════════════════════════════════════════════════
// InMemoryFeedbackStore (mirrors CircleAI.Tests.InMemoryFeedbackStoreTests)
// ══════════════════════════════════════════════════════════════════════════

describe('InMemoryFeedbackStore', () => {
  it('rejects a null signal', async () => {
    const store = new InMemoryFeedbackStore();
    await assert.rejects(() => store.addAsync(null as unknown as FeedbackSignal));
  });

  it('add increments the count', async () => {
    const store = new InMemoryFeedbackStore();
    await store.addAsync(make(FeedbackPolarity.Positive));
    assert.equal(await store.countAsync(), 1);
  });

  it('getRecent on an empty store returns empty', async () => {
    const store = new InMemoryFeedbackStore();
    assert.deepEqual(await store.getRecentAsync(10), []);
  });

  it('getRecent returns newest-first', async () => {
    const store = new InMemoryFeedbackStore();
    const now = Date.now();
    await store.addAsync(make(FeedbackPolarity.Positive, new Date(now - 600_000), 'old'));
    await store.addAsync(make(FeedbackPolarity.Negative, new Date(now), 'new'));

    const result = await store.getRecentAsync(10);
    assert.equal(result.length, 2);
    assert.equal(result[0].userText, 'new');
  });

  it('positiveRatio returns null with no signals', async () => {
    const store = new InMemoryFeedbackStore();
    assert.equal(await store.positiveRatioAsync(), null);
  });

  it('positiveRatio returns 1.0 when all positive', async () => {
    const store = new InMemoryFeedbackStore();
    await store.addAsync(make(FeedbackPolarity.Positive));
    await store.addAsync(make(FeedbackPolarity.Positive));
    assert.equal(await store.positiveRatioAsync(), 1.0);
  });

  it('positiveRatio returns the right fraction for mixed signals', async () => {
    const store = new InMemoryFeedbackStore();
    await store.addAsync(make(FeedbackPolarity.Positive));
    await store.addAsync(make(FeedbackPolarity.Positive));
    await store.addAsync(make(FeedbackPolarity.Negative));
    const ratio = await store.positiveRatioAsync();
    assert.ok(ratio !== null && ratio > 0.66 && ratio < 0.68); // 2/3
  });

  it('evicts the oldest when maxSignals is exceeded (FIFO)', async () => {
    const store = new InMemoryFeedbackStore(3);
    for (let i = 0; i < 5; i++) await store.addAsync(make(FeedbackPolarity.Positive, undefined, `u${i}`));
    assert.equal(await store.countAsync(), 3);
  });

  it('rejects a non-positive maxSignals', () => {
    assert.throws(() => new InMemoryFeedbackStore(0), RangeError);
  });
});
