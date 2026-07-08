// inference_runtime.test.ts
//
// Exercises PowerBudgetPolicy, KvCompression apply, ContextWindowBudgetManager,
// and PrefixCacheService.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { PowerBudget } from '../src/inference/index';
import {
  resolvePowerBudget,
  KvCompressionMode,
  KvCompressionApplyResult,
  InMemoryKvCompressionController,
} from '../src/inference/power_budget';
import { ContextWindowBudgetManager } from '../src/inference/context_budget';
import { PrefixCacheService, InMemoryPrefixCacheStore } from '../src/inference/prefix_cache';

describe('resolvePowerBudget', () => {
  it('caps Low at 64 tokens and prefers a smaller model', () => {
    const r = resolvePowerBudget(PowerBudget.Low, 1000);
    assert.equal(r.maxTokens, 64);
    assert.equal(r.preferredKvMode, KvCompressionMode.TurboQuant4Bit);
    assert.equal(r.preferSmallerModelInChain, true);
  });

  it('caps Normal at 512 and High at 2048 (with FP16 KV)', () => {
    assert.equal(resolvePowerBudget(PowerBudget.Normal, 5000).maxTokens, 512);
    const high = resolvePowerBudget(PowerBudget.High, 5000);
    assert.equal(high.maxTokens, 2048);
    assert.equal(high.preferredKvMode, KvCompressionMode.Off);
  });

  it('honours requested tokens under None', () => {
    assert.equal(resolvePowerBudget(PowerBudget.None, 33).maxTokens, 33);
  });

  it('auto-downgrades Normal to Low below 15% battery', () => {
    const r = resolvePowerBudget(PowerBudget.Normal, 1000, 10);
    assert.equal(r.maxTokens, 64);
    assert.equal(r.preferSmallerModelInChain, true);
  });

  it('auto-downgrades High to Normal when thermally throttled', () => {
    const r = resolvePowerBudget(PowerBudget.High, 5000, null, true);
    assert.equal(r.maxTokens, 512);
    assert.equal(r.preferredKvMode, KvCompressionMode.TurboQuant4Bit);
  });
});

describe('InMemoryKvCompressionController', () => {
  it('applies a valid mode and reads it back', () => {
    const c = new InMemoryKvCompressionController();
    assert.equal(c.set(KvCompressionMode.TurboQuant3Bit), KvCompressionApplyResult.Applied);
    assert.equal(c.get(), KvCompressionMode.TurboQuant3Bit);
  });

  it('rejects an out-of-range mode', () => {
    const c = new InMemoryKvCompressionController();
    assert.equal(c.set(99 as KvCompressionMode), KvCompressionApplyResult.InvalidMode);
    assert.equal(c.get(), KvCompressionMode.Off); // unchanged
  });
});

describe('ContextWindowBudgetManager', () => {
  it('tracks usage, fill ratio, and eviction signal', () => {
    const m = new ContextWindowBudgetManager(100, 0.85);
    m.recordExchange(40, 40);
    assert.equal(m.used, 80);
    assert.equal(m.remainingTokens, 20);
    assert.ok(Math.abs(m.fillRatio - 0.8) < 1e-9);
    assert.equal(m.shouldEvict, false);
    m.recordExchange(10, 0);
    assert.equal(m.shouldEvict, true);
  });

  it('calculates eviction count back to the target fill', () => {
    const m = new ContextWindowBudgetManager(100);
    m.recordExchange(90, 0);
    assert.equal(m.calculateEvictionCount(0.5), 40); // 90 - 50
    assert.equal(m.calculateEvictionCount(0.95), 0); // already below
  });

  it('validates constructor + method arguments', () => {
    assert.throws(() => new ContextWindowBudgetManager(0));
    assert.throws(() => new ContextWindowBudgetManager(10, 2));
    const m = new ContextWindowBudgetManager(10);
    assert.throws(() => m.recordExchange(-1, 0));
    assert.throws(() => m.calculateEvictionCount(2));
  });

  it('reset zeroes the used counter', () => {
    const m = new ContextWindowBudgetManager(100);
    m.recordExchange(50, 0);
    m.reset();
    assert.equal(m.used, 0);
  });
});

describe('PrefixCacheService', () => {
  it('keys on (modelId, systemPrompt) and is stable', () => {
    const k1 = PrefixCacheService.keyFor('model-a', 'you are helpful');
    const k2 = PrefixCacheService.keyFor('model-a', 'you are helpful');
    assert.equal(k1, k2);
    assert.ok(k1 && /^[0-9a-f]{16}_[0-9a-f]{16}$/.test(k1));
  });

  it('returns null without a system prompt', () => {
    assert.equal(PrefixCacheService.keyFor('m', null), null);
    assert.equal(PrefixCacheService.keyFor('m', ''), null);
    assert.equal(PrefixCacheService.keyFor('', 'sys'), null);
  });

  it('writes, detects, and evicts entries under the cap', async () => {
    const store = new InMemoryPrefixCacheStore();
    const cache = new PrefixCacheService('/cache', store);
    const key = PrefixCacheService.keyFor('m', 'sys')!;
    assert.equal(await cache.hasEntry(key), false);
    await cache.writeEntry(key);
    assert.equal(await cache.hasEntry(key), true);
    // Eviction under the 500MB cap is a no-op for a tiny entry.
    await cache.evictIfNeeded();
    assert.equal(await cache.hasEntry(key), true);
  });

  it('round-trips raw session markers', async () => {
    const cache = new PrefixCacheService('/cache', new InMemoryPrefixCacheStore());
    await cache.writeRaw('/p/x', 'circleai-session-marker\n');
    assert.equal((await cache.readRaw('/p/x'))?.startsWith('circleai-session-marker'), true);
    assert.equal(await cache.readRaw('/p/missing'), null);
  });
});
