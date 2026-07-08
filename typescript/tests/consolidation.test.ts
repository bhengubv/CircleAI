// consolidation.test.ts
//
// Verifies the hierarchical memory-consolidation subsystem ported from
// CircleAI.Memory.Consolidation. Uses a fixed injected clock and hand-built
// EpisodicMemoryEntry lists so every deterministic formula can be asserted
// exactly. Covers: daily summary produced for a completed day + idempotency,
// today's episodes excluded, the salience/topicConcentration formula on a small
// example, weekly clustering's 2-day threshold, high-salience → core promotion,
// retention pruning, persona-delta new-topic detection, and full-cosine ranking
// in the in-memory stores.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  SleepKind,
  CoreMemoryKind,
  HeuristicSummarizer,
  MemoryConsolidator,
  InMemoryDailyMemoryStore,
  InMemorySemanticMemoryStore,
  InMemoryPersonaDeltaStore,
  InMemoryCoreMemoryStore,
  cosineFull,
  dayKeyOf,
  mondayOf,
  addDays,
  monthFirstDayOf,
  createDailySummary,
  createCoreMemory,
  createSemanticCluster,
  type DailyMemorySummary,
} from '../src/memory/consolidation';
import { InMemoryEpisodicStore, InMemoryPersonaStore } from '../src/memory/stores';
import { PersonaState } from '../src/memory/index';
import type { EpisodicMemoryEntry } from '../src/memory/index';

// ── Fixtures ────────────────────────────────────────────────────────────────

let idCounter = 0;
function entry(overrides: Partial<EpisodicMemoryEntry> = {}): EpisodicMemoryEntry {
  return {
    id: overrides.id ?? `e${idCounter++}`,
    recordedAtUtc: overrides.recordedAtUtc ?? new Date('2026-06-01T12:00:00Z'),
    userText: overrides.userText ?? 'u',
    assistantText: overrides.assistantText ?? 'a',
    embedding: overrides.embedding,
    tags: overrides.tags,
    appContext: overrides.appContext,
  };
}

/** Clock fixed at a Monday (2026-06-08 is a Monday) so week math is stable. */
function fixedClock(iso: string): () => Date {
  const d = new Date(iso);
  return () => d;
}

/** Wires a consolidator over fresh in-memory stores; returns the parts. */
function makeConsolidator(clock: () => Date, options?: ConstructorParameters<typeof MemoryConsolidator>[7]) {
  const episodic = new InMemoryEpisodicStore(100000);
  const daily = new InMemoryDailyMemoryStore();
  const semantic = new InMemorySemanticMemoryStore();
  const personaDelta = new InMemoryPersonaDeltaStore();
  const core = new InMemoryCoreMemoryStore();
  const personaStore = new InMemoryPersonaStore();
  const summarizer = new HeuristicSummarizer({ clock });
  const consolidator = new MemoryConsolidator(
    episodic,
    daily,
    semantic,
    personaDelta,
    core,
    personaStore,
    summarizer,
    options,
    clock,
  );
  return { episodic, daily, semantic, personaDelta, core, personaStore, summarizer, consolidator };
}

// ── Day helpers ───────────────────────────────────────────────────────────

describe('day helpers', () => {
  it('dayKeyOf uses UTC calendar day', () => {
    assert.equal(dayKeyOf(new Date('2026-06-08T23:59:59Z')), '2026-06-08');
    assert.equal(dayKeyOf(new Date('2026-01-05T00:00:00Z')), '2026-01-05');
  });

  it('mondayOf returns the Monday of the week (Sunday=0)', () => {
    assert.equal(mondayOf('2026-06-08'), '2026-06-08'); // Monday → itself
    assert.equal(mondayOf('2026-06-14'), '2026-06-08'); // Sunday → prior Monday
    assert.equal(mondayOf('2026-06-10'), '2026-06-08'); // Wednesday → Monday
  });

  it('addDays crosses month boundaries', () => {
    assert.equal(addDays('2026-06-01', -1), '2026-05-31');
    assert.equal(addDays('2026-06-30', 1), '2026-07-01');
  });

  it('monthFirstDayOf yields the first of the month', () => {
    assert.equal(monthFirstDayOf('2026-06-17'), '2026-06-01');
  });
});

