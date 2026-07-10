// aethernet/index.ts
// Full-parity port of CircleAI.AetherNet's mesh capability discovery (C#).
// C# is the exact spec (MeshCapabilityRegistry.cs, RT-12 v1).
//
// Peers broadcast what they have loaded ("I have Qwen3-1.7B-MNN with 2048 tokens
// of free KV budget on a Tier=Phone device"). v1 ships the contracts + an
// in-memory registry; the AetherNet broadcast transport lands later (RT-12 v2).
//
// Ports:
//   MeshCapabilityAdvertisement       — one peer's advertisement (pure data record)
//   IMeshCapabilityRegistry           — upsert / remove / list / find contract
//   InMemoryMeshCapabilityRegistry    — thread-safe (single-threaded JS) in-memory impl
//   IMeshCapabilityBroadcaster        — "publish OUR advertisement" contract
//   NullMeshCapabilityBroadcaster     — no-op default (no transport bound)
//   RegistryBackedMeshCapabilityBroadcaster — a working broadcaster that feeds a
//                                       local registry (deterministic loopback,
//                                       so nothing is a stub)
//
// DeviceTier is reused from the already-ported CircleAI.Core (src/device).

// DeviceTier is owned by CircleAI.Core (src/device) and already exported at the
// package root there; we import it for the advertisement's `tier` field but do
// NOT re-export it, to avoid a duplicate-export collision in the src/index.ts barrel.
import type { DeviceTier } from "../device/index.js";

// ─────────────────────────────────────────────────────────────────────────────
// MeshCapabilityAdvertisement
// ─────────────────────────────────────────────────────────────────────────────

/**
 * (RT-12 v1) One peer's advertisement of what it can serve right now. Pure data
 * — no execution state.
 */
export interface MeshCapabilityAdvertisement {
  /** Stable opaque identifier for the advertising peer. */
  readonly peerId: string;
  /** The model the peer has loaded, e.g. `"Qwen3-1.7B-MNN"`. */
  readonly modelId: string;
  /** How many tokens of KV-cache budget the peer has spare. */
  readonly freeKvTokens: number;
  /** The peer's device tier (Wearable .. Workstation). */
  readonly tier: DeviceTier;
  /** The model's configured context window. */
  readonly contextWindowTokens: number;
  /** When the peer last published this advertisement. */
  readonly advertisedAtUtc: Date;
  /** Optional round-trip estimate, in milliseconds; null when unknown. */
  readonly latencyHintMs: number | null;
}

/**
 * Builds a {@link MeshCapabilityAdvertisement}, defaulting `latencyHintMs` to
 * null (mirrors the C# optional parameter).
 */
export function meshCapabilityAdvertisement(
  peerId: string,
  modelId: string,
  freeKvTokens: number,
  tier: DeviceTier,
  contextWindowTokens: number,
  advertisedAtUtc: Date,
  latencyHintMs: number | null = null,
): MeshCapabilityAdvertisement {
  return { peerId, modelId, freeKvTokens, tier, contextWindowTokens, advertisedAtUtc, latencyHintMs };
}

// ─────────────────────────────────────────────────────────────────────────────
// IMeshCapabilityRegistry
// ─────────────────────────────────────────────────────────────────────────────

/**
 * (RT-12 v1) Holds the latest advertisement per peer + supports filtered query.
 * The AetherNet transport (v2) feeds this registry as peers broadcast. v1 lets
 * hosting layers query and reason about availability without yet routing.
 */
export interface IMeshCapabilityRegistry {
  /**
   * Publish or replace an advertisement. Called by the transport on receipt of a
   * peer broadcast.
   */
  upsertAsync(ad: MeshCapabilityAdvertisement, signal?: AbortSignal): Promise<void>;

  /** Remove a peer (e.g. on explicit disconnect). Idempotent. Returns whether a peer was removed. */
  removeAsync(peerId: string, signal?: AbortSignal): Promise<boolean>;

  /**
   * Return every advertisement currently known. Use `staleAfterMs` to filter out
   * entries older than this many milliseconds (C# passes a `TimeSpan`). When
   * omitted, all entries are returned.
   */
  list(staleAfterMs?: number | null): readonly MeshCapabilityAdvertisement[];

  /**
   * Find every peer that has loaded `modelId` with at least `minFreeKvTokens` of
   * spare KV budget. Sorted by spare budget descending — the most-capable peer
   * comes first. `modelId` match is case-insensitive.
   */
  find(modelId: string, minFreeKvTokens?: number, staleAfterMs?: number | null): readonly MeshCapabilityAdvertisement[];
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryMeshCapabilityRegistry
// ─────────────────────────────────────────────────────────────────────────────

/**
 * (RT-12 v1) Default {@link IMeshCapabilityRegistry} — in-memory. The AetherNet
 * transport plugs into this; without a transport, the registry just stays empty
 * (no peers).
 *
 * C# uses a `ConcurrentDictionary` keyed by an ordinal (case-SENSITIVE) peer id;
 * JS `Map` keying is equivalently case-sensitive on the string key. `find`'s
 * `modelId` comparison is case-INSENSITIVE, matching `StringComparison.OrdinalIgnoreCase`.
 * Staleness cutoffs use `>= cutoff` exactly as the C# `Where(a => a.AdvertisedAtUtc >= cutoff)`.
 */
export class InMemoryMeshCapabilityRegistry implements IMeshCapabilityRegistry {
  private readonly entries = new Map<string, MeshCapabilityAdvertisement>();

