// memory_sync_service.test.ts
//
// Verifies the CircleAI.Sync port (MemorySyncService): push builds a broadcast
// SyncDelta with the local device as source and empty target, and the receive
// loop deserialises episodic deltas into the local store while skipping its own
// echoes and non-episodic domains. Also covers the JSON episodic codec and the
// InMemoryGoalStore added to the memory stores module.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  MemorySyncService,
  JsonEpisodicDeltaCodec,
  SyncDeliveryMode,
  SyncDomainKeys,
  type ISyncChannel,
  type SyncDelta,
} from '../src/sync/index';
import { InMemoryEpisodicStore, InMemoryGoalStore } from '../src/memory/stores';
import { Goal, GoalStatus, GoalPriority, type EpisodicMemoryEntry } from '../src/memory/index';

// ─────────────────────────────────────────────────────────────────────────────
// A controllable in-process ISyncChannel: pushes are recorded; the receive
// stream yields whatever the test enqueues, then ends.
// ─────────────────────────────────────────────────────────────────────────────

class FakeSyncChannel implements ISyncChannel {
  readonly pushed: SyncDelta[] = [];
  private readonly inbound: SyncDelta[];

  constructor(inbound: SyncDelta[] = []) {
    this.inbound = inbound;
  }

  pushDeltaAsync(delta: SyncDelta): Promise<void> {
    this.pushed.push(delta);
    return Promise.resolve();
  }

  async *receiveDeltasAsync(_ownerId: string): AsyncGenerator<SyncDelta> {
    for (const d of this.inbound) {
      yield d;
    }
  }

  getLastSequenceAsync(_ownerId: string, _domainKey: string): Promise<number> {
    return Promise.resolve(0);
  }
}

function episodicEntry(id: string, text: string): EpisodicMemoryEntry {
  return {
    id,
    recordedAtUtc: new Date('2026-04-04T12:00:00Z'),
    userText: text,
    assistantText: `re: ${text}`,
    appContext: 'test',
    tags: { topic: 'demo' },
  };
}

