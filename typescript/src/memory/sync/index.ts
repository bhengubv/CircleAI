// memory/sync/index.ts
//
// Companion-state synchronisation engine. Ported from
// CircleAI.Memory/Sync/ (C#) at full parity.
//
// This subtree gives every device a convergent, last-writer-wins view of
// the user's companion state (persona, conversations, LoRA adapters, …)
// without a central server. Each syncable item carries a Hybrid Logical
// Clock (HLC) version so writes on different devices order deterministically
// and converge in <= 2 round-trips per peer pair.
//
// The pieces:
//   • HybridLogicalClock       — monotonic, globally-unique 64-bit versions
//   • SyncableEntry            — the wire unit (opaque JSON payload + version)
//   • SyncEnvelope             — Announce / Request / Push protocol message
//   • ISyncableEntryStore      — the seat the engine reads/writes
//   • InMemorySyncableEntryStore — deterministic in-memory store
//   • ICompanionStateChannel   — transport seam
//   • InProcessCompanionStateChannel + InProcessSyncHub — loopback transport
//   • CompanionStateSyncEngine — the orchestration loop
//   • PersonaStateSyncBridge / CompanionConversationSyncBridge /
//     LoraAdapterSyncBridge   — typed adapters riding the engine

import { createHash } from "node:crypto";
import { PersonaState } from "../index.js";
import type { IPersonaStore } from "../index.js";

// ─────────────────────────────────────────────────────────────────────────────
// SyncableEntry (SyncableEntry.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * A single syncable item — the smallest unit the engine moves between peers.
 *
 * Payload is opaque JSON (or any string); type adapters serialise their own
 * records into the {@link payload} field and back. {@link contentHash} is
 * SHA-256 (lowercase hex) of the payload — used as the tiebreaker when two
 * peers happen to write the same Version (impossibly rare with HLC, but the
 * system must still converge deterministically).
 */
export interface SyncableEntry {
  /** Logical type — e.g. "PersonaState", "CoreMemory", "DailyMemorySummary". */
  readonly entityType: string;
  /** Identifier within the type — e.g. a user ID, a GUID-N format string. */
  readonly entityId: string;
  /** HLC-produced monotonic version stamp. */
  readonly version: bigint;
  /** True when this entry represents a deletion. Payload is empty in that case. */
  readonly isTombstone: boolean;
  /** SHA-256 hex of {@link payload} — content tiebreaker when versions collide. */
  readonly contentHash: string;
  /** Opaque payload — type-specific JSON or any string the adapter chose. */
  readonly payload: string;
  /** Identifier of the node that authored this version (provenance/debugging). */
  readonly sourceNodeId: string;
  /** UTC wall-clock when authored — for display, not for ordering. */
  readonly authoredAt: Date;
}

// ─────────────────────────────────────────────────────────────────────────────
// SyncEnvelope (SyncEnvelope.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Kind of sync envelope.
 *
 *   Announce  — "I am node N. For each entity type, my highest version is V."
 *   Request   — "You have version > mine for type T since version X. Send it."
 *   Push      — "Here are entries you asked for (or that I want you to apply)."
 */
export enum SyncEnvelopeKind {
  /** Broadcast of the sender's per-entity-type high-watermark versions. */
  Announce = "Announce",
  /** Reply to an Announce asking for entries newer than a known version. */
  Request = "Request",
  /** Unsolicited or replied delivery of syncable entries. */
  Push = "Push",
}

/** Per-entity-type high-watermark — used in Announce/Request payloads. */
export interface StateVectorEntry {
  readonly entityType: string;
  readonly maxKnownVersion: bigint;
}

/**
 * Reply-side request item — "send me entries of {@link entityType} strictly
 * newer than {@link sinceVersion}".
 */
export interface RequestItem {
  readonly entityType: string;
  readonly sinceVersion: bigint;
}

/** A sync envelope — the message unit that crosses the channel. */
export interface SyncEnvelope {
  readonly kind: SyncEnvelopeKind;
  readonly fromNodeId: string;
  readonly stateVector: readonly StateVectorEntry[] | null;
  readonly requests: readonly RequestItem[] | null;
  readonly entries: readonly SyncableEntry[] | null;
}

