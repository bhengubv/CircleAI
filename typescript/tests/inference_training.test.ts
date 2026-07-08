// inference_training.test.ts
//
// Exercises FeedbackTrainingQueue (FIFO drain + remainder rewrite) and
// NightlyAdapterTrainer (min-batch gate, train/save/apply, re-queue on
// unsupported training).

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  FeedbackTrainingQueue,
  type TrainingSample,
} from '../src/inference/feedback_training';
import {
  NightlyAdapterTrainer,
  InMemoryLoRAAdapterManager,
  charTokenizer,
  TrainingNotSupportedError,
} from '../src/inference/nightly_trainer';

function sample(user: string, assistant: string, preferred: string, polarity: number): TrainingSample {
  return { userText: user, assistantText: assistant, preferredText: preferred, polarity, atUtc: '2026-01-01T00:00:00.000Z' };
}

describe('FeedbackTrainingQueue', () => {
  it('enqueues, counts, and drains FIFO leaving the remainder', async () => {
    const q = new FeedbackTrainingQueue();
    for (let i = 0; i < 5; i++) await q.enqueue(sample(`u${i}`, `a${i}`, `p${i}`, 1));
    assert.equal(q.pending, 5);

    const first = await q.drain(2);
    assert.equal(first.length, 2);
    assert.equal(first[0]!.userText, 'u0');
    assert.equal(first[1]!.userText, 'u1');
    assert.equal(q.pending, 3);

    const rest = await q.drain(100);
    assert.equal(rest.length, 3);
    assert.equal(rest[0]!.userText, 'u2');
    assert.equal(q.pending, 0);
  });

  it('rejects a non-positive drain count', async () => {
    const q = new FeedbackTrainingQueue();
    await assert.rejects(() => q.drain(0));
  });
});

describe('charTokenizer', () => {
  it('maps chars to UTF-16 code units', () => {
    assert.deepEqual(charTokenizer('AB'), [65, 66]);
    assert.deepEqual(charTokenizer(''), []);
  });
});

describe('NightlyAdapterTrainer', () => {
  it('skips when pending < minBatchSize', async () => {
    const q = new FeedbackTrainingQueue();
    const adapter = new InMemoryLoRAAdapterManager();
    const trainer = new NightlyAdapterTrainer(q, adapter, { minBatchSize: 3 });
    await q.enqueue(sample('u', 'a', 'p', 1));
    await trainer.runOnce();
    assert.equal(adapter.stepCount, 0);
    assert.equal(q.pending, 1); // untouched
  });

  it('trains, saves, and applies when the batch is large enough', async () => {
    const q = new FeedbackTrainingQueue();
    const adapter = new InMemoryLoRAAdapterManager();
    const trainer = new NightlyAdapterTrainer(q, adapter, { minBatchSize: 2, adapterPath: 'a.mnn' });
    await q.enqueue(sample('hi', 'hello', 'hey', 1));
    await q.enqueue(sample('bye', 'goodbye', 'later', -1));
    await trainer.runOnce();
    assert.equal(adapter.stepCount, 2);
    assert.equal(adapter.currentAdapter, 'a.mnn');
    assert.equal(q.pending, 0);
  });

  it('re-queues every sample and bails when training is unsupported', async () => {
    const q = new FeedbackTrainingQueue();
    const adapter = new InMemoryLoRAAdapterManager();
    adapter.trainingSupported = false;
    const trainer = new NightlyAdapterTrainer(q, adapter, { minBatchSize: 2 });
    await q.enqueue(sample('a', 'b', 'c', 1));
    await q.enqueue(sample('d', 'e', 'f', 1));
    await trainer.runOnce();
    assert.equal(adapter.stepCount, 0);
    assert.equal(q.pending, 2); // re-queued
  });

  it('adapter.trainStep throws TrainingNotSupportedError when disabled', () => {
    const adapter = new InMemoryLoRAAdapterManager();
    adapter.trainingSupported = false;
    assert.throws(() => adapter.trainStep([1], [2]), TrainingNotSupportedError);
  });
});
