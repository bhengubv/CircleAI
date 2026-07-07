// episodic_store.test.ts
//
// Verifies InMemoryEpisodicStore: cosine similarity search, recency fallback,
// FIFO capacity eviction, prune, and count.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { InMemoryEpisodicStore } from '../src/memory/stores';
import type { EpisodicMemoryEntry } from '../src/memory/index';

function entry(overrides: Partial<EpisodicMemoryEntry> & { id: string }): EpisodicMemoryEntry {
  return {
    recordedAtUtc: new Date('2026-01-01T00:00:00Z'),
    userText: 'u',
    assistantText: 'a',
    ...overrides,
  };
}

describe('InMemoryEpisodicStore — cosine search', () => {
  it('ranks the nearest embedding first', async () => {
    const store = new InMemoryEpisodicStore();
    await store.addAsync(entry({ id: 'x', userText: 'x-axis', embedding: [1, 0] }));
    await store.addAsync(entry({ id: 'y', userText: 'y-axis', embedding: [0, 1] }));

    const hits = await store.searchAsync([1, 0], 2);
    assert.equal(hits.length, 2);
    assert.equal(hits[0].id, 'x');
    assert.equal(hits[1].id, 'y');
  });

  it('respects topK', async () => {
    const store = new InMemoryEpisodicStore();
    await store.addAsync(entry({ id: 'a', embedding: [1, 0] }));
    await store.addAsync(entry({ id: 'b', embedding: [0.9, 0.1] }));
    await store.addAsync(entry({ id: 'c', embedding: [0, 1] }));

    const hits = await store.searchAsync([1, 0], 1);
    assert.equal(hits.length, 1);
    assert.equal(hits[0].id, 'a');
  });

  it('ignores entries whose embedding dimension differs from the query', async () => {
    const store = new InMemoryEpisodicStore();
    await store.addAsync(entry({ id: 'ok', embedding: [1, 0] }));
    await store.addAsync(entry({ id: 'wrongdim', embedding: [1, 0, 0] }));

    const hits = await store.searchAsync([1, 0], 5);
    assert.equal(hits.length, 1);
    assert.equal(hits[0].id, 'ok');
  });
});

describe('InMemoryEpisodicStore — recency fallback', () => {
  it('returns newest-first when the query embedding is null', async () => {
    const store = new InMemoryEpisodicStore();
    await store.addAsync(entry({ id: 'old', recordedAtUtc: new Date('2026-01-01T00:00:00Z') }));
    await store.addAsync(entry({ id: 'new', recordedAtUtc: new Date('2026-06-01T00:00:00Z') }));

    const hits = await store.searchAsync(null, 5);
    assert.equal(hits[0].id, 'new');
    assert.equal(hits[1].id, 'old');
  });

  it('treats an empty embedding as no embedding (recency)', async () => {
    const store = new InMemoryEpisodicStore();
    await store.addAsync(entry({ id: 'old', recordedAtUtc: new Date('2026-01-01T00:00:00Z') }));
    await store.addAsync(entry({ id: 'new', recordedAtUtc: new Date('2026-06-01T00:00:00Z') }));

    const hits = await store.searchAsync([], 1);
    assert.equal(hits[0].id, 'new');
  });
});

describe('InMemoryEpisodicStore — capacity + maintenance', () => {
  it('evicts oldest entries beyond maxEntries (FIFO)', async () => {
    const store = new InMemoryEpisodicStore(2);
    await store.addAsync(entry({ id: 'a' }));
    await store.addAsync(entry({ id: 'b' }));
    await store.addAsync(entry({ id: 'c' }));

    assert.equal(await store.countAsync(), 2);
    const recent = await store.getRecentAsync(10);
    const ids = recent.map((e) => e.id).sort();
    assert.deepEqual(ids, ['b', 'c']); // 'a' evicted
  });

  it('prunes entries older than the cutoff and returns the removed count', async () => {
    const store = new InMemoryEpisodicStore();
    await store.addAsync(entry({ id: 'old', recordedAtUtc: new Date('2026-01-01T00:00:00Z') }));
    await store.addAsync(entry({ id: 'new', recordedAtUtc: new Date('2026-06-01T00:00:00Z') }));

    const removed = await store.pruneOlderThanAsync(new Date('2026-03-01T00:00:00Z'));
    assert.equal(removed, 1);
    assert.equal(await store.countAsync(), 1);
    const remaining = await store.getRecentAsync(10);
    assert.equal(remaining[0].id, 'new');
  });

  it('rejects a non-positive maxEntries', () => {
    assert.throws(() => new InMemoryEpisodicStore(0), RangeError);
  });
});
