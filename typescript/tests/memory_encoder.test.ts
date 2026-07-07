// memory_encoder.test.ts
//
// Verifies CompanionMemoryEncoder end-to-end: a turn handed to the background
// encoder fills the knowledge graph so associative recall can later reach the
// episode; attributed beliefs are formed off the hot path (a third party's fact
// never becomes the user's); the queue drops rather than blocks when full;
// closeAsync drains remaining work; and an extractor failure is captured, not
// fatal.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { CompanionMemoryEncoder } from '../src/companion/memory_encoder';
import { HeuristicKnowledgeGraphExtractor, type IKnowledgeGraphExtractor } from '../src/memory/extractor';
import { HeuristicBeliefExtractor, SelfBeliefStore } from '../src/companion/belief';
import { InMemoryKnowledgeGraph, InMemoryHippoRagStore } from '../src/memory/graph';

describe('CompanionMemoryEncoder — end-to-end', () => {
  it('encodes a turn so associative recall can reach the episode by a content word', async () => {
    const graph = new InMemoryKnowledgeGraph();
    const enc = new CompanionMemoryEncoder(new HeuristicKnowledgeGraphExtractor(), graph);

    enc.enqueue('I love hiking in Drakensberg', 'Sounds wonderful', 'ep-hike');
    await enc.closeAsync();

    assert.ok(graph.allTriples().length > 0, 'graph should have filled from the turn');

    const hippo = new InMemoryHippoRagStore(graph);
    const hits = await hippo.multiHopRecallAsync('drakensberg', 5);
    const episode = hits.find((h) => h.item.id === 'ep-hike');
    assert.ok(episode, 'recall should reach the episode via the extracted edges');
    assert.equal(episode!.item.text, 'I love hiking in Drakensberg');
  });

  it('forms attributed beliefs off the hot path — the mother\'s fact never becomes the user\'s', async () => {
    const graph = new InMemoryKnowledgeGraph();
    const beliefs = new SelfBeliefStore();
    const enc = new CompanionMemoryEncoder(
      new HeuristicKnowledgeGraphExtractor(),
      graph,
      new HeuristicBeliefExtractor(),
      beliefs,
    );

    enc.enqueue('my mother is diabetic', 'Noted', 'ep1');
    enc.enqueue('i am vegetarian', 'Got it', 'ep2');
    await enc.closeAsync();

    const facts = beliefs.selfFacts();
    assert.ok(!facts.some((f) => f.object.includes('diabetic')), "mother's condition must never be a user fact");
    assert.ok(facts.some((f) => f.object === 'vegetarian'));
    assert.ok(beliefs.nonSelf().some((b) => b.object === 'diabetic'), 'it is still remembered as an audit fact');
  });
});

describe('CompanionMemoryEncoder — queue behaviour', () => {
  it('drops writes beyond capacity rather than blocking', async () => {
    const graph = new InMemoryKnowledgeGraph();
    const enc = new CompanionMemoryEncoder(new HeuristicKnowledgeGraphExtractor(), graph, null, null, 2);

    // Enqueued synchronously before the drain resumes: the 3rd overflows a
    // capacity-2 queue and is dropped.
    enc.enqueue('alpha', '', 'e1');
    enc.enqueue('bravo', '', 'e2');
    enc.enqueue('charlie', '', 'e3');
    await enc.closeAsync();

    assert.notEqual(graph.getNode('e1'), null);
    assert.notEqual(graph.getNode('e2'), null);
    assert.equal(graph.getNode('e3'), null, 'the overflow write should have been dropped');
  });

  it('ignores an enqueue with a blank episode id', async () => {
    const graph = new InMemoryKnowledgeGraph();
    const enc = new CompanionMemoryEncoder(new HeuristicKnowledgeGraphExtractor(), graph);
    enc.enqueue('hello', '', '');
    enc.enqueue('hello', '', '   ');
    await enc.closeAsync();
    assert.equal(graph.allTriples().length, 0);
  });

  it('captures an extractor failure without crashing the drain', async () => {
    const graph = new InMemoryKnowledgeGraph();
    const throwing: IKnowledgeGraphExtractor = {
      async extractFromTurnAsync() {
        throw new Error('boom');
      },
    };
    const enc = new CompanionMemoryEncoder(throwing, graph);
    enc.enqueue('x', '', 'e1');
    await enc.closeAsync();

    assert.ok(enc.lastError instanceof Error);
    assert.equal((enc.lastError as Error).message, 'boom');
    // The node was upserted before the extractor ran, so it survives.
    assert.notEqual(graph.getNode('e1'), null);
  });
});
