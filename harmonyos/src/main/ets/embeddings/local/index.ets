/**
 * embeddings/local/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// embeddings/local/index.ts
//
// Port of CircleAI.Embeddings.Local:
//   • EmbeddingDocument / EmbeddingSearchHit   (ICircleEmbeddingStore.cs)
//   • IEmbeddingEncoder                        (ICircleEmbeddingStore.cs)
//   • ICircleEmbeddingStore                    (ICircleEmbeddingStore.cs)
//   • EmbeddingIndexHit / IEmbeddingIndex      (IEmbeddingIndex.cs)
//   • InMemoryEmbeddingStore                   (InMemoryEmbeddingStore.cs)
//
// InMemoryEmbeddingStore keeps TurboQuant-compressed vectors in memory and does
// brute-force cosine search. Its persistence format is byte-matched to the C#
// BinaryWriter/BinaryReader output (magic "CELQ", version 1, …). The TurboQuant
// codec is reused from ../../memory/compression.js so payloads are identical
// across every language in the SDK.

import { Files as fs } from "../../platform";
import { Path as path } from "../../platform";
import {
  TurboQuantCodec,
  type TurboQuantPayload,
} from "../../memory/compression";

// ─────────────────────────────────────────────────────────────────────────────
// Records
// ─────────────────────────────────────────────────────────────────────────────

/**
 * One document in the store. `id` is caller-chosen and uniquely identifies the
 * document for delete / update.
 */
export interface EmbeddingDocument {
  readonly id: string;
  readonly text: string;
  readonly metadata?: Readonly<Record<string, string>> | null;
}

/** Build an {@link EmbeddingDocument} (mirrors the positional C# record ctor). */
export function makeEmbeddingDocument(
  id: string,
  text: string,
  metadata: Readonly<Record<string, string>> | null = null,
): EmbeddingDocument {
  return { id, text, metadata };
}

/**
 * One hit from {@link ICircleEmbeddingStore.searchAsync}. Higher `score` =
 * closer. Cosine similarity: 1.0 = identical, -1.0 = opposite, 0.0 = orthogonal.
 */
export interface EmbeddingSearchHit {
  readonly document: EmbeddingDocument;
  readonly score: number;
}

/**
 * One hit from {@link IEmbeddingIndex.searchAsync}. `internalId` is the
 * insertion-order id assigned by {@link IEmbeddingIndex.addAsync}. Higher
 * `score` = closer.
 */
export interface EmbeddingIndexHit {
  readonly internalId: number;
  readonly score: number;
}

// ─────────────────────────────────────────────────────────────────────────────
// IEmbeddingEncoder — CircleAI.Embeddings.Local.IEmbeddingEncoder
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Translates text into a dense vector. Bring your own — sentence-transformers
 * via ONNX, a small MNN encoder, or a cloud API.
 */
export interface IEmbeddingEncoder {
  /**
   * Vector dimension this encoder produces. All vectors fed to the store from
   * the same encoder must agree.
   */
  readonly dimension: number;

  /** Encode one text into a dense vector. */
  encodeAsync(text: string): Promise<Float32Array>;
}

// ─────────────────────────────────────────────────────────────────────────────
// ICircleEmbeddingStore — CircleAI.Embeddings.Local.ICircleEmbeddingStore
// ─────────────────────────────────────────────────────────────────────────────

/**
 * On-device embedding store with a built-in RAG primitive. Add documents once,
 * search by text or vector. Vectors are TurboQuant-compressed so the store fits
 * ~8× more documents than raw FP32.
 */
export interface ICircleEmbeddingStore {
  /** Vector dimension this store was created with. */
  readonly dimension: number;

  /** How many documents are currently in the store. */
  readonly count: number;

  /** Add (or replace) one document — the encoder produces the vector. */
  addAsync(document: EmbeddingDocument): Promise<void>;

  /** Add a document with a caller-supplied vector (length must equal `dimension`). */
  addWithVectorAsync(document: EmbeddingDocument, vector: Float32Array): Promise<void>;

  /** Remove a document by id. Returns true if a document was removed. */
  removeAsync(id: string): Promise<boolean>;

  /** Search by text — returns the `topK` closest documents by cosine similarity. */
  searchAsync(queryText: string, topK?: number): Promise<readonly EmbeddingSearchHit[]>;

  /** Search by a pre-computed query vector (length must equal `dimension`). */
  searchByVectorAsync(
    queryVector: Float32Array,
    topK?: number,
  ): Promise<readonly EmbeddingSearchHit[]>;

  /** Persist the store to `path`. Atomic via write-tmp-then-rename. */
  saveAsync(path: string): Promise<void>;

  /** Load a previously-saved store from `path`. Replaces all in-memory state. */
  loadAsync(path: string): Promise<void>;

  /** Release resources. */
  disposeAsync(): Promise<void>;
}

// ─────────────────────────────────────────────────────────────────────────────
// IEmbeddingIndex — CircleAI.Embeddings.Local.IEmbeddingIndex
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Vector index contract. The store layers documents + metadata + persistence on
 * top; the index is the search primitive.
 */
