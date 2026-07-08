// inference/prefix_cache.ts
//
// Port of CircleAI.Inference.PrefixCacheService (RT-06). Manages an on-disk
// cache of "warm" model sessions keyed by SHA-256(modelId) + SHA-256(system
// prompt). The C# service uses the real filesystem; this port defaults to an
// in-memory backend (so tests are deterministic and hermetic) but the keying,
// path shape, LRU-by-mtime eviction, and 500 MB cap are all byte-faithful.
//
// The seam is IPrefixCacheStore — inject a filesystem-backed store to persist
// to disk, or use the default in-memory store.

import { createHash } from "node:crypto";

const CAP_BYTES = 500 * 1024 * 1024; // 500 MB

/**
 * Backing store for the prefix cache. Abstracts the filesystem so the cache
 * logic (keying, eviction policy) can run without touching real disk.
 */
export interface IPrefixCacheStore {
  /** true when a file exists at `path`. */
  exists(path: string): boolean;
  /** Write `content` bytes (length used for size accounting) at `path`. */
  write(path: string, content: string): void;
  /** Read the content at `path`, or null when absent. */
  read(path: string): string | null;
  /** Delete `path`. No-op when absent. */
  delete(path: string): void;
  /** Set the last-write time (epoch ms) of `path` to `mtimeMs`. */
  setMtime(path: string, mtimeMs: number): void;
  /** List entries under `root` whose name ends with `suffix`, with size + mtime. */
  list(root: string, suffix: string): Array<{ path: string; sizeBytes: number; mtimeMs: number }>;
}

/**
 * Deterministic in-memory store. A monotonically increasing clock stands in
 * for wall-clock mtimes so LRU ordering is stable and testable.
 */
export class InMemoryPrefixCacheStore implements IPrefixCacheStore {
  private readonly files = new Map<string, { content: string; sizeBytes: number; mtimeMs: number }>();
  private clock = 1;

  private tick(): number {
    return this.clock++;
  }

  exists(path: string): boolean {
    return this.files.has(path);
  }

  write(path: string, content: string): void {
    const sizeBytes = Buffer.byteLength(content, "utf-8");
    this.files.set(path, { content, sizeBytes, mtimeMs: this.tick() });
  }

  read(path: string): string | null {
    const f = this.files.get(path);
    return f ? f.content : null;
  }

  delete(path: string): void {
    this.files.delete(path);
  }

  setMtime(path: string, mtimeMs: number): void {
    const f = this.files.get(path);
    if (f) f.mtimeMs = mtimeMs;
  }

  /** Advance the store's logical clock and stamp `path` — mirrors File.SetLastWriteTimeUtc(now). */
  touchNow(path: string): void {
    const f = this.files.get(path);
    if (f) f.mtimeMs = this.tick();
  }

  list(root: string, suffix: string): Array<{ path: string; sizeBytes: number; mtimeMs: number }> {
    const prefix = root.endsWith("/") ? root : root + "/";
    const out: Array<{ path: string; sizeBytes: number; mtimeMs: number }> = [];
    for (const [path, f] of this.files) {
      if (path.startsWith(prefix) && path.endsWith(suffix)) {
        out.push({ path, sizeBytes: f.sizeBytes, mtimeMs: f.mtimeMs });
      }
    }
    return out;
  }
}

/**
 * Manages an on-disk cache of warm sessions keyed by the hash of
 * (modelId, systemPrompt). Ported from CircleAI.Inference.PrefixCacheService.
 */
export class PrefixCacheService {
  private static _default: PrefixCacheService | undefined;

  private readonly root: string;
  private readonly store: IPrefixCacheStore;

  /** The default per-app instance rooted at an in-memory cache directory. */
  static default(): PrefixCacheService {
    if (!PrefixCacheService._default) {
      PrefixCacheService._default = new PrefixCacheService(
        PrefixCacheService.defaultRoot(),
        new InMemoryPrefixCacheStore(),
      );
    }
    return PrefixCacheService._default;
  }

