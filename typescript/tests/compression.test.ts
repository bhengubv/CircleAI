// compression.test.ts
//
// Exercises the TurboQuant codec + the compressed store decorators. Mirrors the
// C# CircleAI.Tests.TurboQuantCodecTests + CompressedStoreTests, and pins the
// cross-language wire format against ground-truth captured from the C# codec
// (see PARITY_* below). The encoded payload — the thing that is persisted and
// shared across devices/languages — must be BYTE-IDENTICAL with C#.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  BitPacker,
  BetaLloydMaxCodebook,
  OrthogonalRotation,
  TurboQuantCodec,
  EmbeddingPayloadCodec,
  CompressedEpisodicMemoryStore,
  CompressedMultimodalMemoryStore,
  COMPRESSED_TAG_KEY,
} from '../src/memory/compression';
import { InMemoryEpisodicStore } from '../src/memory/stores';
import {
  InMemoryMultimodalMemoryStore,
  MediaModality,
  makeMultimodalMemoryEntry,
  type MultimodalMemoryEntry,
} from '../src/memory/multimodal';
import type { EpisodicMemoryEntry } from '../src/memory/index';

// ── Helpers (mirror the C# test helpers) ─────────────────────────────────────

/** Deterministic Mulberry32 PRNG so vectors are reproducible across runs. */
function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a |= 0;
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function randomUnit(dim: number, seed: number): number[] {
  const rng = mulberry32(seed);
  const v = new Array<number>(dim);
  let sumSq = 0;
  for (let i = 0; i < dim; i++) {
    v[i] = rng() * 2 - 1;
    sumSq += v[i] * v[i];
  }
  const inv = 1 / Math.sqrt(sumSq);
  for (let i = 0; i < dim; i++) v[i] *= inv;
  return v;
}

function cosine(a: ArrayLike<number>, b: ArrayLike<number>): number {
  let dot = 0;
  let magA = 0;
  let magB = 0;
  for (let i = 0; i < a.length; i++) {
    dot += a[i] * b[i];
    magA += a[i] * a[i];
    magB += b[i] * b[i];
  }
  const denom = Math.sqrt(magA) * Math.sqrt(magB);
  return denom < 1e-30 ? 0 : dot / denom;
}

function hex(b: Uint8Array): string {
  return Buffer.from(b).toString('hex');
}

// ══════════════════════════════════════════════════════════════════════════
// Cross-language parity — ground truth captured from the C# codec.
// If these break, the wire format has diverged from every other SDK language.
// ══════════════════════════════════════════════════════════════════════════

describe('TurboQuant — C# wire-format parity', () => {
  it('BitPacker.pack matches C# for 2/3/4-bit index arrays', () => {
    assert.equal(hex(BitPacker.pack([0, 3, 1, 2, 3, 0, 2, 1], 2)), '9c63');
    assert.equal(hex(BitPacker.pack([0, 7, 3, 5, 1, 6, 2, 4], 3)), 'f81a8b');
    assert.equal(hex(BitPacker.pack([15, 0, 8, 7, 1, 14, 9, 6], 4)), '0f78e169');
  });

  it('BetaLloydMaxCodebook centroids match C# (FP32-exact)', () => {
    const cb = BetaLloydMaxCodebook.get(2, 8);
    assert.deepEqual(Array.from(cb.centroids), [
      -0.5048246383666992, -0.15792210400104523, 0.15792210400104523, 0.5048246383666992,
    ]);
    const cb4 = BetaLloydMaxCodebook.get(4, 16);
    assert.deepEqual(Array.from(cb4.centroids), [
      -0.6039019227027893, -0.4742901921272278, -0.37855634093284607, -0.2978082597255707,
      -0.2253989577293396, -0.1580331176519394, -0.09372113645076752, -0.031065061688423157,
      0.031065061688423157, 0.09372113645076752, 0.1580331176519394, 0.2253989577293396,
      0.2978082597255707, 0.37855634093284607, 0.4742901921272278, 0.6039019227027893,
    ]);
  });

  it('encodes an 8-dim vector to the exact C# base64 payload (2-bit and 4-bit)', () => {
    const v8 = [0.1, -0.2, 0.3, -0.4, 0.5, -0.6, 0.7, -0.8];
    // These are byte-identical to what CircleAI.Memory.Compression emits.
    assert.equal(EmbeddingPayloadCodec.encodeBase64(v8, 2), 'VFEzAQIAAAAIAAAAEdK2P9B5');
    assert.equal(EmbeddingPayloadCodec.encodeBase64(v8, 4), 'VFEzAQQAAAAIAAAAEdK2PzPHpV4=');
    assert.equal(hex(EmbeddingPayloadCodec.encode(v8, 2)), '54513301020000000800000011d2b63fd079');
    assert.equal(hex(EmbeddingPayloadCodec.encode(v8, 4)), '54513301040000000800000011d2b63f33c7a55e');
  });

  it('stores the exact C# norm in the payload', () => {
    const v8 = [0.1, -0.2, 0.3, -0.4, 0.5, -0.6, 0.7, -0.8];
    assert.equal(TurboQuantCodec.encode(v8, 2).norm, 1.4282857179641724);
  });

  it('encodes a tiny 4-dim vector to the exact C# byte layout', () => {
    const v4 = [1, 2, 3, 4];
    assert.equal(hex(EmbeddingPayloadCodec.encode(v4, 2)), '5451330102000000040000006f45af409c');
    assert.equal(EmbeddingPayloadCodec.encodeBase64(v4, 2), 'VFEzAQIAAAAEAAAAb0WvQJw=');
    assert.equal(TurboQuantCodec.encode(v4, 2).norm, 5.4772257804870605);
  });

  it('rotation matrix row 0 (dim=8) matches C# (FP32-exact)', () => {
    const row0 = Array.from(OrthogonalRotation.getMatrix(8).slice(0, 8));
    assert.deepEqual(row0, [
      0.32915404438972473, -0.15729351341724396, -0.6576523184776306, 0.4990078806877136,
      -0.2985365092754364, -0.17185114324092865, 0.024059195071458817, 0.2572260797023773,
    ]);
  });
});