// ── cosineFull ────────────────────────────────────────────────────────────

describe('cosineFull', () => {
  it('is 1 for identical direction, 0 for orthogonal, and normalises magnitude', () => {
    assert.equal(cosineFull([1, 0], [1, 0]), 1);
    assert.equal(cosineFull([1, 0], [0, 1]), 0);
    // Not L2-normalised inputs: full cosine still yields 1 for same direction.
    assert.ok(Math.abs(cosineFull([3, 0], [7, 0]) - 1) < 1e-12);
  });

  it('returns 0 on a length mismatch or a zero vector', () => {
    assert.equal(cosineFull([1, 0], [1, 0, 0]), 0);
    assert.equal(cosineFull([0, 0], [1, 0]), 0);
  });
});

// ── Daily summarization formulas ────────────────────────────────────────────

describe('HeuristicSummarizer.summarizeDayAsync — formulas', () => {
  it('computes topic weights, dispersion, topicConcentration and salience exactly', async () => {
    const s = new HeuristicSummarizer({ clock: fixedClock('2026-06-02T00:00:00Z') });
    // 3 entries: finance×2 (topic tag) + health×1; embeddings [1,0],[0,1],[1,0].
    const entries: EpisodicMemoryEntry[] = [
      entry({ id: 'a', embedding: [1, 0], tags: { topic: 'finance' } }),
      entry({ id: 'b', embedding: [0, 1], tags: { topic: 'health' } }),
      entry({ id: 'c', embedding: [1, 0], tags: { topic: 'finance' } }),
    ];
    const summary = await s.summarizeDayAsync('2026-06-01', entries);

    assert.equal(summary.episodeCount, 3);
    assert.equal(summary.topicWeights.get('finance'), 2);
    assert.equal(summary.topicWeights.get('health'), 1);
    // dispersion = mean(1-cos) over pairs = (1 + 0 + 1)/3 = 2/3
    assert.ok(Math.abs(summary.topicDispersion - 2 / 3) < 1e-12);
    // salience = volume(3/30=0.1)*0.4 + disp(2/3)*0.3 + conc(2/3)*0.3 = 0.44
    assert.ok(Math.abs(summary.salience - 0.44) < 1e-12);
    // summary text shape
    assert.ok(summary.summary.startsWith('On 2026-06-01 you had 3 exchanges.'));
    assert.ok(summary.summary.includes('Top topics: finance, health.'));
  });

  it('splits pipe-delimited "topics" and lowercases/trims', async () => {
    const s = new HeuristicSummarizer();
    const summary = await s.summarizeDayAsync('2026-06-01', [
      entry({ tags: { topics: 'Finance | Health |finance' } }),
    ]);
    assert.equal(summary.topicWeights.get('finance'), 2);
    assert.equal(summary.topicWeights.get('health'), 1);
  });

  it('uses topicConcentration 0.5 when there are no topics', async () => {
    const s = new HeuristicSummarizer();
    // 1 entry, no tags, no embedding → dispersion 0, volume 1/30, conc 0.5
    const summary = await s.summarizeDayAsync('2026-06-01', [entry({})]);
    const expected = (1 / 30) * 0.4 + 0 * 0.3 + 0.5 * 0.3;
    assert.ok(Math.abs(summary.salience - expected) < 1e-12);
    // A single entry is always a highlight, so the standout clause is appended
    // (userText defaults to "u"). No topics → no "Top topics" clause.
    assert.equal(summary.summary, 'On 2026-06-01 you had 1 exchange. Standout moment: "u".');
    assert.ok(!summary.summary.includes('Top topics'));
  });

  it('returns an empty-day summary for zero entries', async () => {
    const s = new HeuristicSummarizer();
    const summary = await s.summarizeDayAsync('2026-06-01', []);
    assert.equal(summary.episodeCount, 0);
    assert.equal(summary.summary, 'No exchanges recorded on 2026-06-01.');
  });
});

