// fused_recall.test.ts
//
// Verifies FusedRecall: Reciprocal Rank Fusion order, cross-source reinforcement,
// cold-start degradation to episodic, the graph confidence gate, empty-query
// short-circuit, and dedup by normalised text.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { FusedRecall } from '../src/memory/recall';
import type { IEpisodicMemoryStore, EpisodicMemoryEntry } from '../src/memory/index';
import type { IHippoRagStore, MemoryHit } from '../src/memory/graph';

// ── Test doubles ────────────────────────────────────────────────────────────

function ep(id: string, userText: string): EpisodicMemoryEntry {
  return {
    id,
    userText,
    assistantText: '',
    recordedAtUtc: new Date('2026-01-01T00:00:00Z'),
  };
}

/** Episodic store that returns a fixed, pre-ranked list from searchAsync. */
class FakeEpisodic implements IEpisodicMemoryStore {
  constructor(private readonly hits: EpisodicMemoryEntry[]) {}
  async addAsync(): Promise<void> {}
  async searchAsync(_q: number[] | null, topK = 5): Promise<readonly EpisodicMemoryEntry[]> {
    return this.hits.slice(0, topK);
  }
  async getRecentAsync(count = 10): Promise<readonly EpisodicMemoryEntry[]> {
    return this.hits.slice(0, count);
  }
  async countAsync(): Promise<number> {
    return this.hits.length;
  }
  async pruneOlderThanAsync(): Promise<number> {
    return 0;
  }
}

/** HippoRAG store that returns a fixed, pre-ranked list from multiHopRecallAsync. */
class FakeHippo implements IHippoRagStore {
  readonly backendId = 'fake-hippo';
  constructor(private readonly hits: MemoryHit[]) {}
  async indexAsync(): Promise<void> {}
  async multiHopRecallAsync(_q: string, topK = 5): Promise<readonly MemoryHit[]> {
    return this.hits.slice(0, topK);
  }
}

function graphHit(id: string, text: string, confidence?: string): MemoryHit {
  const metadata = confidence === undefined ? undefined : { confidence };
  return { item: { id, text, metadata }, score: 1 };
}

// ── Tests ───────────────────────────────────────────────────────────────────

describe('FusedRecall — RRF ordering', () => {
  it('a memory surfaced by BOTH sources outranks one surfaced by only one', async () => {
    const episodic = new FakeEpisodic([ep('a', 'A'), ep('b', 'B'), ep('c', 'C')]);
    const graph = new FakeHippo([graphHit('g', 'B')]); // reinforces B
    const recall = new FusedRecall(episodic, graph);

    const hits = await recall.recallAsync('q', null, 5);
    assert.deepEqual(hits.map((h) => h.item.text), ['B', 'A', 'C']);
  });

  it('cold-start (no graph) yields the episodic order unchanged', async () => {
    const episodic = new FakeEpisodic([ep('a', 'A'), ep('b', 'B'), ep('c', 'C')]);
    const recall = new FusedRecall(episodic, null);

    const hits = await recall.recallAsync('q', null, 5);
    assert.deepEqual(hits.map((h) => h.item.text), ['A', 'B', 'C']);
  });

  it('respects topK', async () => {
    const episodic = new FakeEpisodic([ep('a', 'A'), ep('b', 'B'), ep('c', 'C')]);
    const recall = new FusedRecall(episodic, null);

    const hits = await recall.recallAsync('q', null, 2);
    assert.equal(hits.length, 2);
    assert.deepEqual(hits.map((h) => h.item.text), ['A', 'B']);
  });
});

describe('FusedRecall — integrity gates', () => {
  it('drops graph hits below the confidence threshold', async () => {
    const episodic = new FakeEpisodic([]);
    const graph = new FakeHippo([graphHit('low', 'LOW', '0.2'), graphHit('high', 'HIGH', '0.9')]);
    const recall = new FusedRecall(episodic, graph);

    const hits = await recall.recallAsync('q', null, 5);
    const texts = hits.map((h) => h.item.text);
    assert.ok(!texts.includes('LOW'), 'below-threshold hit must be dropped');
    assert.ok(texts.includes('HIGH'));
  });

  it('keeps graph hits that carry no confidence metadata (gate is a no-op)', async () => {
    const episodic = new FakeEpisodic([]);
    const graph = new FakeHippo([graphHit('g', 'NOCONF')]);
    const recall = new FusedRecall(episodic, graph);

    const hits = await recall.recallAsync('q', null, 5);
    assert.deepEqual(hits.map((h) => h.item.text), ['NOCONF']);
  });

  it('skips the graph entirely for an empty/whitespace query', async () => {
    const episodic = new FakeEpisodic([ep('a', 'A')]);
    const graph = new FakeHippo([graphHit('g', 'GRAPH')]);
    const recall = new FusedRecall(episodic, graph);

    const hits = await recall.recallAsync('   ', null, 5);
    const texts = hits.map((h) => h.item.text);
    assert.deepEqual(texts, ['A']);
    assert.ok(!texts.includes('GRAPH'));
  });

  it('degrades to episodic when the graph throws', async () => {
    const episodic = new FakeEpisodic([ep('a', 'A')]);
    const throwing: IHippoRagStore = {
      backendId: 'boom',
      async indexAsync() {},
      async multiHopRecallAsync() {
        throw new Error('graph unavailable');
      },
    };
    const recall = new FusedRecall(episodic, throwing);

    const hits = await recall.recallAsync('q', null, 5);
    assert.deepEqual(hits.map((h) => h.item.text), ['A']);
  });
});

describe('FusedRecall — dedup', () => {
  it('fuses two hits with the same normalised text into one entry', async () => {
    const episodic = new FakeEpisodic([ep('a', 'Durban  Weather')]);
    const graph = new FakeHippo([graphHit('g', 'durban weather')]); // same key
    const recall = new FusedRecall(episodic, graph);

    const hits = await recall.recallAsync('q', null, 5);
    assert.equal(hits.length, 1);
  });
});
