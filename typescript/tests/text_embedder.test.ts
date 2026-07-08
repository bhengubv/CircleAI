// text_embedder.test.ts
//
// Exercises CircleAI.Embeddings.TextEmbedder with an injected model manager +
// backend factory (no native MNN library). Verifies the resolve → verify →
// build → embed handshake and l2Normalize.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  TextEmbedder,
  l2Normalize,
  type IEmbeddingBackend,
  type EmbeddingBackendFactory,
} from '../src/embeddings/index';
import type { IModelManager } from '../src/core/index';

/** Fake model manager: records the verify calls and its verdict is toggleable. */
class FakeModelManager implements IModelManager {
  resolved = 0;
  verified = 0;
  constructor(
    private readonly modelPath: string,
    private readonly verdict: boolean,
  ) {}
  getModelPathAsync(_modelId: string): Promise<string> {
    this.resolved++;
    return Promise.resolve(this.modelPath);
  }
  verifyModelAsync(_modelPath: string, _expected: Uint8Array): Promise<boolean> {
    this.verified++;
    return Promise.resolve(this.verdict);
  }
  dispose(): void {}
}

/** Backend that returns a fixed (already-normalised) vector and counts builds. */
function fakeBackend(dim: number): { factory: EmbeddingBackendFactory; builds: () => number } {
  let builds = 0;
  const factory: EmbeddingBackendFactory = (_path: string): IEmbeddingBackend => {
    builds++;
    return {
      dimension: dim,
      embed: (text: string) => {
        const v = new Float32Array(dim);
        for (let i = 0; i < dim; i++) v[i] = (text.length + i) % 7;
        l2Normalize(v);
        return v;
      },
      dispose: () => {},
    };
  };
  return { factory, builds: () => builds };
}

describe('l2Normalize', () => {
  it('normalises to unit length', () => {
    const v = Float32Array.from([3, 4]);
    l2Normalize(v);
    const norm = Math.hypot(v[0], v[1]);
    assert.ok(Math.abs(norm - 1) < 1e-6);
  });

  it('leaves a zero vector unchanged', () => {
    const v = new Float32Array([0, 0, 0]);
    l2Normalize(v);
    assert.deepEqual(Array.from(v), [0, 0, 0]);
  });
});

describe('TextEmbedder', () => {
  it('resolves + verifies the model once, then embeds', async () => {
    const mgr = new FakeModelManager('/models/emb', true);
    const be = fakeBackend(8);
    const embedder = new TextEmbedder(mgr, new Uint8Array([1, 2, 3]), be.factory);

    const a = await embedder.generateAsync('hello');
    const b = await embedder.generateAsync('world!');
    assert.equal(a.length, 8);
    assert.equal(b.length, 8);
    // Lazy init happens exactly once even across calls.
    assert.equal(mgr.resolved, 1);
    assert.equal(mgr.verified, 1);
    assert.equal(be.builds(), 1);
  });

  it('shares a single initialisation across concurrent callers', async () => {
    const mgr = new FakeModelManager('/models/emb', true);
    const be = fakeBackend(4);
    const embedder = new TextEmbedder(mgr, new Uint8Array([9]), be.factory);
    await Promise.all([
      embedder.generateAsync('a'),
      embedder.generateAsync('b'),
      embedder.generateAsync('c'),
    ]);
    assert.equal(be.builds(), 1);
    assert.equal(mgr.resolved, 1);
  });

  it('throws when checksum verification fails', async () => {
    const mgr = new FakeModelManager('/models/emb', false);
    const be = fakeBackend(4);
    const embedder = new TextEmbedder(mgr, new Uint8Array([1]), be.factory);
    await assert.rejects(
      () => embedder.generateAsync('x'),
      /checksum verification failed/,
    );
    // Failed init is not cached — a later call retries the handshake.
    assert.equal(be.builds(), 0);
  });

  it('rejects empty text', async () => {
    const mgr = new FakeModelManager('/models/emb', true);
    const embedder = new TextEmbedder(mgr, new Uint8Array([1]), fakeBackend(4).factory);
    await assert.rejects(() => embedder.generateAsync(''), /cannot be empty/);
    await assert.rejects(() => embedder.generateAsync('   '), /cannot be empty/);
  });

  it('throws after dispose', async () => {
    const mgr = new FakeModelManager('/models/emb', true);
    const embedder = new TextEmbedder(mgr, new Uint8Array([1]), fakeBackend(4).factory);
    await embedder.generateAsync('warm');
    embedder.dispose();
    await assert.rejects(() => embedder.generateAsync('x'), /disposed/);
  });

  it('validates constructor arguments', () => {
    const mgr = new FakeModelManager('/m', true);
    assert.throws(
      () => new TextEmbedder(null as unknown as IModelManager, new Uint8Array(), fakeBackend(4).factory),
      /modelManager is required/,
    );
    assert.throws(
      () => new TextEmbedder(mgr, null as unknown as Uint8Array, fakeBackend(4).factory),
      /expectedChecksum is required/,
    );
    assert.throws(
      () => new TextEmbedder(mgr, new Uint8Array(), null as unknown as EmbeddingBackendFactory),
      /backendFactory is required/,
    );
  });
});