// ── Daily pass: production, idempotency, today-exclusion ─────────────────────

describe('MemoryConsolidator — daily pass', () => {
  it('produces a summary for a completed day and is idempotent on re-tick', async () => {
    const clock = fixedClock('2026-06-08T09:00:00Z'); // "today" = 2026-06-08
    const { episodic, daily, consolidator } = makeConsolidator(clock);
    await episodic.addAsync(entry({ recordedAtUtc: new Date('2026-06-06T10:00:00Z'), tags: { topic: 'x' } }));
    await episodic.addAsync(entry({ recordedAtUtc: new Date('2026-06-06T11:00:00Z'), tags: { topic: 'x' } }));

    const r1 = await consolidator.tickAsync(SleepKind.Daily);
    assert.equal(r1.dailySummariesProduced, 1);
    const summary = await daily.getAsync('2026-06-06');
    assert.ok(summary);
    assert.equal(summary!.episodeCount, 2);

    // Second tick with no new episodes → idempotent skip (episodeCount matches).
    const r2 = await consolidator.tickAsync(SleepKind.Daily);
    assert.equal(r2.dailySummariesProduced, 0);
    assert.equal(await daily.countAsync(), 1);
  });

  it('does NOT summarise today\'s (incomplete) day', async () => {
    const clock = fixedClock('2026-06-08T09:00:00Z');
    const { episodic, daily, consolidator } = makeConsolidator(clock);
    // Episode recorded "today" → excluded (day is not < today).
    await episodic.addAsync(entry({ recordedAtUtc: new Date('2026-06-08T08:00:00Z') }));

    const r = await consolidator.tickAsync(SleepKind.Daily);
    assert.equal(r.dailySummariesProduced, 0);
    assert.equal(await daily.countAsync(), 0);
  });

  it('re-summarises a day when new episodes arrive for it (count mismatch)', async () => {
    const clock = fixedClock('2026-06-08T09:00:00Z');
    const { episodic, daily, consolidator } = makeConsolidator(clock);
    await episodic.addAsync(entry({ id: 'p1', recordedAtUtc: new Date('2026-06-06T10:00:00Z') }));
    await consolidator.tickAsync(SleepKind.Daily);
    assert.equal((await daily.getAsync('2026-06-06'))!.episodeCount, 1);

    await episodic.addAsync(entry({ id: 'p2', recordedAtUtc: new Date('2026-06-06T12:00:00Z') }));
    const r = await consolidator.tickAsync(SleepKind.Daily);
    assert.equal(r.dailySummariesProduced, 1);
    assert.equal((await daily.getAsync('2026-06-06'))!.episodeCount, 2);
  });
});

// ── High-salience daily → core promotion (≥0.80) ────────────────────────────

