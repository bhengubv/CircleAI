// rag.test.ts
//
// Exercises RagContextBuilder + RagPipelineBuilder. Mirrors
// CircleAI.Tests.RagContextBuilderTests plus the fluent-builder surface and the
// embedder ranking path.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { RagContextBuilder, RagPipelineBuilder, type ITextEmbedder } from '../src/memory/rag';
import { InMemoryEpisodicStore } from '../src/memory/stores';
import type { EpisodicMemoryEntry, IEpisodicMemoryStore } from '../src/memory/index';

// ── Helpers ──────────────────────────────────────────────────────────────────

function episodic(overrides: Partial<EpisodicMemoryEntry>): EpisodicMemoryEntry {
  return {
    id: overrides.id ?? crypto.randomUUID(),
    recordedAtUtc: overrides.recordedAtUtc ?? new Date('2026-06-01T12:34:00Z'),
    userText: 'u',
    assistantText: 'a',
    ...overrides,
  };
}

function countOccurrences(text: string, token: string): number {
  let count = 0;
  let start = 0;
  for (;;) {
    const i = text.indexOf(token, start);
    if (i < 0) break;
    count++;
    start = i + token.length;
  }
  return count;
}

/** Store that always throws — used to test resilience. */
class ThrowingEpisodicStore implements IEpisodicMemoryStore {
  addAsync(): Promise<void> {
    throw new Error('store failure');
  }
  searchAsync(): Promise<readonly EpisodicMemoryEntry[]> {
    throw new Error('store failure');
  }
  getRecentAsync(): Promise<readonly EpisodicMemoryEntry[]> {
    throw new Error('store failure');
  }
  countAsync(): Promise<number> {
    throw new Error('store failure');
  }
  pruneOlderThanAsync(): Promise<number> {
    throw new Error('store failure');
  }
}

// ══════════════════════════════════════════════════════════════════════════
// Constructor guards
// ══════════════════════════════════════════════════════════════════════════

describe('RagContextBuilder — constructor guards', () => {
  it('throws when the store is null', () => {
    assert.throws(() => new RagContextBuilder(null as unknown as IEpisodicMemoryStore));
  });
});

// ══════════════════════════════════════════════════════════════════════════
// Empty / missing query
// ══════════════════════════════════════════════════════════════════════════

describe('RagContextBuilder — empty / missing query', () => {
  it('empty query returns empty', async () => {
    const b = new RagContextBuilder(new InMemoryEpisodicStore());
    assert.equal(await b.buildContextAsync(''), '');
  });

  it('whitespace query returns empty', async () => {
    const b = new RagContextBuilder(new InMemoryEpisodicStore());
    assert.equal(await b.buildContextAsync('   '), '');
  });
});

// ══════════════════════════════════════════════════════════════════════════
// Empty store
// ══════════════════════════════════════════════════════════════════════════

describe('RagContextBuilder — empty store', () => {
  it('empty store returns empty', async () => {
    const b = new RagContextBuilder(new InMemoryEpisodicStore());
    assert.equal(await b.buildContextAsync('hello'), '');
  });
});

// ══════════════════════════════════════════════════════════════════════════
// Non-empty store — recency fallback (no embedder)
// ══════════════════════════════════════════════════════════════════════════

describe('RagContextBuilder — formatting', () => {
  it('returns a formatted block with the header and both texts', async () => {
    const store = new InMemoryEpisodicStore();
    await store.addAsync(
      episodic({
        userText: 'What is SDPKT?',
        assistantText: 'SDPKT is the TGN wallet.',
        recordedAtUtc: new Date('2026-06-01T11:00:00Z'),
      }),
    );

    const b = new RagContextBuilder(store, null, 3);
    const result = await b.buildContextAsync('tell me about the wallet');

    assert.notEqual(result, '');
    assert.ok(result.includes('What is SDPKT?'));
    assert.ok(result.includes('SDPKT is the TGN wallet.'));
    assert.ok(result.includes('[Relevant past exchanges'));
  });

  it('formats the UTC timestamp as yyyy-MM-dd HH:mm and labels User/B!', async () => {
    const store = new InMemoryEpisodicStore();
    await store.addAsync(
      episodic({ userText: 'q', assistantText: 'r', recordedAtUtc: new Date('2026-06-01T09:05:00Z') }),
    );
    const b = new RagContextBuilder(store, null, 1);
    const result = await b.buildContextAsync('anything');
    assert.ok(result.includes('[2026-06-01 09:05 UTC]'));
    assert.ok(result.includes('User: q'));
    assert.ok(result.includes('B!: r'));
  });

  it('respects topK (counts bullet prefixes)', async () => {
    const store = new InMemoryEpisodicStore();
    for (let i = 0; i < 10; i++)
      await store.addAsync(episodic({ userText: `question ${i}`, assistantText: `answer ${i}` }));

    const b = new RagContextBuilder(store, null, 2);
    const result = await b.buildContextAsync('any question');
    assert.equal(countOccurrences(result, '• ['), 2);
  });

  it('includes the app context when set', async () => {
    const store = new InMemoryEpisodicStore();
    await store.addAsync(episodic({ userText: 'bid query', assistantText: 'bid answer', appContext: 'tgn.bidbaas' }));
    const b = new RagContextBuilder(store, null, 3);
    const result = await b.buildContextAsync('bidding');
    assert.ok(result.includes('tgn.bidbaas'));
  });

  it('truncates long texts to half the per-entry budget with an ellipsis', async () => {
    const store = new InMemoryEpisodicStore();
    const longText = 'x'.repeat(500);
    await store.addAsync(episodic({ userText: longText, assistantText: 'a' }));
    // maxCharsPerEntry 100 → half 50 → truncate to 49 chars + "…"
    const b = new RagContextBuilder(store, null, 1, 100);
    const result = await b.buildContextAsync('q');
    assert.ok(result.includes('x'.repeat(49) + '…'));
    assert.ok(!result.includes('x'.repeat(51)));
  });
});

