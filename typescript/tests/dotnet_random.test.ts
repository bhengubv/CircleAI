// dotnet_random.test.ts
//
// Pins the DotNetRandom port against ground truth captured from the real
// .NET `new Random(seed)` (System.Random legacy seeded path). If these values
// drift, ShardKvCodec's default V codebook stops matching C# byte-for-byte.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { DotNetRandom } from '../src/core/dotnet_random';

// Ground truth: `new Random(seed).NextDouble()` × 8, captured from dotnet 10.
const GROUND_TRUTH: Record<number, number[]> = {
  0: [
    0.7262432699679598, 0.8173253595909687, 0.7680226893946634,
    0.5581611914365372, 0.2060331540210327, 0.5588847946184151,
    0.9060270660119257, 0.44217787331071584,
  ],
  1: [
    0.24866858415709278, 0.11074397718102856, 0.46701067987224587,
    0.7716041220219825, 0.657518893786482, 0.43278260130099144,
    0.3540837636003661, 0.9438622761256351,
  ],
  42: [
    0.6681064659115423, 0.14090729837348093, 0.12551828945312568,
    0.5227642760252413, 0.16843422416990353, 0.26259267528662117,
    0.7244083647264207, 0.5129227915373271,
  ],
};

describe('DotNetRandom', () => {
  it('matches .NET Random(seed).NextDouble() byte-for-byte', () => {
    for (const [seedStr, expected] of Object.entries(GROUND_TRUTH)) {
      const seed = Number(seedStr);
      const r = new DotNetRandom(seed);
      for (let i = 0; i < expected.length; i++) {
        assert.equal(
          r.nextDouble(),
          expected[i],
          `seed=${seed} sample ${i}`,
        );
      }
    }
  });

  it('handles negative seeds (Abs) — pinned against C#', () => {
    // new Random(-7).NextDouble() first two, from dotnet 10.
    const r = new DotNetRandom(-7);
    assert.equal(r.nextDouble(), 0.38322046929189024);
    assert.equal(r.nextDouble(), 0.8712556827213874);
  });

  it('is deterministic — two instances with the same seed agree', () => {
    const a = new DotNetRandom(12345);
    const b = new DotNetRandom(12345);
    for (let i = 0; i < 50; i++) assert.equal(a.nextDouble(), b.nextDouble());
  });

  it('internalSample stays within [0, int.MaxValue)', () => {
    const r = new DotNetRandom(99);
    for (let i = 0; i < 1000; i++) {
      const s = r.internalSample();
      assert.ok(s >= 0 && s < 2147483647, `sample out of range: ${s}`);
    }
  });
});