describe('MemoryConsolidator — core promotion from a high-salience day', () => {
  it('promotes a day whose salience ≥ 0.80 to a HighSalience core memory', async () => {
    const clock = fixedClock('2026-06-08T09:00:00Z');
    const { episodic, core, consolidator } = makeConsolidator(clock);

    // 30 entries, single topic 'finance' (conc=1); embeddings 15×[1,0] + 15×[0,1]
    // → dispersion ≈ 0.5172, salience ≈ 0.8552 (≥ 0.80).
    for (let i = 0; i < 30; i++) {
      await episodic.addAsync(
        entry({
          id: `h${i}`,
          recordedAtUtc: new Date(`2026-06-06T${String(i % 24).padStart(2, '0')}:00:00Z`),
          embedding: i < 15 ? [1, 0] : [0, 1],
          tags: { topic: 'finance' },
        }),
      );
    }

    const r = await consolidator.tickAsync(SleepKind.Daily);
    assert.equal(r.dailySummariesProduced, 1);
    assert.equal(r.corePromotions, 1);

    const all = await core.listAllAsync();
    assert.equal(all.length, 1);
    assert.equal(all[0].kind, CoreMemoryKind.HighSalience);
    assert.equal(all[0].topic, 'finance');
    assert.equal(all[0].statement, '"finance" mattered enough on 2026-06-06 to be remembered.');
    // Highlight embedding carried onto the core memory.
    assert.ok(all[0].embedding != null);
  });

  it('does NOT promote a low-salience day', async () => {
    const clock = fixedClock('2026-06-08T09:00:00Z');
    const { episodic, core, consolidator } = makeConsolidator(clock);
    await episodic.addAsync(entry({ recordedAtUtc: new Date('2026-06-06T10:00:00Z'), tags: { topic: 'x' } }));
    const r = await consolidator.tickAsync(SleepKind.Daily);
    assert.equal(r.corePromotions, 0);
    assert.equal(await core.countAsync(), 0);
  });
});

// ── Weekly clustering + 2-day threshold ─────────────────────────────────────

describe('HeuristicSummarizer.consolidateWeekAsync — 2-day threshold', () => {
  it('clusters only topics appearing in ≥ 2 days, salience per formula', async () => {
    const s = new HeuristicSummarizer({ clock: fixedClock('2026-06-08T00:00:00Z') });
    // Day1: finance=1, health=1 ; Day2: finance=1.
    // finance → 2 days (weight 2) → cluster ; health → 1 day → excluded.
    const day1 = createDailySummary({
      day: '2026-06-01',
      episodeCount: 2,
      topicWeights: new Map([
        ['finance', 1],
        ['health', 1],
      ]),
    });
    const day2 = createDailySummary({
      day: '2026-06-02',
      episodeCount: 1,
      topicWeights: new Map([['finance', 1]]),
    });

    const clusters = await s.consolidateWeekAsync('2026-06-01', [day1, day2]);
    assert.equal(clusters.length, 1);
    assert.equal(clusters[0].topic, 'finance');
    assert.equal(clusters[0].topicWeight, 2);
    // salience = min(1, 2/3 + (2/7)*0.25) = 0.7380952…
    assert.ok(Math.abs(clusters[0].salience - (2 / 3 + (2 / 7) * 0.25)) < 1e-12);
    assert.equal(clusters[0].summary, 'Across 2 days this week you returned to "finance" — 3 exchanges in total.');
    assert.deepEqual([...clusters[0].sourceDailyIds].sort(), [day1.id, day2.id].sort());
  });

  it('returns no clusters when every topic is single-day', async () => {
    const s = new HeuristicSummarizer();
    const clusters = await s.consolidateWeekAsync('2026-06-01', [
      createDailySummary({ day: '2026-06-01', topicWeights: new Map([['a', 1]]) }),
      createDailySummary({ day: '2026-06-02', topicWeights: new Map([['b', 1]]) }),
    ]);
    assert.equal(clusters.length, 0);
  });

  it('computes the centroid as the mean of highlight embeddings', async () => {
    const s = new HeuristicSummarizer();
    const h1 = entry({ id: 'h1', embedding: [2, 0] });
    const h2 = entry({ id: 'h2', embedding: [0, 4] });
    const day1 = createDailySummary({
      day: '2026-06-01',
      topicWeights: new Map([['t', 1]]),
      highlightEntries: [h1],
    });
    const day2 = createDailySummary({
      day: '2026-06-02',
      topicWeights: new Map([['t', 1]]),
      highlightEntries: [h2],
    });
    const clusters = await s.consolidateWeekAsync('2026-06-01', [day1, day2]);
    assert.equal(clusters.length, 1);
    assert.deepEqual(clusters[0].centroidEmbedding, [1, 2]); // ([2,0]+[0,4])/2
  });
});