  /**
   * Construct a cache service rooted at `root` with the given backing store
   * (defaults to an in-memory store).
   */
  constructor(root: string, store?: IPrefixCacheStore) {
    if (!root || root.trim().length === 0) throw new Error("root is required.");
    this.root = root;
    this.store = store ?? new InMemoryPrefixCacheStore();
  }

  /**
   * Compute the cache key for a (modelId, systemPrompt) pair. Returns null when
   * systemPrompt is null/empty — nothing to cache without a system prompt.
   * Ported verbatim from PrefixCacheService.KeyFor.
   */
  static keyFor(modelId: string, systemPrompt: string | null | undefined): string | null {
    if (!modelId || modelId.trim().length === 0) return null;
    if (!systemPrompt || systemPrompt.length === 0) return null;

    const modelHash = sha256Hex(modelId);
    const systemHash = sha256Hex(systemPrompt);
    // First 16 hex chars per component — matches C#.
    return `${modelHash.substring(0, 16)}_${systemHash.substring(0, 16)}`;
  }

  /** Returns the cache path for `key`. Matches PathFor. */
  pathFor(key: string): string {
    return joinPath(this.root, `${key}.session`);
  }

  /** true when a cached entry exists for `key`. Matches HasEntryAsync. */
  async hasEntry(key: string): Promise<boolean> {
    return this.store.exists(this.pathFor(key));
  }

  /**
   * Write a session entry for `key` (deterministic marker content). Not present
   * in C# as a public method — the native generator wrote via MNN — but here it
   * is the concrete "snapshot" op the deterministic generator performs.
   */
  async writeEntry(key: string): Promise<void> {
    this.store.write(this.pathFor(key), `session:${key}`);
  }

  /** Write raw content at an arbitrary path (session save/load markers). */
  async writeRaw(path: string, content: string): Promise<void> {
    this.store.write(path, content);
  }

  /** Read raw content at an arbitrary path, or null. */
  async readRaw(path: string): Promise<string | null> {
    return this.store.read(path);
  }

  /**
   * Touch the entry's mtime so LRU eviction treats it as recently used. Called
   * after a successful load. Matches Touch.
   */
  touch(key: string): void {
    const path = this.pathFor(key);
    if (this.store.exists(path)) {
      if (this.store instanceof InMemoryPrefixCacheStore) this.store.touchNow(path);
      else this.store.setMtime(path, Date.now());
    }
  }

  /**
   * Evict oldest entries until the directory is under 500 MB. Called after
   * every successful save. Ported from EvictIfNeededAsync — oldest (smallest
   * mtime) first.
   */
  async evictIfNeeded(): Promise<void> {
    const files = this.store
      .list(this.root, ".session")
      .sort((a, b) => a.mtimeMs - b.mtimeMs);

    let total = files.reduce((sum, f) => sum + f.sizeBytes, 0);
    let i = 0;
    while (total > CAP_BYTES && i < files.length) {
      const f = files[i++]!;
      total -= f.sizeBytes;
      this.store.delete(f.path);
    }
  }

  private static defaultRoot(): string {
    // Windows: %LOCALAPPDATA%/CircleAI/prefix-cache
    // Unix-like: ~/.circleai/prefix-cache
    const local = process.env.LOCALAPPDATA;
    if (local && local.trim().length > 0) {
      return joinPath(local, joinPath("CircleAI", "prefix-cache"));
    }
    const home = process.env.HOME ?? process.env.USERPROFILE ?? ".";
    return joinPath(home, joinPath(".circleai", "prefix-cache"));
  }
}

// ── helpers ──────────────────────────────────────────────────────────────────

function sha256Hex(input: string): string {
  return createHash("sha256").update(input, "utf-8").digest("hex");
}

function joinPath(a: string, b: string): string {
  const left = a.endsWith("/") || a.endsWith("\\") ? a.slice(0, -1) : a;
  return `${left}/${b}`;
}