// ══════════════════════════════════════════════════════════════════════════
// BitPacker (mirrors TurboQuantCodecTests.BitPacker_*)
// ══════════════════════════════════════════════════════════════════════════

describe('BitPacker', () => {
  for (const bits of [1, 2, 3, 4, 8]) {
    it(`round-trips indices at ${bits} bits`, () => {
      const max = (1 << bits) - 1;
      const rng = mulberry32(123 + bits);
      const indices = new Uint16Array(256);
      for (let i = 0; i < indices.length; i++) indices[i] = Math.floor(rng() * (max + 1));

      const packed = BitPacker.pack(indices, bits);
      const unpacked = BitPacker.unpack(packed, indices.length, bits);

      assert.equal(unpacked.length, indices.length);
      for (let i = 0; i < indices.length; i++) assert.equal(unpacked[i], indices[i]);
    });
  }

  it('byte count matches spec: 1536 indices @ 2 bits = 384 bytes', () => {
    const indices = new Uint16Array(1536);
    assert.equal(BitPacker.pack(indices, 2).length, 384);
  });

  it('rejects an overflowing index (value 4 at 2 bits)', () => {
    assert.throws(() => BitPacker.pack([4], 2), /exceeds 2-bit range/);
  });

  it('rejects an out-of-range width', () => {
    assert.throws(() => BitPacker.pack([0], 0), RangeError);
    assert.throws(() => BitPacker.pack([0], 17), RangeError);
  });
});

// ══════════════════════════════════════════════════════════════════════════
// OrthogonalRotation (mirrors Rotation_*)
// ══════════════════════════════════════════════════════════════════════════

describe('OrthogonalRotation', () => {
  it('preserves L2 norm', () => {
    const dim = 64;
    const v = randomUnit(dim, 42);
    const r = new Float32Array(dim);
    OrthogonalRotation.rotate(dim, v, r);
    let sqA = 0;
    let sqR = 0;
    for (let i = 0; i < dim; i++) {
      sqA += v[i] * v[i];
      sqR += r[i] * r[i];
    }
    assert.ok(Math.abs(Math.sqrt(sqR) - Math.sqrt(sqA)) < 1e-3);
  });

  it('rotate then unrotate recovers the input', () => {
    const dim = 64;
    const v = randomUnit(dim, 7);
    const r = new Float32Array(dim);
    const v2 = new Float32Array(dim);
    OrthogonalRotation.rotate(dim, v, r);
    OrthogonalRotation.unrotate(dim, r, v2);
    for (let i = 0; i < dim; i++) assert.ok(Math.abs(v2[i] - v[i]) < 1e-3);
  });

  it('is deterministic / cached across calls (same reference)', () => {
    const a = OrthogonalRotation.getMatrix(32);
    const b = OrthogonalRotation.getMatrix(32);
    assert.equal(a, b); // cached: identical reference
  });
});

// ══════════════════════════════════════════════════════════════════════════
// BetaLloydMaxCodebook (mirrors Codebook_*)
// ══════════════════════════════════════════════════════════════════════════