describe('MemoryConsolidator — weekly pass', () => {
  it('clusters the last completed week and is idempotent', async () => {
    // "today" Monday 2026-06-08 → thisMonday 06-08, lastMonday 06-01..lastSunday 06-07.
    const clock = fixedClock('2026-06-08T09:00:00Z');
    const { daily, semantic, consolidator } = makeConsolidator(clock);
    await daily.upsertAsync(
      createDailySummary({ day: '2026-06-01', episodeCount: 2, topicWeights: new Map([['finance', 1]]) }),
    );
    await daily.upsertAsync(
      createDailySummary({ day: '2026-06-03', episodeCount: 1, topicWeights: new Map([['finance', 1]]) }),
    );

    const r1 = await consolidator.tickAsync(SleepKind.Weekly);
    assert.equal(r1.semanticClustersProduced, 1);
    assert.equal(await semantic.countAsync(), 1);

    const r2 = await consolidator.tickAsync(SleepKind.Weekly);
    assert.equal(r2.semanticClustersProduced, 0); // getWeek non-empty → skip
    assert.equal(await semantic.countAsync(), 1);
  });
});

// ── Retention pruning ───────────────────────────────────────────────────────

describe('MemoryConsolidator — retention', () => {
  it('prunes episodic entries older than 7 days on the daily pass', async () => {
    const clock = fixedClock('2026-06-08T09:00:00Z');
    const { episodic, consolidator } = makeConsolidator(clock);
    // cutoff = now - 7 days = 2026-06-01T09:00:00Z
    await episodic.addAsync(entry({ id: 'old', recordedAtUtc: new Date('2026-05-20T00:00:00Z') }));
    await episodic.addAsync(entry({ id: 'fresh', recordedAtUtc: new Date('2026-06-06T00:00:00Z') }));

    const r = await consolidator.tickAsync(SleepKind.Daily);
    assert.equal(r.episodesPruned, 1);
    assert.equal(await episodic.countAsync(), 1);
    const remaining = await episodic.getRecentAsync(10);
    assert.equal(remaining[0].id, 'fresh');
  });

  it('prunes daily summaries older than 30 days on the weekly pass', async () => {
    const clock = fixedClock('2026-06-08T09:00:00Z');
    const { daily, consolidator } = makeConsolidator(clock);
    // cutoff = 2026-06-08 - 30 = 2026-05-09. Older day is pruned.
    await daily.upsertAsync(createDailySummary({ day: '2026-04-01' })); // < cutoff → pruned
    await daily.upsertAsync(createDailySummary({ day: '2026-06-03' })); // kept

    const r = await consolidator.tickAsync(SleepKind.Weekly);
    assert.equal(r.dailiesPruned, 1);
    assert.equal(await daily.getAsync('2026-04-01'), null);
    assert.ok(await daily.getAsync('2026-06-03'));
  });

  it('prunes semantic clusters older than 365 days on the monthly pass', async () => {
    const clock = fixedClock('2026-06-08T09:00:00Z');
    const { semantic, consolidator } = makeConsolidator(clock);
    // cutoff = 2026-06-08 - 365 = 2025-06-08.
    await semantic.addAsync(createSemanticCluster({ weekStartingMonday: '2024-01-01', topic: 't' }));
    await semantic.addAsync(createSemanticCluster({ weekStartingMonday: '2026-05-04', topic: 't' }));

    const r = await consolidator.tickAsync(SleepKind.Monthly);
    assert.equal(r.semanticsPruned, 1);
    assert.equal(await semantic.countAsync(), 1);
  });
});

// ── Monthly persona-delta ───────────────────────────────────────────────────

