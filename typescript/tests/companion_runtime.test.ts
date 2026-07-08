// companion_runtime.test.ts
//
// Verifies the CircleAI.Memory.Runtime port (CompanionRuntime): start/stop
// lifecycle, catch-up-on-start, consolidateNow / syncNow forwarding, media
// ingestion forwarding, and the graceful "no subsystem wired" paths. The
// periodic timer loops are exercised only for their arm/disarm behaviour (the
// default 6h/24h/48h cadences never fire inside a unit test).

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  CompanionRuntime,
  DEFAULT_COMPANION_RUNTIME_OPTIONS,
  type CompanionRuntimeDeps,
} from '../src/memory/runtime/index';
import { SleepKind, type ConsolidationOutcome, type IMemoryConsolidator } from '../src/memory/consolidation';
import {
  MediaModality,
  MultimodalMemoryIngester,
  HeuristicMultimodalCaptioner,
  InMemoryMultimodalMemoryStore,
} from '../src/memory/multimodal';
import {
  HybridLogicalClock,
  InMemorySyncableEntryStore,
  InProcessSyncHub,
  InProcessCompanionStateChannel,
  CompanionStateSyncEngine,
  type ICompanionStateSyncEngine,
} from '../src/memory/sync/index';

// ─────────────────────────────────────────────────────────────────────────────
// Fakes
// ─────────────────────────────────────────────────────────────────────────────

/** Deterministic consolidator that records how it was ticked. */
class FakeConsolidator implements IMemoryConsolidator {
  readonly ticks: SleepKind[] = [];
  produce = 0; // how many "daily summaries" each tick claims to produce

  async tickAsync(kind: SleepKind): Promise<ConsolidationOutcome> {
    this.ticks.push(kind);
    return {
      kind,
      dailySummariesProduced: this.produce,
      semanticClustersProduced: 0,
      personaDeltasProduced: 0,
      corePromotions: 0,
      episodesPruned: 0,
      dailiesPruned: 0,
      semanticsPruned: 0,
      ranAtUtc: new Date('2026-06-06T00:00:00Z'),
    };
  }
}

/** Sync engine spy — counts startAsync / syncNowAsync / disposeAsync calls. */
class SpySyncEngine implements ICompanionStateSyncEngine {
  started = 0;
  synced = 0;
  disposed = 0;
  async startAsync(): Promise<void> {
    this.started++;
  }
  async syncNowAsync(): Promise<void> {
    this.synced++;
  }
  async writeLocalAsync(): Promise<never> {
    throw new Error('not used in this test');
  }
  async disposeAsync(): Promise<void> {
    this.disposed++;
  }
}

function buildIngester(): MultimodalMemoryIngester {
  return new MultimodalMemoryIngester([new HeuristicMultimodalCaptioner()], new InMemoryMultimodalMemoryStore());
}

// No timers should fire during the test → disable every periodic loop.
const NO_TIMERS = {
  dailyTickIntervalMs: 0,
  weeklyTickIntervalMs: 0,
  monthlyTickIntervalMs: 0,
  syncBroadcastIntervalMs: 0,
  initialDelayMs: 0,
};

// ─────────────────────────────────────────────────────────────────────────────
// Options defaults
// ─────────────────────────────────────────────────────────────────────────────

