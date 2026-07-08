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

import { promises as fs } from "node:fs";
import * as path from "node:path";
import {
  TurboQuantCodec,
  type TurboQuantPayload,
} from "../../memory/compression.js";

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
  for (let i = 0; i < v.length; i++) sum += v[i] * v[i];
  return Math.fround(Math.sqrt(sum));
}