describe('MemoryConsolidator — monthly persona delta', () => {
  it('derives a delta detecting a new topic and is idempotent by month', async () => {
    // "today" 2026-06-08 → previous month = May 2026 (2026-05-01..2026-05-31).
    const clock = fixedClock('2026-06-08T09:00:00Z');
    const { daily, personaDelta, personaStore, consolidator } = makeConsolidator(clock);

    // A daily summary inside May so the month has data.
    await daily.upsertAsync(createDailySummary({ day: '2026-05-15', episodeCount: 4 }));

    // Persona "after" has a topic the fresh "before" lacks → newTopic.
    const after = new PersonaState();
    after.userId = 'default';
    after.topicWeights = { finance: 3 };
    after.totalInteractions = 10;
    after.positiveSignals = 6;
    after.negativeSignals = 1;
    await personaStore.saveAsync(after);

    const r1 = await consolidator.tickAsync(SleepKind.Monthly);
    assert.equal(r1.personaDeltasProduced, 1);
    const deltas = await personaDelta.getForUserAsync('default');
    assert.equal(deltas.length, 1);
    assert.equal(deltas[0].newTopics.get('finance'), 3);
    assert.equal(deltas[0].periodStart, '2026-05-15');
    assert.equal(deltas[0].periodEnd, '2026-05-15');
    assert.ok(deltas[0].narrative.includes('New interests appeared: finance.'));

    // Second monthly tick → idempotent (delta already exists for May).
    const r2 = await consolidator.tickAsync(SleepKind.Monthly);
    assert.equal(r2.personaDeltasProduced, 0);
    assert.equal((await personaDelta.getForUserAsync('default')).length, 1);
  });

  it('produces no delta when the previous month has no daily summaries', async () => {
    const clock = fixedClock('2026-06-08T09:00:00Z');
    const { consolidator, personaDelta } = makeConsolidator(clock);
    const r = await consolidator.tickAsync(SleepKind.Monthly);
    assert.equal(r.personaDeltasProduced, 0);
    assert.equal(await personaDelta.countAsync(), 0);
  });
});

describe('HeuristicSummarizer.derivePersonaDeltaAsync', () => {
  it('separates new topics from strengthened ones and computes signal deltas', async () => {
    const s = new HeuristicSummarizer();
    const before = new PersonaState();
    before.topicWeights = { finance: 2 };
    before.positiveSignals = 1;
    before.negativeSignals = 1;
    before.totalInteractions = 5;
    before.verbosity = 'balanced';

    const after = new PersonaState();
    after.topicWeights = { finance: 5, travel: 3 }; // finance strengthened(+3), travel new
    after.positiveSignals = 7;
    after.negativeSignals = 2;
    after.totalInteractions = 20;
    after.verbosity = 'detailed';

    const day = createDailySummary({ day: '2026-05-10' });
    const delta = await s.derivePersonaDeltaAsync(before, after, [day]);

    assert.equal(delta.newTopics.get('travel'), 3);
    assert.equal(delta.newTopics.has('finance'), false);
    assert.equal(delta.strengthenedTopics.get('finance'), 3);
    // netSignals = (7-1) - (2-1) = 5 ; interactions = 20-5 = 15
    assert.equal(delta.netSignalDelta, 5);
    assert.equal(delta.interactionsInPeriod, 15);
    assert.ok(delta.narrative.includes('Preferred verbosity shifted from balanced to detailed.'));
    assert.ok(delta.narrative.includes('Net feedback was positive (+5).'));
  });
});

// ── OnDemand runs every tier ────────────────────────────────────────────────