describe('CompanionRuntimeOptions defaults', () => {
  it('match the C# TimeSpan defaults in milliseconds', () => {
    assert.equal(DEFAULT_COMPANION_RUNTIME_OPTIONS.dailyTickIntervalMs, 6 * 60 * 60 * 1000);
    assert.equal(DEFAULT_COMPANION_RUNTIME_OPTIONS.weeklyTickIntervalMs, 24 * 60 * 60 * 1000);
    assert.equal(DEFAULT_COMPANION_RUNTIME_OPTIONS.monthlyTickIntervalMs, 48 * 60 * 60 * 1000);
    assert.equal(DEFAULT_COMPANION_RUNTIME_OPTIONS.syncBroadcastIntervalMs, 5 * 60 * 1000);
    assert.equal(DEFAULT_COMPANION_RUNTIME_OPTIONS.initialDelayMs, 30 * 1000);
    assert.equal(DEFAULT_COMPANION_RUNTIME_OPTIONS.catchUpOnStart, true);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Lifecycle
// ─────────────────────────────────────────────────────────────────────────────

describe('CompanionRuntime — start/stop', () => {
  it('requires a consolidator', () => {
    assert.throws(() => new CompanionRuntime({} as CompanionRuntimeDeps), /consolidator required/);
  });

  it('runs a catch-up OnDemand tick on start when enabled', async () => {
    const consolidator = new FakeConsolidator();
    const rt = new CompanionRuntime({ consolidator, options: { ...NO_TIMERS, catchUpOnStart: true } });
    await rt.startAsync();
    assert.deepEqual(consolidator.ticks, [SleepKind.OnDemand]);
    await rt.stopAsync();
  });

  it('skips the catch-up tick when disabled', async () => {
    const consolidator = new FakeConsolidator();
    const rt = new CompanionRuntime({ consolidator, options: { ...NO_TIMERS, catchUpOnStart: false } });
    await rt.startAsync();
    assert.deepEqual(consolidator.ticks, []);
    await rt.stopAsync();
  });

  it('starts and disposes a wired sync engine', async () => {
    const consolidator = new FakeConsolidator();
    const engine = new SpySyncEngine();
    const rt = new CompanionRuntime({
      consolidator,
      syncEngine: engine,
      options: { ...NO_TIMERS, catchUpOnStart: false },
    });
    await rt.startAsync();
    assert.equal(engine.started, 1);
    await rt.stopAsync();
    assert.equal(engine.disposed, 1);
  });

  it('does not throw when no sync engine is wired', async () => {
    const consolidator = new FakeConsolidator();
    const rt = new CompanionRuntime({ consolidator, options: { ...NO_TIMERS, catchUpOnStart: false } });
    await rt.startAsync();
    await rt.syncNowAsync(); // no-op
    await rt.stopAsync();
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Public helpers
// ─────────────────────────────────────────────────────────────────────────────

describe('CompanionRuntime — consolidateNow / syncNow', () => {
  it('consolidateNow ticks OnDemand and returns the outcome', async () => {
    const consolidator = new FakeConsolidator();
    consolidator.produce = 4;
    const rt = new CompanionRuntime({ consolidator, options: { ...NO_TIMERS, catchUpOnStart: false } });
    await rt.startAsync();
    const outcome = await rt.consolidateNowAsync();
    assert.equal(outcome.kind, SleepKind.OnDemand);
    assert.equal(outcome.dailySummariesProduced, 4);
    assert.deepEqual(consolidator.ticks, [SleepKind.OnDemand]);
    await rt.stopAsync();
  });

  it('syncNow forwards to the sync engine', async () => {
    const consolidator = new FakeConsolidator();
    const engine = new SpySyncEngine();
    const rt = new CompanionRuntime({
      consolidator,
      syncEngine: engine,
      options: { ...NO_TIMERS, catchUpOnStart: false },
    });
    await rt.startAsync();
    await rt.syncNowAsync();
    assert.equal(engine.synced, 1);
    await rt.stopAsync();
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Media ingestion
// ─────────────────────────────────────────────────────────────────────────────

describe('CompanionRuntime — ingestMedia', () => {
  it('throws when no ingester is wired', () => {
    const consolidator = new FakeConsolidator();
    const rt = new CompanionRuntime({ consolidator, options: { ...NO_TIMERS, catchUpOnStart: false } });
    assert.throws(
      () => rt.ingestMediaAsync(MediaModality.Image, new Uint8Array([1, 2, 3])),
      /without a MultimodalMemoryIngester/,
    );
  });

  it('forwards ingestion to the ingester and stores an entry', async () => {
    const consolidator = new FakeConsolidator();
    const ingester = buildIngester();
    const rt = new CompanionRuntime({
      consolidator,
      ingester,
      options: { ...NO_TIMERS, catchUpOnStart: false },
    });
    await rt.startAsync();
    const result = await rt.ingestMediaAsync(
      MediaModality.Image,
      new Uint8Array([9, 8, 7, 6]),
      'image/png',
      'file://pic.png',
      { album: 'holiday' },
    );
    assert.equal(result.wasDeduplicated, false);
    assert.equal(result.entry.modality, MediaModality.Image);
    assert.equal(result.entry.sourceMimeType, 'image/png');
    assert.equal(result.entry.tags?.album, 'holiday');

    // Second ingest of identical bytes must dedupe.
    const again = await rt.ingestMediaAsync(MediaModality.Image, new Uint8Array([9, 8, 7, 6]), 'image/png');
    assert.equal(again.wasDeduplicated, true);
    await rt.stopAsync();
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Integration — runtime driving a real sync engine end to end
// ─────────────────────────────────────────────────────────────────────────────

describe('CompanionRuntime — with a real sync engine', () => {
  it('syncNow broadcasts the local state vector so a peer catches up', async () => {
    const hub = new InProcessSyncHub();

    const chA = new InProcessCompanionStateChannel(hub, 'A');
    const storeA = new InMemorySyncableEntryStore();
    const engineA = new CompanionStateSyncEngine(chA, storeA, new HybridLogicalClock(1, () => 1000n));

    const chB = new InProcessCompanionStateChannel(hub, 'B');
    const storeB = new InMemorySyncableEntryStore();
    const engineB = new CompanionStateSyncEngine(chB, storeB, new HybridLogicalClock(2, () => 1000n));
    await engineB.startAsync();

    const consolidator = new FakeConsolidator();
    const rt = new CompanionRuntime({
      consolidator,
      syncEngine: engineA,
      options: { ...NO_TIMERS, catchUpOnStart: false },
    });
    await rt.startAsync(); // starts engineA

    await engineA.writeLocalAsync('DailyMemorySummary', 'd1', 'today was good');
    await rt.syncNowAsync();
    for (let i = 0; i < 20; i++) await Promise.resolve();

    assert.equal((await storeB.getAsync('DailyMemorySummary', 'd1'))?.payload, 'today was good');

    await rt.stopAsync();
    await engineB.disposeAsync();
  });
});
