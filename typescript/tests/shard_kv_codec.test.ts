// shard_kv_codec.test.ts
//
// Exercises ShardKvCodec. Pins the compressed-frame WIRE BYTES against ground
// truth captured from the real C# CircleAI.Core.Compression.ShardKvCodec — the
// CompressedK / CompressedV payloads are what cross device/language boundaries,
// so they must be BYTE-IDENTICAL.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { ShardKvCodec, type ShardCompressedFrame } from '../src/core/shard_kv_codec';

function hex(b: Uint8Array): string {
  return Array.from(b).map((x) => x.toString(16).padStart(2, '0')).join('');
}

// The exact scenario captured from the C# probe.
function buildPinnedCodec(): ShardKvCodec {
  const codec = new ShardKvCodec(8, 4, 6, 16, 123);
  codec.observeK([0.1, -0.2, 0.3, 0.4, -0.5, 0.6, 0.7, -0.8]);
  codec.observeK([0.2, 0.1, -0.1, 0.0, 0.5, -0.3, 0.2, 0.9]);
  return codec;
}
const PINNED_K = [1.0, -1.0, 0.5, 0.25, -0.75, 0.33, 0.66, -0.9];
const PINNED_V = [0.4, -0.2, 0.1, 0.9, -0.5, 0.7];

describe('ShardKvCodec', () => {
  it('encodes a frame whose wire bytes match C# byte-for-byte', () => {
    const codec = buildPinnedCodec();
    const frame = codec.encode(PINNED_K, PINNED_V);
    // Ground truth from dotnet 10.
    assert.equal(hex(frame.compressedK), 'f584993ccd7fecde');
    assert.equal(hex(frame.compressedV), '06');
    assert.equal(frame.kOriginalDim, 8);
    assert.equal(frame.vOriginalDim, 6);
    assert.equal(frame.kPrincipalAxes.length, 4 * 8);
  });

  it('decodes back to the same K/V the C# codec produced', () => {
    const codec = buildPinnedCodec();
    const frame = codec.encode(PINNED_K, PINNED_V);
    const { k, v } = codec.decode(frame);
    // C# decode ground truth (float32 exact bits).
    const expectedK = [
      0.2015354484319687, -0.4341731667518616, 0.40452754497528076,
      -0.24976374208927155, 0.05153544247150421, -0.23417317867279053,
      0.7545275092124939, -0.39976373314857483,
    ];
    const expectedV = [
      0.8347179293632507, -0.5892817974090576, -0.1857467144727707,
      0.5944676399230957, 0.8634610176086426, 0.7766090631484985,
    ];
    for (let i = 0; i < expectedK.length; i++)
      assert.equal(k[i], Math.fround(expectedK[i]), `k[${i}]`);
    for (let i = 0; i < expectedV.length; i++)
      assert.equal(v[i], Math.fround(expectedV[i]), `v[${i}]`);
  });

  it('round-trips V exactly through the codeword lookup', () => {
    const codec = buildPinnedCodec();
    const frame = codec.encode(PINNED_K, PINNED_V);
    const dec = codec.decode(frame);
    // V is quantised to the nearest codeword, so a second encode of the decoded
    // V must select the same index → identical CompressedV.
    const frame2 = codec.encode(PINNED_K, Array.from(dec.v));
    assert.equal(hex(frame2.compressedV), hex(frame.compressedV));
  });

  it('validates constructor arguments', () => {
    assert.throws(() => new ShardKvCodec(0, 1, 4, 4), /kDim/);
    assert.throws(() => new ShardKvCodec(8, 9, 4, 4), /kRank/);
    assert.throws(() => new ShardKvCodec(8, 4, 0, 4), /vDim/);
    // Not a power of two.
    assert.throws(() => new ShardKvCodec(8, 4, 4, 6), /power of two/);
    // <= 1.
    assert.throws(() => new ShardKvCodec(8, 4, 4, 1), /power of two/);
  });

  it('rejects dimension-mismatched inputs', () => {
    const codec = new ShardKvCodec(8, 4, 6, 16);
    assert.throws(() => codec.encode([1, 2, 3], PINNED_V), /K dim mismatch/);
    assert.throws(() => codec.encode(PINNED_K, [1, 2]), /V dim mismatch/);
    assert.throws(() => codec.observeK([1, 2, 3]), /dim mismatch/i);
  });

  it('tracks samplesObserved', () => {
    const codec = new ShardKvCodec(4, 2, 4, 4);
    assert.equal(codec.samplesObserved, 0);
    codec.observeK([1, 2, 3, 4]);
    codec.observeK([4, 3, 2, 1]);
    assert.equal(codec.samplesObserved, 2);
  });

  it('honours an injected V codebook for encode/decode', () => {
    const codec = new ShardKvCodec(4, 2, 3, 4);
    const codebook = [
      [0, 0, 0],
      [1, 1, 1],
      [-1, -1, -1],
      [0.5, 0.5, 0.5],
    ];
    codec.setVCodebook(codebook);
    // A V very close to codeword 1 must decode to exactly codeword 1.
    const frame = codec.encode([1, 0, 0, 0], [0.9, 1.1, 0.95]);
    const dec = codec.decode(frame);
    assert.deepEqual(Array.from(dec.v), [1, 1, 1]);
  });

  it('picks a wider index width for large codebooks', () => {
    // 512 codewords → 2-byte index.
    const codec = new ShardKvCodec(4, 2, 2, 512);
    const frame = codec.encode([1, 0, 0, 0], [0.1, 0.2]);
    assert.equal(frame.compressedV.length, 2);
  });

  it('decode rejects a frame from a mismatched codec', () => {
    const a = new ShardKvCodec(8, 4, 6, 16, 123);
    const frame: ShardCompressedFrame = a.encode(PINNED_K, PINNED_V);
    const b = new ShardKvCodec(4, 2, 6, 16, 123); // different kDim
    assert.throws(() => b.decode(frame), /K-dim/);
  });

  it('accepts injected principal axes', () => {
    const codec = new ShardKvCodec(4, 2, 3, 4);
    // Identity-top-2 axes (row-major 2×4).
    const axes = [1, 0, 0, 0, 0, 1, 0, 0];
    codec.setPrincipalAxes(axes);
    const frame = codec.encode([1, 2, 3, 4], [0.1, 0.2, 0.3]);
    assert.equal(frame.kPrincipalAxes.length, 8);
    // Round-trips without throwing.
    const dec = codec.decode(frame);
    assert.equal(dec.k.length, 4);
  });
});