describe('BetaLloydMaxCodebook', () => {
  for (const [bits, dim] of [
    [1, 16],
    [2, 64],
    [3, 128],
    [4, 256],
  ] as const) {
    it(`has correct sizes at ${bits} bits, dim ${dim}`, () => {
      const cb = BetaLloydMaxCodebook.get(bits, dim);
      const n = 1 << bits;
      assert.equal(cb.centroids.length, n);
      assert.equal(cb.boundaries.length, n - 1);
    });
  }

  it('centroids are strictly monotonic', () => {
    const cb = BetaLloydMaxCodebook.get(4, 128);
    for (let i = 1; i < cb.centroids.length; i++)
      assert.ok(cb.centroids[i] > cb.centroids[i - 1]);
  });

  it('binFor round-trips through the boundaries', () => {
    const cb = BetaLloydMaxCodebook.get(2, 64);
    for (let i = 0; i < cb.boundaries.length; i++) {
      const justBefore = cb.boundaries[i] - 1e-6;
      const justAfter = cb.boundaries[i] + 1e-6;
      assert.equal(BetaLloydMaxCodebook.binFor(justBefore, cb.boundaries), i);
      assert.equal(BetaLloydMaxCodebook.binFor(justAfter, cb.boundaries), i + 1);
    }
  });
});

// ══════════════════════════════════════════════════════════════════════════
// TurboQuantCodec end-to-end (mirrors Codec_*)
// ══════════════════════════════════════════════════════════════════════════

describe('TurboQuantCodec', () => {
  for (const [dim, bits, floor] of [
    [64, 4, 0.99],
    [128, 4, 0.99],
    [256, 3, 0.96],
    [512, 2, 0.85],
  ] as const) {
    it(`round-trip preserves geometry (dim ${dim}, ${bits}-bit → cos ≥ ${floor})`, () => {
      const v = randomUnit(dim, 42);
      const reconstructed = TurboQuantCodec.roundTrip(v, bits);
      assert.equal(reconstructed.length, dim);
      const cos = cosine(v, reconstructed);
      assert.ok(cos >= floor, `dim=${dim} bits=${bits}: cos ${cos} below floor ${floor}`);
    });
  }

  it('zero vector round-trips to zeros', () => {
    const z = new Array<number>(64).fill(0);
    const r = TurboQuantCodec.roundTrip(z, 2);
    for (const x of r) assert.equal(x, 0);
  });

  it('payload size matches spec (1536-dim @ 2 bits = 384 bytes)', () => {
    assert.equal(TurboQuantCodec.payloadByteCount(1536, 2), 384);
  });

  it('compression ratio at 1536-dim/2-bit exceeds 15×', () => {
    const ratio = TurboQuantCodec.compressionRatio(1536, 2);
    assert.ok(ratio > 15.0, `got ${ratio}`);
    assert.equal(ratio, 15.835051546391753);
  });

  it('rejects invalid bit widths', () => {
    const v = new Array<number>(32).fill(0);
    v[0] = 1;
    assert.throws(() => TurboQuantCodec.encode(v, 0), RangeError);
    assert.throws(() => TurboQuantCodec.encode(v, 9), RangeError);
  });

  it('rejects a length-1 vector', () => {
    assert.throws(() => TurboQuantCodec.encode([1], 2));
  });

  it('encode is deterministic across runs', () => {
    const v = randomUnit(128, 7);
    const a = TurboQuantCodec.encode(v, 3);
    const b = TurboQuantCodec.encode(v, 3);
    assert.equal(a.norm, b.norm);
    assert.deepEqual(Array.from(a.packedIndices), Array.from(b.packedIndices));
  });

  it('preserves inner product between correlated compressed vectors', () => {
    const dim = 128;
    const a = randomUnit(dim, 1);
    const b = randomUnit(dim, 2);
    const blended = new Array<number>(dim);
    for (let i = 0; i < dim; i++) blended[i] = 0.7 * a[i] + 0.3 * b[i];
    let bn = 0;
    for (let i = 0; i < dim; i++) bn += blended[i] * blended[i];
    const invN = 1 / Math.sqrt(bn);
    for (let i = 0; i < dim; i++) blended[i] *= invN;

    const trueCos = cosine(a, blended);
    const aHat = TurboQuantCodec.roundTrip(a, 4);
    const blendHat = TurboQuantCodec.roundTrip(blended, 4);
    const reconCos = cosine(aHat, blendHat);
    assert.ok(Math.abs(reconCos - trueCos) <= 0.05, `true=${trueCos} recon=${reconCos}`);
  });
});

