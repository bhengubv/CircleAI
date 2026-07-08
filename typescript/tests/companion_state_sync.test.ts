// companion_state_sync.test.ts
//
// Verifies the CircleAI.Memory.Sync port: the Hybrid Logical Clock, the
// SyncableEntry store apply rules, the Announce/Request/Push convergence
// protocol over the in-process hub, and the three typed bridges
// (PersonaState / ConversationState / LoraAdapter).

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  HybridLogicalClock,
  InMemorySyncableEntryStore,
  InProcessSyncHub,
  InProcessCompanionStateChannel,
  CompanionStateSyncEngine,
  SyncEnvelopeKind,
  PersonaStateSyncBridge,
  CompanionConversationSyncBridge,
  LoraAdapterSyncBridge,
  InMemoryAdapterFileStore,
  type SyncableEntry,
  type ConversationStateDelta,
} from '../src/memory/sync/index';
import { PersonaState } from '../src/memory/index';

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

function sha256Hex(s: string): string {
  return createHash('sha256').update(s, 'utf8').digest('hex');
}

function entry(partial: Partial<SyncableEntry> & Pick<SyncableEntry, 'entityType' | 'entityId' | 'version'>): SyncableEntry {
  const payload = partial.payload ?? '';
  return {
    entityType: partial.entityType,
    entityId: partial.entityId,
    version: partial.version,
    isTombstone: partial.isTombstone ?? false,
    contentHash: partial.contentHash ?? sha256Hex(payload),
    payload,
    sourceNodeId: partial.sourceNodeId ?? 'nodeA',
    authoredAt: partial.authoredAt ?? new Date('2026-01-01T00:00:00Z'),
  };
}

/** Drains all currently-pending microtasks so async loopback delivery settles. */
async function settle(): Promise<void> {
  for (let i = 0; i < 20; i++) await Promise.resolve();
}

// ─────────────────────────────────────────────────────────────────────────────
// HybridLogicalClock
// ─────────────────────────────────────────────────────────────────────────────

describe('HybridLogicalClock — compose/decompose', () => {
  it('round-trips the three components', () => {
    const version = HybridLogicalClock.compose(1_700_000_000_000n, 42n, 7n);
    const [phys, logical, node] = HybridLogicalClock.decompose(version);
    assert.equal(phys, 1_700_000_000_000n);
    assert.equal(logical, 42n);
    assert.equal(node, 7n);
  });

  it('masks logical to 10 bits and node to 6 bits', () => {
    // logical 0x3FF is the max; 0x400 wraps to 0; node 0x3F is the max.
    const v = HybridLogicalClock.compose(5n, 0x3ffn, 0x3fn);
    const [, logical, node] = HybridLogicalClock.decompose(v);
    assert.equal(logical, 0x3ffn);
    assert.equal(node, 0x3fn);
  });

  it('rejects nodeShortId outside 0..63', () => {
    assert.throws(() => new HybridLogicalClock(64), /0\.\.63/);
    assert.throws(() => new HybridLogicalClock(-1), /0\.\.63/);
  });
});

describe('HybridLogicalClock — tick monotonicity', () => {
  it('increments the logical counter when physical time is frozen', () => {
    let now = 1000n;
    const clk = new HybridLogicalClock(3, () => now);
    const v1 = clk.tick();
    const v2 = clk.tick();
    const v3 = clk.tick();
    assert.ok(v2 > v1, 'v2 must exceed v1');
    assert.ok(v3 > v2, 'v3 must exceed v2');
    // Same physical ms → logical advances 1,2,3.
    assert.equal(HybridLogicalClock.decompose(v1)[1], 1n);
    assert.equal(HybridLogicalClock.decompose(v2)[1], 2n);
    assert.equal(HybridLogicalClock.decompose(v3)[1], 3n);
  });

  it('resets logical to 0 when physical advances', () => {
    let now = 1000n;
    const clk = new HybridLogicalClock(0, () => now);
    clk.tick(); // logical 1
    now = 2000n;
    const v = clk.tick();
    assert.equal(HybridLogicalClock.decompose(v)[0], 2000n);
    assert.equal(HybridLogicalClock.decompose(v)[1], 0n);
  });

  it('bumps physical when the logical counter overflows within a ms', () => {
    let now = 500n;
    const clk = new HybridLogicalClock(0, () => now);
    // 1024 ticks at the same ms: first tick logical=1 ... after 1023 more the
    // counter reaches 1024 and rolls, bumping physical to 501.
    let last = 0n;
    for (let i = 0; i < 1024; i++) last = clk.tick();
    assert.equal(HybridLogicalClock.decompose(last)[0], 501n);
    assert.equal(HybridLogicalClock.decompose(last)[1], 0n);
  });
});

