// memory/multimodal.ts
// Compressed semantic memory for media artefacts (image / audio / video /
// document). Ported from CircleAI.Memory.Multimodal (C#):
//   • MediaModality, MultimodalMemoryEntry
//   • IMultimodalCaptioner + CaptionResult + HeuristicMultimodalCaptioner
//   • IMultimodalMemoryStore + InMemoryMultimodalMemoryStore
//   • MultimodalMemoryIngester (+ IngestionResult)
//
// The whole point: we DO NOT store the pixels / audio samples / video frames —
// we store the caption, the embedding, and a SHA-256 of the original so the
// host can reference it back if it kept the file elsewhere. Raw bytes never
// leave the captioner; the store only ever holds the semantic record.

import { createHash } from "node:crypto";

// ─────────────────────────────────────────────────────────────────────────────
// MediaModality — CircleAI.Memory.Multimodal.MediaModality
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Modality of a multimodal memory entry. Drives how the ingester routes the raw
 * bytes to the captioner and which side-channel metadata is captured.
 */
export enum MediaModality {
  /** Still image — JPEG, PNG, HEIC, WebP, AVIF. */
  Image = "Image",
  /** Audio clip — Opus, WAV, MP3, M4A. */
  Audio = "Audio",
  /** Video — MP4, MOV, WebM. Captioned via key-frame extraction by the host. */
  Video = "Video",
  /** Text document — PDF, DOCX, plain text snippet larger than a single message. */
  TextDocument = "TextDocument",
}

// ─────────────────────────────────────────────────────────────────────────────
// MultimodalMemoryEntry — CircleAI.Memory.Multimodal.MultimodalMemoryEntry
// ─────────────────────────────────────────────────────────────────────────────

/**
 * One semantically-compressed media memory. The caption + embedding capture the
 * meaning; raw bytes are never retained by the memory layer.
 *
 * `referenceCount` is mutable (incremented on dedup hits); everything else is
 * effectively write-once, matching the C# `init`/`set` split.
 */
export interface MultimodalMemoryEntry {
  /** Stable identifier (UUID v4). */
  readonly id: string;
  /** UTC timestamp the memory was recorded. */
  readonly recordedAtUtc: Date;
  /** Which kind of media this came from. */
  readonly modality: MediaModality;
  /** Caption — the semantic content. */
  readonly caption: string;
  /** Embedding of the caption (and, for richer captioners, the joint embedding). */
  readonly embedding?: number[];
  /**
   * SHA-256 of the original bytes, hex-lower. Lets the host dedupe, reference a
   * kept file, and verify a re-uploaded file matches what was remembered.
   */
  readonly sourceSha256: string;
  /** Original MIME type (e.g. image/jpeg). Captured for diagnostics. */
  readonly sourceMimeType?: string;
  /** Size in bytes of the original artefact. */
  readonly sourceByteCount: number;
  /** Optional URI of the original artefact if the host retained it elsewhere. */
  readonly sourceUri?: string;
  /** Image / video width in pixels, when applicable. */
  readonly widthPx?: number;
  /** Image / video height in pixels, when applicable. */
  readonly heightPx?: number;
  /** Audio / video duration in milliseconds, when applicable. */
  readonly durationMs?: number;
  /**
   * How many times this artefact has been re-presented to the ingester.
   * Incremented on every dedup hit instead of creating a new entry. Mutable.
   */
  referenceCount: number;
  /** Optional tags (e.g. location, person, topic). */
  readonly tags?: Record<string, string>;
}

/**
 * Builds a {@link MultimodalMemoryEntry} filling the same defaults the C#
 * record's initialisers do: fresh UUID id, `recordedAtUtc = now`,
 * `referenceCount = 1`. Callers override any field via `fields`.
 */