// ══════════════════════════════════════════════════════════════════════════
// EmbeddingPayloadCodec (mirrors CompressedStoreTests.Codec_*)
// ══════════════════════════════════════════════════════════════════════════

describe('EmbeddingPayloadCodec', () => {
  it('round-trip preserves cosine (4-bit ≥ 0.99)', () => {
    const v = randomUnit(128, 42);
    const encoded = EmbeddingPayloadCodec.encode(v, 4);
    const decoded = EmbeddingPayloadCodec.decode(encoded);
    assert.ok(cosine(v, decoded) >= 0.99);
  });

  it('detects its own header', () => {
    const encoded = EmbeddingPayloadCodec.encode(randomUnit(64, 1), 2);
    assert.equal(EmbeddingPayloadCodec.isEncoded(encoded), true);
    assert.equal(EmbeddingPayloadCodec.isEncoded(Uint8Array.of(0, 1, 2)), false);
  });

  it('rejects a too-short payload', () => {
    assert.throws(() => EmbeddingPayloadCodec.decode(Uint8Array.of(1, 2, 3)), /too short/i);
  });

  it('rejects a payload without the magic header', () => {
    const bad = new Uint8Array(20); // right length, wrong magic
    assert.throws(() => EmbeddingPayloadCodec.decode(bad), /magic/i);
  });

  it('base64 round-trip preserves cosine (3-bit ≥ 0.96)', () => {
    const v = randomUnit(64, 7);
    const b64 = EmbeddingPayloadCodec.encodeBase64(v, 3);
    const back = EmbeddingPayloadCodec.decodeBase64(b64);
    assert.ok(cosine(v, back) >= 0.96);
  });

  it('payload at 2 bits is > 12× smaller than FP32 at 1536-dim', () => {
    const v = randomUnit(1536, 42);
    const encoded = EmbeddingPayloadCodec.encode(v, 2);
    const ratio = (v.length * 4) / encoded.length;
    assert.ok(ratio > 12.0, `got ${ratio}`);
  });
});

// ══════════════════════════════════════════════════════════════════════════
// CompressedEpisodicMemoryStore (mirrors CompressedStoreTests.EpisodicStore_*)
// ══════════════════════════════════════════════════════════════════════════

function episodic(overrides: Partial<EpisodicMemoryEntry> & { id?: string }): EpisodicMemoryEntry {
  return {
    id: overrides.id ?? crypto.randomUUID(),
    recordedAtUtc: overrides.recordedAtUtc ?? new Date('2026-01-01T00:00:00Z'),
    userText: 'u',
    assistantText: 'a',
    ...overrides,
  };
}

describe('CompressedEpisodicMemoryStore', () => {
  it('stores the embedding as a compressed tag, not a float array', async () => {
    const inner = new InMemoryEpisodicStore();
    const outer = new CompressedEpisodicMemoryStore(inner, 2);
    await outer.addAsync(episodic({ userText: 'hello', assistantText: 'hi', embedding: randomUnit(128, 1) }));

    const rawRecent = await inner.getRecentAsync(1);
    assert.equal(rawRecent.length, 1);
    assert.equal(rawRecent[0].embedding, undefined);
    assert.ok(rawRecent[0].tags);
    assert.ok(COMPRESSED_TAG_KEY in rawRecent[0].tags!);
  });

  it('getRecent rehydrates the embedding (cosine ≥ 0.99 at 4-bit)', async () => {
    const inner = new InMemoryEpisodicStore();
    const outer = new CompressedEpisodicMemoryStore(inner, 4);
    const original = randomUnit(64, 1);
    await outer.addAsync(episodic({ embedding: original }));

    const got = await outer.getRecentAsync(1);
    assert.equal(got.length, 1);
    assert.ok(got[0].embedding);
    assert.ok(cosine(original, got[0].embedding!) >= 0.99);
  });

  it('search ranks by cosine through compression', async () => {
    const inner = new InMemoryEpisodicStore();
    const outer = new CompressedEpisodicMemoryStore(inner, 4);
    const v1 = randomUnit(64, 1);
    const v2 = randomUnit(64, 2);
    await outer.addAsync(episodic({ userText: 'near', embedding: v1 }));
    await outer.addAsync(episodic({ userText: 'far', embedding: v2 }));

    const results = await outer.searchAsync(v1, 2);
    assert.equal(results.length, 2);
    assert.equal(results[0].userText, 'near');
  });

  it('search with a null query returns recency (topK respected)', async () => {
    const inner = new InMemoryEpisodicStore();
    const outer = new CompressedEpisodicMemoryStore(inner, 4);
    await outer.addAsync(episodic({ userText: 'old', recordedAtUtc: new Date('2026-01-01T00:00:00Z'), embedding: randomUnit(32, 1) }));
    await outer.addAsync(episodic({ userText: 'new', recordedAtUtc: new Date('2026-06-01T00:00:00Z'), embedding: randomUnit(32, 2) }));
    const results = await outer.searchAsync(null, 1);
    assert.equal(results.length, 1);
    assert.equal(results[0].userText, 'new');
  });

  it('an entry without an embedding passes through unchanged', async () => {
    const inner = new InMemoryEpisodicStore();
    const outer = new CompressedEpisodicMemoryStore(inner);
    await outer.addAsync(episodic({ userText: 'u', assistantText: 'a' }));
    const raw = await inner.getRecentAsync(1);
    assert.equal(raw.length, 1);
    assert.equal(raw[0].embedding, undefined);
    assert.ok(raw[0].tags == null || !(COMPRESSED_TAG_KEY in raw[0].tags));
  });

  it('rejects an invalid bit width', () => {
    assert.throws(() => new CompressedEpisodicMemoryStore(new InMemoryEpisodicStore(), 9), RangeError);
  });

  it('exposes the compressed-tag key constant', () => {
    assert.equal(CompressedEpisodicMemoryStore.CompressedTagKey, 'x-tq-embedding');
  });
});

