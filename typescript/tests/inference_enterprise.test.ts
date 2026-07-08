// inference_enterprise.test.ts
//
// Exercises the enterprise tier: RoundRobinTenantRouter, InMemoryBatchScheduler,
// EvenSplitModelShardPlanner, PolicyCrossTierOffload, and the Null* defaults.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  ServerTier,
  RoundRobinTenantRouter,
  InMemoryBatchScheduler,
  EvenSplitModelShardPlanner,
  PolicyCrossTierOffload,
  NullTenantRouter,
  NullBatchScheduler,
  NullModelShardPlanner,
  NullCrossTierOffload,
  type TenantContext,
  type TenantQuota,
} from '../src/inference/server/enterprise';

const tenant: TenantContext = { tenantId: 't1' };

describe('RoundRobinTenantRouter', () => {
  it('round-robins across registered nodes per model', async () => {
    const r = new RoundRobinTenantRouter();
    r.registerNode('m', 'n1');
    r.registerNode('m', 'n2');
    r.registerNode('m', 'n2'); // dedupe
    assert.equal(await r.chooseNode(tenant, 'm'), 'n1');
    assert.equal(await r.chooseNode(tenant, 'm'), 'n2');
    assert.equal(await r.chooseNode(tenant, 'm'), 'n1'); // wraps
  });

  it('returns null for an unknown model', async () => {
    const r = new RoundRobinTenantRouter();
    assert.equal(await r.chooseNode(tenant, 'unknown'), null);
  });

  it('stores and retrieves tenant quotas', async () => {
    const r = new RoundRobinTenantRouter();
    const quota: TenantQuota = {
      tenantId: 't1',
      maxConcurrentRequests: 4,
      maxModelsLoaded: 2,
      maxBytesInFlight: 1024,
      dailyTokenBudget: 100000,
    };
    await r.setQuota(quota);
    assert.deepEqual(await r.getQuota('t1'), quota);
    assert.equal(await r.getQuota('nobody'), null);
  });
});

describe('InMemoryBatchScheduler', () => {
  it('reserves a slot with a future deadline and releases it', async () => {
    const s = new InMemoryBatchScheduler();
    const before = Date.now();
    const slot = await s.reserve('m', 100, 5000);
    assert.equal(slot.modelId, 'm');
    assert.equal(slot.tokens, 100);
    assert.ok(slot.slotId.startsWith('slot-'));
    assert.ok(new Date(slot.deadlineUtc).getTime() >= before + 5000 - 50);
    await s.release(slot); // no throw
  });

  it('validates arguments', async () => {
    const s = new InMemoryBatchScheduler();
    await assert.rejects(() => s.reserve('m', 0, 100));
    await assert.rejects(() => s.reserve('m', 10, 0));
    await assert.rejects(() => s.reserve('', 10, 100));
  });
});

describe('EvenSplitModelShardPlanner', () => {
  it('splits param bytes into even buckets, front-loading the remainder', async () => {
    const planner = new EvenSplitModelShardPlanner(() => ['a', 'b', 'c']);
    const shards = await planner.plan('m', 10, undefined);
    assert.equal(shards.length, 3);
    // 10 / 3 = 3 rem 1 -> sizes 4,3,3 -> ranges [0,4),[4,7),[7,10)
    assert.deepEqual(shards.map((s) => [s.rangeStart, s.rangeEnd]), [
      [0, 4],
      [4, 7],
      [7, 10],
    ]);
    assert.deepEqual(shards.map((s) => s.nodeId), ['a', 'b', 'c']);
    assert.equal(shards[0]!.shardId, 'shard-m-0');
  });

  it('returns no shards when there are no nodes', async () => {
    const planner = new EvenSplitModelShardPlanner(() => []);
    assert.deepEqual(await planner.plan('m', 100), []);
  });

  it('validates arguments', async () => {
    const planner = new EvenSplitModelShardPlanner(() => ['a']);
    await assert.rejects(() => planner.plan('', 10));
    await assert.rejects(() => planner.plan('m', 0));
  });
});

describe('PolicyCrossTierOffload', () => {
  it('offloads when the prompt exceeds the local ceiling', async () => {
    const o = new PolicyCrossTierOffload(2048, 'farm-1');
    const d = await o.shouldOffload('m', 3000, ServerTier.SingleNode);
    assert.equal(d.shouldOffload, true);
    assert.equal(d.targetNodeId, 'farm-1');
  });

  it('keeps small prompts local', async () => {
    const o = new PolicyCrossTierOffload(2048);
    const d = await o.shouldOffload('m', 100, ServerTier.Server);
    assert.equal(d.shouldOffload, false);
    assert.equal(d.reason, 'Prompt fits locally');
  });

  it('never offloads from the top tier', async () => {
    const o = new PolicyCrossTierOffload(10);
    const d = await o.shouldOffload('m', 9999, ServerTier.ServerFarm);
    assert.equal(d.shouldOffload, false);
    assert.equal(d.reason, 'Caller is already top-tier');
  });
});

describe('Null enterprise defaults', () => {
  it('all decline / return empty as single-node fallbacks', async () => {
    assert.equal(await NullTenantRouter.instance.chooseNode(tenant, 'm'), null);
    assert.equal(await NullTenantRouter.instance.getQuota('t'), null);
    const slot = await NullBatchScheduler.instance.reserve('m', 5, 100);
    assert.equal(slot.slotId, '00000000-0000-0000-0000-000000000000');
    assert.deepEqual(await NullModelShardPlanner.instance.plan('m', 100), []);
    const off = await NullCrossTierOffload.instance.shouldOffload('m', 9999, ServerTier.SingleNode);
    assert.equal(off.shouldOffload, false);
  });
});
