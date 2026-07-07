// kg_extractor.test.ts
//
// Verifies HeuristicKnowledgeGraphExtractor: bidirectional mentions/seenin
// triples on content words, stop-word + short-word filtering, dedup, and the
// memory-id fallback to userText when no episode id is given.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { HeuristicKnowledgeGraphExtractor } from '../src/memory/extractor';

const ex = new HeuristicKnowledgeGraphExtractor();

describe('HeuristicKnowledgeGraphExtractor', () => {
  it('emits a two-way link per content word, keyed by the episode id', async () => {
    const triples = await ex.extractFromTurnAsync('Durban weather is sunny', '', 'ep1');

    // content words: durban, weather, sunny  ("is" is a short stop word)
    assert.equal(triples.length, 6);

    const has = (s: string, p: string, o: string) =>
      triples.some((t) => t.subject === s && t.predicate === p && t.object === o);
    assert.ok(has('ep1', 'mentions', 'durban'));
    assert.ok(has('durban', 'seenin', 'ep1'));
    assert.ok(has('ep1', 'mentions', 'weather'));
    assert.ok(has('ep1', 'mentions', 'sunny'));
  });

  it('drops stop words and words shorter than 3 chars', async () => {
    const triples = await ex.extractFromTurnAsync('I am at the shop', '', 'ep2');
    const objects = triples.filter((t) => t.predicate === 'mentions').map((t) => t.object);
    // "i","am","at","the" are all stop/short; only "shop" survives.
    assert.deepEqual(objects, ['shop']);
  });

  it('dedupes a repeated word', async () => {
    const triples = await ex.extractFromTurnAsync('test test test', '', 'ep3');
    assert.equal(triples.length, 2); // one mentions + one seenin for "test"
  });

  it('includes assistant-side content words', async () => {
    const triples = await ex.extractFromTurnAsync('tell me about', 'Johannesburg traffic', 'ep4');
    const objects = triples.filter((t) => t.predicate === 'mentions').map((t) => t.object).sort();
    assert.deepEqual(objects, ['johannesburg', 'tell', 'traffic']);
  });

  it('falls back to userText as the memory id when no episode id is given', async () => {
    const triples = await ex.extractFromTurnAsync('hello world', '', null);
    assert.ok(triples.some((t) => t.subject === 'hello world' && t.predicate === 'mentions'));
  });

  it('returns nothing for an empty turn', async () => {
    assert.deepEqual(await ex.extractFromTurnAsync('', '', null), []);
  });

  it('tags every triple with the source episode id and default confidence', async () => {
    const triples = await ex.extractFromTurnAsync('coffee', '', 'ep5');
    assert.ok(triples.length > 0);
    for (const t of triples) {
      assert.equal(t.source, 'ep5');
      assert.equal(t.confidence, 0.6);
    }
  });
});