// ══════════════════════════════════════════════════════════════════════════
// CompressedMultimodalMemoryStore (mirrors CompressedStoreTests.MultimodalStore_*)
// ══════════════════════════════════════════════════════════════════════════

function mm(overrides: Partial<MultimodalMemoryEntry> & { sourceSha256: string }): MultimodalMemoryEntry {
  return makeMultimodalMemoryEntry(overrides);
}

describe('CompressedMultimodalMemoryStore', () => {
  it('round-trips the embedding + metadata (cosine ≥ 0.99 at 4-bit)', async () => {
    const inner = new InMemoryMultimodalMemoryStore();
    const outer = new CompressedMultimodalMemoryStore(inner, 4);
    // 4-bit ≥ 0.99 is a statistical bound, not a hard guarantee for every random
    // vector; seed 42 (the codec-test standard) clears it comfortably (~0.9953).
    const emb = randomUnit(128, 42);
    await outer.addAsync(
      mm({
        sourceSha256: 'deadbeef',
        modality: MediaModality.Image,
        caption: 'a sunny beach',
        embedding: emb,
        widthPx: 1920,
        heightPx: 1080,
      }),
    );

    const got = await outer.getByHashAsync('deadbeef');
    assert.ok(got);
    assert.equal(got!.caption, 'a sunny beach');
    assert.equal(got!.widthPx, 1920);
    assert.equal(got!.heightPx, 1080);
    assert.ok(got!.embedding);
    assert.ok(cosine(emb, got!.embedding!) >= 0.99);
  });

  it('inner store sees a null embedding + a compressed tag', async () => {
    const inner = new InMemoryMultimodalMemoryStore();
    const outer = new CompressedMultimodalMemoryStore(inner);
    await outer.addAsync(mm({ sourceSha256: 'abc', caption: 'x', embedding: randomUnit(64, 1) }));

    const raw = await inner.getByHashAsync('abc');
    assert.ok(raw);
    assert.equal(raw!.embedding, undefined);
    assert.ok(raw!.tags && COMPRESSED_TAG_KEY in raw!.tags);
  });

  it('search ranks by cosine through compression', async () => {
    const inner = new InMemoryMultimodalMemoryStore();
    const outer = new CompressedMultimodalMemoryStore(inner, 4);
    const v1 = randomUnit(64, 1);
    const v2 = randomUnit(64, 2);
    await outer.addAsync(mm({ sourceSha256: 'a', caption: 'near', embedding: v1 }));
    await outer.addAsync(mm({ sourceSha256: 'b', caption: 'far', embedding: v2 }));

    const results = await outer.searchAsync(v1, 2);
    assert.equal(results.length, 2);
    assert.equal(results[0].caption, 'near');
  });

  it('reinforce + prune delegate to the inner store through the decorator', async () => {
    const inner = new InMemoryMultimodalMemoryStore();
    const outer = new CompressedMultimodalMemoryStore(inner, 4);
    await outer.addAsync(mm({ sourceSha256: 'x', caption: 'x', embedding: randomUnit(32, 1) }));
    await outer.reinforceAsync('x');
    const got = await outer.getByHashAsync('x');
    assert.equal(got!.referenceCount, 2);
    assert.equal(await outer.countAsync(), 1);
  });
});