export interface IEmbeddingIndex {
  /** Vector dimensionality. Locked at construction. */
  readonly dimension: number;

  /** How many vectors are currently in the index. */
  readonly count: number;

  /** Append one vector. Returns the internal id the index assigned. */
  addAsync(vector: Float32Array): Promise<number>;

  /** Search for the top-`topK` nearest neighbours. */
  searchAsync(queryVector: Float32Array, topK: number): Promise<EmbeddingIndexHit[]>;

  /** Persist the index to `path`. */
  saveAsync(path: string): Promise<void>;

  /** Reload from `path`, replacing the in-memory state. */
  loadAsync(path: string): Promise<void>;

  /** Release resources. */
  dispose(): void;
}

// ─────────────────────────────────────────────────────────────────────────────
// Binary read/write helpers — byte-match System.IO.BinaryWriter/BinaryReader
// ─────────────────────────────────────────────────────────────────────────────

/** Growable little-endian writer matching BinaryWriter's UTF-8 + 7-bit-length string encoding. */
class BinaryWriter {
  private buf = new Uint8Array(256);
  private len = 0;

  private ensure(extra: number): void {
    if (this.len + extra <= this.buf.length) return;
    let cap = this.buf.length;
    while (cap < this.len + extra) cap *= 2;
    const next = new Uint8Array(cap);
    next.set(this.buf.subarray(0, this.len));
    this.buf = next;
  }

  writeInt32(value: number): void {
    this.ensure(4);
    new DataView(this.buf.buffer).setInt32(this.len, value | 0, true);
    this.len += 4;
  }

  writeUInt16(value: number): void {
    this.ensure(2);
    new DataView(this.buf.buffer).setUint16(this.len, value & 0xffff, true);
    this.len += 2;
  }

  writeFloat32(value: number): void {
    this.ensure(4);
    new DataView(this.buf.buffer).setFloat32(this.len, value, true);
    this.len += 4;
  }

  /** BinaryWriter.Write(bool): a single byte, 0 or 1. */
  writeBoolean(value: boolean): void {
    this.ensure(1);
    this.buf[this.len++] = value ? 1 : 0;
  }

  writeBytes(bytes: Uint8Array): void {
    this.ensure(bytes.length);
    this.buf.set(bytes, this.len);
    this.len += bytes.length;
  }

  /** BinaryWriter.Write(string): 7-bit-encoded UTF-8 byte-length prefix, then UTF-8 bytes. */
  writeString(value: string): void {
    const utf8 = new TextEncoder().encode(value);
    this.write7BitEncodedInt(utf8.length);
    this.writeBytes(utf8);
  }

  private write7BitEncodedInt(value: number): void {
    let v = value >>> 0;
    while (v >= 0x80) {
      this.ensure(1);
      this.buf[this.len++] = (v & 0x7f) | 0x80;
      v >>>= 7;
    }
    this.ensure(1);
    this.buf[this.len++] = v;
  }

  toUint8Array(): Uint8Array {
    return this.buf.subarray(0, this.len);
  }
}

/** Little-endian reader matching BinaryReader. */
class BinaryReader {
  private pos = 0;
  private readonly view: DataView;

  constructor(private readonly bytes: Uint8Array) {
    this.view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  }

  readInt32(): number {
    const v = this.view.getInt32(this.pos, true);
    this.pos += 4;
    return v;
  }

  readUInt16(): number {
    const v = this.view.getUint16(this.pos, true);
    this.pos += 2;
    return v;
  }

  readFloat32(): number {
    const v = this.view.getFloat32(this.pos, true);
    this.pos += 4;
    return v;
  }

  /** BinaryReader.ReadBoolean(): one byte, non-zero = true. */
  readBoolean(): boolean {
    return this.bytes[this.pos++] !== 0;
  }

  readBytes(count: number): Uint8Array {
    const out = this.bytes.subarray(this.pos, this.pos + count);
    this.pos += count;
    return new Uint8Array(out);
  }

  readString(): string {
    const byteLen = this.read7BitEncodedInt();
    const slice = this.bytes.subarray(this.pos, this.pos + byteLen);
    this.pos += byteLen;
    return new TextDecoder().decode(slice);
  }

