/**
 * media/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// media/index.ts
// Full-parity port of CircleAI.Media (MediaPrimitives.cs). C# is the exact spec.
//
// Real domain types + in-memory library for the Media vertical (audio + video +
// image asset catalog).
//
// NOTE: This module had no TypeScript counterpart before the StubGuard parity
// pass; it is ported here in full (base types + interface + in-memory impl,
// including the StubGuard additions Remove/Count/TotalBytes/ByMime) so the port
// stays at parity with the C# reference.
//
// Type mappings (C# → TS):
//   enum MediaKind                   → const enum-like (Audio=0, Video=1, Image=2)
//   record MediaAsset                → readonly interface (+ positional factory)
//   TimeSpan? Duration               → number | null  (milliseconds)
//   long Bytes                       → number
//   DateTimeOffset CreatedAtUtc      → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   ListByKind — assets of a kind, newest-first (CreatedAtUtc descending).
//   ByMime     — Mime starts-with prefix (OrdinalIgnoreCase), newest-first;
//                empty prefix → []. TotalBytes sums every asset's Bytes.
//   Search     — Title contains q (OrdinalIgnoreCase), newest-first, top-K.

/** The kind of a media asset. Mirrors C# `MediaKind` (Audio = 0). */
export type MediaKind = 0 | 1 | 2;
/** Frozen value object for {@link MediaKind} members. */
export const MediaKind = Object.freeze({
  Audio: 0,
  Video: 1,
  Image: 2,
} as const) satisfies Map<string, MediaKind>;

/** A catalogued media asset. Mirrors C# `MediaAsset` record. */
export interface MediaAsset {
  readonly assetId: string;
  readonly title: string;
  readonly kind: MediaKind;
  /** Optional duration in milliseconds (C# `TimeSpan? Duration`); null for images. */
  readonly durationMs: number | null;
  /** On-disk footprint in bytes (C# `long Bytes`). */
  readonly bytes: number;
  readonly mime: string;
  /** UTC creation instant (C# `DateTimeOffset CreatedAtUtc`). */
  readonly createdAtUtc: Date;
}

/** Constructs a {@link MediaAsset}. */
export function mediaAsset(
  assetId: string,
  title: string,
  kind: MediaKind,
  durationMs: number | null,
  bytes: number,
  mime: string,
  createdAtUtc: Date,
): MediaAsset {
  return { assetId, title, kind, durationMs, bytes, mime, createdAtUtc };
}

/** The media library contract. Mirrors C# `IMediaLibrary`. */
export interface IMediaLibrary {
  add(a: MediaAsset): void;
  get(id: string): MediaAsset | undefined;
  /** Remove an asset by id. Returns whether it was present. */
  remove(id: string): boolean;
  /** Number of assets currently catalogued. */
  readonly count: number;
  /** Total on-disk footprint of every catalogued asset, in bytes. */
  readonly totalBytes: number;
  listByKind(kind: MediaKind): readonly MediaAsset[];
  /** Assets whose MIME starts with `mimePrefix` (case-insensitive), newest-first. */
  byMime(mimePrefix: string): readonly MediaAsset[];
  search(q: string, topK?: number): readonly MediaAsset[];
}

/** Deterministic in-memory {@link IMediaLibrary}. */
export class InMemoryMediaLibrary implements IMediaLibrary {
  private readonly items = new Map<string, MediaAsset>();

  add(a: MediaAsset): void {
    if (a == null) throw new Error("a required");
    if (a.assetId == null || a.assetId.trim().length === 0) throw new Error("AssetId required");
    this.items.set(a.assetId, a);
  }

  get(id: string): MediaAsset | undefined {
    return this.items.get(id);
  }

  /** Remove an asset by id. Returns whether it was present. Mirrors C# `Remove`. */
  remove(id: string): boolean {
    if (id == null || id.length === 0) return false;
    return this.items.delete(id);
  }

  /** Number of assets currently catalogued. Mirrors C# `Count`. */
  get count(): number {
    return this.items.size;
  }

  /** Total on-disk footprint of every catalogued asset, in bytes. Mirrors C# `TotalBytes`. */
  get totalBytes(): number {
    let sum = 0;
    for (const a of this.items.values()) sum += a.bytes;
    return sum;
  }

  listByKind(kind: MediaKind): readonly MediaAsset[] {
    return [...this.items.values()]
      .filter((a) => a.kind === kind)
      .sort((x, y) => y.createdAtUtc.getTime() - x.createdAtUtc.getTime());
  }

  /**
   * Assets whose MIME type starts with a given prefix (e.g. "image/", "audio/"),
   * matched case-insensitively and returned newest-first. Empty prefix yields
   * nothing. Mirrors C# `ByMime`.
   */
  byMime(mimePrefix: string): readonly MediaAsset[] {
    if (mimePrefix == null || mimePrefix.length === 0) return [];
    const prefix = mimePrefix.toLowerCase();
    return [...this.items.values()]
      .filter((a) => a.mime.toLowerCase().startsWith(prefix))
      .sort((x, y) => y.createdAtUtc.getTime() - x.createdAtUtc.getTime());
  }

  search(q: string, topK = 20): readonly MediaAsset[] {
    if (q == null) throw new Error("q required");
    if (topK <= 0) throw new Error("topK must be positive");
    const needle = q.toLowerCase();
    return [...this.items.values()]
      .filter((a) => a.title.toLowerCase().includes(needle))
      .sort((x, y) => y.createdAtUtc.getTime() - x.createdAtUtc.getTime())
      .slice(0, topK);
  }
}