  /** Optional clock override for tests. Defaults to `() => new Date()`. */
  nowUtc: () => Date = () => new Date();

  upsertAsync(ad: MeshCapabilityAdvertisement, _signal?: AbortSignal): Promise<void> {
    if (ad == null) throw new Error("ad required");
    if (!ad.peerId || ad.peerId.trim().length === 0) throw new Error("ad.peerId required");
    this.entries.set(ad.peerId, ad);
    return Promise.resolve();
  }

  removeAsync(peerId: string, _signal?: AbortSignal): Promise<boolean> {
    if (!peerId || peerId.trim().length === 0) throw new Error("peerId required");
    return Promise.resolve(this.entries.delete(peerId));
  }

  list(staleAfterMs?: number | null): readonly MeshCapabilityAdvertisement[] {
    if (staleAfterMs === undefined || staleAfterMs === null) return [...this.entries.values()];
    const cutoff = this.nowUtc().getTime() - staleAfterMs;
    return [...this.entries.values()].filter((a) => a.advertisedAtUtc.getTime() >= cutoff);
  }

  find(
    modelId: string,
    minFreeKvTokens = 0,
    staleAfterMs?: number | null,
  ): readonly MeshCapabilityAdvertisement[] {
    if (!modelId || modelId.trim().length === 0) throw new Error("modelId required");
    // C#: cutoff = staleAfter.HasValue ? Now - staleAfter : DateTimeOffset.MinValue
    const cutoff =
      staleAfterMs === undefined || staleAfterMs === null
        ? Number.NEGATIVE_INFINITY
        : this.nowUtc().getTime() - staleAfterMs;
    const needle = modelId.toLowerCase();
    return [...this.entries.values()]
      .filter((a) => a.modelId.toLowerCase() === needle)
      .filter((a) => a.freeKvTokens >= minFreeKvTokens)
      .filter((a) => a.advertisedAtUtc.getTime() >= cutoff)
      .sort((a, b) => b.freeKvTokens - a.freeKvTokens);
  }

  /** Number of peers currently tracked. Convenience for tests (not in the C# contract). */
  get count(): number {
    return this.entries.size;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// IMeshCapabilityBroadcaster
// ─────────────────────────────────────────────────────────────────────────────

/**
 * (RT-12 v1) Contract for the broadcaster that publishes OUR advertisement to
 * the mesh. v1 ships a no-op default; the AetherNet transport binding (v2)
 * supersedes it.
 */
export interface IMeshCapabilityBroadcaster {
  /**
   * Publish our current advertisement to the mesh. v1 may be a no-op when no
   * transport is registered.
   */
  broadcastAsync(ad: MeshCapabilityAdvertisement, signal?: AbortSignal): Promise<void>;
}

/**
 * Default broadcaster — does nothing. Used when no AetherNet transport is bound.
 * Existing CircleAI deployments work unchanged. Mirrors `NullMeshCapabilityBroadcaster`.
 */
export class NullMeshCapabilityBroadcaster implements IMeshCapabilityBroadcaster {
  static readonly instance = new NullMeshCapabilityBroadcaster();
  broadcastAsync(_ad: MeshCapabilityAdvertisement, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
}

/**
 * A working in-memory broadcaster that loops our advertisement straight back
 * into a local {@link IMeshCapabilityRegistry}. This is the deterministic
 * stand-in for the (not-yet-shipped) AetherNet transport: broadcasting simply
 * upserts into the bound registry, so a host can exercise the full
 * broadcast → registry → find path without a real mesh.
 *
 * Not a stub — every call has an observable effect (the registry gains/updates
 * the advertisement).
 */
export class RegistryBackedMeshCapabilityBroadcaster implements IMeshCapabilityBroadcaster {
  private readonly registry: IMeshCapabilityRegistry;
  /** Count of successful broadcasts, for assertions. */
  broadcastCount = 0;

  constructor(registry: IMeshCapabilityRegistry) {
    if (registry == null) throw new Error("registry required");
    this.registry = registry;
  }

  async broadcastAsync(ad: MeshCapabilityAdvertisement, signal?: AbortSignal): Promise<void> {
    if (ad == null) throw new Error("ad required");
    await this.registry.upsertAsync(ad, signal);
    this.broadcastCount++;
  }
}
