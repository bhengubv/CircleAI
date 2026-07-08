// world_model.test.ts
//
// Verifies the two IWorldModel implementations against the C# reference:
//   FrequencyWorldModel (HerJarvisRealImplementations.cs #5) and
//   BayesianWorldModel  (BayesianWorldModel.cs).
// Expected outcomes/probabilities were captured from a .NET 10 probe running
// the identical algorithm on the same training set + scenario, so the numbers
// below are the exact IEEE-754 doubles the C# produces.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  FrequencyWorldModel,
  BayesianWorldModel,
  extractObservations,
  jsonElementToString,
} from '../src/companion/reasoning/index';

// Shared training set used by both models (matches the C# probe).
function train(m: { observe(obs: Iterable<string>, outcome: string): void }): void {
  m.observe(['weather=rain', 'mood=low'], 'stay-in');
  m.observe(['weather=rain'], 'stay-in');
  m.observe(['weather=sun'], 'go-out');
  m.observe(['weather=rain'], 'go-out');
}

describe('FrequencyWorldModel', () => {
  it('returns the most-frequent outcome and its share of the tally', async () => {
    const m = new FrequencyWorldModel();
    train(m);
    const p = await m.predictAsync('{"weather":"rain"}');
    // rain co-occurred with stay-in ×2, go-out ×1 → 2/3.
    assert.equal(p.outcome, 'stay-in');
    assert.equal(p.probability, 0.6666666666666666);
    assert.deepEqual(p.supportingFactors, ['weather=rain']);
  });

  it('returns unknown/0.5 with no supporters when nothing matches', async () => {
    const m = new FrequencyWorldModel();
    train(m);
    const p = await m.predictAsync('{"weather":"snow"}');
    assert.equal(p.outcome, 'unknown');
    assert.equal(p.probability, 0.5);
    assert.deepEqual(p.supportingFactors, []);
  });

  it('returns unknown/0.5 for a non-object or malformed scenario', async () => {
    const m = new FrequencyWorldModel();
    train(m);
    for (const bad of ['[1,2,3]', '"just a string"', 'not json', '', '42']) {
      const p = await m.predictAsync(bad);
      assert.equal(p.outcome, 'unknown');
      assert.equal(p.probability, 0.5);
    }
  });

  it('matches observations case-insensitively (OrdinalIgnoreCase)', async () => {
    const m = new FrequencyWorldModel();
    m.observe(['Weather=Rain'], 'stay-in');
    const p = await m.predictAsync('{"weather":"rain"}'); // different case
    assert.equal(p.outcome, 'stay-in');
    assert.equal(p.probability, 1);
  });

  it('rejects a blank outcome on observe', () => {
    const m = new FrequencyWorldModel();
    assert.throws(() => m.observe(['x=y'], '   '), /outcome required/);
  });
});

describe('BayesianWorldModel', () => {
  it('scores every outcome by Laplace-smoothed log-posterior and softmaxes', async () => {
    const m = new BayesianWorldModel(); // alpha = 1.0
    train(m);
    const p = await m.predictAsync('{"weather":"rain"}');
    // From the C# probe: winner stay-in, softmax prob 0.5555555555555556,
    // supporters echo the scenario observations.
    assert.equal(p.outcome, 'stay-in');
    assert.equal(p.probability, 0.5555555555555556);
    assert.deepEqual(p.supportingFactors, ['weather=rain']);
  });

  it('returns unknown/0.5 with empty supporters when the model is empty', async () => {
    const m = new BayesianWorldModel();
    const p = await m.predictAsync('{"weather":"rain"}');
    assert.equal(p.outcome, 'unknown');
    assert.equal(p.probability, 0.5);
    assert.deepEqual(p.supportingFactors, []);
  });

  it('returns unknown/0.5 when the scenario yields no observations', async () => {
    const m = new BayesianWorldModel();
    train(m);
    const p = await m.predictAsync('[1,2,3]'); // not an object
    assert.equal(p.outcome, 'unknown');
    assert.equal(p.probability, 0.5);
  });

  it('rejects a non-positive Laplace alpha', () => {
    assert.throws(() => new BayesianWorldModel(0), /laplaceAlpha out of range/);
    assert.throws(() => new BayesianWorldModel(-1), /laplaceAlpha out of range/);
  });

  it('probability is a valid softmax value in (0,1]', async () => {
    const m = new BayesianWorldModel();
    train(m);
    const p = await m.predictAsync('{"weather":"sun"}');
    assert.ok(p.probability > 0 && p.probability <= 1);
  });
});

describe('extractObservations — JsonElement.ToString() parity', () => {
  it('emits name=value for each property with STJ ToString semantics', () => {
    const obs = extractObservations(
      '{"s":"hi","n":5,"f":3.14,"t":true,"fa":false,"nu":null,"o":{"x":1},"a":[1,2],"e":""}',
    );
    assert.deepEqual(obs, [
      's=hi',
      'n=5',
      'f=3.14',
      't=True', // booleans capitalise
      'fa=False',
      'nu=', // null -> empty
      'o={"x":1}', // nested object -> raw compact JSON
      'a=[1,2]', // array -> raw compact JSON
      'e=', // empty string -> empty
    ]);
  });

  it('returns [] for null/blank/non-object/malformed input', () => {
    assert.deepEqual(extractObservations(null), []);
    assert.deepEqual(extractObservations('   '), []);
    assert.deepEqual(extractObservations('[1,2]'), []);
    assert.deepEqual(extractObservations('"x"'), []);
    assert.deepEqual(extractObservations('nonsense'), []);
  });

  it('jsonElementToString matches STJ for each kind', () => {
    assert.equal(jsonElementToString('hi'), 'hi');
    assert.equal(jsonElementToString(''), '');
    assert.equal(jsonElementToString(5), '5');
    assert.equal(jsonElementToString(3.14), '3.14');
    assert.equal(jsonElementToString(true), 'True');
    assert.equal(jsonElementToString(false), 'False');
    assert.equal(jsonElementToString(null), '');
    assert.equal(jsonElementToString({ x: 1 }), '{"x":1}');
    assert.equal(jsonElementToString([1, 2]), '[1,2]');
  });
});
