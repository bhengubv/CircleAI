// belief_integrity.test.ts
//
// Verifies the memory-integrity core: attribution discipline (self/other/world),
// and SelfBeliefStore filtering, revision (supersede), correction (retract), and
// provenance. The headline guarantee: "my mother is diabetic" never becomes a
// fact about the user.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  Attribution,
  HeuristicBeliefExtractor,
  SelfBeliefStore,
  type PersonalBelief,
} from '../src/companion/belief';

const ex = new HeuristicBeliefExtractor();

async function one(text: string): Promise<PersonalBelief> {
  const beliefs = await ex.extractAsync(text, 'turn');
  assert.equal(beliefs.length, 1, `expected one belief from "${text}"`);
  return beliefs[0];
}

describe('HeuristicBeliefExtractor — attribution', () => {
  it('"my mother is diabetic" → Other, about the mother', async () => {
    const b = await one('my mother is diabetic');
    assert.equal(b.attribution, Attribution.Other);
    assert.equal(b.subject, 'mother');
    assert.equal(b.object, 'diabetic');
  });

  it('"i am vegetarian" → Self, about the user', async () => {
    const b = await one('i am vegetarian');
    assert.equal(b.attribution, Attribution.Self);
    assert.equal(b.subject, 'user');
    assert.equal(b.object, 'vegetarian');
  });

  it('"my car is fast" (my + non-relation) → Self', async () => {
    const b = await one('my car is fast');
    assert.equal(b.attribution, Attribution.Self);
    assert.equal(b.subject, 'user');
  });

  it('a bare relation as subject → Other', async () => {
    const b = await one('brother lives in Cape Town');
    assert.equal(b.attribution, Attribution.Other);
    assert.equal(b.subject, 'brother');
  });

  it('a general statement → World', async () => {
    const b = await one('paris is beautiful');
    assert.equal(b.attribution, Attribution.World);
    assert.equal(b.subject, 'paris');
  });
});

describe('SelfBeliefStore — filtering, revision, correction', () => {
  it('only Self beliefs become user facts; Other/World are audited', async () => {
    const store = new SelfBeliefStore();
    for (const b of await ex.extractAsync('my mother is diabetic', 't1')) store.record(b);
    for (const b of await ex.extractAsync('i am vegetarian', 't2')) store.record(b);

    const facts = store.selfFacts();
    assert.equal(facts.length, 1);
    assert.equal(facts[0].object, 'vegetarian');

    // The mother's fact is remembered, but never as a user fact.
    assert.ok(!facts.some((f) => f.object.includes('diabetic')));
    assert.ok(store.nonSelf().some((b) => b.object === 'diabetic'));
  });

  it('a newer self-belief supersedes the older one on the same predicate', () => {
    const store = new SelfBeliefStore();
    const mk = (obj: string): PersonalBelief => ({
      attribution: Attribution.Self,
      subject: 'user',
      predicate: 'isAbout',
      object: obj,
      confidence: 0.6,
      source: 't',
      recordedAtUtc: new Date(),
    });
    store.record(mk('vegetarian'));
    store.record(mk('vegan'));

    const facts = store.selfFacts();
    assert.equal(facts.length, 1);
    assert.equal(facts[0].object, 'vegan');
  });

  it('retract removes user facts mentioning the text', async () => {
    const store = new SelfBeliefStore();
    for (const b of await ex.extractAsync('i am vegetarian', 't1')) store.record(b);
    const removed = store.retract('vegetarian');
    assert.equal(removed, 1);
    assert.equal(store.selfFacts().length, 0);
  });

  it('provenance returns the distinct source turns behind user facts', () => {
    // Distinct predicates so both survive — the heuristic extractor always uses
    // "isAbout", which would (correctly) supersede one self-fact with the next.
    const store = new SelfBeliefStore();
    const mk = (obj: string, predicate: string, source: string): PersonalBelief => ({
      attribution: Attribution.Self,
      subject: 'user',
      predicate,
      object: obj,
      confidence: 0.6,
      source,
      recordedAtUtc: new Date(),
    });
    store.record(mk('vegetarian', 'diet', 't1'));
    store.record(mk('hiking', 'hobby', 't2'));
    const prov = [...store.provenance()].sort();
    assert.deepEqual(prov, ['t1', 't2']);
  });
});
