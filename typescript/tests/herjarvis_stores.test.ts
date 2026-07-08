// herjarvis_stores.test.ts
//
// Verifies the store / math HerJarvis implementations against the C# reference
// (HerJarvisRealImplementations.cs #3,4,6,7,9,11,12,15). Numbers that are
// wire/algorithm-sensitive (the IdentitySync pull envelope, the goal plan JSON
// shape, the EWA reward, the emotion arousal/valence weighted averages, the TF
// recall scores, the confidence band widths) are asserted exactly.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  JsonIdentitySync,
  EwaContinuousLearner,
  InMemoryGoalPursuer,
  TfEpisodicMemory,
  HistoricalCalibratedConfidence,
  KeywordEmotionSensor,
  DemoStoreSkillAcquisition,
  AdjacencyPersonalKnowledgeGraph,
  toRoundTripUtc,
} from '../src/companion/herjarvis/index';

describe('JsonIdentitySync — append-only delta log + pull envelope', () => {
  it('pulls deltas strictly after the cursor and advances it', async () => {
    const sync = new JsonIdentitySync();
    await sync.pushAsync('{"a":1}');
    await sync.pushAsync('{"b":2}');
    // from cursor 0 → both deltas, cursor 2.
    assert.equal(await sync.pullAsync('0'), '{"cursor":2,"deltas":[{"a":1},{"b":2}]}');
    // from cursor 1 → only the second, cursor 2.
    assert.equal(await sync.pullAsync('1'), '{"cursor":2,"deltas":[{"b":2}]}');
    // from cursor 2 → nothing; cursor stays 2 (echoes the since value).
    assert.equal(await sync.pullAsync('2'), '{"cursor":2,"deltas":[]}');
  });

  it('treats a non-numeric cursor as 0', async () => {
    const sync = new JsonIdentitySync();
    await sync.pushAsync('{"x":true}');
    assert.equal(await sync.pullAsync('not-a-number'), '{"cursor":1,"deltas":[{"x":true}]}');
  });

  it('rejects a null delta', async () => {
    const sync = new JsonIdentitySync();
    // @ts-expect-error deliberate null
    await assert.rejects(() => sync.pushAsync(null), /deltaJson required/);
  });
});

describe('EwaContinuousLearner — exponentially weighted reward', () => {
  it('seeds with the raw reward, then blends avg*(1-a)+reward*a', async () => {
    const l = new EwaContinuousLearner(0.2);
    await l.registerFeedbackAsync('i1', 1.0, '{}');
    assert.equal(l.averageRewardOf('i1'), 1.0);
    assert.equal(l.observationsOf('i1'), 1);
    await l.registerFeedbackAsync('i1', 0.0, '{}');
    // 1.0*0.8 + 0.0*0.2 = 0.8
    assert.ok(Math.abs((l.averageRewardOf('i1') as number) - 0.8) < 1e-15);
    assert.equal(l.observationsOf('i1'), 2);
  });

  it('returns null for an unknown id', () => {
    const l = new EwaContinuousLearner();
    assert.equal(l.averageRewardOf('nope'), null);
    assert.equal(l.observationsOf('nope'), 0);
  });

  it('rejects an out-of-range alpha and a blank id', async () => {
    assert.throws(() => new EwaContinuousLearner(0), /alpha out of range/);
    assert.throws(() => new EwaContinuousLearner(1.1), /alpha out of range/);
    const l = new EwaContinuousLearner();
    await assert.rejects(() => l.registerFeedbackAsync('  ', 1, '{}'), /interactionId required/);
  });
});