describe('MemoryConsolidator — OnDemand', () => {
  it('runs daily, weekly and monthly passes in one tick', async () => {
    const clock = fixedClock('2026-06-08T09:00:00Z');
    const { episodic, daily, semantic, personaStore, personaDelta, consolidator } =
      makeConsolidator(clock);

    // Daily fuel: a completed day earlier this week.
    await episodic.addAsync(entry({ recordedAtUtc: new Date('2026-06-06T10:00:00Z'), tags: { topic: 'finance' } }));
    await episodic.addAsync(entry({ recordedAtUtc: new Date('2026-06-06T11:00:00Z'), tags: { topic: 'finance' } }));
    // Weekly fuel: dailies inside last week (06-01..06-07) with a repeated topic.
    await daily.upsertAsync(createDailySummary({ day: '2026-06-01', episodeCount: 2, topicWeights: new Map([['finance', 1]]) }));
    await daily.upsertAsync(createDailySummary({ day: '2026-06-02', episodeCount: 1, topicWeights: new Map([['finance', 1]]) }));
    // Monthly fuel: a daily inside May + a persona.
    await daily.upsertAsync(createDailySummary({ day: '2026-05-20', episodeCount: 3 }));
    const p = new PersonaState();
    p.topicWeights = { finance: 2 };
    p.totalInteractions = 6;
    await personaStore.saveAsync(p);

    const r = await consolidator.tickAsync(SleepKind.OnDemand);
    assert.equal(r.kind, SleepKind.OnDemand);
    assert.ok(r.dailySummariesProduced >= 1);
    assert.ok(r.semanticClustersProduced >= 1);
    assert.equal(r.personaDeltasProduced, 1);
    assert.equal(r.ranAtUtc.getTime(), clock().getTime());
    assert.ok(await semantic.countAsync() >= 1);
    assert.ok((await personaDelta.getForUserAsync('default')).length === 1);
  });
});

// ── In-memory store cosine ranking + ordering ───────────────────────────────

describe('in-memory stores — full-cosine search and ordering', () => {
  it('CoreMemoryStore ranks by full cosine to the query centroid', async () => {
    const core = new InMemoryCoreMemoryStore();
    await core.addAsync(createCoreMemory({ statement: 'x', embedding: [1, 0] }));
    await core.addAsync(createCoreMemory({ statement: 'y', embedding: [0, 1] }));
    await core.addAsync(createCoreMemory({ statement: 'diag', embedding: [1, 1] }));

    const ranked = await core.searchAsync([1, 0], 3);
    assert.equal(ranked[0].statement, 'x'); // cos 1
    assert.equal(ranked[2].statement, 'y'); // cos 0
    // 'diag' cos([1,1],[1,0]) = 0.707 → middle
    assert.equal(ranked[1].statement, 'diag');
  });

  it('CoreMemoryStore falls back to reinforcement order when query is null', async () => {
    const core = new InMemoryCoreMemoryStore();
    const a = createCoreMemory({ statement: 'a' });
    const b = createCoreMemory({ statement: 'b' });
    await core.addAsync(a);
    await core.addAsync(b);
    await core.reinforceAsync(b.id);
    await core.reinforceAsync(b.id);

    const top = await core.searchAsync(null, 2);
    assert.equal(top[0].statement, 'b'); // more reinforced first
    assert.equal(top[0].reinforcementCount, 2);
  });

  it('SemanticMemoryStore.getWeek orders by topicWeight desc; search ranks by centroid cosine', async () => {
    const sem = new InMemorySemanticMemoryStore();
    await sem.addAsync(createSemanticCluster({ weekStartingMonday: '2026-06-01', topic: 'low', topicWeight: 1, centroidEmbedding: [0, 1] }));
    await sem.addAsync(createSemanticCluster({ weekStartingMonday: '2026-06-01', topic: 'high', topicWeight: 5, centroidEmbedding: [1, 0] }));

    const week = await sem.getWeekAsync('2026-06-01');
    assert.deepEqual(week.map((c) => c.topic), ['high', 'low']);

    const ranked = await sem.searchAsync([1, 0], 2);
    assert.equal(ranked[0].topic, 'high'); // centroid [1,0] cos 1
  });

  it('DailyMemoryStore.getRange returns day-ordered inclusive results', async () => {
    const daily = new InMemoryDailyMemoryStore();
    await daily.upsertAsync(createDailySummary({ day: '2026-06-03' }));
    await daily.upsertAsync(createDailySummary({ day: '2026-06-01' }));
    await daily.upsertAsync(createDailySummary({ day: '2026-06-10' }));

    const range = await daily.getRangeAsync('2026-06-01', '2026-06-05');
    assert.deepEqual(range.map((d) => d.day), ['2026-06-01', '2026-06-03']);
  });
});