  private read7BitEncodedInt(): number {
    let count = 0;
    let shift = 0;
    let b: number;
    do {
      if (shift === 5 * 7) throw new Error("Bad 7-bit encoded int.");
      b = this.bytes[this.pos++];
      count |= (b & 0x7f) << shift;
      shift += 7;
    } while ((b & 0x80) !== 0);
    return count >>> 0;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryEmbeddingStore — CircleAI.Embeddings.Local.InMemoryEmbeddingStore
// ─────────────────────────────────────────────────────────────────────────────

interface StoreEntry {
  document: EmbeddingDocument;
  payload: TurboQuantPayload;
}

const FILE_MAGIC = 0x4c455143; // "CELQ" little-endian
const FILE_VERSION = 1;
const DEFAULT_BITS_PER_DIM = 4;

/** Default {@link ICircleEmbeddingStore}: brute-force cosine over TurboQuant-compressed vectors. */
export class InMemoryEmbeddingStore implements ICircleEmbeddingStore {
  private readonly encoder: IEmbeddingEncoder;
  private readonly bitsPerDim: number;
  private readonly entries = new Map<string, StoreEntry>();
  private disposed = false;

  /**
   * @param encoder produces vectors from text.
   * @param bitsPerDim TurboQuant quantisation depth (1–8). Default 4 (~8× shrink).
   */
  constructor(encoder: IEmbeddingEncoder, bitsPerDim = DEFAULT_BITS_PER_DIM) {
    if (encoder === null || encoder === undefined)
      throw new Error("encoder is required");
    if (bitsPerDim < 1 || bitsPerDim > 8)
      throw new RangeError("Valid range: 1–8.");
    this.encoder = encoder;
    this.bitsPerDim = bitsPerDim;
  }

  get dimension(): number {
    return this.encoder.dimension;
  }

  get count(): number {
    return this.entries.size;
  }

  async addAsync(document: EmbeddingDocument): Promise<void> {
    if (document === null || document === undefined)
      throw new Error("document is required");
    const vector = await this.encoder.encodeAsync(document.text);
    await this.addWithVectorAsync(document, vector);
  }

  // eslint-disable-next-line @typescript-eslint/require-await
  async addWithVectorAsync(
    document: EmbeddingDocument,
    vector: Float32Array,
  ): Promise<void> {
    if (document === null || document === undefined)
      throw new Error("document is required");
    this.throwIfDisposed();
    if (vector.length !== this.dimension)
      throw new Error(
        `Vector length ${vector.length} != store dimension ${this.dimension}.`,
      );

    const payload = TurboQuantCodec.encode(vector, this.bitsPerDim);
    this.entries.set(document.id, { document, payload });
  }

  // eslint-disable-next-line @typescript-eslint/require-await
  async removeAsync(id: string): Promise<boolean> {
    if (id === null || id === undefined || id.trim() === "")
      throw new Error("id is required");
    this.throwIfDisposed();
    return this.entries.delete(id);
  }

  async searchAsync(
    queryText: string,
    topK = 5,
  ): Promise<readonly EmbeddingSearchHit[]> {
    if (queryText === null || queryText === undefined || queryText === "")
      throw new Error("queryText is required");
    const vector = await this.encoder.encodeAsync(queryText);
    return this.searchByVectorAsync(vector, topK);
  }

  // eslint-disable-next-line @typescript-eslint/require-await
  async searchByVectorAsync(
    queryVector: Float32Array,
    topK = 5,
  ): Promise<readonly EmbeddingSearchHit[]> {
    this.throwIfDisposed();
    if (queryVector.length !== this.dimension)
      throw new Error(
        `Vector length ${queryVector.length} != store dimension ${this.dimension}.`,
      );
    if (topK <= 0) throw new RangeError("topK");

    const qNorm = normSafe(queryVector);
    const q = Float32Array.from(queryVector);
    if (qNorm > 0) for (let i = 0; i < q.length; i++) q[i] = q[i] / qNorm;

    // Brute-force cosine, decoding each entry on demand.
    const scored: Array<{ score: number; id: string }> = [];
    for (const [id, entry] of this.entries) {
      const decoded = TurboQuantCodec.decode(
        entry.payload,
        this.dimension,
        this.bitsPerDim,
      );
      const entryNorm = normSafe(decoded);
      if (entryNorm <= 0) continue;
      let dot = 0;
      for (let i = 0; i < this.dimension; i++)
        dot += q[i] * (decoded[i] / entryNorm);
      scored.push({ score: dot, id });
    }

    // Top-K ordering: score descending, id ordinal ascending for ties — the
    // ordering the C# SortedSet + OrderByDescending produces.
    scored.sort((a, b) => {
      if (a.score !== b.score) return b.score - a.score;
      return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
    });

    const ordered = scored
      .slice(0, topK)
      .map<EmbeddingSearchHit>((t) => {
        const entry = this.entries.get(t.id);
        if (entry === undefined) throw new Error("entry vanished during search");
        return { document: entry.document, score: t.score };
      });
    return ordered;
  }

  async saveAsync(filePath: string): Promise<void> {
    if (filePath === null || filePath === undefined || filePath.trim() === "")
      throw new Error("path is required");
    this.throwIfDisposed();

    const dir = path.dirname(filePath);
    if (dir) await fs.mkdir(dir, { recursive: true });

    const bw = new BinaryWriter();
    bw.writeInt32(FILE_MAGIC);
    bw.writeUInt16(FILE_VERSION);
    bw.writeUInt16(this.bitsPerDim);
    bw.writeInt32(this.dimension);
    bw.writeInt32(this.entries.size);
    for (const [id, entry] of this.entries) {
      bw.writeString(id);
      bw.writeString(entry.document.text);
      const metaCount = entry.document.metadata
        ? Object.keys(entry.document.metadata).length
        : 0;
      bw.writeInt32(metaCount);
      if (entry.document.metadata) {
        for (const [k, v] of Object.entries(entry.document.metadata)) {
          bw.writeString(k);
          bw.writeString(v);
        }
      }
      bw.writeFloat32(entry.payload.norm);
      bw.writeInt32(entry.payload.packedIndices.length);
      bw.writeBytes(entry.payload.packedIndices);
    }

    const tmp = filePath + ".tmp";
    await fs.writeFile(tmp, bw.toUint8Array());
    await fs.rm(filePath, { force: true });
    await fs.rename(tmp, filePath);
  }

  async loadAsync(filePath: string): Promise<void> {
    if (filePath === null || filePath === undefined || filePath.trim() === "")
      throw new Error("path is required");
    this.throwIfDisposed();

    let raw: Uint8Array;
    try {
      raw = new Uint8Array(await fs.readFile(filePath));
    } catch {
      throw new Error(`Embedding store file not found: ${filePath}`);
    }

    const br = new BinaryReader(raw);
    const magic = br.readInt32();
    if (magic !== FILE_MAGIC)
      throw new Error("Not a CircleAI embedding store file.");
    const version = br.readUInt16();
    if (version !== FILE_VERSION)
      throw new Error(`Unsupported file version ${version}.`);
    const fileBits = br.readUInt16();
    if (fileBits !== this.bitsPerDim)
      throw new Error(
        `Bits-per-dim mismatch: store=${this.bitsPerDim}, file=${fileBits}.`,
      );
    const fileDim = br.readInt32();
    if (fileDim !== this.dimension)
      throw new Error(
        `Dimension mismatch: store=${this.dimension}, file=${fileDim}.`,
      );

    const count = br.readInt32();
    this.entries.clear();
    for (let i = 0; i < count; i++) {
      const id = br.readString();
      const text = br.readString();
      const metaCount = br.readInt32();
      let metadata: Record<string, string> | null = null;
      if (metaCount > 0) {
        metadata = {};
        for (let m = 0; m < metaCount; m++) {
          const key = br.readString();
          metadata[key] = br.readString();
        }
      }
      const norm = br.readFloat32();
      const packedLen = br.readInt32();
      const packed = br.readBytes(packedLen);
      this.entries.set(id, {
        document: { id, text, metadata },
        payload: { norm, packedIndices: packed },
      });
    }
  }

  disposeAsync(): Promise<void> {
    if (this.disposed) return Promise.resolve();
    this.disposed = true;
    this.entries.clear();
    return Promise.resolve();
  }

  private throwIfDisposed(): void {
    if (this.disposed) throw new Error("InMemoryEmbeddingStore is disposed");
  }
}

function normSafe(v: ArrayLike<number>): number {
  let sum = 0;
  for (let i = 0; i < v.length; i++) sum += Math.fround(v[i] * v[i]);
  return Math.fround(Math.sqrt(sum));
}

// ─────────────────────────────────────────────────────────────────────────────
// TurboVecEmbeddingIndex — CircleAI.Embeddings.Local.TurboVecEmbeddingIndex
//
// The C# type wraps the vendored `turbovec` Rust crate through the
// `turbovecbridge` cdylib for SIMD-blocked quantised search. The native SIMD
// kernel has no in-process JS analogue, so — per the port's "inject native
// behind the interface" convention — the SIMD search is a pluggable seam
// (INativeVectorKernel). When no kernel is injected, a faithful, portable
// TurboQuant-quantised cosine search runs in-process: vectors are quantised to
// `bitWidth` bits/dim via the shared TurboQuantCodec (identical codec to
// InMemoryEmbeddingStore), and search decodes + scores by cosine — the same
// arithmetic the Rust crate implements, minus the SIMD blocking. Save/Load
// persist the quantised slot payloads so a reloaded index scores identically.
// ─────────────────────────────────────────────────────────────────────────────

/** Status codes mirroring turbovecbridge.h (TurboVecInterop.cs). */
export const TvbStatus = {
  Ok: 0,
  ErrNullHandle: -1,
  ErrInvalidArg: -2,
  ErrPanic: -3,
  ErrConstruct: -4,
  ErrAdd: -5,
  ErrIo: -6,
  ErrInvalidUtf8: -7,
} as const;

/** Human-readable status string. Mirrors TurboVecInterop.DescribeStatus. */
export function describeTvbStatus(code: number): string {
  switch (code) {
    case TvbStatus.Ok:
      return "OK";
    case TvbStatus.ErrNullHandle:
      return "null handle";
    case TvbStatus.ErrInvalidArg:
      return "invalid argument";
    case TvbStatus.ErrPanic:
      return "native panic (caught at FFI boundary)";
    case TvbStatus.ErrConstruct:
      return "construction failed (dim or bit_width out of range)";
    case TvbStatus.ErrAdd:
      return "add failed";
    case TvbStatus.ErrIo:
      return "I/O error";
    case TvbStatus.ErrInvalidUtf8:
      return "invalid UTF-8 in path";
    default:
      return `unknown status ${code}`;
  }
}

/**
 * Injection seam for the native turbovec SIMD kernel. A host that has the
 * `turbovecbridge` cdylib (e.g. via a N-API addon or WASM build) supplies an
 * implementation; the store then routes search through SIMD-blocked native
 * code. When absent, {@link TurboVecEmbeddingIndex} uses its portable in-process
 * cosine search. This is the TS analogue of the P/Invoke surface in
 * TurboVecInterop.cs.
 */
export interface INativeVectorKernel {
  /** Native ABI version — compare against the value you compiled against. */
  readonly abiVersion: number;
  /** Construct a native index; returns an opaque handle (non-zero on success). */
  indexNew(dim: number, bitWidth: number): number;
  /** Append `count` contiguous vectors from `vectors`. Returns a TvbStatus. */
  indexAdd(handle: number, vectors: Float32Array, count: number): number;
  /**
   * Search: writes up to `k` ids into `outIndices` (−1 in unused slots) and
   * their scores into `outScores`. Returns a TvbStatus.
   */
  indexSearch(
    handle: number,
    query: Float32Array,
    k: number,
    outIndices: Float64Array,
    outScores: Float32Array,
  ): number;
  indexLen(handle: number): number;
  indexDim(handle: number): number;
  indexBitWidth(handle: number): number;
  indexSave(handle: number, path: string): number;
  /** Load a native index from disk; returns an opaque handle (0 on failure). */
  indexLoad(path: string): number;
  indexFree(handle: number): void;
}

const TVEC_MAGIC = 0x43455654; // "TVEC" little-endian
const TVEC_VERSION = 1;

/**
 * Default {@link IEmbeddingIndex}. Faithful port of TurboVecEmbeddingIndex.cs.
 * SIMD-blocked native search is used when an {@link INativeVectorKernel} is
 * injected; otherwise a portable TurboQuant-quantised cosine search runs
 * in-process. Mutation is serialised (SemaphoreSlim(1,1) analogue); search is
 * concurrent-safe against a stable snapshot.
 */
export class TurboVecEmbeddingIndex implements IEmbeddingIndex {
  private readonly bitWidth: number;
  private readonly kernel: INativeVectorKernel | null;

  // Native path state.
  private handle = 0;

  // Portable path state: one TurboQuant payload per insertion-order slot.
  private readonly slots: TurboQuantPayload[] = [];

  private countValue = 0;
  private disposed = false;
  private writeChain: Promise<unknown> = Promise.resolve();

  readonly dimension: number;

  /**
   * @param dimension vector length; must be > 0 and a multiple of 8.
   * @param bitWidth quantisation depth, one of {2, 3, 4}. Default 4.
   * @param kernel optional native SIMD kernel; portable in-process search when null.
   */
  constructor(dimension: number, bitWidth = 4, kernel: INativeVectorKernel | null = null) {
    if (dimension <= 0)
      throw new RangeError("Dimension must be positive.");
    if (dimension % 8 !== 0)
      throw new Error("Dimension must be a multiple of 8.");
    if (bitWidth < 2 || bitWidth > 4)
      throw new RangeError("BitWidth must be 2, 3, or 4.");

    this.dimension = dimension;
    this.bitWidth = bitWidth;
    this.kernel = kernel;

    if (kernel !== null) {
      this.handle = kernel.indexNew(dimension, bitWidth);
      if (this.handle === 0)
        throw new Error(
          "turbovecbridge: tvb_index_new returned NULL. Native library load failed or arguments rejected.",
        );
    }
  }

  /** Bit-width used to quantise vectors. One of {2, 3, 4}. */
  get bitWidthValue(): number {
    return this.bitWidth;
  }

  get count(): number {
    this.throwIfDisposed();
    return this.countValue;
  }

  addAsync(vector: Float32Array): Promise<number> {
    this.throwIfDisposed();
    if (vector.length !== this.dimension)
      throw new Error(
        `Vector length ${vector.length} != index dimension ${this.dimension}.`,
      );
    // Serialise mutation through the write chain (the SemaphoreSlim(1,1) analogue).
    const run = this.writeChain.then(() => this.addLocked(vector));
    this.writeChain = run.catch(() => undefined);
    return run;
  }

  private addLocked(vector: Float32Array): number {
    if (this.kernel !== null) {
      const status = this.kernel.indexAdd(this.handle, vector, 1);
      if (status !== TvbStatus.Ok)
        throw new Error("turbovecbridge.add failed: " + describeTvbStatus(status));
    } else {
      this.slots.push(TurboQuantCodec.encode(vector, this.bitWidth));
    }
    const id = this.countValue;
    this.countValue += 1;
    return id;
  }

  // eslint-disable-next-line @typescript-eslint/require-await
  async searchAsync(queryVector: Float32Array, topK: number): Promise<EmbeddingIndexHit[]> {
    this.throwIfDisposed();
    if (queryVector.length !== this.dimension)
      throw new Error(
        `Query length ${queryVector.length} != index dimension ${this.dimension}.`,
      );
    if (topK <= 0) throw new RangeError("topK");
    if (this.countValue === 0) return [];

    if (this.kernel !== null) {
      const indices = new Float64Array(topK);
      const scores = new Float32Array(topK);
      const status = this.kernel.indexSearch(this.handle, queryVector, topK, indices, scores);
      if (status !== TvbStatus.Ok)
        throw new Error("turbovecbridge.search failed: " + describeTvbStatus(status));
      const hits: EmbeddingIndexHit[] = [];
      for (let i = 0; i < topK; i++) {
        if (indices[i] >= 0) hits.push({ internalId: indices[i], score: scores[i] });
      }
      return hits;
    }

    // Portable cosine over quantised slots — same scoring the crate performs.
    const qNorm = normSafe(queryVector);
    const q = Float32Array.from(queryVector);
    if (qNorm > 0) for (let i = 0; i < q.length; i++) q[i] = Math.fround(q[i] / qNorm);

    const scored: Array<{ score: number; id: number }> = [];
    for (let id = 0; id < this.slots.length; id++) {
      const decoded = TurboQuantCodec.decode(this.slots[id], this.dimension, this.bitWidth);
      const dNorm = normSafe(decoded);
      if (dNorm <= 0) continue;
      let dot = 0;
      for (let i = 0; i < this.dimension; i++)
        dot = Math.fround(dot + Math.fround(q[i] * Math.fround(decoded[i] / dNorm)));
      scored.push({ score: dot, id });
    }
    // Score descending, id ascending on ties (matches native id-ordering).
    scored.sort((a, b) => (a.score !== b.score ? b.score - a.score : a.id - b.id));
    return scored.slice(0, topK).map<EmbeddingIndexHit>((s) => ({ internalId: s.id, score: s.score }));
  }

  async saveAsync(filePath: string): Promise<void> {
    if (filePath == null || filePath.trim() === "") throw new Error("path is required");
    this.throwIfDisposed();
    await this.enqueue(async () => {
      const dir = path.dirname(filePath);
      if (dir) await fs.mkdir(dir, { recursive: true });

      if (this.kernel !== null) {
        const status = this.kernel.indexSave(this.handle, filePath);
        if (status !== TvbStatus.Ok)
          throw new Error("turbovecbridge.save failed: " + describeTvbStatus(status));
        return;
      }

      const bw = new BinaryWriter();
      bw.writeInt32(TVEC_MAGIC);
      bw.writeUInt16(TVEC_VERSION);
      bw.writeUInt16(this.bitWidth);
      bw.writeInt32(this.dimension);
      bw.writeInt32(this.slots.length);
      for (const slot of this.slots) {
        bw.writeFloat32(slot.norm);
        bw.writeInt32(slot.packedIndices.length);
        bw.writeBytes(slot.packedIndices);
      }
      const tmp = filePath + ".tmp";
      await fs.writeFile(tmp, bw.toUint8Array());
      await fs.rm(filePath, { force: true });
      await fs.rename(tmp, filePath);
    });
  }

  async loadAsync(filePath: string): Promise<void> {
    if (filePath == null || filePath.trim() === "") throw new Error("path is required");
    this.throwIfDisposed();
    await this.enqueue(async () => {
      if (this.kernel !== null) {
        const newHandle = this.kernel.indexLoad(filePath);
        if (newHandle === 0)
          throw new Error(
            "turbovecbridge.load returned NULL. File may be corrupt, version-mismatched, or unreadable.",
          );
        const loadedDim = this.kernel.indexDim(newHandle);
        if (loadedDim !== this.dimension) {
          this.kernel.indexFree(newHandle);
          throw new Error(`Loaded index dim ${loadedDim} != configured dim ${this.dimension}.`);
        }
        const old = this.handle;
        this.handle = newHandle;
        this.countValue = this.kernel.indexLen(newHandle);
        if (old !== 0) this.kernel.indexFree(old);
        return;
      }

      let raw: Uint8Array;
      try {
        raw = new Uint8Array(await fs.readFile(filePath));
      } catch {
        throw new Error(`Index file not found: ${filePath}`);
      }
      const br = new BinaryReader(raw);
      if (br.readInt32() !== TVEC_MAGIC) throw new Error("Not a TurboVec index file.");
      const version = br.readUInt16();
      if (version !== TVEC_VERSION) throw new Error(`Unsupported index version ${version}.`);
      const fileBits = br.readUInt16();
      if (fileBits !== this.bitWidth)
        throw new Error(`Bits-per-dim mismatch: index=${this.bitWidth}, file=${fileBits}.`);
      const fileDim = br.readInt32();
      if (fileDim !== this.dimension)
        throw new Error(`Loaded index dim ${fileDim} != configured dim ${this.dimension}.`);
      const count = br.readInt32();
      this.slots.length = 0;
      for (let i = 0; i < count; i++) {
        const norm = br.readFloat32();
        const packedLen = br.readInt32();
        const packed = br.readBytes(packedLen);
        this.slots.push({ norm, packedIndices: packed });
      }
      this.countValue = count;
    });
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    if (this.kernel !== null && this.handle !== 0) {
      try {
        this.kernel.indexFree(this.handle);
      } catch {
        /* swallow */
      }
      this.handle = 0;
    }
    this.slots.length = 0;
  }

  /** ABI version reported by the injected native kernel, or 0 when portable. */
  nativeAbiVersion(): number {
    return this.kernel?.abiVersion ?? 0;
  }

  private enqueue<T>(work: () => Promise<T>): Promise<T> {
    const run = this.writeChain.then(work);
    this.writeChain = run.catch(() => undefined);
    return run;
  }

  private throwIfDisposed(): void {
    if (this.disposed) throw new Error("TurboVecEmbeddingIndex is disposed");
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// HnswEmbeddingStore — CircleAI.Embeddings.Local.HnswEmbeddingStore
//
// ICircleEmbeddingStore backed by a TurboVecEmbeddingIndex. Same public contract
// as InMemoryEmbeddingStore; the search path is O(N / SIMD-block) via the index
// seam instead of brute force. On disk: the index writes `<path>` itself and a
// sidecar `<path>.docs` holds the ordinal-keyed id→document map (magic "HGCS",
// version 1), byte-matched to the C# BinaryWriter output. v1 is add-only;
// RemoveAsync tombstones the id so subsequent searches skip the slot.
// ─────────────────────────────────────────────────────────────────────────────

const DOCS_MAGIC = 0x53434847; // "HGCS" — Hnsw Generic Circle Store
const DOCS_VERSION = 1;
const HNSW_DEFAULT_BIT_WIDTH = 4;

/**
 * (RT-09b) Embedding store backed by a {@link TurboVecEmbeddingIndex}. Vectors
 * are quantised to `bitWidth` bits/dim (default 4); search is SIMD-blocked when
 * a native kernel is injected into the index, portable otherwise. Faithful port
 * of HnswEmbeddingStore.cs.
 */
export class HnswEmbeddingStore implements ICircleEmbeddingStore {
  private readonly encoder: IEmbeddingEncoder;
  private readonly index: TurboVecEmbeddingIndex;

  /** Ordinal internal-id → document. Index aligns with turbovec slot ids. */
  private readonly byId: EmbeddingDocument[] = [];
  /** External-document-id → internal-id, for O(1) remove. Absent = tombstoned. */
  private readonly idLookup = new Map<string, number>();

  private gate: Promise<unknown> = Promise.resolve();
  private disposed = false;

  /**
   * @param encoder produces vectors from text; its dimension must be > 0 and a
   *   multiple of 8 (turbovec SIMD alignment).
   * @param bitWidth quantisation depth, one of {2, 3, 4}. Default 4.
   * @param kernel optional native SIMD kernel forwarded to the index.
   */
  constructor(
    encoder: IEmbeddingEncoder,
    bitWidth = HNSW_DEFAULT_BIT_WIDTH,
    kernel: INativeVectorKernel | null = null,
  ) {
    if (encoder == null) throw new Error("encoder is required");
    if (encoder.dimension <= 0 || encoder.dimension % 8 !== 0)
      throw new Error(
        `Encoder dimension ${encoder.dimension} must be > 0 and a multiple of 8 for turbovec.`,
      );
    this.encoder = encoder;
    this.index = new TurboVecEmbeddingIndex(encoder.dimension, bitWidth, kernel);
  }

  get dimension(): number {
    return this.encoder.dimension;
  }

  get count(): number {
    return this.byId.length;
  }

  async addAsync(document: EmbeddingDocument): Promise<void> {
    if (document == null) throw new Error("document is required");
    const vector = await this.encoder.encodeAsync(document.text);
    await this.addWithVectorAsync(document, vector);
  }

  async addWithVectorAsync(document: EmbeddingDocument, vector: Float32Array): Promise<void> {
    if (document == null) throw new Error("document is required");
    this.throwIfDisposed();
    if (vector.length !== this.dimension)
      throw new Error(`Vector length ${vector.length} != store dimension ${this.dimension}.`);

    await this.enqueue(async () => {
      // Replace-by-id is not supported by turbovec yet; v1 contract is add-only.
      if (this.idLookup.has(document.id))
        throw new Error(`Document id '${document.id}' already exists. Call removeAsync first.`);
      const internalId = await this.index.addAsync(vector);
      this.byId.push(document);
      this.idLookup.set(document.id, internalId);
    });
  }

  // eslint-disable-next-line @typescript-eslint/require-await
  async removeAsync(id: string): Promise<boolean> {
    if (id == null || id.trim() === "") throw new Error("id is required");
    this.throwIfDisposed();
    // Tombstone in the lookup; compaction is a follow-up (parity with C#).
    return this.idLookup.delete(id);
  }

  async searchAsync(queryText: string, topK = 5): Promise<readonly EmbeddingSearchHit[]> {
    if (queryText == null || queryText === "") throw new Error("queryText is required");
    const vector = await this.encoder.encodeAsync(queryText);
    return this.searchByVectorAsync(vector, topK);
  }

  async searchByVectorAsync(
    queryVector: Float32Array,
    topK = 5,
  ): Promise<readonly EmbeddingSearchHit[]> {
    this.throwIfDisposed();
    if (queryVector.length !== this.dimension)
      throw new Error(`Query length ${queryVector.length} != store dimension ${this.dimension}.`);
    if (topK <= 0) throw new RangeError("topK");

    // Over-fetch to compensate for removed slots; cap to current count.
    const overFetch = Math.min(this.index.count, Math.max(topK * 2, topK + 10));
    if (overFetch === 0) return [];

    const rawHits = await this.index.searchAsync(queryVector, overFetch);
    if (rawHits.length === 0) return [];

    const results: EmbeddingSearchHit[] = [];
    for (const hit of rawHits) {
      if (hit.internalId < 0 || hit.internalId >= this.byId.length) continue;
      const doc = this.byId[hit.internalId];
      if (!this.idLookup.has(doc.id)) continue; // removed
      results.push({ document: doc, score: hit.score });
      if (results.length === topK) break;
    }
    return results;
  }

  async saveAsync(filePath: string): Promise<void> {
    if (filePath == null || filePath.trim() === "") throw new Error("path is required");
    this.throwIfDisposed();
    await this.enqueue(async () => {
      const dir = path.dirname(filePath);
      if (dir) await fs.mkdir(dir, { recursive: true });

      // Persist turbovec slot data through the index.
      await this.index.saveAsync(filePath);

      // Persist the doc sidecar.
      const docsPath = filePath + ".docs";
      const tmp = docsPath + ".tmp";
      const bw = new BinaryWriter();
      bw.writeInt32(DOCS_MAGIC);
      bw.writeUInt16(DOCS_VERSION);
      bw.writeInt32(this.dimension);
      bw.writeInt32(this.byId.length);
      for (const doc of this.byId) {
        bw.writeString(doc.id);
        bw.writeString(doc.text);
        bw.writeBoolean(this.idLookup.has(doc.id)); // live flag
        const metaCount = doc.metadata ? Object.keys(doc.metadata).length : 0;
        bw.writeInt32(metaCount);
        if (doc.metadata) {
          for (const [k, v] of Object.entries(doc.metadata)) {
            bw.writeString(k);
            bw.writeString(v);
          }
        }
      }
      await fs.writeFile(tmp, bw.toUint8Array());
      await fs.rm(docsPath, { force: true });
      await fs.rename(tmp, docsPath);
    });
  }

  async loadAsync(filePath: string): Promise<void> {
    if (filePath == null || filePath.trim() === "") throw new Error("path is required");
    this.throwIfDisposed();
    const docsPath = filePath + ".docs";

    await this.enqueue(async () => {
      await this.index.loadAsync(filePath);

      let raw: Uint8Array;
      try {
        raw = new Uint8Array(await fs.readFile(docsPath));
      } catch {
        throw new Error(`Docs sidecar not found: ${docsPath}`);
      }
      const br = new BinaryReader(raw);
      if (br.readInt32() !== DOCS_MAGIC)
        throw new Error("Not an HnswEmbeddingStore docs sidecar.");
      const version = br.readUInt16();
      if (version !== DOCS_VERSION) throw new Error(`Unsupported docs version ${version}.`);
      const fileDim = br.readInt32();
      if (fileDim !== this.dimension)
        throw new Error(`Dimension mismatch: store=${this.dimension}, file=${fileDim}.`);
      const count = br.readInt32();

      this.byId.length = 0;
      this.idLookup.clear();
      for (let i = 0; i < count; i++) {
        const id = br.readString();
        const text = br.readString();
        const live = br.readBoolean();
        const metaCount = br.readInt32();
        let metadata: Record<string, string> | null = null;
        if (metaCount > 0) {
          metadata = {};
          for (let m = 0; m < metaCount; m++) metadata[br.readString()] = br.readString();
        }
        this.byId.push({ id, text, metadata });
        if (live) this.idLookup.set(id, i);
      }
    });
  }

  disposeAsync(): Promise<void> {
    if (this.disposed) return Promise.resolve();
    this.disposed = true;
    this.index.dispose();
    this.byId.length = 0;
    this.idLookup.clear();
    return Promise.resolve();
  }

  private enqueue<T>(work: () => Promise<T>): Promise<T> {
    const run = this.gate.then(work);
    this.gate = run.catch(() => undefined);
    return run;
  }

  private throwIfDisposed(): void {
    if (this.disposed) throw new Error("HnswEmbeddingStore is disposed");
  }
}