// ─────────────────────────────────────────────────────────────────────────────
// HybridLogicalClock (HybridLogicalClock.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Hybrid Logical Clock (HLC) — produces monotonic, globally-unique version
 * stamps for syncable entries.
 *
 * Layout of the 64-bit version:
 *   high 48 bits — physical time in milliseconds (Unix epoch)
 *   mid  10 bits — logical counter (resets when physical advances)
 *   low   6 bits — node short ID (0..63)
 *
 * The JavaScript port uses {@link bigint} throughout so the full 64-bit range
 * survives the << 16 shift without precision loss (C# uses `long`).
 */
export class HybridLogicalClock {
  private readonly physicalNowMs: () => bigint;
  private readonly nodeShortId: bigint;
  private lastPhysical: bigint;
  private logical: bigint;

  /**
   * @param nodeShortId 0..63 — packs into the low 6 bits of every version.
   *   Each device a user has should pick a stable distinct value.
   * @param physicalNowMs Source of physical time in ms. Defaults to system
   *   time; override in tests for determinism. May return a number or bigint.
   */
  constructor(
    nodeShortId: number | bigint,
    physicalNowMs?: () => number | bigint,
  ) {
    const nid = BigInt(nodeShortId);
    if (nid < 0n || nid > 63n) {
      throw new RangeError("nodeShortId must be in 0..63");
    }
    this.nodeShortId = nid;
    const src = physicalNowMs ?? HybridLogicalClock.defaultNow;
    this.physicalNowMs = () => BigInt(src());
    this.lastPhysical = this.physicalNowMs();
    this.logical = 0n;
  }

  /** Produces the next outgoing version (for a write we originated). */
  tick(): bigint {
    const now = this.physicalNowMs();
    if (now > this.lastPhysical) {
      this.lastPhysical = now;
      this.logical = 0n;
    } else {
      this.logical++;
      if (this.logical >= 1024n) {
        // Logical counter overflowed within the same ms — bump physical.
        this.lastPhysical++;
        this.logical = 0n;
      }
    }
    return HybridLogicalClock.compose(this.lastPhysical, this.logical, this.nodeShortId);
  }

  /**
   * Updates the clock from a received version (must be called on every inbound
   * apply so subsequent local ticks remain monotonic w.r.t. peers).
   */
  observe(incoming: bigint): bigint {
    const [incomingPhysical] = HybridLogicalClock.decompose(incoming);
    const now = this.physicalNowMs();
    const maxPhysical = bigMax(bigMax(this.lastPhysical, incomingPhysical), now);

    if (maxPhysical === this.lastPhysical && maxPhysical === incomingPhysical) this.logical++;
    else if (maxPhysical === this.lastPhysical) this.logical++;
    else if (maxPhysical === incomingPhysical) this.logical = HybridLogicalClock.decompose(incoming)[1] + 1n;
    else this.logical = 0n;

    this.lastPhysical = maxPhysical;
    return HybridLogicalClock.compose(this.lastPhysical, this.logical, this.nodeShortId);
  }

  /** Composes the three components into a 64-bit version. */
  static compose(physicalMs: bigint, logical: bigint, nodeShortId: bigint): bigint {
    return (physicalMs << 16n) | ((logical & 0x3ffn) << 6n) | (nodeShortId & 0x3fn);
  }

  /** Decomposes a version into [physicalMs, logical, nodeShortId]. */
  static decompose(version: bigint): [bigint, bigint, bigint] {
    return [version >> 16n, (version >> 6n) & 0x3ffn, version & 0x3fn];
  }

  private static defaultNow(): bigint {
    return BigInt(Date.now());
  }
}

function bigMax(a: bigint, b: bigint): bigint {
  return a >= b ? a : b;
}

// ─────────────────────────────────────────────────────────────────────────────
// ISyncableEntryStore (ISyncableEntryStore.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * The seat the sync engine reads from and writes to. Implementations track the
 * local view of all known syncable entries plus their version stamps.
 *
 * Apply rules — implementations MUST enforce these for convergence:
 *   • Higher Version wins
 *   • On tie (same Version), higher ContentHash (string compare) wins
 *   • Tombstones replace any non-tombstone of equal-or-lower Version
 */
export interface ISyncableEntryStore {
  /**
   * Applies an incoming entry. Returns true when local state was actually
   * updated (incoming was strictly newer / preferred). Returns false when the
   * local entry was already at or beyond the incoming version.
   */
  applyAsync(entry: SyncableEntry): Promise<boolean>;

  /**
   * Returns the current entry for the given (type, id), or null when not known
   * locally. Tombstones ARE returned — callers needing "is it deleted?" should
   * check {@link SyncableEntry.isTombstone}.
   */
  getAsync(entityType: string, entityId: string): Promise<SyncableEntry | null>;

  /**
   * Returns every entry of the given type whose Version is strictly greater
   * than {@link sinceVersion}, ordered ascending by Version.
   */
  getSinceAsync(entityType: string, sinceVersion: bigint): Promise<readonly SyncableEntry[]>;

  /**
   * Returns the highest known Version per entity type — the local node's state
   * vector. Types with no entries are omitted.
   */
  getStateVectorAsync(): Promise<readonly StateVectorEntry[]>;
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemorySyncableEntryStore (InMemorySyncableEntryStore.cs)
// ─────────────────────────────────────────────────────────────────────────────

/** In-memory {@link ISyncableEntryStore}. */
export class InMemorySyncableEntryStore implements ISyncableEntryStore {
  // Keyed by "type id" so writes are O(1).
  private readonly entries = new Map<string, SyncableEntry>();
  private readonly maxVersionByType = new Map<string, bigint>();

  applyAsync(entry: SyncableEntry): Promise<boolean> {
    if (!entry) throw new Error("entry required");
    const key = InMemorySyncableEntryStore.key(entry.entityType, entry.entityId);

    let applied = false;
    const existing = this.entries.get(key);
    if (existing === undefined) {
      this.entries.set(key, entry);
      applied = true;
    } else if (InMemorySyncableEntryStore.shouldApply(existing, entry)) {
      this.entries.set(key, entry);
      applied = true;
    }

    if (applied) {
      const current = this.maxVersionByType.get(entry.entityType);
      if (current === undefined || entry.version > current) {
        this.maxVersionByType.set(entry.entityType, entry.version);
      }
    }
    return Promise.resolve(applied);
  }

  getAsync(entityType: string, entityId: string): Promise<SyncableEntry | null> {
    const e = this.entries.get(InMemorySyncableEntryStore.key(entityType, entityId));
    return Promise.resolve(e ?? null);
  }

  getSinceAsync(entityType: string, sinceVersion: bigint): Promise<readonly SyncableEntry[]> {
    const result = [...this.entries.values()]
      .filter((e) => e.entityType === entityType && e.version > sinceVersion)
      .sort((a, b) => bigCompare(a.version, b.version));
    return Promise.resolve(result);
  }

  getStateVectorAsync(): Promise<readonly StateVectorEntry[]> {
    const vector: StateVectorEntry[] = [...this.maxVersionByType.entries()]
      .map(([entityType, maxKnownVersion]) => ({ entityType, maxKnownVersion }))
      .sort((a, b) => ordinalCompare(a.entityType, b.entityType));
    return Promise.resolve(vector);
  }

  /**
   * Apply rule: higher Version wins; on tie, higher ContentHash (string
   * compare) wins; tombstone replaces a non-tombstone of equal version.
   */
  private static shouldApply(existing: SyncableEntry, incoming: SyncableEntry): boolean {
    if (incoming.version > existing.version) return true;
    if (incoming.version < existing.version) return false;
    // Equal versions — tombstone-of-non-tombstone wins.
    if (incoming.isTombstone && !existing.isTombstone) return true;
    if (!incoming.isTombstone && existing.isTombstone) return false;
    // Same tombstone state, same version — content hash tiebreaker.
    return ordinalCompare(incoming.contentHash, existing.contentHash) > 0;
  }

  private static key(type: string, id: string): string {
    return `${type} ${id}`;
  }
}

/** Ordinal (code-unit) string comparison, matching C# string.CompareOrdinal. */
function ordinalCompare(a: string, b: string): number {
  if (a === b) return 0;
  const len = Math.min(a.length, b.length);
  for (let i = 0; i < len; i++) {
    const d = a.charCodeAt(i) - b.charCodeAt(i);
    if (d !== 0) return d;
  }
  return a.length - b.length;
}

function bigCompare(a: bigint, b: bigint): number {
  if (a < b) return -1;
  if (a > b) return 1;
  return 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// ICompanionStateChannel (ICompanionStateChannel.cs)
// ─────────────────────────────────────────────────────────────────────────────

/** Handler invoked for each inbound envelope. */
export type EnvelopeHandler = (envelope: SyncEnvelope) => Promise<void>;

/** Disposable returned by subscription — call {@link dispose} to unsubscribe. */
export interface ISyncSubscription {
  dispose(): void;
}

/** Transport that moves {@link SyncEnvelope} messages between peers. */
export interface ICompanionStateChannel {
  /**
   * Stable identifier of THIS node on this channel. Stamped onto every envelope
   * as {@link SyncEnvelope.fromNodeId}.
   */
  readonly localNodeId: string;

  /**
   * Sends an envelope to peers. Channel decides whether this is broadcast (to
   * every peer) or routed. For v0.1 every channel implements broadcast.
   */
  sendAsync(envelope: SyncEnvelope): Promise<void>;

  /** Subscribe to inbound envelopes. The returned disposable unsubscribes. */
  subscribe(handler: EnvelopeHandler): ISyncSubscription;
}

// ─────────────────────────────────────────────────────────────────────────────
// InProcessCompanionStateChannel + InProcessSyncHub (InProcessCompanionStateChannel.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Routes envelopes between every {@link InProcessCompanionStateChannel} that
 * has joined the hub. One hub per simulated "mesh".
 */
export class InProcessSyncHub {
  private readonly channels = new Map<string, InProcessCompanionStateChannel>();

  /** @internal */
  join(channel: InProcessCompanionStateChannel): void {
    this.channels.set(channel.localNodeId, channel);
  }

  /** @internal */
  leave(nodeId: string): void {
    this.channels.delete(nodeId);
  }

  /** @internal */
  async broadcastAsync(envelope: SyncEnvelope, senderNodeId: string): Promise<void> {
    const peers = [...this.channels.values()].filter((c) => c.localNodeId !== senderNodeId);
    for (const peer of peers) {
      await peer.deliverAsync(envelope);
    }
  }

  /** Channels currently on this hub. */
  get connectedNodeIds(): readonly string[] {
    return [...this.channels.keys()];
  }
}

/**
 * In-process {@link ICompanionStateChannel}. Broadcasts via an
 * {@link InProcessSyncHub}. Loopback transport for tests + same-device sim.
 */
export class InProcessCompanionStateChannel implements ICompanionStateChannel {
  private readonly hub: InProcessSyncHub;
  private readonly handlers: EnvelopeHandler[] = [];
  private disposed = false;

  readonly localNodeId: string;

  constructor(hub: InProcessSyncHub, localNodeId: string) {
    if (!hub) throw new Error("hub required");
    if (!localNodeId || localNodeId.trim().length === 0) throw new Error("localNodeId required");
    this.hub = hub;
    this.localNodeId = localNodeId;
    this.hub.join(this);
  }

  sendAsync(envelope: SyncEnvelope): Promise<void> {
    if (!envelope) throw new Error("envelope required");
    if (this.disposed) throw new Error("InProcessCompanionStateChannel disposed");
    return this.hub.broadcastAsync(envelope, this.localNodeId);
  }

  subscribe(handler: EnvelopeHandler): ISyncSubscription {
    if (!handler) throw new Error("handler required");
    if (this.disposed) throw new Error("InProcessCompanionStateChannel disposed");
    this.handlers.push(handler);
    const self = this;
    return {
      dispose(): void {
        const idx = self.handlers.indexOf(handler);
        if (idx >= 0) self.handlers.splice(idx, 1);
      },
    };
  }

  /** @internal */
  async deliverAsync(envelope: SyncEnvelope): Promise<void> {
    const snapshot = [...this.handlers];
    for (const h of snapshot) {
      await h(envelope);
    }
  }

  /** Unregisters from the hub. */
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.hub.leave(this.localNodeId);
    this.handlers.length = 0;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// ICompanionStateSyncEngine (ICompanionStateSyncEngine.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Engine that broadcasts local state vectors, fulfils peer Requests, and
 * applies inbound Push entries. Hosts call {@link startAsync} once at startup,
 * then either rely on event-driven sync (handlers respond as envelopes arrive)
 * or trigger {@link syncNowAsync} after notable local writes to immediately
 * propagate.
 */
export interface ICompanionStateSyncEngine {
  /** Subscribes the engine to channel envelopes. */
  startAsync(): Promise<void>;

  /** Broadcasts the local state vector to all peers immediately. */
  syncNowAsync(): Promise<void>;

  /**
   * Convenience to apply a locally-authored entry: stamps it with a fresh HLC
   * version, persists it to the local store, and (if started) broadcasts it via
   * Push. Returns the resulting entry with its assigned Version.
   */
  writeLocalAsync(
    entityType: string,
    entityId: string,
    payload: string,
    isTombstone?: boolean,
  ): Promise<SyncableEntry>;

  /** Releases the channel subscription. Mirrors C# IAsyncDisposable. */
  disposeAsync(): Promise<void>;
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionStateSyncEngine (CompanionStateSyncEngine.cs)
// ─────────────────────────────────────────────────────────────────────────────

/** Default {@link ICompanionStateSyncEngine}. */
export class CompanionStateSyncEngine implements ICompanionStateSyncEngine {
  private readonly channel: ICompanionStateChannel;
  private readonly store: ISyncableEntryStore;
  private readonly clock: HybridLogicalClock;
  private readonly wallClock: () => Date;
  private subscription: ISyncSubscription | null = null;
  private disposed = false;

  constructor(
    channel: ICompanionStateChannel,
    store: ISyncableEntryStore,
    clock: HybridLogicalClock,
    wallClock?: () => Date,
  ) {
    if (!channel) throw new Error("channel required");
    if (!store) throw new Error("store required");
    if (!clock) throw new Error("clock required");
    this.channel = channel;
    this.store = store;
    this.clock = clock;
    this.wallClock = wallClock ?? (() => new Date());
  }

  startAsync(): Promise<void> {
    this.throwIfDisposed();
    if (this.subscription === null) {
      this.subscription = this.channel.subscribe((env) => this.handleEnvelopeAsync(env));
    }
    return Promise.resolve();
  }

  async syncNowAsync(): Promise<void> {
    this.throwIfDisposed();
    const vector = await this.store.getStateVectorAsync();
    await this.channel.sendAsync({
      kind: SyncEnvelopeKind.Announce,
      fromNodeId: this.channel.localNodeId,
      stateVector: vector,
      requests: null,
      entries: null,
    });
  }

  async writeLocalAsync(
    entityType: string,
    entityId: string,
    payload: string,
    isTombstone = false,
  ): Promise<SyncableEntry> {
    this.throwIfDisposed();
    if (!entityType || entityType.trim().length === 0) throw new Error("entityType required");
    if (!entityId || entityId.trim().length === 0) throw new Error("entityId required");

    const effectivePayload = payload ?? "";
    const entry: SyncableEntry = {
      entityType,
      entityId,
      version: this.clock.tick(),
      isTombstone,
      contentHash: computeHash(effectivePayload),
      payload: effectivePayload,
      sourceNodeId: this.channel.localNodeId,
      authoredAt: this.wallClock(),
    };

    await this.store.applyAsync(entry);

    if (this.subscription !== null) {
      await this.channel.sendAsync({
        kind: SyncEnvelopeKind.Push,
        fromNodeId: this.channel.localNodeId,
        stateVector: null,
        requests: null,
        entries: [entry],
      });
    }
    return entry;
  }

  // ── Inbound envelope handling ──────────────────────────────────────────────

  private async handleEnvelopeAsync(envelope: SyncEnvelope): Promise<void> {
    switch (envelope.kind) {
      case SyncEnvelopeKind.Announce:
        await this.handleAnnounceAsync(envelope);
        break;
      case SyncEnvelopeKind.Request:
        await this.handleRequestAsync(envelope);
        break;
      case SyncEnvelopeKind.Push:
        await this.handlePushAsync(envelope);
        break;
    }
  }

  private async handleAnnounceAsync(envelope: SyncEnvelope): Promise<void> {
    if (envelope.stateVector === null) return;
    const local = await this.store.getStateVectorAsync();
    const localMap = new Map<string, bigint>();
    for (const v of local) localMap.set(v.entityType, v.maxKnownVersion);

    const requests: RequestItem[] = [];
    for (const peer of envelope.stateVector) {
      const ourMax = localMap.get(peer.entityType) ?? 0n;
      if (peer.maxKnownVersion > ourMax) {
        requests.push({ entityType: peer.entityType, sinceVersion: ourMax });
      }
    }
    if (requests.length === 0) return;

    await this.channel.sendAsync({
      kind: SyncEnvelopeKind.Request,
      fromNodeId: this.channel.localNodeId,
      stateVector: null,
      requests,
      entries: null,
    });
  }

  private async handleRequestAsync(envelope: SyncEnvelope): Promise<void> {
    if (envelope.requests === null || envelope.requests.length === 0) return;
    const collected: SyncableEntry[] = [];
    for (const req of envelope.requests) {
      const newer = await this.store.getSinceAsync(req.entityType, req.sinceVersion);
      collected.push(...newer);
    }
    if (collected.length === 0) return;

    await this.channel.sendAsync({
      kind: SyncEnvelopeKind.Push,
      fromNodeId: this.channel.localNodeId,
      stateVector: null,
      requests: null,
      entries: collected,
    });
  }

  private async handlePushAsync(envelope: SyncEnvelope): Promise<void> {
    if (envelope.entries === null) return;
    let anyApplied = false;
    for (const e of envelope.entries) {
      this.clock.observe(e.version);
      const applied = await this.store.applyAsync(e);
      anyApplied = anyApplied || applied;
    }
    // If anything applied, re-announce so other peers can converge too.
    if (anyApplied) await this.syncNowAsync();
  }

  // ── Disposal ────────────────────────────────────────────────────────────────

  disposeAsync(): Promise<void> {
    if (this.disposed) return Promise.resolve();
    this.disposed = true;
    this.subscription?.dispose();
    this.subscription = null;
    return Promise.resolve();
  }

  private throwIfDisposed(): void {
    if (this.disposed) throw new Error("CompanionStateSyncEngine disposed");
  }
}

/** SHA-256 (lowercase hex) of the payload's UTF-8 bytes. Matches the C#
 * Convert.ToHexString(SHA256.HashData(...)).ToLowerInvariant() output. */
function computeHash(payload: string): string {
  return createHash("sha256").update(payload, "utf8").digest("hex");
}

// ─────────────────────────────────────────────────────────────────────────────
// PersonaStateSyncBridge (PersonaStateSyncBridge.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Bridges {@link IPersonaStore} ↔ {@link ICompanionStateSyncEngine}.
 * On {@link saveAsync}, the persona is JSON-serialised and pushed.
 */
export class PersonaStateSyncBridge {
  /** EntityType used on the wire for PersonaState entries. */
  static readonly entityType = "PersonaState";

  private readonly store: IPersonaStore;
  private readonly engine: ICompanionStateSyncEngine;

  constructor(store: IPersonaStore, engine: ICompanionStateSyncEngine) {
    if (!store) throw new Error("store required");
    if (!engine) throw new Error("engine required");
    this.store = store;
    this.engine = engine;
  }

  /** Persists {@link persona} locally AND broadcasts it via sync. */
  async saveAsync(persona: PersonaState): Promise<void> {
    if (!persona) throw new Error("persona required");
    await this.store.saveAsync(persona);
    const payload = serialisePersona(persona);
    await this.engine.writeLocalAsync(
      PersonaStateSyncBridge.entityType,
      persona.userId,
      payload,
      false,
    );
  }

  /**
   * Decodes a {@link SyncableEntry} back into a {@link PersonaState}. Useful for
   * handlers that subscribe to inbound updates. Returns null for tombstones,
   * wrong entity types, or undecodable payloads.
   */
  static tryDecode(entry: SyncableEntry): PersonaState | null {
    if (entry.isTombstone) return null;
    if (entry.entityType !== PersonaStateSyncBridge.entityType) return null;
    try {
      return deserialisePersona(entry.payload);
    } catch {
      return null;
    }
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionConversationSyncBridge (CompanionConversationSyncBridge.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Wire-format payload of an in-flight conversation turn. The EntityId is the
 * SessionId so multiple sessions converge independently.
 */
export interface ConversationStateDelta {
  /** Stable identifier the originating device uses for this conversation. */
  readonly sessionId: string;
  /** The latest user utterance for this turn (may be partial transcript). */
  readonly userText: string;
  /** Assistant reply so far — empty until the model starts emitting tokens. */
  readonly assistantText: string;
  /** True once the turn finished; false during streaming. */
  readonly isTurnComplete: boolean;
  /** When the originating device started the turn. */
  readonly startedAtUtc: Date;
  /** When this delta was authored. */
  readonly updatedAtUtc: Date;
}

/**
 * Bridges live {@link ConversationStateDelta} snapshots to the existing
 * {@link ICompanionStateSyncEngine} wire so any peer device subscribing to the
 * "ConversationState" entity type can mirror or hand off the conversation.
 */
export class CompanionConversationSyncBridge {
  /** EntityType used on the wire for conversation-state entries. */
  static readonly entityType = "ConversationState";

  private readonly engine: ICompanionStateSyncEngine;

  constructor(engine: ICompanionStateSyncEngine) {
    if (!engine) throw new Error("engine required");
    this.engine = engine;
  }

  /**
   * Broadcast a conversation-state snapshot to peer devices. The receiving
   * device's bridge subscribes via {@link ICompanionStateChannel} and routes
   * the delta into its own runtime.
   */
  async publishAsync(delta: ConversationStateDelta): Promise<SyncableEntry> {
    if (!delta) throw new Error("delta required");
    if (!delta.sessionId || delta.sessionId.trim().length === 0) {
      throw new Error("SessionId required");
    }
    const payload = serialiseConversationDelta(delta);
    return this.engine.writeLocalAsync(
      CompanionConversationSyncBridge.entityType,
      delta.sessionId,
      payload,
      false,
    );
  }

  /**
   * Mark the session as ended so peers can clean up shadow state. Uses the
   * sync-layer tombstone primitive — peers receive an empty payload.
   */
  async terminateAsync(sessionId: string): Promise<SyncableEntry> {
    if (!sessionId || sessionId.trim().length === 0) throw new Error("sessionId required");
    return this.engine.writeLocalAsync(
      CompanionConversationSyncBridge.entityType,
      sessionId,
      "",
      true,
    );
  }

  /** Decode a sync-layer entry back to a typed delta. */
  static tryDecode(entry: SyncableEntry): ConversationStateDelta | null {
    if (!entry) throw new Error("entry required");
    if (entry.isTombstone) return null;
    if (entry.entityType !== CompanionConversationSyncBridge.entityType) return null;
    try {
      return deserialiseConversationDelta(entry.payload);
    } catch {
      return null;
    }
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// LoraAdapterSyncBridge (LoraAdapterSyncBridge.cs)
// ─────────────────────────────────────────────────────────────────────────────

/** Payload of a synced LoRA adapter snapshot. */
export interface LoraAdapterSnapshot {
  /** Stable id (typically "personal-{userId}"). */
  readonly adapterId: string;
  /** Adapter file contents, base64-encoded. */
  readonly base64Bytes: string;
  /** When training that produced these bytes finished. */
  readonly trainedAtUtc: Date;
  /** Total training steps so far (monotonic). */
  readonly stepCount: bigint;
}

/**
 * Abstraction over adapter file storage so the bridge stays in-memory /
 * dependency-injected. The C# reference reads/writes disk directly via
 * System.IO; the TS port injects this seam instead.
 */
export interface IAdapterFileStore {
  /** Reads the raw adapter bytes at the given path. Throws if not found. */
  readAllBytesAsync(path: string): Promise<Uint8Array>;
  /** Returns true when a file exists at the given path. */
  existsAsync(path: string): Promise<boolean>;
  /** Writes the raw adapter bytes to the given path (creating dirs as needed). */
  writeAllBytesAsync(path: string, bytes: Uint8Array): Promise<void>;
}

/**
 * Deterministic in-memory {@link IAdapterFileStore}. Backs the LoRA adapter
 * bridge in tests and headless hosts — no real filesystem is touched.
 */
export class InMemoryAdapterFileStore implements IAdapterFileStore {
  private readonly files = new Map<string, Uint8Array>();

  /** Seeds a file so {@link LoraAdapterSyncBridge.publishAsync} can read it. */
  set(path: string, bytes: Uint8Array): void {
    this.files.set(path, Uint8Array.from(bytes));
  }

  /** Returns a copy of the stored bytes, or null when absent. */
  get(path: string): Uint8Array | null {
    const b = this.files.get(path);
    return b ? Uint8Array.from(b) : null;
  }

  readAllBytesAsync(path: string): Promise<Uint8Array> {
    const b = this.files.get(path);
    if (b === undefined) return Promise.reject(new Error(`adapter file not found: ${path}`));
    return Promise.resolve(Uint8Array.from(b));
  }

  existsAsync(path: string): Promise<boolean> {
    return Promise.resolve(this.files.has(path));
  }

  writeAllBytesAsync(path: string, bytes: Uint8Array): Promise<void> {
    this.files.set(path, Uint8Array.from(bytes));
    return Promise.resolve();
  }
}

/**
 * Bridges trained LoRA adapter bytes across the user's devices through the
 * {@link CompanionStateSyncEngine}. Adapter bytes are base64-encoded into the
 * SyncableEntry payload; receiving devices decode and persist for the
 * LoRAAdapterManager to apply.
 */
export class LoraAdapterSyncBridge {
  /** EntityType used on the wire. */
  static readonly entityType = "LoraAdapter";

  private readonly engine: ICompanionStateSyncEngine;
  private readonly files: IAdapterFileStore;

  constructor(engine: ICompanionStateSyncEngine, files: IAdapterFileStore) {
    if (!engine) throw new Error("engine required");
    if (!files) throw new Error("files required");
    this.engine = engine;
    this.files = files;
  }

  /** Publish a trained adapter to peer devices. */
  async publishAsync(adapterId: string, adapterPath: string, stepCount: bigint): Promise<void> {
    if (!adapterId || adapterId.trim().length === 0) throw new Error("adapterId required");
    if (!adapterPath || adapterPath.trim().length === 0) throw new Error("adapterPath required");
    if (!(await this.files.existsAsync(adapterPath))) {
      throw new Error(`adapter file not found: ${adapterPath}`);
    }
    const bytes = await this.files.readAllBytesAsync(adapterPath);
    const snapshot: LoraAdapterSnapshot = {
      adapterId,
      base64Bytes: base64Encode(bytes),
      trainedAtUtc: new Date(),
      stepCount,
    };
    const payload = serialiseLoraSnapshot(snapshot);
    await this.engine.writeLocalAsync(LoraAdapterSyncBridge.entityType, adapterId, payload, false);
  }

  /**
   * Decode an inbound {@link SyncableEntry}, write the adapter to
   * {@link destinationPath}. Returns the decoded snapshot for caller-side
   * bookkeeping (e.g. trigger Apply), or null when the entry is not a LoRA
   * adapter payload.
   */
  static async tryWriteAsync(
    entry: SyncableEntry,
    destinationPath: string,
    files: IAdapterFileStore,
  ): Promise<LoraAdapterSnapshot | null> {
    if (!entry) throw new Error("entry required");
    if (entry.isTombstone) return null;
    if (entry.entityType !== LoraAdapterSyncBridge.entityType) return null;

    let snapshot: LoraAdapterSnapshot | null;
    try {
      snapshot = deserialiseLoraSnapshot(entry.payload);
    } catch {
      return null;
    }
    if (snapshot === null) return null;

    // Normalise a null base64 field to empty (matches C# `with` fix-up).
    const normalised: LoraAdapterSnapshot = {
      ...snapshot,
      base64Bytes: snapshot.base64Bytes ?? "",
    };
    if (normalised.base64Bytes.length === 0) return normalised;

    try {
      const bytes = base64Decode(normalised.base64Bytes);
      await files.writeAllBytesAsync(destinationPath, bytes);
    } catch {
      // Best-effort — mirror C# swallow-and-log.
    }
    return normalised;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Serialisation helpers — camelCase JSON, matching the payloads the bridges
// round-trip. Bigints are serialised as JSON numbers where they fit (step
// counts are small); wall-clock timestamps use ISO-8601 strings.
// ─────────────────────────────────────────────────────────────────────────────

interface PersonaWire {
  userId: string;
  lastUpdatedUtc: string;
  verbosity: string;
  formality: string;
  preferredLocale: string | null;
  topicWeights: Record<string, number>;
  disfavouredTopics: string[];
  totalInteractions: number;
  positiveSignals: number;
  negativeSignals: number;
}

function serialisePersona(p: PersonaState): string {
  const wire: PersonaWire = {
    userId: p.userId,
    lastUpdatedUtc: p.lastUpdatedUtc.toISOString(),
    verbosity: p.verbosity,
    formality: p.formality,
    preferredLocale: p.preferredLocale,
    topicWeights: { ...p.topicWeights },
    disfavouredTopics: [...p.disfavouredTopics],
    totalInteractions: p.totalInteractions,
    positiveSignals: p.positiveSignals,
    negativeSignals: p.negativeSignals,
  };
  return JSON.stringify(wire);
}

function deserialisePersona(json: string): PersonaState {
  const wire = JSON.parse(json) as Partial<PersonaWire>;
  const state = new PersonaState();
  if (wire.userId !== undefined) state.userId = wire.userId;
  if (wire.lastUpdatedUtc !== undefined) state.lastUpdatedUtc = new Date(wire.lastUpdatedUtc);
  if (wire.verbosity !== undefined) state.verbosity = wire.verbosity;
  if (wire.formality !== undefined) state.formality = wire.formality;
  if (wire.preferredLocale !== undefined) state.preferredLocale = wire.preferredLocale;
  if (wire.topicWeights !== undefined) state.topicWeights = { ...wire.topicWeights };
  if (wire.disfavouredTopics !== undefined) state.disfavouredTopics = new Set(wire.disfavouredTopics);
  if (wire.totalInteractions !== undefined) state.totalInteractions = wire.totalInteractions;
  if (wire.positiveSignals !== undefined) state.positiveSignals = wire.positiveSignals;
  if (wire.negativeSignals !== undefined) state.negativeSignals = wire.negativeSignals;
  return state;
}

interface ConversationWire {
  sessionId: string;
  userText: string;
  assistantText: string;
  isTurnComplete: boolean;
  startedAtUtc: string;
  updatedAtUtc: string;
}

function serialiseConversationDelta(d: ConversationStateDelta): string {
  const wire: ConversationWire = {
    sessionId: d.sessionId,
    userText: d.userText,
    assistantText: d.assistantText,
    isTurnComplete: d.isTurnComplete,
    startedAtUtc: d.startedAtUtc.toISOString(),
    updatedAtUtc: d.updatedAtUtc.toISOString(),
  };
  return JSON.stringify(wire);
}

function deserialiseConversationDelta(json: string): ConversationStateDelta {
  const wire = JSON.parse(json) as ConversationWire;
  return {
    sessionId: wire.sessionId,
    userText: wire.userText,
    assistantText: wire.assistantText,
    isTurnComplete: wire.isTurnComplete,
    startedAtUtc: new Date(wire.startedAtUtc),
    updatedAtUtc: new Date(wire.updatedAtUtc),
  };
}

interface LoraWire {
  adapterId: string;
  base64Bytes: string;
  trainedAtUtc: string;
  stepCount: number;
}

function serialiseLoraSnapshot(s: LoraAdapterSnapshot): string {
  const wire: LoraWire = {
    adapterId: s.adapterId,
    base64Bytes: s.base64Bytes,
    trainedAtUtc: s.trainedAtUtc.toISOString(),
    stepCount: Number(s.stepCount),
  };
  return JSON.stringify(wire);
}

function deserialiseLoraSnapshot(json: string): LoraAdapterSnapshot | null {
  const wire = JSON.parse(json) as Partial<LoraWire>;
  if (wire.adapterId === undefined) return null;
  return {
    adapterId: wire.adapterId,
    base64Bytes: wire.base64Bytes ?? "",
    trainedAtUtc: wire.trainedAtUtc !== undefined ? new Date(wire.trainedAtUtc) : new Date(0),
    stepCount: BigInt(wire.stepCount ?? 0),
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// base64 — Node Buffer when available, portable fallback otherwise.
// ─────────────────────────────────────────────────────────────────────────────

function base64Encode(bytes: Uint8Array): string {
  if (typeof Buffer !== "undefined") {
    return Buffer.from(bytes).toString("base64");
  }
  let binary = "";
  for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
  // btoa exists in browser/worker contexts.
  return btoa(binary);
}

function base64Decode(b64: string): Uint8Array {
  if (typeof Buffer !== "undefined") {
    return new Uint8Array(Buffer.from(b64, "base64"));
  }
  const binary = atob(b64);
  const out = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) out[i] = binary.charCodeAt(i);
  return out;
}