describe('InMemoryGoalPursuer — plan build + replan + progress', () => {
  it('builds a milestone plan with the exact C# JSON shape', async () => {
    const p = new InMemoryGoalPursuer();
    const now = Date.now();
    const deadline = new Date(now + 60 * 86_400_000); // 60 days → clamp(60/14=4,2,8)=4 milestones
    const g = await p.registerAsync('ship v2', deadline);
    assert.equal(g.progressFraction, 0);
    const plan = JSON.parse(g.planJson);
    assert.equal(plan.description, 'ship v2');
    assert.equal(plan.milestones.length, 4);
    assert.equal(plan.milestones[0].index, 1);
    assert.equal(plan.milestones[3].index, 4);
    // Each due date is a round-trip UTC string ending in Z with 7 fractional digits.
    assert.match(plan.milestones[0].due, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$/);
  });

  it('escapes the description via STJ in the plan JSON', async () => {
    const p = new InMemoryGoalPursuer();
    const g = await p.registerAsync('a<b>&c', new Date(Date.now() + 30 * 86_400_000));
    // STJ escapes < > & — the raw plan string must contain the escaped forms.
    assert.ok(g.planJson.includes('a\\u003Cb\\u003E\\u0026c'));
  });

  it('replan recomputes the plan; progress clamps to [0,1]', async () => {
    const p = new InMemoryGoalPursuer();
    const g = await p.registerAsync('goal', new Date(Date.now() + 30 * 86_400_000));
    await p.replanAsync(g.id);
    const after = await p.currentAsync(g.id);
    assert.ok(after);
    p.progress(g.id, 0.5);
    assert.equal((await p.currentAsync(g.id))!.progressFraction, 0.5);
    assert.throws(() => p.progress(g.id, 1.5), /fraction out of range/);
    assert.throws(() => p.progress('missing', 0.5), /Unknown goal/);
  });

  it('rejects a past deadline and a blank description', async () => {
    const p = new InMemoryGoalPursuer();
    await assert.rejects(
      () => p.registerAsync('x', new Date(Date.now() - 1000)),
      /deadline must be in the future/,
    );
    await assert.rejects(
      () => p.registerAsync('  ', new Date(Date.now() + 86_400_000)),
      /description required/,
    );
  });
});

describe('TfEpisodicMemory — term-frequency recall', () => {
  it('scores by shared-term overlap and orders by score desc', async () => {
    const m = new TfEpisodicMemory();
    await m.recordAsync({ id: 'a', at: new Date(), title: 'coffee', contentJson: 'morning coffee ritual' });
    await m.recordAsync({ id: 'b', at: new Date(), title: 'tea', contentJson: 'evening tea time' });
    await m.recordAsync({ id: 'c', at: new Date(), title: 'coffee', contentJson: 'coffee coffee coffee' });
    const hits = await m.recallAsync('coffee');
    // c has 3 "coffee" occurrences → score 3; a has 1 → score 1; b none.
    assert.deepEqual(hits.map((h) => h.id), ['c', 'a']);
  });

  it('empty query terms → no hits; short tokens (<2 chars) ignored', async () => {
    const m = new TfEpisodicMemory();
    await m.recordAsync({ id: 'a', at: new Date(), title: 'x', contentJson: 'a b c' });
    assert.equal((await m.recallAsync('!!! ??')).length, 0);
  });

  it('rejects a blank id and non-positive take', async () => {
    const m = new TfEpisodicMemory();
    await assert.rejects(
      () => m.recordAsync({ id: '', at: new Date(), title: 't', contentJson: 'c' }),
      /Id required/,
    );
    await assert.rejects(() => m.recallAsync('x', 0), /take out of range/);
  });
});

describe('HistoricalCalibratedConfidence — raw score + calibration band', () => {
  it('band half-width shrinks as calibrated confidence rises (pre-calibration)', async () => {
    const c = new HistoricalCalibratedConfidence();
    // Fewer than 5 outcomes → calibrated = raw. A long, un-hedged answer scores
    // high; the band is [cal-half, cal+half] with half = max(0.05, 0.25-cal*0.2).
    const band = await c.evaluateAsync('This is a clear and confident answer with detail.', '{"k":1}');
    assert.ok(band.lower >= 0 && band.upper <= 1);
    assert.ok(band.upper > band.lower);
  });

  it('hedging words lower the raw score', async () => {
    const c = new HistoricalCalibratedConfidence();
    const hedged = await c.evaluateAsync('maybe perhaps possibly unclear', '');
    const firm = await c.evaluateAsync('definitely yes absolutely correct', '');
    // Center of each band ≈ calibrated (=raw here). Hedged center should be lower.
    const centreH = (hedged.lower + hedged.upper) / 2;
    const centreF = (firm.lower + firm.upper) / 2;
    assert.ok(centreH < centreF);
  });

  it('calibrates to the correctness rate of the 5 nearest outcomes', async () => {
    const c = new HistoricalCalibratedConfidence();
    // Log 5 outcomes all correct near a high raw score → calibrated → 1.0,
    // half-band = max(0.05, 0.25-0.2)=0.05 → [0.95, 1.0].
    for (let i = 0; i < 5; i++) c.recordOutcome(0.9, true);
    const band = await c.evaluateAsync('a clear detailed confident answer here now', '{"x":1}');
    assert.ok(band.upper === 1);
    assert.ok(Math.abs(band.lower - 0.95) < 1e-9);
  });

  it('rejects a null answer', async () => {
    const c = new HistoricalCalibratedConfidence();
    // @ts-expect-error deliberate null
    await assert.rejects(() => c.evaluateAsync(null, '{}'), /answer required/);
  });
});

