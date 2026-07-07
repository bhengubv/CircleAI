// knowledge_graph.test.ts
//
// Verifies InMemoryKnowledgeGraph (triples + nodes) and InMemoryHippoRagStore
// (Personalised PageRank multi-hop recall) — including the three precision
// guarantees: no-seed→empty, seeds excluded from results, confidence-weighting.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  InMemoryKnowledgeGraph,
  InMemoryHippoRagStore,
} from '../src/memory/graph';

describe('InMemoryKnowledgeGraph', () => {
  it('stores and returns triples', () => {
    const kg = new InMemoryKnowledgeGraph();
    kg.addTriple('a', 'rel', 'b', 'ep1', 1.0);
    const all = kg.allTriples();
    assert.equal(all.length, 1);
    assert.equal(all[0].subject, 'a');
    assert.equal(all[0].object, 'b');
    assert.equal(all[0].confidence, 1.0);
  });

  it('replaces a triple with the same (subject, predicate, object)', () => {
    const kg = new InMemoryKnowledgeGraph();
    kg.addTriple('a', 'rel', 'b', 'ep1', 0.5);
    kg.addTriple('a', 'rel', 'b', 'ep2', 0.9);
    const all = kg.allTriples();
    assert.equal(all.length, 1);
    assert.equal(all[0].confidence, 0.9);
    assert.equal(all[0].source, 'ep2');
  });

  it('upserts and fetches nodes', () => {
    const kg = new InMemoryKnowledgeGraph();
    kg.upsertNode({ id: 'heart', kind: 'organ', name: 'the heart' });
    assert.equal(kg.getNode('heart')?.name, 'the heart');
    assert.equal(kg.getNode('missing'), null);
  });

  it('rejects out-of-range confidence', () => {
    const kg = new InMemoryKnowledgeGraph();
    assert.throws(() => kg.addTriple('a', 'r', 'b', null, 1.5), RangeError);
  });
});

describe('InMemoryHippoRagStore — multi-hop recall', () => {
  it('reaches associated nodes across hops and excludes the seed', async () => {
    // chest → heart → father_cardiac_event
    const kg = new InMemoryKnowledgeGraph();
    kg.addTriple('chest', 'relates', 'heart', 'ep1', 1.0);
    kg.addTriple('heart', 'relates', 'father_cardiac_event', 'ep2', 1.0);
    const hippo = new InMemoryHippoRagStore(kg);

    const hits = await hippo.multiHopRecallAsync('chest tightness', 5);
    const ids = hits.map((h) => h.item.id);

    assert.ok(!ids.includes('chest'), 'seed node must be excluded');
    assert.ok(ids.includes('heart'), 'one-hop node should be recalled');
    assert.ok(ids.includes('father_cardiac_event'), 'two-hop node should be recalled');

    // One hop carries more PPR mass than two hops.
    const heart = hits.find((h) => h.item.id === 'heart')!;
    const father = hits.find((h) => h.item.id === 'father_cardiac_event')!;
    assert.ok(heart.score >= father.score);
  });

  it('returns empty when no query term touches the graph (no fabricated association)', async () => {
    const kg = new InMemoryKnowledgeGraph();
    kg.addTriple('chest', 'relates', 'heart', 'ep1', 1.0);
    const hippo = new InMemoryHippoRagStore(kg);

    const hits = await hippo.multiHopRecallAsync('banana apple', 5);
    assert.equal(hits.length, 0);
  });

  it('returns empty on an empty graph', async () => {
    const hippo = new InMemoryHippoRagStore(new InMemoryKnowledgeGraph());
    const hits = await hippo.multiHopRecallAsync('anything', 5);
    assert.equal(hits.length, 0);
  });

  it('confidence-weights edge spread: a stated fact outranks a guess', async () => {
    // root → alpha (stated, 1.0) and root → beta (guessed, 0.1)
    const kg = new InMemoryKnowledgeGraph();
    kg.addTriple('root', 'r', 'alpha', 'ep1', 1.0);
    kg.addTriple('root', 'r', 'beta', 'ep2', 0.1);
    const hippo = new InMemoryHippoRagStore(kg);

    const hits = await hippo.multiHopRecallAsync('root', 5);
    const ids = hits.map((h) => h.item.id);
    assert.ok(!ids.includes('root'), 'seed excluded');
    assert.equal(hits[0].item.id, 'alpha');
    assert.equal(hits[1].item.id, 'beta');
    assert.ok(hits[0].score > hits[1].score);
  });

  it('uses the node name as recall text when a node is present', async () => {
    const kg = new InMemoryKnowledgeGraph();
    kg.addTriple('chest', 'relates', 'heart', 'ep1', 1.0);
    kg.upsertNode({ id: 'heart', kind: 'organ', name: 'the heart' });
    const hippo = new InMemoryHippoRagStore(kg);

    const hits = await hippo.multiHopRecallAsync('chest', 5);
    const heart = hits.find((h) => h.item.id === 'heart')!;
    assert.equal(heart.item.text, 'the heart');
  });

  it('indexAsync registers the item + its metadata as graph triples', async () => {
    const kg = new InMemoryKnowledgeGraph();
    const hippo = new InMemoryHippoRagStore(kg);
    await hippo.indexAsync({ id: 'note1', text: 'durban weather', metadata: { topic: 'durban' } });

    const preds = kg.readTriples('note1').map((t) => t.predicate).sort();
    assert.deepEqual(preds, ['memory_text', 'topic']);
  });

  it('recalls a memory node reached from a query-term seed (reverse edge)', async () => {
    // Extractor-style reverse edge: the term "durban" points to the memory that
    // mentions it, so a forward walk from the seed reaches the memory node.
    const kg = new InMemoryKnowledgeGraph();
    kg.addTriple('durban', 'seenin', 'note1', 'ep1', 1.0);
    kg.upsertNode({ id: 'note1', kind: 'memory', name: 'durban weather' });
    const hippo = new InMemoryHippoRagStore(kg);

    const hits = await hippo.multiHopRecallAsync('durban', 5);
    const ids = hits.map((h) => h.item.id);
    assert.ok(!ids.includes('durban'), 'seed excluded');
    assert.ok(ids.includes('note1'));
    assert.equal(hits.find((h) => h.item.id === 'note1')!.item.text, 'durban weather');
  });
});