describe('HybridLogicalClock — observe', () => {
  it('advances past an incoming version from the future', () => {
    let now = 1000n;
    const clk = new HybridLogicalClock(1, () => now);
    const future = HybridLogicalClock.compose(5000n, 3n, 9n);
    clk.observe(future);
    const next = clk.tick();
    // After observing a version at physical 5000, our next tick must be > it.
    assert.ok(next > future, 'local tick after observe must exceed the observed version');
    assert.ok(HybridLogicalClock.decompose(next)[0] >= 5000n);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// InMemorySyncableEntryStore — apply rules
// ─────────────────────────────────────────────────────────────────────────────

describe('InMemorySyncableEntryStore — apply rules', () => {
  it('applies a brand-new entry and reports true', async () => {
    const store = new InMemorySyncableEntryStore();
    const applied = await store.applyAsync(entry({ entityType: 'T', entityId: '1', version: 10n }));
    assert.equal(applied, true);
    const got = await store.getAsync('T', '1');
    assert.equal(got?.version, 10n);
  });

  it('higher version wins; lower version is rejected', async () => {
    const store = new InMemorySyncableEntryStore();
    await store.applyAsync(entry({ entityType: 'T', entityId: '1', version: 10n, payload: 'v10' }));
    assert.equal(await store.applyAsync(entry({ entityType: 'T', entityId: '1', version: 20n, payload: 'v20' })), true);
    assert.equal(await store.applyAsync(entry({ entityType: 'T', entityId: '1', version: 5n, payload: 'v5' })), false);
    assert.equal((await store.getAsync('T', '1'))?.payload, 'v20');
  });

  it('tombstone beats a non-tombstone at equal version', async () => {
    const store = new InMemorySyncableEntryStore();
    await store.applyAsync(entry({ entityType: 'T', entityId: '1', version: 10n, payload: 'live' }));
    const applied = await store.applyAsync(
      entry({ entityType: 'T', entityId: '1', version: 10n, isTombstone: true, payload: '' }),
    );
    assert.equal(applied, true);
    assert.equal((await store.getAsync('T', '1'))?.isTombstone, true);
  });

  it('a non-tombstone does NOT overwrite a tombstone at equal version', async () => {
    const store = new InMemorySyncableEntryStore();
    await store.applyAsync(entry({ entityType: 'T', entityId: '1', version: 10n, isTombstone: true, payload: '' }));
    const applied = await store.applyAsync(entry({ entityType: 'T', entityId: '1', version: 10n, payload: 'live' }));
    assert.equal(applied, false);
    assert.equal((await store.getAsync('T', '1'))?.isTombstone, true);
  });

  it('content-hash is the tiebreaker at equal version + equal tombstone flag', async () => {
    const store = new InMemorySyncableEntryStore();
    // Choose two payloads with a known hash ordering.
    const lo = entry({ entityType: 'T', entityId: '1', version: 10n, payload: 'aaa' });
    const hi = entry({ entityType: 'T', entityId: '1', version: 10n, payload: 'bbb' });
    const loHash = lo.contentHash;
    const hiHash = hi.contentHash;
    const higher = loHash < hiHash ? hi : lo;
    const lower = loHash < hiHash ? lo : hi;
    await store.applyAsync(lower);
    assert.equal(await store.applyAsync(higher), true, 'higher content-hash wins');
    assert.equal(await store.applyAsync(lower), false, 'lower content-hash loses');
    assert.equal((await store.getAsync('T', '1'))?.contentHash, higher.contentHash);
  });

  it('getSince returns only strictly-newer entries, ascending', async () => {
    const store = new InMemorySyncableEntryStore();
    await store.applyAsync(entry({ entityType: 'T', entityId: 'a', version: 10n }));
    await store.applyAsync(entry({ entityType: 'T', entityId: 'b', version: 30n }));
    await store.applyAsync(entry({ entityType: 'T', entityId: 'c', version: 20n }));
    await store.applyAsync(entry({ entityType: 'Other', entityId: 'd', version: 40n }));
    const since = await store.getSinceAsync('T', 10n);
    assert.deepEqual(since.map((e) => e.version), [20n, 30n]);
  });

  it('state vector reports the per-type high-watermark, ordinal-sorted', async () => {
    const store = new InMemorySyncableEntryStore();
    await store.applyAsync(entry({ entityType: 'Zeta', entityId: '1', version: 5n }));
    await store.applyAsync(entry({ entityType: 'Alpha', entityId: '1', version: 7n }));
    await store.applyAsync(entry({ entityType: 'Alpha', entityId: '2', version: 99n }));
    const vec = await store.getStateVectorAsync();
    assert.deepEqual(
      vec.map((v) => [v.entityType, v.maxKnownVersion]),
      [['Alpha', 99n], ['Zeta', 5n]],
    );
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Convergence protocol — two engines on one hub
// ─────────────────────────────────────────────────────────────────────────────

function buildNode(hub: InProcessSyncHub, nodeId: string, nodeShort: number, clockMs: () => bigint) {
  const channel = new InProcessCompanionStateChannel(hub, nodeId);
  const store = new InMemorySyncableEntryStore();
  const clock = new HybridLogicalClock(nodeShort, clockMs);
  const engine = new CompanionStateSyncEngine(channel, store, clock, () => new Date('2026-02-02T00:00:00Z'));
  return { channel, store, clock, engine };
}

describe('CompanionStateSyncEngine — Push propagation', () => {
  it('a local write propagates to a started peer via Push', async () => {
    let ms = 1000n;
    const hub = new InProcessSyncHub();
    const a = buildNode(hub, 'A', 1, () => ms);
    const b = buildNode(hub, 'B', 2, () => ms);
    await a.engine.startAsync();
    await b.engine.startAsync();

    await a.engine.writeLocalAsync('PersonaState', 'user-1', '{"v":1}');
    await settle();

    const onB = await b.store.getAsync('PersonaState', 'user-1');
    assert.ok(onB, 'entry must have reached node B');
    assert.equal(onB?.payload, '{"v":1}');

    await a.engine.disposeAsync();
    await b.engine.disposeAsync();
  });
});

describe('CompanionStateSyncEngine — Announce/Request/Push convergence', () => {
  it('a peer that joins after a write catches up via SyncNow', async () => {
    let ms = 1000n;
    const hub = new InProcessSyncHub();
    const a = buildNode(hub, 'A', 1, () => ms);
    const b = buildNode(hub, 'B', 2, () => ms);

    // A writes BEFORE B is subscribed → B misses the Push.
    await a.engine.startAsync();
    await a.engine.writeLocalAsync('CoreMemory', 'c1', 'alpha');
    ms = 1001n;
    await a.engine.writeLocalAsync('CoreMemory', 'c2', 'beta');

    await b.engine.startAsync();
    assert.equal(await b.store.getAsync('CoreMemory', 'c1'), null, 'B has not caught up yet');

    // A announces its state vector; B requests what it lacks; A pushes; B applies.
    await a.engine.syncNowAsync();
    await settle();

    assert.equal((await b.store.getAsync('CoreMemory', 'c1'))?.payload, 'alpha');
    assert.equal((await b.store.getAsync('CoreMemory', 'c2'))?.payload, 'beta');

    await a.engine.disposeAsync();
    await b.engine.disposeAsync();
  });

  it('converges bidirectionally when both nodes hold distinct writes', async () => {
    let ms = 2000n;
    const hub = new InProcessSyncHub();
    const a = buildNode(hub, 'A', 1, () => ms);
    const b = buildNode(hub, 'B', 2, () => ms);
    await a.engine.startAsync();
    await b.engine.startAsync();

    await a.engine.writeLocalAsync('Note', 'from-a', 'AAA');
    ms = 2001n;
    await b.engine.writeLocalAsync('Note', 'from-b', 'BBB');
    await settle();

    // Kick a round of announces from both sides to force full convergence.
    await a.engine.syncNowAsync();
    await b.engine.syncNowAsync();
    await settle();

    assert.equal((await a.store.getAsync('Note', 'from-b'))?.payload, 'BBB');
    assert.equal((await b.store.getAsync('Note', 'from-a'))?.payload, 'AAA');

    await a.engine.disposeAsync();
    await b.engine.disposeAsync();
  });
});

describe('CompanionStateSyncEngine — guards', () => {
  it('writeLocal rejects blank entity type/id', async () => {
    const hub = new InProcessSyncHub();
    const a = buildNode(hub, 'A', 1, () => 1n);
    await assert.rejects(() => a.engine.writeLocalAsync('', 'x', 'p'), /entityType required/);
    await assert.rejects(() => a.engine.writeLocalAsync('T', '  ', 'p'), /entityId required/);
    await a.engine.disposeAsync();
  });

  it('operations after dispose throw', async () => {
    const hub = new InProcessSyncHub();
    const a = buildNode(hub, 'A', 1, () => 1n);
    await a.engine.startAsync();
    await a.engine.disposeAsync();
    await assert.rejects(() => a.engine.syncNowAsync(), /disposed/);
  });

  it('content hash matches SHA-256 of the payload', async () => {
    const hub = new InProcessSyncHub();
    const a = buildNode(hub, 'A', 1, () => 1n);
    const e = await a.engine.writeLocalAsync('T', 'x', 'hello');
    assert.equal(e.contentHash, sha256Hex('hello'));
    await a.engine.disposeAsync();
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// InProcessSyncHub / channel
// ─────────────────────────────────────────────────────────────────────────────

describe('InProcessSyncHub — membership', () => {
  it('tracks and drops connected node ids', () => {
    const hub = new InProcessSyncHub();
    const c1 = new InProcessCompanionStateChannel(hub, 'n1');
    const c2 = new InProcessCompanionStateChannel(hub, 'n2');
    assert.deepEqual([...hub.connectedNodeIds].sort(), ['n1', 'n2']);
    c1.dispose();
    assert.deepEqual([...hub.connectedNodeIds], ['n2']);
    c2.dispose();
  });

  it('does not deliver an envelope back to its sender', async () => {
    const hub = new InProcessSyncHub();
    const c1 = new InProcessCompanionStateChannel(hub, 'n1');
    let received = 0;
    c1.subscribe(async () => {
      received++;
    });
    await c1.sendAsync({ kind: SyncEnvelopeKind.Announce, fromNodeId: 'n1', stateVector: [], requests: null, entries: null });
    await settle();
    assert.equal(received, 0, 'sender must not receive its own broadcast');
    c1.dispose();
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// PersonaStateSyncBridge
// ─────────────────────────────────────────────────────────────────────────────

class RecordingPersonaStore {
  saved: PersonaState[] = [];
  async loadAsync(userId: string): Promise<PersonaState> {
    const p = new PersonaState();
    p.userId = userId;
    return p;
  }
  async saveAsync(persona: PersonaState): Promise<void> {
    this.saved.push(persona);
  }
}

describe('PersonaStateSyncBridge', () => {
  it('saves locally, pushes to peer, and decodes back to a PersonaState', async () => {
    const hub = new InProcessSyncHub();
    const a = buildNode(hub, 'A', 1, () => 1n);
    const b = buildNode(hub, 'B', 2, () => 1n);
    await a.engine.startAsync();
    await b.engine.startAsync();

    const store = new RecordingPersonaStore();
    const bridge = new PersonaStateSyncBridge(store, a.engine);

    const persona = new PersonaState();
    persona.userId = 'user-42';
    persona.verbosity = 'detailed';
    persona.formality = 'formal';
    persona.preferredLocale = 'en-ZA';
    persona.topicWeights = { finance: 3.5, sport: 1.0 };
    persona.disfavouredTopics = new Set(['politics']);
    persona.totalInteractions = 12;
    persona.positiveSignals = 9;
    persona.negativeSignals = 3;

    await bridge.saveAsync(persona);
    await settle();

    assert.equal(store.saved.length, 1, 'persona persisted locally');

    const onB = await b.store.getAsync(PersonaStateSyncBridge.entityType, 'user-42');
    assert.ok(onB, 'persona reached node B');
    const decoded = PersonaStateSyncBridge.tryDecode(onB!);
    assert.ok(decoded);
    assert.equal(decoded!.userId, 'user-42');
    assert.equal(decoded!.verbosity, 'detailed');
    assert.equal(decoded!.formality, 'formal');
    assert.equal(decoded!.preferredLocale, 'en-ZA');
    assert.equal(decoded!.topicWeights.finance, 3.5);
    assert.ok(decoded!.disfavouredTopics.has('politics'));
    assert.equal(decoded!.totalInteractions, 12);
    assert.equal(decoded!.positiveSignals, 9);
    assert.equal(decoded!.negativeSignals, 3);

    await a.engine.disposeAsync();
    await b.engine.disposeAsync();
  });

  it('tryDecode returns null for tombstones and foreign entity types', () => {
    const t = entry({ entityType: PersonaStateSyncBridge.entityType, entityId: 'x', version: 1n, isTombstone: true });
    assert.equal(PersonaStateSyncBridge.tryDecode(t), null);
    const foreign = entry({ entityType: 'Other', entityId: 'x', version: 1n, payload: '{}' });
    assert.equal(PersonaStateSyncBridge.tryDecode(foreign), null);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// CompanionConversationSyncBridge
// ─────────────────────────────────────────────────────────────────────────────

describe('CompanionConversationSyncBridge', () => {
  it('publishes a delta and decodes it back', async () => {
    const hub = new InProcessSyncHub();
    const a = buildNode(hub, 'A', 1, () => 1n);
    const bridge = new CompanionConversationSyncBridge(a.engine);

    const delta: ConversationStateDelta = {
      sessionId: 'sess-1',
      userText: 'hello there',
      assistantText: 'General Kenobi',
      isTurnComplete: false,
      startedAtUtc: new Date('2026-03-03T10:00:00Z'),
      updatedAtUtc: new Date('2026-03-03T10:00:05Z'),
    };
    const stored = await bridge.publishAsync(delta);
    const decoded = CompanionConversationSyncBridge.tryDecode(stored);
    assert.ok(decoded);
    assert.equal(decoded!.sessionId, 'sess-1');
    assert.equal(decoded!.userText, 'hello there');
    assert.equal(decoded!.assistantText, 'General Kenobi');
    assert.equal(decoded!.isTurnComplete, false);
    assert.equal(decoded!.startedAtUtc.toISOString(), '2026-03-03T10:00:00.000Z');

    await a.engine.disposeAsync();
  });

  it('terminate writes a tombstone that decodes to null', async () => {
    const hub = new InProcessSyncHub();
    const a = buildNode(hub, 'A', 1, () => 1n);
    const bridge = new CompanionConversationSyncBridge(a.engine);
    const tomb = await bridge.terminateAsync('sess-9');
    assert.equal(tomb.isTombstone, true);
    assert.equal(tomb.payload, '');
    assert.equal(CompanionConversationSyncBridge.tryDecode(tomb), null);
    await a.engine.disposeAsync();
  });

  it('rejects a blank session id', async () => {
    const hub = new InProcessSyncHub();
    const a = buildNode(hub, 'A', 1, () => 1n);
    const bridge = new CompanionConversationSyncBridge(a.engine);
    await assert.rejects(
      () => bridge.publishAsync({ sessionId: '', userText: '', assistantText: '', isTurnComplete: false, startedAtUtc: new Date(), updatedAtUtc: new Date() }),
      /SessionId required/,
    );
    await a.engine.disposeAsync();
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// LoraAdapterSyncBridge
// ─────────────────────────────────────────────────────────────────────────────

describe('LoraAdapterSyncBridge', () => {
  it('publishes adapter bytes (base64) and TryWrite decodes + persists them', async () => {
    const hub = new InProcessSyncHub();
    const a = buildNode(hub, 'A', 1, () => 1n);
    const b = buildNode(hub, 'B', 2, () => 1n);
    await a.engine.startAsync();
    await b.engine.startAsync();

    const srcFiles = new InMemoryAdapterFileStore();
    const originalBytes = new Uint8Array([1, 2, 3, 4, 250, 251, 252, 253]);
    srcFiles.set('/models/personal.bin', originalBytes);

    const bridge = new LoraAdapterSyncBridge(a.engine, srcFiles);
    await bridge.publishAsync('personal-user1', '/models/personal.bin', 5000n);
    await settle();

    const onB = await b.store.getAsync(LoraAdapterSyncBridge.entityType, 'personal-user1');
    assert.ok(onB, 'adapter reached node B');

    const destFiles = new InMemoryAdapterFileStore();
    const snap = await LoraAdapterSyncBridge.tryWriteAsync(onB!, '/dest/personal.bin', destFiles);
    assert.ok(snap);
    assert.equal(snap!.adapterId, 'personal-user1');
    assert.equal(snap!.stepCount, 5000n);
    const written = destFiles.get('/dest/personal.bin');
    assert.ok(written);
    assert.deepEqual([...written!], [...originalBytes], 'round-tripped adapter bytes must match');

    await a.engine.disposeAsync();
    await b.engine.disposeAsync();
  });

  it('publish throws when the adapter file is missing', async () => {
    const hub = new InProcessSyncHub();
    const a = buildNode(hub, 'A', 1, () => 1n);
    const bridge = new LoraAdapterSyncBridge(a.engine, new InMemoryAdapterFileStore());
    await assert.rejects(() => bridge.publishAsync('id', '/nope.bin', 1n), /not found/);
    await a.engine.disposeAsync();
  });

  it('tryWrite returns null for tombstones / foreign types', async () => {
    const t = entry({ entityType: LoraAdapterSyncBridge.entityType, entityId: 'x', version: 1n, isTombstone: true });
    assert.equal(await LoraAdapterSyncBridge.tryWriteAsync(t, '/x', new InMemoryAdapterFileStore()), null);
    const foreign = entry({ entityType: 'Other', entityId: 'x', version: 1n, payload: '{}' });
    assert.equal(await LoraAdapterSyncBridge.tryWriteAsync(foreign, '/x', new InMemoryAdapterFileStore()), null);
  });
});
