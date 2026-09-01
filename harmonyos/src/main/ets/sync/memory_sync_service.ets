/**
 * sync/memory_sync_service
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// sync/memory_sync_service.ts
//
// IMemorySyncService + MemorySyncService — the push/receive orchestrator.
// Ported from CircleAI.Sync/ (IMemorySyncService.cs + MemorySyncService.cs).
//
// Serialises memory deltas, routes them through an ISyncChannel, and applies
// received deltas to the local IEpisodicMemoryStore. The transport is decided
// by the ISyncChannel — the app code is identical whether the delta travels
// gRPC, BLE mesh, or DTN bundle.
//
// The C# reference leaves the inbound episodic-apply as a `// Full wire`
// comment; per the no-stubs rule this port implements a real, deterministic
// upsert behind an injectable codec so the receive loop actually mutates the
// local store.

import type { EpisodicMemoryEntry, IEpisodicMemoryStore } from "../memory/index";
import { SyncDeliveryMode, SyncDomainKeys, type ISyncChannel, type SyncDelta } from "./index";

// ─────────────────────────────────────────────────────────────────────────────
// IMemorySyncService (IMemorySyncService.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Pushes and receives memory deltas across all owned devices. The transport is
 * determined by {@link ISyncChannel} — the app code is identical whether the
 * delta travels gRPC, BLE mesh, or DTN bundle.
 */
export interface IMemorySyncService {
  /** Push a memory delta for {@link ownerId} to all other devices. */
  pushMemoryDeltaAsync(
    ownerId: string,
    domainKey: string,
    delta: Uint8Array,
    mode?: SyncDeliveryMode,
  ): Promise<void>;

  /** Start receiving and applying incoming deltas for {@link ownerId}. */
  startReceivingAsync(ownerId: string): Promise<void>;

  /** Stop receiving. */
  stopReceivingAsync(): Promise<void>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Episodic payload codec — the deterministic seam that makes the receive loop
// actually apply inbound episodic deltas (the C# comment made concrete).
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Encodes/decodes an {@link EpisodicMemoryEntry} to and from the raw bytes
 * carried in a {@link SyncDelta.payload}. Injected so hosts can swap the wire
 * format (MessagePack, protobuf, …). The default is UTF-8 JSON.
 */
export interface IEpisodicDeltaCodec {
  encode(entry: EpisodicMemoryEntry): Uint8Array;
  /** Returns the decoded entry, or null when the bytes are not a valid entry. */
  decode(bytes: Uint8Array): EpisodicMemoryEntry | null;
}

interface EpisodicWire {
  id: string;
  recordedAtUtc: string;
  userText: string;
  assistantText: string;
  appContext?: string;
  embedding?: number[];
  tags?: Record<string, string>;
}

/** Default UTF-8 JSON {@link IEpisodicDeltaCodec}. */
export class JsonEpisodicDeltaCodec implements IEpisodicDeltaCodec {
  encode(entry: EpisodicMemoryEntry): Uint8Array {
    const wire: EpisodicWire = {
      id: entry.id,
      recordedAtUtc: entry.recordedAtUtc.toISOString(),
      userText: entry.userText,
      assistantText: entry.assistantText,
      appContext: entry.appContext,
      embedding: entry.embedding,
      tags: entry.tags,
    };
    return new TextEncoder().encode(JSON.stringify(wire));
  }

  decode(bytes: Uint8Array): EpisodicMemoryEntry | null {
    let wire: Partial<EpisodicWire>;
    try {
      wire = JSON.parse(new TextDecoder().decode(bytes)) as Partial<EpisodicWire>;
    } catch {
      return null;
    }
    if (wire.id === undefined || wire.recordedAtUtc === undefined) return null;
    return {
      id: wire.id,
      recordedAtUtc: new Date(wire.recordedAtUtc),
      userText: wire.userText ?? "",
      assistantText: wire.assistantText ?? "",
      appContext: wire.appContext,
      embedding: wire.embedding,
      tags: wire.tags,
    };
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// MemorySyncService (MemorySyncService.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Default {@link IMemorySyncService} implementation. Serialises memory deltas,
 * routes them through {@link ISyncChannel}, and applies received deltas to the
 * local {@link IEpisodicMemoryStore}.
 */
export class MemorySyncService implements IMemorySyncService {
  private readonly channel: ISyncChannel;
  private readonly store: IEpisodicMemoryStore;
  private readonly localDeviceId: string;
  private readonly codec: IEpisodicDeltaCodec;
  private readonly now: () => Date;

  private receiveController: AbortController | null = null;
  private receiveLoop: Promise<void> | null = null;

  constructor(
    channel: ISyncChannel,
    store: IEpisodicMemoryStore,
    localDeviceId: string,
    codec: IEpisodicDeltaCodec = new JsonEpisodicDeltaCodec(),
    now: () => Date = () => new Date(),
  ) {
    if (!channel) throw new Error("channel required");
    if (!store) throw new Error("store required");
    this.channel = channel;
    this.store = store;
    this.localDeviceId = localDeviceId;
    this.codec = codec;
    this.now = now;
  }

  pushMemoryDeltaAsync(
    ownerId: string,
    domainKey: string,
    delta: Uint8Array,
    mode: SyncDeliveryMode = SyncDeliveryMode.GUARANTEED,
  ): Promise<void> {
    const createdAt = this.now();
    const syncDelta: SyncDelta = {
      ownerId,
      sourceDeviceId: this.localDeviceId,
      targetDeviceId: "", // broadcast to all owned devices
      domainKey,
      payload: delta,
      sequence: createdAt.getTime(),
      deliveryMode: mode,
      ttlMs: undefined,
      createdAt,
    };
    return this.channel.pushDeltaAsync(syncDelta);
  }

  startReceivingAsync(ownerId: string): Promise<void> {
    this.receiveController = new AbortController();
    // Fire-and-forget receive loop (mirrors the C# `_ = ReceiveLoopAsync(...)`).
    this.receiveLoop = this.receiveLoopAsync(ownerId, this.receiveController.signal);
    return Promise.resolve();
  }

  async stopReceivingAsync(): Promise<void> {
    if (this.receiveController !== null) {
      this.receiveController.abort();
    }
    const loop = this.receiveLoop;
    this.receiveController = null;
    this.receiveLoop = null;
    if (loop) {
      try {
        await loop;
      } catch {
        /* expected on abort */
      }
    }
  }

  private async receiveLoopAsync(ownerId: string, signal: AbortSignal): Promise<void> {
    for await (const delta of this.channel.receiveDeltasAsync(ownerId)) {
      if (signal.aborted) break;
      if (delta.sourceDeviceId === this.localDeviceId) continue; // skip own echoes

      if (delta.domainKey === SyncDomainKeys.EPISODIC_MEMORY) {
        // Full wire: deserialise and upsert into the local episodic store.
        const entry = this.codec.decode(delta.payload);
        if (entry !== null) {
          await this.store.addAsync(entry);
        }
      }
      // Additional domain handlers (affect, persona, goals) go here.
    }
  }
}
