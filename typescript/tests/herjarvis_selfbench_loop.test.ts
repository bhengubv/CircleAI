// herjarvis_selfbench_loop.test.ts
//
// Verifies SelfBenchSelfImprovementLoop (SelfBenchSelfImprovementLoop.cs). The
// SelfBench surface is injected as fakes: an empty suite is skipped; a promoting
// verdict invokes onPromote + records the best score; a rejecting verdict does
// not.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  SelfBenchSelfImprovementLoop,
  defaultRegressionGateConfig,
  type IBenchSuiteRegistry,
  type IAbBenchRunner,
  type AbVerdict,
  type BenchTask,
  type IAIService,
} from '../src/companion/herjarvis/index';

function registry(tasksById: Record<string, readonly BenchTask[]>): IBenchSuiteRegistry {
  return { get: (suiteId) => tasksById[suiteId] ?? [] };
}

function runnerReturning(verdict: AbVerdict): IAbBenchRunner {
  return { compareAsync: async () => verdict };
}

const svc: IAIService = { id: 'svc' };
const factory = async () => svc;

describe('SelfBenchSelfImprovementLoop', () => {
  it('skips an empty suite', async () => {
    const loop = new SelfBenchSelfImprovementLoop(
      registry({}),
      runnerReturning({ shouldPromote: true, candidateSummary: { meanScore: 1 }, reason: 'x' }),
      factory,
      factory,
    );
    const v = await loop.cycleAsync('missing');
    assert.deepEqual(v, { improvementsApplied: 'skipped: no tasks in suite', newBenchScore: 0.0 });
  });

  it('promotes when the verdict says so, invoking onPromote + recording best', async () => {
    let promoted: AbVerdict | null = null;
    const verdict: AbVerdict = {
      shouldPromote: true,
      candidateSummary: { meanScore: 0.82 },
      reason: 'mean +0.05',
    };
    const loop = new SelfBenchSelfImprovementLoop(
      registry({ suite: [{ id: 't1', prompt: 'p' }] }),
      runnerReturning(verdict),
      factory,
      factory,
      async (v) => {
        promoted = v;
      },
      defaultRegressionGateConfig(),
    );
    const v = await loop.cycleAsync('suite');
    assert.equal(v.improvementsApplied, 'promoted candidate (mean +0.05)');
    assert.equal(v.newBenchScore, 0.82);
    assert.ok(promoted);
    assert.equal(loop.bestScoreFor('suite'), 0.82);
  });

  it('rejects when the verdict declines, leaving best score untouched', async () => {
    const verdict: AbVerdict = {
      shouldPromote: false,
      candidateSummary: { meanScore: 0.3 },
      reason: 'below threshold',
    };
    const loop = new SelfBenchSelfImprovementLoop(
      registry({ suite: [{ id: 't1', prompt: 'p' }] }),
      runnerReturning(verdict),
      factory,
      factory,
    );
    const v = await loop.cycleAsync('suite');
    assert.equal(v.improvementsApplied, 'rejected (below threshold)');
    assert.equal(v.newBenchScore, 0.3);
    assert.equal(loop.bestScoreFor('suite'), 0);
  });

  it('defaults a blank suite id to "default"', async () => {
    let seenSuite = '';
    const runner: IAbBenchRunner = {
      compareAsync: async (suiteId) => {
        seenSuite = suiteId;
        return { shouldPromote: false, candidateSummary: { meanScore: 0 }, reason: 'r' };
      },
    };
    const loop = new SelfBenchSelfImprovementLoop(
      registry({ default: [{ id: 't', prompt: 'p' }] }),
      runner,
      factory,
      factory,
    );
    await loop.cycleAsync('   ');
    assert.equal(seenSuite, 'default');
  });

  it('rejects null collaborators in the constructor', () => {
    // @ts-expect-error deliberate null registry
    assert.throws(() => new SelfBenchSelfImprovementLoop(null, runnerReturning({} as AbVerdict), factory, factory), /registry required/);
  });
});