function delta(over: Partial<SyncDelta> & Pick<SyncDelta, 'ownerId' | 'sourceDeviceId' | 'domainKey' | 'payload'>): SyncDelta {
  return {
    ownerId: over.ownerId,
    sourceDeviceId: over.sourceDeviceId,
    targetDeviceId: over.targetDeviceId ?? '',
    domainKey: over.domainKey,
    payload: over.payload,
    sequence: over.sequence ?? 1,
    deliveryMode: over.deliveryMode ?? SyncDeliveryMode.GUARANTEED,
    ttlMs: over.ttlMs,
    createdAt: over.createdAt ?? new Date('2026-04-04T12:00:00Z'),
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// JsonEpisodicDeltaCodec
// ─────────────────────────────────────────────────────────────────────────────

describe('JsonEpisodicDeltaCodec', () => {
  it('round-trips an episodic entry', () => {
    const codec = new JsonEpisodicDeltaCodec();
    const original = episodicEntry('e1', 'hello');
    const decoded = codec.decode(codec.encode(original));
    assert.ok(decoded);
    assert.equal(decoded!.id, 'e1');
    assert.equal(decoded!.userText, 'hello');
    assert.equal(decoded!.assistantText, 're: hello');
    assert.equal(decoded!.appContext, 'test');
    assert.equal(decoded!.tags?.topic, 'demo');
    assert.equal(decoded!.recordedAtUtc.toISOString(), '2026-04-04T12:00:00.000Z');
  });

  it('returns null for garbage bytes', () => {
    const codec = new JsonEpisodicDeltaCodec();
    assert.equal(codec.decode(new TextEncoder().encode('not json')), null);
    assert.equal(codec.decode(new TextEncoder().encode('{"no":"id"}')), null);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// MemorySyncService — push
// ─────────────────────────────────────────────────────────────────────────────

describe('MemorySyncService — pushMemoryDelta', () => {
  it('builds a broadcast delta stamped with the local device id', async () => {
    const channel = new FakeSyncChannel();
    const store = new InMemoryEpisodicStore();
    const fixedNow = new Date('2026-05-05T00:00:00Z');
    const svc = new MemorySyncService(channel, store, 'device-local', new JsonEpisodicDeltaCodec(), () => fixedNow);

    const payload = new Uint8Array([1, 2, 3]);
    await svc.pushMemoryDeltaAsync('owner-1', SyncDomainKeys.EPISODIC_MEMORY, payload, SyncDeliveryMode.URGENT);

    assert.equal(channel.pushed.length, 1);
    const d = channel.pushed[0];
    assert.equal(d.ownerId, 'owner-1');
    assert.equal(d.sourceDeviceId, 'device-local');
    assert.equal(d.targetDeviceId, '', 'broadcast → empty target');
    assert.equal(d.domainKey, SyncDomainKeys.EPISODIC_MEMORY);
    assert.deepEqual([...d.payload], [1, 2, 3]);
    assert.equal(d.deliveryMode, SyncDeliveryMode.URGENT);
    assert.equal(d.sequence, fixedNow.getTime());
    assert.equal(d.createdAt.getTime(), fixedNow.getTime());
  });

  it('defaults the delivery mode to Guaranteed', async () => {
    const channel = new FakeSyncChannel();
    const svc = new MemorySyncService(channel, new InMemoryEpisodicStore(), 'dev');
    await svc.pushMemoryDeltaAsync('o', SyncDomainKeys.PERSONA, new Uint8Array([0]));
    assert.equal(channel.pushed[0].deliveryMode, SyncDeliveryMode.GUARANTEED);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// MemorySyncService — receive
// ─────────────────────────────────────────────────────────────────────────────

describe('MemorySyncService — receive loop', () => {
  it('applies inbound episodic deltas from other devices into the store', async () => {
    const codec = new JsonEpisodicDeltaCodec();
    const inbound = [
      delta({
        ownerId: 'owner-1',
        sourceDeviceId: 'device-remote',
        domainKey: SyncDomainKeys.EPISODIC_MEMORY,
        payload: codec.encode(episodicEntry('remote-1', 'from the phone')),
      }),
    ];
    const channel = new FakeSyncChannel(inbound);
    const store = new InMemoryEpisodicStore();
    const svc = new MemorySyncService(channel, store, 'device-local', codec);

    await svc.startReceivingAsync('owner-1');
    // Let the fire-and-forget receive loop drain the finite inbound stream.
    for (let i = 0; i < 20; i++) await Promise.resolve();
    await svc.stopReceivingAsync();

    assert.equal(await store.countAsync(), 1);
    const recent = await store.getRecentAsync(10);
    assert.equal(recent[0].id, 'remote-1');
    assert.equal(recent[0].userText, 'from the phone');
  });

  it('skips its own echoes (matching source device id)', async () => {
    const codec = new JsonEpisodicDeltaCodec();
    const inbound = [
      delta({
        ownerId: 'owner-1',
        sourceDeviceId: 'device-local', // our own echo
        domainKey: SyncDomainKeys.EPISODIC_MEMORY,
        payload: codec.encode(episodicEntry('echo', 'mine')),
      }),
    ];
    const svc = new MemorySyncService(new FakeSyncChannel(inbound), new InMemoryEpisodicStore(), 'device-local', codec);
    const store = (svc as unknown as { store: InMemoryEpisodicStore }).store;

    await svc.startReceivingAsync('owner-1');
    for (let i = 0; i < 20; i++) await Promise.resolve();
    await svc.stopReceivingAsync();

    assert.equal(await store.countAsync(), 0, 'own echo must not be applied');
  });

  it('ignores non-episodic domains', async () => {
    const codec = new JsonEpisodicDeltaCodec();
    const inbound = [
      delta({
        ownerId: 'owner-1',
        sourceDeviceId: 'device-remote',
        domainKey: SyncDomainKeys.PERSONA, // not episodic
        payload: codec.encode(episodicEntry('persona', 'nope')),
      }),
    ];
    const store = new InMemoryEpisodicStore();
    const svc = new MemorySyncService(new FakeSyncChannel(inbound), store, 'device-local', codec);

    await svc.startReceivingAsync('owner-1');
    for (let i = 0; i < 20; i++) await Promise.resolve();
    await svc.stopReceivingAsync();

    assert.equal(await store.countAsync(), 0, 'non-episodic domain must be ignored by the episodic handler');
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryGoalStore
// ─────────────────────────────────────────────────────────────────────────────

function goal(id: string, userId: string, status: GoalStatus): Goal {
  const g = new Goal();
  g.id = id;
  g.userId = userId;
  g.title = `Goal ${id}`;
  g.status = status;
  g.priority = GoalPriority.Normal;
  g.createdUtc = new Date('2026-01-01T00:00:00Z');
  return g;
}

describe('InMemoryGoalStore', () => {
  it('upserts, gets, lists, filters active, and deletes', async () => {
    const store = new InMemoryGoalStore();
    await store.upsertAsync(goal('g1', 'u1', GoalStatus.Active));
    await store.upsertAsync(goal('g2', 'u1', GoalStatus.Completed));
    await store.upsertAsync(goal('g3', 'u2', GoalStatus.Active));

    assert.equal((await store.getAsync('g1'))?.id, 'g1');
    assert.equal(await store.getAsync('missing'), null);

    const u1 = await store.listAsync('u1');
    assert.deepEqual(u1.map((g) => g.id).sort(), ['g1', 'g2']);

    const activeU1 = await store.getActiveAsync('u1');
    assert.deepEqual(activeU1.map((g) => g.id), ['g1']);

    await store.deleteAsync('g1');
    assert.equal(await store.getAsync('g1'), null);
    assert.deepEqual((await store.listAsync('u1')).map((g) => g.id), ['g2']);
  });

  it('upsert replaces an existing goal by id and returns it', async () => {
    const store = new InMemoryGoalStore();
    await store.upsertAsync(goal('g1', 'u1', GoalStatus.Active));
    const updated = goal('g1', 'u1', GoalStatus.Abandoned);
    const returned = await store.upsertAsync(updated);
    assert.equal(returned.status, GoalStatus.Abandoned);
    assert.equal((await store.getAsync('g1'))?.status, GoalStatus.Abandoned);
    assert.equal((await store.listAsync('u1')).length, 1, 'upsert must not duplicate');
  });

  it('rejects blank ids / user ids', async () => {
    const store = new InMemoryGoalStore();
    await assert.rejects(() => store.getAsync(''), /id required/);
    await assert.rejects(() => store.listAsync('  '), /userId required/);
    await assert.rejects(() => store.getActiveAsync(''), /userId required/);
  });
});