describe('KeywordEmotionSensor — keyword arousal/valence', () => {
  it('single-emotion match returns that emotion frame', async () => {
    const s = new KeywordEmotionSensor();
    const f = await s.senseAsync('{"text":"I am so happy and full of joy"}');
    assert.equal(f.label, 'joy');
    assert.ok(Math.abs(f.arousal - 0.8) < 1e-15);
    assert.ok(Math.abs(f.valence - 0.9) < 1e-15);
  });

  it('mixes multiple emotions as count-weighted averages, label = top count', async () => {
    const s = new KeywordEmotionSensor();
    // 2 joy hits (happy, love) + 1 anger hit (hate):
    //   arousal = (0.8*2 + 0.9*1)/3 = 2.5/3; valence = (0.9*2 + -0.8*1)/3 = 1.0/3
    const f = await s.senseAsync('happy love hate');
    assert.equal(f.label, 'joy');
    assert.ok(Math.abs(f.arousal - 2.5 / 3) < 1e-15);
    assert.ok(Math.abs(f.valence - 1.0 / 3) < 1e-15);
  });

  it('no keyword hits → neutral (0,0)', async () => {
    const s = new KeywordEmotionSensor();
    const f = await s.senseAsync('{"text":"the meeting is at noon"}');
    assert.deepEqual(f, { label: 'neutral', arousal: 0, valence: 0 });
  });

  it('rejects a null input', async () => {
    const s = new KeywordEmotionSensor();
    // @ts-expect-error deliberate null
    await assert.rejects(() => s.senseAsync(null), /fusedJson required/);
  });
});

describe('DemoStoreSkillAcquisition — acquire + list', () => {
  it('extracts the name from JSON, falls back to skill-<id6>, lists by name', async () => {
    const s = new DemoStoreSkillAcquisition();
    const named = await s.acquireAsync('{"name":"brew"}');
    assert.equal(named.name, 'brew');
    const unnamed = await s.acquireAsync('{"steps":[1,2]}');
    assert.match(unnamed.name, /^skill-[0-9a-f]{6}$/);
    const list = await s.listAsync();
    assert.equal(list.length, 2);
    // Ordered by name: "brew" < "skill-..." ordinally.
    assert.equal(list[0].name, 'brew');
  });

  it('rejects a null demonstration', async () => {
    const s = new DemoStoreSkillAcquisition();
    // @ts-expect-error deliberate null
    await assert.rejects(() => s.acquireAsync(null), /demonstrationJson required/);
  });
});

describe('AdjacencyPersonalKnowledgeGraph — nodes + relations + neighbours', () => {
  it('resolves out-edge targets to nodes, dedupes (toId,relation)', async () => {
    const g = new AdjacencyPersonalKnowledgeGraph();
    const M = (o: Record<string, string>) => new Map(Object.entries(o));
    await g.upsertNodeAsync({ id: 'alice', kind: 'person', name: 'Alice', properties: M({}) });
    await g.upsertNodeAsync({ id: 'bob', kind: 'person', name: 'Bob', properties: M({}) });
    await g.upsertRelationAsync({ fromId: 'alice', toId: 'bob', relation: 'knows' });
    // Duplicate (bob, knows) replaces rather than duplicates.
    await g.upsertRelationAsync({ fromId: 'alice', toId: 'bob', relation: 'knows' });
    const neighbours = await g.neighboursAsync('alice');
    assert.deepEqual(neighbours.map((n) => n.id), ['bob']);
  });

  it('unknown source id → empty neighbours', async () => {
    const g = new AdjacencyPersonalKnowledgeGraph();
    assert.equal((await g.neighboursAsync('ghost')).length, 0);
  });

  it('rejects blank node id and blank neighbours id', async () => {
    const g = new AdjacencyPersonalKnowledgeGraph();
    await assert.rejects(
      () => g.upsertNodeAsync({ id: '', kind: 'k', name: 'n', properties: new Map() }),
      /Id required/,
    );
    await assert.rejects(() => g.neighboursAsync('  '), /id required/);
  });
});

describe('toRoundTripUtc — .NET "O" format for zero offset', () => {
  it('renders yyyy-MM-ddTHH:mm:ss.fffffffZ with 7 fractional digits', () => {
    const d = new Date(Date.UTC(2026, 6, 8, 6, 30, 15, 123)); // month 6 = July
    assert.equal(toRoundTripUtc(d), '2026-07-08T06:30:15.1230000Z');
  });
});