export function makeMultimodalMemoryEntry(
  fields: Partial<MultimodalMemoryEntry> & { sourceSha256?: string },
): MultimodalMemoryEntry {
  return {
    id: fields.id ?? crypto.randomUUID(),
    recordedAtUtc: fields.recordedAtUtc ?? new Date(),
    modality: fields.modality ?? MediaModality.Image,
    caption: fields.caption ?? "",
    embedding: fields.embedding,
    sourceSha256: fields.sourceSha256 ?? "",
    sourceMimeType: fields.sourceMimeType,
    sourceByteCount: fields.sourceByteCount ?? 0,
    sourceUri: fields.sourceUri,
    widthPx: fields.widthPx,
    heightPx: fields.heightPx,
    durationMs: fields.durationMs,
    referenceCount: fields.referenceCount ?? 1,
    tags: fields.tags,
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// IMultimodalCaptioner + CaptionResult + HeuristicMultimodalCaptioner
// ─────────────────────────────────────────────────────────────────────────────

/** Output of a single captioning call. */
export interface CaptionResult {
  /** Human-readable semantic description of the artefact. Must not be empty. */
  readonly caption: string;
  /** Embedding of the artefact. Null when the captioner has no embedding backend. */
  readonly embedding?: number[];
  /** Image / video width when known. */
  readonly widthPx?: number;
  /** Image / video height when known. */
  readonly heightPx?: number;
  /** Audio / video duration when known. */
  readonly durationMs?: number;
}

/** Converts raw media bytes into a semantic representation. */
export interface IMultimodalCaptioner {
  /**
   * True when this captioner can handle the given modality + mime. The ingester
   * picks among multiple captioners using this predicate.
   */
  canCaption(modality: MediaModality, mimeType: string | null | undefined): boolean;

  /**
   * Produces a {@link CaptionResult} for the given source bytes. Implementations
   * must not retain the bytes after the call returns.
   */
  captionAsync(
    modality: MediaModality,
    sourceBytes: Uint8Array,
    mimeType: string | null | undefined,
  ): Promise<CaptionResult>;
}

/**
 * Default {@link IMultimodalCaptioner}. Returns a descriptive shell caption —
 * never fabricates semantic content. Always available, zero model dependency,
 * zero token cost.
 */
export class HeuristicMultimodalCaptioner implements IMultimodalCaptioner {
  canCaption(_modality: MediaModality, _mimeType: string | null | undefined): boolean {
    return true;
  }

  async captionAsync(
    modality: MediaModality,
    sourceBytes: Uint8Array,
    mimeType: string | null | undefined,
  ): Promise<CaptionResult> {
    const detected = detectMime(sourceBytes, mimeType);
    const len = sourceBytes.length;
    let caption: string;
    switch (modality) {
      case MediaModality.Image:
        caption = `[Image — no captioner wired. ${detected}, ${len} bytes.]`;
        break;
      case MediaModality.Audio:
        caption = `[Audio — no captioner wired. ${detected}, ${len} bytes.]`;
        break;
      case MediaModality.Video:
        caption = `[Video — no captioner wired. ${detected}, ${len} bytes.]`;
        break;
      case MediaModality.TextDocument:
        caption = `[Document — no captioner wired. ${detected}, ${len} bytes.]`;
        break;
      default:
        caption = `[Media — no captioner wired. ${detected}, ${len} bytes.]`;
        break;
    }
    return { caption, embedding: undefined };
  }
}

function detectMime(bytes: Uint8Array, declared: string | null | undefined): string {
  if (declared != null && declared.trim().length > 0) return declared;
  if (bytes.length >= 4) {
    if (bytes[0] === 0xff && bytes[1] === 0xd8) return "image/jpeg";
    if (bytes[0] === 0x89 && bytes[1] === 0x50 && bytes[2] === 0x4e && bytes[3] === 0x47)
      return "image/png";
    if (bytes[0] === 0x47 && bytes[1] === 0x49 && bytes[2] === 0x46) return "image/gif";
    if (bytes[0] === 0x52 && bytes[1] === 0x49 && bytes[2] === 0x46 && bytes[3] === 0x46)
      return "audio/wav";
    if (bytes[0] === 0x25 && bytes[1] === 0x50 && bytes[2] === 0x44 && bytes[3] === 0x46)
      return "application/pdf";
  }
  return "application/octet-stream";
}

// ─────────────────────────────────────────────────────────────────────────────
// IMultimodalMemoryStore + InMemoryMultimodalMemoryStore
// ─────────────────────────────────────────────────────────────────────────────

/** Persistent store of compressed multimodal memories. */
export interface IMultimodalMemoryStore {
  /** Adds an entry. Duplicate SHA-256 hits should be handled via getByHashAsync. */
  addAsync(entry: MultimodalMemoryEntry): Promise<void>;
  /** Returns the entry with the given hash, or null if unknown. */
  getByHashAsync(sourceSha256: string): Promise<MultimodalMemoryEntry | null>;
  /** Increments referenceCount for the entry whose hash matches. No-op when unknown. */
  reinforceAsync(sourceSha256: string): Promise<void>;
  /**
   * Returns the top-topK entries whose embedding is most similar (cosine) to
   * queryEmbedding. When the query is null, falls back to most-recent.
   */
  searchAsync(
    queryEmbedding: number[] | null,
    topK?: number,
  ): Promise<readonly MultimodalMemoryEntry[]>;
  /** Returns the most recent count entries. */
  getRecentAsync(count?: number): Promise<readonly MultimodalMemoryEntry[]>;
  /** Removes entries older than cutoff. Returns count removed. */
  pruneOlderThanAsync(cutoff: Date): Promise<number>;
  /** Total entries currently stored. */
  countAsync(): Promise<number>;
}

/** In-memory {@link IMultimodalMemoryStore}. Keyed by SHA-256 (case-insensitive). */
export class InMemoryMultimodalMemoryStore implements IMultimodalMemoryStore {
  // C# uses a ConcurrentDictionary with OrdinalIgnoreCase; we lower-case the key
  // to reproduce case-insensitive hash lookups.
  private readonly byHash = new Map<string, MultimodalMemoryEntry>();

  async addAsync(entry: MultimodalMemoryEntry): Promise<void> {
    if (!entry) throw new Error("entry required");
    if (entry.sourceSha256 == null || entry.sourceSha256.trim().length === 0)
      throw new Error("SourceSha256 is required.");
    this.byHash.set(keyOf(entry.sourceSha256), entry);
  }

  async getByHashAsync(sourceSha256: string): Promise<MultimodalMemoryEntry | null> {
    return this.byHash.get(keyOf(sourceSha256)) ?? null;
  }

  async reinforceAsync(sourceSha256: string): Promise<void> {
    const e = this.byHash.get(keyOf(sourceSha256));
    if (e) e.referenceCount++;
  }

  async searchAsync(
    queryEmbedding: number[] | null,
    topK = 5,
  ): Promise<readonly MultimodalMemoryEntry[]> {
    if (queryEmbedding == null) {
      return [...this.byHash.values()]
        .sort((a, b) => b.recordedAtUtc.getTime() - a.recordedAtUtc.getTime())
        .slice(0, topK);
    }

    return [...this.byHash.values()]
      .filter((e) => e.embedding != null && e.embedding.length > 0)
      .map((e) => ({ e, score: cosineScore(queryEmbedding, e.embedding!) }))
      .sort((x, y) => y.score - x.score)
      .slice(0, topK)
      .map((t) => t.e);
  }

  async getRecentAsync(count = 10): Promise<readonly MultimodalMemoryEntry[]> {
    return [...this.byHash.values()]
      .sort((a, b) => b.recordedAtUtc.getTime() - a.recordedAtUtc.getTime())
      .slice(0, count);
  }

  async pruneOlderThanAsync(cutoff: Date): Promise<number> {
    const cutoffMs = cutoff.getTime();
    const doomed: string[] = [];
    for (const e of this.byHash.values())
      if (e.recordedAtUtc.getTime() < cutoffMs) doomed.push(keyOf(e.sourceSha256));
    for (const h of doomed) this.byHash.delete(h);
    return doomed.length;
  }

  async countAsync(): Promise<number> {
    return this.byHash.size;
  }
}

function keyOf(sha: string): string {
  return sha.toLowerCase();
}

/** Cosine similarity — matches the C# store's internal CosineSimilarity.Score. */
function cosineScore(a: number[], b: number[]): number {
  if (a.length !== b.length) return 0;
  let dot = 0;
  let magA = 0;
  let magB = 0;
  for (let i = 0; i < a.length; i++) {
    dot += a[i] * b[i];
    magA += a[i] * a[i];
    magB += b[i] * b[i];
  }
  const denom = Math.sqrt(magA) * Math.sqrt(magB);
  return denom < Number.EPSILON ? 0 : dot / denom;
}

// ─────────────────────────────────────────────────────────────────────────────
// MultimodalMemoryIngester — CircleAI.Memory.Multimodal.MultimodalMemoryIngester
// ─────────────────────────────────────────────────────────────────────────────

/** Outcome of a {@link MultimodalMemoryIngester.ingestAsync} call. */
export interface IngestionResult {
  readonly entry: MultimodalMemoryEntry;
  readonly wasDeduplicated: boolean;
}

/** Optional per-call inputs for {@link MultimodalMemoryIngester.ingestAsync}. */
export interface IngestOptions {
  /** Optional MIME type for the source. */
  readonly mimeType?: string | null;
  /** Optional URI of the original (host-retained). */
  readonly sourceUri?: string | null;
  /** Optional caller-supplied tags. */
  readonly tags?: Record<string, string> | null;
}

/**
 * Ingests raw media bytes into compressed semantic memory.
 *
 *   1. Hashes the source (SHA-256, hex-lower).
 *   2. Dedupes — if the hash is known, reinforces the existing entry and returns
 *      it (no re-captioning, no duplicate storage).
 *   3. Picks a captioner via canCaption().
 *   4. Asks the captioner for a CaptionResult.
 *   5. Persists a MultimodalMemoryEntry to the store.
 *
 * Raw bytes are never persisted. The hash is the only durable handle the memory
 * layer keeps for the original artefact.
 */
export class MultimodalMemoryIngester {
  private readonly captioners: readonly IMultimodalCaptioner[];
  private readonly store: IMultimodalMemoryStore;

  /**
   * Captioners are tried in order — the first one whose canCaption() returns
   * true wins. The host typically registers richer captioners first and the
   * heuristic fallback last.
   */
  constructor(
    captioners: Iterable<IMultimodalCaptioner>,
    store: IMultimodalMemoryStore,
  ) {
    if (!captioners) throw new Error("captioners required");
    if (!store) throw new Error("store required");
    this.captioners = [...captioners];
    if (this.captioners.length === 0)
      throw new Error("At least one captioner is required.");
    this.store = store;
  }

  /**
   * Ingests an artefact. When the SHA-256 matches an existing entry the stored
   * record is reinforced rather than re-captioned, and the result's
   * `wasDeduplicated` is true.
   */
  async ingestAsync(
    modality: MediaModality,
    sourceBytes: Uint8Array,
    options: IngestOptions = {},
  ): Promise<IngestionResult> {
    if (sourceBytes == null || sourceBytes.length === 0)
      throw new Error("Source bytes are empty.");

    const { mimeType = null, sourceUri = null, tags = null } = options;

    const hash = computeSha256(sourceBytes);
    const existing = await this.store.getByHashAsync(hash);
    if (existing !== null) {
      await this.store.reinforceAsync(hash);
      return { entry: existing, wasDeduplicated: true };
    }

    const captioner = this.pickCaptioner(modality, mimeType);
    const caption = await captioner.captionAsync(modality, sourceBytes, mimeType);

    const entry = makeMultimodalMemoryEntry({
      modality,
      caption: caption.caption,
      embedding: caption.embedding,
      sourceSha256: hash,
      sourceMimeType: mimeType ?? undefined,
      sourceByteCount: sourceBytes.length,
      sourceUri: sourceUri ?? undefined,
      widthPx: caption.widthPx,
      heightPx: caption.heightPx,
      durationMs: caption.durationMs,
      tags: tags ?? undefined,
    });

    await this.store.addAsync(entry);
    return { entry, wasDeduplicated: false };
  }

  private pickCaptioner(
    modality: MediaModality,
    mime: string | null | undefined,
  ): IMultimodalCaptioner {
    for (const c of this.captioners) {
      if (c.canCaption(modality, mime)) return c;
    }
    // The last registered captioner should accept everything; if no host-supplied
    // captioner matches, the heuristic fallback wins.
    return this.captioners[this.captioners.length - 1];
  }
}

function computeSha256(bytes: Uint8Array): string {
  return createHash("sha256").update(bytes).digest("hex");
}