// ══════════════════════════════════════════════════════════════════════════
// Embedder ranking path
// ══════════════════════════════════════════════════════════════════════════

describe('RagContextBuilder — embedder path', () => {
  it('ranks by the embedding when an embedder is supplied', async () => {
    const store = new InMemoryEpisodicStore();
    await store.addAsync(episodic({ userText: 'near', assistantText: 'n', embedding: [1, 0] }));
    await store.addAsync(episodic({ userText: 'far', assistantText: 'f', embedding: [0, 1] }));

    // Embedder maps any query to the x-axis, so "near" should rank first.
    const embedder: ITextEmbedder = { async generateAsync() { return [1, 0]; } };
    const b = new RagContextBuilder(store, embedder, 1);
    const result = await b.buildContextAsync('anything');
    assert.ok(result.includes('near'));
    assert.ok(!result.includes('far'));
  });

  it('falls back to recency when the embedder throws (still best-effort)', async () => {
    const store = new InMemoryEpisodicStore();
    await store.addAsync(
      episodic({ userText: 'only', assistantText: 'entry', recordedAtUtc: new Date('2026-06-01T00:00:00Z') }),
    );
    const embedder: ITextEmbedder = {
      async generateAsync() {
        throw new Error('embedder offline');
      },
    };
    const b = new RagContextBuilder(store, embedder, 3);
    const result = await b.buildContextAsync('q');
    assert.ok(result.includes('only'));
  });
});

// ══════════════════════════════════════════════════════════════════════════
// Resilience — store throws
// ══════════════════════════════════════════════════════════════════════════

describe('RagContextBuilder — resilience', () => {
  it('returns empty when the store throws (RAG is best-effort)', async () => {
    const b = new RagContextBuilder(new ThrowingEpisodicStore());
    assert.equal(await b.buildContextAsync('query'), '');
  });
});

// ══════════════════════════════════════════════════════════════════════════
// RagPipelineBuilder
// ══════════════════════════════════════════════════════════════════════════

describe('RagPipelineBuilder', () => {
  it('builds from an in-memory store and produces a working builder', async () => {
    const store = new InMemoryEpisodicStore();
    await store.addAsync(episodic({ userText: 'hi', assistantText: 'hello' }));
    const rag = RagPipelineBuilder.create().withStore(store).withTopK(2).withMaxCharsPerEntry(500).build();
    const ctx = await rag.buildContextAsync('greeting');
    assert.ok(ctx.includes('hi'));
  });

  it('withInMemoryStore wires a fresh store', async () => {
    const rag = RagPipelineBuilder.create().withInMemoryStore().build();
    assert.equal(await rag.buildContextAsync('nothing stored'), '');
  });

  it('build without a store throws', () => {
    assert.throws(() => RagPipelineBuilder.create().build(), /episodic memory store is required/i);
  });

  it('withTopK rejects values below 1', () => {
    assert.throws(() => RagPipelineBuilder.create().withTopK(0), RangeError);
  });

  it('withMaxCharsPerEntry rejects values below 50', () => {
    assert.throws(() => RagPipelineBuilder.create().withMaxCharsPerEntry(49), RangeError);
  });

  it('withEmbedder wires the semantic-ranking seam', async () => {
    const store = new InMemoryEpisodicStore();
    await store.addAsync(episodic({ userText: 'near', assistantText: 'n', embedding: [1, 0] }));
    await store.addAsync(episodic({ userText: 'far', assistantText: 'f', embedding: [0, 1] }));
    const embedder: ITextEmbedder = { async generateAsync() { return [1, 0]; } };
    const rag = RagPipelineBuilder.create().withStore(store).withEmbedder(embedder).withTopK(1).build();
    const ctx = await rag.buildContextAsync('q');
    assert.ok(ctx.includes('near'));
  });
});
