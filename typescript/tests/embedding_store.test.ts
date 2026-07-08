// embedding_store.test.ts
//
// Exercises InMemoryEmbeddingStore + the encoder/store/index contracts. Pins the
// persisted file bytes against ground truth captured from the real C#
// InMemoryEmbeddingStore.SaveAsync — the on-disk "CELQ" format is what a store
// saved by one language loads in another, so it must be BYTE-IDENTICAL.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { promises as fs } from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import {
  InMemoryEmbeddingStore,
  makeEmbeddingDocument,
  type IEmbeddingEncoder,
} from '../src/embeddings/local/index';

function hex(b: Uint8Array): string {
  return Array.from(b).map((x) => x.toString(16).padStart(2, '0')).join('');
}

/**
 * Deterministic FNV-1a encoder — byte-identical to the C# probe encoder so the
 * saved file bytes line up. Roughly maps text into [-1, 1] per dimension.
 */
class DeterministicEncoder implements IEmbeddingEncoder {
  readonly dimension: number;
  constructor(dim: number) {
    this.dimension = dim;
  }
  encodeAsync(text: string): Promise<Float32Array> {
    const v = new Float32Array(this.dimension);
    for (let i = 0; i < this.dimension; i++) {
      let h = 2166136261 >>> 0;
      for (const c of text) {
        h = (h ^ c.charCodeAt(0)) >>> 0;
        h = Math.imul(h, 16777619) >>> 0;
      }
      h = (h ^ (i >>> 0)) >>> 0;
      h = Math.imul(h, 16777619) >>> 0;
      v[i] = Math.fround((h % 2000) / 1000.0 - 1.0);
    }
    return Promise.resolve(v);
  }
}

async function tmpFile(name: string): Promise<string> {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'celq-'));
  return path.join(dir, name);
}

describe('InMemoryEmbeddingStore', () => {
  it('saves a file byte-identical to C# InMemoryEmbeddingStore.SaveAsync', async () => {
    const store = new InMemoryEmbeddingStore(new DeterministicEncoder(16), 4);
    await store.addAsync(
      makeEmbeddingDocument('doc-1', 'hello world', { lang: 'en', src: 'probe' }),
    );
    await store.addAsync(makeEmbeddingDocument('doc-2', 'goodbye'));

    const file = await tmpFile('store.celq');
    await store.saveAsync(file);
    const bytes = new Uint8Array(await fs.readFile(file));

    // Ground truth from the C# probe.
    assert.equal(
      hex(bytes),
      '4351454c01000400100000000200000005646f632d310b68656c6c6f20776f726c6402000000' +
        '046c616e6702656e037372630570726f62651288144008000000e4d73257acb9bb5705646f' +
        '632d3207676f6f64627965000000003380134008000000b0994a65c27abaac',
    );
  });

  it('round-trips through save + load', async () => {
    const enc = new DeterministicEncoder(16);
    const store = new InMemoryEmbeddingStore(enc, 4);
    await store.addAsync(makeEmbeddingDocument('a', 'alpha', { k: 'v' }));
    await store.addAsync(makeEmbeddingDocument('b', 'beta'));
    const file = await tmpFile('rt.celq');
    await store.saveAsync(file);

    const loaded = new InMemoryEmbeddingStore(enc, 4);
    await loaded.loadAsync(file);
    assert.equal(loaded.count, 2);

    // Its own save reproduces identical bytes.
    const file2 = await tmpFile('rt2.celq');
    await loaded.saveAsync(file2);
    const b1 = new Uint8Array(await fs.readFile(file));
    const b2 = new Uint8Array(await fs.readFile(file2));
    assert.equal(hex(b2), hex(b1));
  });

  it('adds, counts, and removes documents', async () => {
    const store = new InMemoryEmbeddingStore(new DeterministicEncoder(8), 4);
    assert.equal(store.count, 0);
    assert.equal(store.dimension, 8);
    await store.addAsync(makeEmbeddingDocument('x', 'text one'));
    await store.addAsync(makeEmbeddingDocument('y', 'text two'));
    assert.equal(store.count, 2);
    assert.equal(await store.removeAsync('x'), true);
    assert.equal(await store.removeAsync('missing'), false);
    assert.equal(store.count, 1);
  });

  it('finds the closest document by cosine (text query)', async () => {
    const enc = new DeterministicEncoder(32);
    const store = new InMemoryEmbeddingStore(enc, 6);
    await store.addAsync(makeEmbeddingDocument('same', 'the quick brown fox'));
    await store.addAsync(makeEmbeddingDocument('other', 'completely different content here'));

    const hits = await store.searchAsync('the quick brown fox', 2);
    assert.equal(hits.length, 2);
    // Identical text → top hit is the matching doc with score near 1.
    assert.equal(hits[0].document.id, 'same');
    assert.ok(hits[0].score > 0.9, `expected high self-similarity, got ${hits[0].score}`);
    // Descending order.
    assert.ok(hits[0].score >= hits[1].score);
  });

  it('search-by-vector honours topK and tie ordering', async () => {
    const enc = new DeterministicEncoder(8);
    const store = new InMemoryEmbeddingStore(enc, 4);
    await store.addWithVectorAsync(
      makeEmbeddingDocument('v1', 't1'),
      Float32Array.from([1, 0, 0, 0, 0, 0, 0, 0]),
    );
    await store.addWithVectorAsync(
      makeEmbeddingDocument('v2', 't2'),
      Float32Array.from([0, 1, 0, 0, 0, 0, 0, 0]),
    );
    const hits = await store.searchByVectorAsync(
      Float32Array.from([1, 0, 0, 0, 0, 0, 0, 0]),
      1,
    );
    assert.equal(hits.length, 1);
    assert.equal(hits[0].document.id, 'v1');
  });

  it('validates vector dimension on add + search', async () => {
    const store = new InMemoryEmbeddingStore(new DeterministicEncoder(8), 4);
    await assert.rejects(
      () => store.addWithVectorAsync(makeEmbeddingDocument('bad', 't'), new Float32Array(4)),
      /Vector length 4 != store dimension 8/,
    );
    await assert.rejects(
      () => store.searchByVectorAsync(new Float32Array(3), 5),
      /Vector length 3 != store dimension 8/,
    );
  });

  it('rejects invalid bitsPerDim', () => {
    assert.throws(() => new InMemoryEmbeddingStore(new DeterministicEncoder(8), 0), /1–8/);
    assert.throws(() => new InMemoryEmbeddingStore(new DeterministicEncoder(8), 9), /1–8/);
  });

  it('load rejects a corrupt magic + bits/dim mismatch', async () => {
    const store = new InMemoryEmbeddingStore(new DeterministicEncoder(16), 4);
    await store.addAsync(makeEmbeddingDocument('a', 'x'));
    const file = await tmpFile('m.celq');
    await store.saveAsync(file);

    // Wrong bits-per-dim on the reader.
    const wrongBits = new InMemoryEmbeddingStore(new DeterministicEncoder(16), 2);
    await assert.rejects(() => wrongBits.loadAsync(file), /Bits-per-dim mismatch/);

    // Corrupt magic.
    const bad = await tmpFile('bad.celq');
    await fs.writeFile(bad, Buffer.from([0, 0, 0, 0, 1, 0]));
    await assert.rejects(() => store.loadAsync(bad), /Not a CircleAI embedding store file/);
  });

  it('throws after disposeAsync', async () => {
    const store = new InMemoryEmbeddingStore(new DeterministicEncoder(8), 4);
    await store.disposeAsync();
    await assert.rejects(
      () => store.searchByVectorAsync(new Float32Array(8), 1),
      /disposed/,
    );
  });
});
