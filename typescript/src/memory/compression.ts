// memory/compression.ts
// TurboQuant embedding compression + the compressed store decorators.
//
// Ported EXACTLY from the C# reference so a payload encoded by any language in
// the SDK decodes byte-identically in every other:
//   • CircleAI.Core.Compression.BitPacker
//   • CircleAI.Core.Compression.OrthogonalRotation (+ SeededGaussian)
//   • CircleAI.Core.Compression.BetaLloydMaxCodebook
//   • CircleAI.Core.Compression.TurboQuantCodec (+ TurboQuantPayload)
//   • CircleAI.Memory.Compression.EmbeddingPayloadCodec
//   • CircleAI.Memory.Compression.CompressedEpisodicMemoryStore
//   • CircleAI.Memory.Compression.CompressedMultimodalMemoryStore
//
// TurboQuant is Google Research's data-oblivious vector quantizer
// (arxiv:2504.19874). Per-vector: norm → unit-normalise → fixed orthogonal
// rotation → per-coordinate Lloyd-Max quantise (codebook optimal for the
// Beta((d-1)/2,(d-1)/2) coordinate distribution of a rotated unit vector) →
// bit-pack. Decode reverses it.
//
// Numeric fidelity notes (why this round-trips bit-for-bit with C#):
//   • The SplitMix64 PRNG state is `ulong`; JS numbers can't hold 64 unsigned
//     bits, so the state math runs in BigInt masked to 64 bits.
//   • Every place C# stores a `float` (norm, matrix cells, centroids) we narrow
//     through Math.fround so the FP32 rounding matches.
//   • The wire format writes float32 little-endian via DataView, same as
//     BinaryPrimitives.WriteSingleLittleEndian.

import type {
  EpisodicMemoryEntry,
  IEpisodicMemoryStore,
} from "./index.js";
import type {
  IMultimodalMemoryStore,
  MultimodalMemoryEntry,
} from "./multimodal.js";

// ─────────────────────────────────────────────────────────────────────────────
// BitPacker — CircleAI.Core.Compression.BitPacker
// ─────────────────────────────────────────────────────────────────────────────

/** Bit-packing primitives for arbitrary widths (1..16 bits/index). */
export const BitPacker = {
  /**
   * Packs `indices` at `bitsPerIndex` into a new byte array. Indices are
   * written least-significant-bit first.
   */
  pack(indices: ArrayLike<number>, bitsPerIndex: number): Uint8Array {
    validateWidth(bitsPerIndex);
    const totalBits = indices.length * bitsPerIndex;
    const packed = new Uint8Array((totalBits + 7) >> 3);

    let bitPos = 0;
    for (let i = 0; i < indices.length; i++) {
      const value = indices[i] >>> 0;
      if (bitsPerIndex < 16 && value >= 1 << bitsPerIndex)
        throw new Error(
          `Index ${value} at position ${i} exceeds ${bitsPerIndex}-bit range.`,
        );

      let remaining = bitsPerIndex;
      let byteIdx = bitPos >> 3;
      let bitOffset = bitPos & 7;

      while (remaining > 0) {
        const take = Math.min(remaining, 8 - bitOffset);
        const shift = bitsPerIndex - remaining;
        const chunk = (value >>> shift) & ((1 << take) - 1);
        packed[byteIdx] |= (chunk << bitOffset) & 0xff;

        remaining -= take;
        bitOffset = 0;
        byteIdx++;
      }
      bitPos += bitsPerIndex;
    }
    return packed;
  },

  /**
   * Unpacks `count` indices of `bitsPerIndex` each from `packed`.
   */
  unpack(packed: Uint8Array, count: number, bitsPerIndex: number): Uint16Array {
    validateWidth(bitsPerIndex);
    const requiredBytes = (count * bitsPerIndex + 7) >> 3;
    if (packed.length < requiredBytes)
      throw new Error(
        `Packed buffer too small: need ${requiredBytes} bytes, got ${packed.length}.`,
      );

    const result = new Uint16Array(count);
    let bitPos = 0;
    for (let i = 0; i < count; i++) {
      let remaining = bitsPerIndex;
      let byteIdx = bitPos >> 3;
      let bitOffset = bitPos & 7;
      let value = 0;

      while (remaining > 0) {
        const take = Math.min(remaining, 8 - bitOffset);
        const shift = bitsPerIndex - remaining;
        const chunk = (packed[byteIdx] >>> bitOffset) & ((1 << take) - 1);
        value |= chunk << shift;

        remaining -= take;
        bitOffset = 0;
        byteIdx++;
      }
      result[i] = value & 0xffff;
      bitPos += bitsPerIndex;
    }
    return result;
  },
};

function validateWidth(bitsPerIndex: number): void {
  if (!Number.isInteger(bitsPerIndex) || bitsPerIndex < 1 || bitsPerIndex > 16)
    throw new RangeError("Bits per index must be 1..16.");
}

// ─────────────────────────────────────────────────────────────────────────────
// SeededGaussian — SplitMix64 + Box-Muller (internal SeededGaussian in C#)
// ─────────────────────────────────────────────────────────────────────────────

const U64_MASK = (1n << 64n) - 1n;
const SPLITMIX_GAMMA = 0x9e3779b97f4a7c15n;
const SPLITMIX_M1 = 0xbf58476d1ce4e5b9n;
const SPLITMIX_M2 = 0x94d049bb133111ebn;
const TWO_POW_53 = Math.pow(2, 53);

/**
 * Deterministic Gaussian sampler — Box-Muller over a seeded SplitMix64 PRNG.
 * Hand-rolled (not Math.random) so output is reproducible across platforms and
 * byte-identical with the C# `SeededGaussian`.
 */
class SeededGaussian {
  private state: bigint;
  private hasSpare = false;
  private spare = 0;

  constructor(seed: bigint) {
    this.state = seed === 0n ? 0xdeadbeefcafebaben : seed & U64_MASK;
  }

  sample(): number {
    if (this.hasSpare) {
      this.hasSpare = false;
      return this.spare;
    }

    // Two uniforms in (0, 1].
    let u: number;
    let v: number;
    do {
      u = this.nextUniform();
    } while (u <= 1e-300);
    v = this.nextUniform();
    const magnitude = Math.sqrt(-2.0 * Math.log(u));
    const angle = 2.0 * Math.PI * v;
    this.spare = magnitude * Math.sin(angle);
    this.hasSpare = true;
    return magnitude * Math.cos(angle);
  }

  private nextUniform(): number {
    // SplitMix64 step (all arithmetic masked to 64 unsigned bits).
    this.state = (this.state + SPLITMIX_GAMMA) & U64_MASK;
    let z = this.state;
    z = ((z ^ (z >> 30n)) * SPLITMIX_M1) & U64_MASK;
    z = ((z ^ (z >> 27n)) * SPLITMIX_M2) & U64_MASK;
    z = (z ^ (z >> 31n)) & U64_MASK;
    // Convert top 53 bits to a double in [0, 1).
    return Number(z >> 11n) * (1.0 / TWO_POW_53);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// OrthogonalRotation — CircleAI.Core.Compression.OrthogonalRotation
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Deterministic random orthogonal rotation matrix for a given dimension.
 * Constructed via QR (modified Gram-Schmidt) of a seeded Gaussian matrix, then
 * sign-corrected. Cached per dimension (construction is O(d^3)).
 */
export const OrthogonalRotation = {
  /**
   * Fixed seed shared across every CircleAI process so the rotation is portable:
   * compress on device A, decode on device B works identically.
   */
  ROTATION_SEED: 0xc1c1ea10c1c1ea10n,

  _cache: new Map<number, Float32Array>(),

  /**
   * Returns the dim×dim orthogonal matrix in row-major layout (length dim*dim).
   * Cached after the first call for a given dimension.
   */
  getMatrix(dim: number): Float32Array {
    if (dim <= 0) throw new RangeError("dim must be positive");
    let m = this._cache.get(dim);
    if (m === undefined) {
      m = buildMatrix(dim);
      this._cache.set(dim, m);
    }
    return m;
  },

  /**
   * output[i] = Σ R[i,j] * vector[j].
   */
  rotate(dim: number, vector: ArrayLike<number>, output: Float32Array): void {
    if (vector.length !== dim) throw new Error("vector length must equal dim.");
    if (output.length !== dim) throw new Error("output length must equal dim.");
    const matrix = this.getMatrix(dim);
    for (let i = 0; i < dim; i++) {
      let sum = 0;
      const rowStart = i * dim;
      for (let j = 0; j < dim; j++) sum = Math.fround(sum + matrix[rowStart + j] * vector[j]);
      output[i] = sum;
    }
  },

  /**
   * Inverse rotation — multiplies the TRANSPOSE of the rotation matrix by
   * `vector`. The transpose of an orthogonal matrix is its inverse.
   */
  unrotate(dim: number, vector: ArrayLike<number>, output: Float32Array): void {
    if (vector.length !== dim) throw new Error("vector length must equal dim.");
    if (output.length !== dim) throw new Error("output length must equal dim.");
    const matrix = this.getMatrix(dim);
    for (let i = 0; i < dim; i++) {
      let sum = 0;
      for (let j = 0; j < dim; j++) sum = Math.fround(sum + matrix[j * dim + i] * vector[j]);
      output[i] = sum;
    }
  },
};

function buildMatrix(dim: number): Float32Array {
  // 1. Generate a seeded Gaussian matrix G (dim × dim).
  const gauss = new Float64Array(dim * dim);
  const rng = new SeededGaussian(OrthogonalRotation.ROTATION_SEED);
  for (let i = 0; i < gauss.length; i++) gauss[i] = rng.sample();

  // 2. QR decomposition via modified Gram-Schmidt.
  const q = modifiedGramSchmidt(gauss, dim);

  // 3. Sign-correct columns so Q is deterministic.
  signCorrectColumns(q, dim);

  // 4. Convert to row-major float32.
  const result = new Float32Array(dim * dim);
  for (let i = 0; i < result.length; i++) result[i] = Math.fround(q[i]);
  return result;
}

/**
 * Modified Gram-Schmidt QR. Returns Q (orthonormal columns) in row-major flat
 * layout. The input `g` is not reused after this call.
 */
function modifiedGramSchmidt(g: Float64Array, dim: number): Float64Array {
  const q = new Float64Array(dim * dim);

  for (let j = 0; j < dim; j++) {
    // Copy column j of g into a working vector.
    for (let i = 0; i < dim; i++) q[i * dim + j] = g[i * dim + j];

    // Subtract projections onto already-processed columns.
    for (let k = 0; k < j; k++) {
      let dot = 0;
      for (let i = 0; i < dim; i++) dot += q[i * dim + j] * q[i * dim + k];
      for (let i = 0; i < dim; i++) q[i * dim + j] -= dot * q[i * dim + k];
    }

    // Normalise column j.
    let norm = 0;
    for (let i = 0; i < dim; i++) norm += q[i * dim + j] * q[i * dim + j];
    norm = Math.sqrt(norm);
    if (norm < 1e-15)
      throw new Error(
        `Gram-Schmidt produced a near-zero column at j=${j} (dim=${dim}). ` +
          "This is statistically impossible for a Gaussian matrix; check the RNG seed.",
      );
    const inv = 1.0 / norm;
    for (let i = 0; i < dim; i++) q[i * dim + j] *= inv;
  }
  return q;
}

function signCorrectColumns(q: Float64Array, dim: number): void {
  for (let j = 0; j < dim; j++) {
    // Diagonal-based sign convention: ensure q[j,j] >= 0.
    const diag = q[j * dim + j];
    if (diag < 0.0) {
      for (let i = 0; i < dim; i++) q[i * dim + j] = -q[i * dim + j];
    }
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// BetaLloydMaxCodebook — CircleAI.Core.Compression.BetaLloydMaxCodebook
// ─────────────────────────────────────────────────────────────────────────────

/**
 * A Lloyd-Max codebook for Beta((d-1)/2,(d-1)/2) on [-1, 1].
 * `boundaries` has length 2^bits-1; `centroids` has length 2^bits.
 */
export interface BetaCodebook {
  readonly boundaries: Float32Array;
  readonly centroids: Float32Array;
}

const codebookCache = new Map<string, BetaCodebook>();

/** Computes / caches Lloyd-Max codebooks for Beta((d-1)/2,(d-1)/2). */
export const BetaLloydMaxCodebook = {
  /**
   * Returns the codebook for the given bit width and dimension, computing it on
   * first request. Cached by (bits, dim).
   */
  get(bits: number, dim: number): BetaCodebook {
    if (bits < 1 || bits > 8) throw new RangeError("bits must be in 1..8.");
    if (dim <= 1) throw new RangeError("dim must be > 1.");
    const key = `${bits}:${dim}`;
    let cb = codebookCache.get(key);
    if (cb === undefined) {
      cb = computeCodebook(bits, dim);
      codebookCache.set(key, cb);
    }
    return cb;
  },

  /**
   * Returns the bin index for `value` against `boundaries` (linear scan — for
   * small codebooks this beats a branch-heavy binary search).
   */
  binFor(value: number, boundaries: ArrayLike<number>): number {
    for (let i = 0; i < boundaries.length; i++) {
      if (value < boundaries[i]) return i;
    }
    return boundaries.length;
  },
};

function computeCodebook(
  bits: number,
  dim: number,
  maxIter = 200,
  tol = 1e-12,
): BetaCodebook {
  const a = (dim - 1.0) / 2.0;
  const nLevels = 1 << bits;

  // Initial centroids: evenly spaced across ±3σ of the Beta-on-[-1,1].
  const std = Math.sqrt((2.0 * a) / ((2.0 * a + 1.0) * 4.0 * a));
  const spread = 3.0 * std;
  let centroids = new Float64Array(nLevels);
  for (let i = 0; i < nLevels; i++)
    centroids[i] = -spread + (2.0 * spread * i) / (nLevels - 1);

  for (let iter = 0; iter < maxIter; iter++) {
    // Boundaries = midpoints between adjacent centroids.
    const boundaries = new Float64Array(nLevels - 1);
    for (let i = 0; i < nLevels - 1; i++)
      boundaries[i] = (centroids[i] + centroids[i + 1]) / 2.0;

    const edges = new Float64Array(nLevels + 1);
    edges[0] = -1.0;
    for (let i = 0; i < boundaries.length; i++) edges[i + 1] = boundaries[i];
    edges[nLevels] = 1.0;

    const newCentroids = new Float64Array(nLevels);
    for (let i = 0; i < nLevels; i++) {
      const lo = edges[i];
      const hi = edges[i + 1];
      const cdfLo = betaCdfSymmetric(a, (lo + 1.0) / 2.0);
      const cdfHi = betaCdfSymmetric(a, (hi + 1.0) / 2.0);
      const prob = cdfHi - cdfLo;

      if (prob < 1e-15) {
        newCentroids[i] = centroids[i];
      } else {
        const mean = adaptiveSimpson(
          (x) => (x * betaPdfSymmetric(a, (x + 1.0) / 2.0)) / 2.0,
          lo,
          hi,
          1e-14,
          50,
        );
        newCentroids[i] = mean / prob;
      }
    }

    let maxChange = 0.0;
    for (let i = 0; i < nLevels; i++)
      maxChange = Math.max(maxChange, Math.abs(centroids[i] - newCentroids[i]));
    centroids = newCentroids;

    if (maxChange < tol) break;
  }

  const finalBoundaries = new Float32Array(nLevels - 1);
  for (let i = 0; i < nLevels - 1; i++)
    finalBoundaries[i] = Math.fround((centroids[i] + centroids[i + 1]) / 2.0);
  const finalCentroids = new Float32Array(nLevels);
  for (let i = 0; i < nLevels; i++) finalCentroids[i] = Math.fround(centroids[i]);
  return { boundaries: finalBoundaries, centroids: finalCentroids };
}

// ── Beta(a, a) PDF / CDF on [0, 1] ─────────────────────────────────────────
// The "Symmetric" suffix is a reminder that we always use shape Beta(a, a).

function betaPdfSymmetric(a: number, x: number): number {
  if (x <= 0.0 || x >= 1.0) return 0.0;
  // f(x) = x^(a-1) * (1-x)^(a-1) / B(a, a); log-space for stability at large a.
  const logPdf =
    (a - 1.0) * Math.log(x) +
    (a - 1.0) * Math.log(1.0 - x) -
    logBeta(a, a);
  return Math.exp(logPdf);
}

function betaCdfSymmetric(a: number, x: number): number {
  if (x <= 0.0) return 0.0;
  if (x >= 1.0) return 1.0;
  return regularizedIncompleteBeta(a, a, x);
}

function logBeta(a: number, b: number): number {
  return logGamma(a) + logGamma(b) - logGamma(a + b);
}

// Lanczos coefficients for g = 7.
const LANCZOS_G7 = [
  0.99999999999980993, 676.5203681218851, -1259.1392167224028,
  771.32342877765313, -176.61502916214059, 12.507343278686905,
  -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
];

/** log Γ(x) for x > 0 via the Lanczos approximation (g = 7, n = 9). */
function logGamma(x: number): number {
  if (x < 0.5) {
    // Reflection: Γ(x)Γ(1-x) = π/sin(πx)
    return Math.log(Math.PI / Math.sin(Math.PI * x)) - logGamma(1.0 - x);
  }
  x -= 1.0;
  const t = x + 7.5;
  let sum = LANCZOS_G7[0];
  for (let i = 1; i < LANCZOS_G7.length; i++) sum += LANCZOS_G7[i] / (x + i);
  return 0.5 * Math.log(2.0 * Math.PI) + (x + 0.5) * Math.log(t) - t + Math.log(sum);
}

/** Regularised incomplete beta function I_x(a, b) (Numerical Recipes 6.4). */
function regularizedIncompleteBeta(a: number, b: number, x: number): number {
  if (x < 0.0 || x > 1.0) throw new RangeError("x must be in [0, 1].");
  if (x === 0.0 || x === 1.0) return x;

  const bt = Math.exp(
    logGamma(a + b) -
      logGamma(a) -
      logGamma(b) +
      a * Math.log(x) +
      b * Math.log(1.0 - x),
  );
  if (x < (a + 1.0) / (a + b + 2.0))
    return (bt * betaContinuedFraction(a, b, x)) / a;
  return 1.0 - (bt * betaContinuedFraction(b, a, 1.0 - x)) / b;
}

function betaContinuedFraction(a: number, b: number, x: number): number {
  const maxIter = 200;
  const eps = 3e-15;
  const fpmin = 1e-300;

  const qab = a + b;
  const qap = a + 1.0;
  const qam = a - 1.0;
  let c = 1.0;
  let d = 1.0 - (qab * x) / qap;
  if (Math.abs(d) < fpmin) d = fpmin;
  d = 1.0 / d;
  let h = d;

  for (let m = 1; m <= maxIter; m++) {
    const m2 = 2 * m;
    let aa = (m * (b - m) * x) / ((qam + m2) * (a + m2));
    d = 1.0 + aa * d;
    if (Math.abs(d) < fpmin) d = fpmin;
    c = 1.0 + aa / c;
    if (Math.abs(c) < fpmin) c = fpmin;
    d = 1.0 / d;
    h *= d * c;

    aa = (-(a + m) * (qab + m) * x) / ((a + m2) * (qap + m2));
    d = 1.0 + aa * d;
    if (Math.abs(d) < fpmin) d = fpmin;
    c = 1.0 + aa / c;
    if (Math.abs(c) < fpmin) c = fpmin;
    d = 1.0 / d;
    const delta = d * c;
    h *= delta;
    if (Math.abs(delta - 1.0) < eps) return h;
  }
  return h; // best effort if no convergence
}

// ── Adaptive Simpson integration ───────────────────────────────────────────

function adaptiveSimpson(
  f: (x: number) => number,
  a: number,
  b: number,
  tol: number,
  maxDepth: number,
): number {
  const mid = (a + b) / 2.0;
  const fa = f(a);
  const fb = f(b);
  const fm = f(mid);
  const whole = ((b - a) / 6.0) * (fa + 4.0 * fm + fb);
  return adaptiveSimpsonRec(f, a, b, fa, fb, fm, whole, tol, maxDepth);
}

function adaptiveSimpsonRec(
  f: (x: number) => number,
  a: number,
  b: number,
  fa: number,
  fb: number,
  fm: number,
  whole: number,
  tol: number,
  depth: number,
): number {
  const mid = (a + b) / 2.0;
  const m1 = (a + mid) / 2.0;
  const m2 = (mid + b) / 2.0;
  const fm1 = f(m1);
  const fm2 = f(m2);
  const left = ((mid - a) / 6.0) * (fa + 4.0 * fm1 + fm);
  const right = ((b - mid) / 6.0) * (fm + 4.0 * fm2 + fb);
  const refined = left + right;

  if (depth === 0 || Math.abs(refined - whole) < 15.0 * tol)
    return refined + (refined - whole) / 15.0;
  return (
    adaptiveSimpsonRec(f, a, mid, fa, fm, fm1, left, tol / 2.0, depth - 1) +
    adaptiveSimpsonRec(f, mid, b, fm, fb, fm2, right, tol / 2.0, depth - 1)
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// TurboQuantCodec — CircleAI.Core.Compression.TurboQuantCodec
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Output of {@link TurboQuantCodec.encode}.
 * - `norm`: L2 norm of the original vector — needed to reconstruct magnitude.
 * - `packedIndices`: bit-packed Lloyd-Max bin indices, one per dimension.
 */
export interface TurboQuantPayload {
  readonly norm: number;
  readonly packedIndices: Uint8Array;
}

/** TurboQuant encoder / decoder. */
export const TurboQuantCodec = {
  /**
   * Encodes a float vector at `bitsPerDim` bits per dimension. Higher bits =
   * better fidelity, larger payload. Typical: 2 bits (16×), 3 bits (~10×).
   */
  encode(vector: ArrayLike<number>, bitsPerDim: number): TurboQuantPayload {
    if (vector.length <= 1) throw new Error("Vector must have length > 1.");
    if (bitsPerDim < 1 || bitsPerDim > 8)
      throw new RangeError("bitsPerDim must be 1..8.");

    const dim = vector.length;

    // 1. Norm.
    let sumSq = 0.0;
    for (let i = 0; i < dim; i++) sumSq += vector[i] * vector[i];
    const norm = Math.fround(Math.sqrt(sumSq));

    // Edge case — zero vector. Round-trip preserves the all-zero shape.
    if (norm < 1e-20) {
      const allZeros = new Uint8Array((dim * bitsPerDim + 7) >> 3);
      return { norm: 0, packedIndices: allZeros };
    }

    // 2. Unit-normalise.
    const unit = new Float32Array(dim);
    const invNorm = Math.fround(1 / norm);
    for (let i = 0; i < dim; i++) unit[i] = Math.fround(vector[i] * invNorm);

    // 3. Rotate.
    const rotated = new Float32Array(dim);
    OrthogonalRotation.rotate(dim, unit, rotated);

    // 4. Quantize per-coordinate.
    const codebook = BetaLloydMaxCodebook.get(bitsPerDim, dim);
    const indices = new Uint16Array(dim);
    for (let i = 0; i < dim; i++)
      indices[i] = BetaLloydMaxCodebook.binFor(rotated[i], codebook.boundaries);

    // 5. Pack.
    const packed = BitPacker.pack(indices, bitsPerDim);
    return { norm, packedIndices: packed };
  },

  /**
   * Decodes a TurboQuant payload back into the original-magnitude vector
   * (modulo quantization error).
   */
  decode(payload: TurboQuantPayload, dim: number, bitsPerDim: number): Float32Array {
    if (!payload) throw new Error("payload required");
    if (dim <= 1) throw new RangeError("dim must be > 1");
    if (bitsPerDim < 1 || bitsPerDim > 8) throw new RangeError("bitsPerDim must be 1..8");

    const result = new Float32Array(dim);
    if (payload.norm === 0) return result; // all zeros

    // 1. Unpack indices.
    const indices = BitPacker.unpack(payload.packedIndices, dim, bitsPerDim);

    // 2. Map indices → centroids (rotated-space reconstruction).
    const rotated = new Float32Array(dim);
    const centroids = BetaLloydMaxCodebook.get(bitsPerDim, dim).centroids;
    for (let i = 0; i < dim; i++) rotated[i] = centroids[indices[i]];

    // 3. Inverse rotation.
    const unit = new Float32Array(dim);
    OrthogonalRotation.unrotate(dim, rotated, unit);

    // 4. Scale by stored norm.
    const scale = payload.norm;
    for (let i = 0; i < dim; i++) result[i] = Math.fround(unit[i] * scale);
    return result;
  },

  /** Convenience: encode then decode, returning the reconstruction. */
  roundTrip(vector: ArrayLike<number>, bitsPerDim: number): Float32Array {
    const encoded = this.encode(vector, bitsPerDim);
    return this.decode(encoded, vector.length, bitsPerDim);
  },

  /**
   * Bytes-per-vector required at the given dim and bitsPerDim (excluding the
   * 4-byte norm header).
   */
  payloadByteCount(dim: number, bitsPerDim: number): number {
    return (dim * bitsPerDim + 7) >> 3;
  },

  /**
   * Compression ratio vs raw FP32 (vector bytes / encoded bytes incl. norm).
   */
  compressionRatio(dim: number, bitsPerDim: number): number {
    const raw = dim * 4;
    const encoded = this.payloadByteCount(dim, bitsPerDim) + 4; /* norm */
    return raw / encoded;
  },
};

// ─────────────────────────────────────────────────────────────────────────────
// EmbeddingPayloadCodec — CircleAI.Memory.Compression.EmbeddingPayloadCodec
// ─────────────────────────────────────────────────────────────────────────────
//
// Wire format (binary):
//   bytes [0..3]   = magic "TQ3\1" (0x54 0x51 0x33 0x01)
//   bytes [4..7]   = bit-width as uint32 little-endian
//   bytes [8..11]  = dimension as uint32 little-endian
//   bytes [12..15] = norm as float32 little-endian
//   bytes [16..]   = packed indices
// Base64-encoded for tag storage. Bit-width + dim are embedded so callers can
// decode without out-of-band metadata.

/** Magic header bytes that identify a TurboQuant-encoded blob ("TQ3\1"). */
const MAGIC = Uint8Array.of(0x54, 0x51, 0x33, 0x01);

/**
 * Encodes and decodes TurboQuant-compressed embeddings as binary blobs suitable
 * for persistence (e.g. in a tag value).
 */
export const EmbeddingPayloadCodec = {
  /** Magic header bytes that identify a TurboQuant-encoded blob. */
  MAGIC,

  /**
   * Encodes `vector` at `bitsPerDim` bits per coordinate into a self-describing
   * byte payload.
   */
  encode(vector: ArrayLike<number>, bitsPerDim: number): Uint8Array {
    if (vector.length <= 1) throw new Error("Vector must have length > 1.");

    const payload = TurboQuantCodec.encode(vector, bitsPerDim);
    const buf = new Uint8Array(MAGIC.length + 4 + 4 + 4 + payload.packedIndices.length);
    const dv = new DataView(buf.buffer);
    let o = 0;
    buf.set(MAGIC, 0);
    o += MAGIC.length;
    dv.setUint32(o, bitsPerDim >>> 0, true);
    o += 4;
    dv.setUint32(o, vector.length >>> 0, true);
    o += 4;
    dv.setFloat32(o, payload.norm, true);
    o += 4;
    buf.set(payload.packedIndices, o);
    return buf;
  },

  /** Decodes a byte payload produced by {@link encode} back into a float array. */
  decode(bytes: Uint8Array): number[] {
    if (bytes.length < MAGIC.length + 12) throw new Error("Payload too short.");
    if (!hasMagic(bytes))
      throw new Error("Magic header missing — not a TurboQuant payload.");

    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    let o = MAGIC.length;
    const bitsPerDim = dv.getUint32(o, true);
    o += 4;
    const dim = dv.getUint32(o, true);
    o += 4;
    const norm = dv.getFloat32(o, true);
    o += 4;
    const packed = bytes.slice(o);
    const decoded = TurboQuantCodec.decode({ norm, packedIndices: packed }, dim, bitsPerDim);
    return Array.from(decoded);
  },

  /** True when the byte span begins with the TurboQuant magic header. */
  isEncoded(bytes: Uint8Array): boolean {
    return bytes.length >= MAGIC.length && hasMagic(bytes);
  },

  /** Convenience: encode + base64-stringify for tag-style storage. */
  encodeBase64(vector: ArrayLike<number>, bitsPerDim: number): string {
    return bytesToBase64(this.encode(vector, bitsPerDim));
  },

  /** Convenience: base64-decode + decode. */
  decodeBase64(base64: string): number[] {
    if (base64 == null) throw new Error("base64 required");
    return this.decode(base64ToBytes(base64));
  },
};

function hasMagic(bytes: Uint8Array): boolean {
  return (
    bytes[0] === MAGIC[0] &&
    bytes[1] === MAGIC[1] &&
    bytes[2] === MAGIC[2] &&
    bytes[3] === MAGIC[3]
  );
}

function bytesToBase64(bytes: Uint8Array): string {
  return Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength).toString("base64");
}

function base64ToBytes(base64: string): Uint8Array {
  return new Uint8Array(Buffer.from(base64, "base64"));
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared cosine — matches the C# stores' internal CosineSimilarity.Score
// ─────────────────────────────────────────────────────────────────────────────

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

/** Tag key under which the compressed embedding is stored. */
export const COMPRESSED_TAG_KEY = "x-tq-embedding";

// ─────────────────────────────────────────────────────────────────────────────
// CompressedEpisodicMemoryStore — CircleAI.Memory.Compression
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Wraps any {@link IEpisodicMemoryStore} and stores its embeddings in
 * TurboQuant-compressed form. Default 2 bits per dim (~16× shrink).
 *
 * The inner store sees `embedding = undefined`; the compressed base64 payload
 * lives in the entry's tags under {@link COMPRESSED_TAG_KEY}. Reads rehydrate
 * the embedding by decoding the tag, and search rebuilds embeddings on the read
 * path so cosine ranking works against the reconstructed vectors.
 */
export class CompressedEpisodicMemoryStore implements IEpisodicMemoryStore {
  /** Tag key under which the compressed embedding is stored. */
  static readonly CompressedTagKey = COMPRESSED_TAG_KEY;

  private readonly inner: IEpisodicMemoryStore;
  private readonly bitsPerDim: number;

  constructor(inner: IEpisodicMemoryStore, bitsPerDim = 2) {
    if (!inner) throw new Error("inner required");
    if (bitsPerDim < 1 || bitsPerDim > 8)
      throw new RangeError("bitsPerDim must be 1..8");
    this.inner = inner;
    this.bitsPerDim = bitsPerDim;
  }

  async addAsync(entry: EpisodicMemoryEntry): Promise<void> {
    if (!entry) throw new Error("entry required");
    const rewritten: EpisodicMemoryEntry =
      entry.embedding != null && entry.embedding.length > 1
        ? {
            id: entry.id,
            recordedAtUtc: entry.recordedAtUtc,
            userText: entry.userText,
            assistantText: entry.assistantText,
            appContext: entry.appContext,
            embedding: undefined, // dropped — lives in tags
            tags: this.copyTagsWithCompressed(entry.tags, entry.embedding),
          }
        : entry;
    return this.inner.addAsync(rewritten);
  }

  async searchAsync(
    queryEmbedding: number[] | null,
    topK = 5,
  ): Promise<readonly EpisodicMemoryEntry[]> {
    // The inner store sees embedding = undefined on every entry, so we cannot
    // defer to its cosine ranking. Load recent, rehydrate, then rank here.
    const all = await this.inner.getRecentAsync(Number.MAX_SAFE_INTEGER);
    const rehydrated = all.map(rehydrateEpisodic);

    if (queryEmbedding == null) return rehydrated.slice(0, topK);

    return rehydrated
      .filter((e) => e.embedding != null && e.embedding.length > 0)
      .map((e) => ({ entry: e, score: cosineScore(queryEmbedding, e.embedding!) }))
      .sort((x, y) => y.score - x.score)
      .slice(0, topK)
      .map((t) => t.entry);
  }

  async getRecentAsync(count = 10): Promise<readonly EpisodicMemoryEntry[]> {
    const recent = await this.inner.getRecentAsync(count);
    return recent.map(rehydrateEpisodic);
  }

  countAsync(): Promise<number> {
    return this.inner.countAsync();
  }

  pruneOlderThanAsync(cutoff: Date): Promise<number> {
    return this.inner.pruneOlderThanAsync(cutoff);
  }

  private copyTagsWithCompressed(
    src: Record<string, string> | undefined,
    embedding: number[],
  ): Record<string, string> {
    const dict: Record<string, string> = src ? { ...src } : {};
    dict[COMPRESSED_TAG_KEY] = EmbeddingPayloadCodec.encodeBase64(embedding, this.bitsPerDim);
    return dict;
  }
}

function rehydrateEpisodic(e: EpisodicMemoryEntry): EpisodicMemoryEntry {
  if (e.embedding != null && e.embedding.length > 0) return e; // never compressed
  const b64 = e.tags?.[COMPRESSED_TAG_KEY];
  if (b64 === undefined) return e;
  try {
    const floats = EmbeddingPayloadCodec.decodeBase64(b64);
    return {
      id: e.id,
      recordedAtUtc: e.recordedAtUtc,
      userText: e.userText,
      assistantText: e.assistantText,
      appContext: e.appContext,
      embedding: floats,
      tags: e.tags,
    };
  } catch {
    // Malformed tag — return entry as-is so the caller can still see it.
    return e;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// CompressedMultimodalMemoryStore — CircleAI.Memory.Compression
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Wraps any {@link IMultimodalMemoryStore} and stores its embeddings in
 * TurboQuant-compressed form. Same wire format + tag key as the episodic
 * decorator.
 */
export class CompressedMultimodalMemoryStore implements IMultimodalMemoryStore {
  /** Tag key under which the compressed embedding is stored. */
  static readonly CompressedTagKey = COMPRESSED_TAG_KEY;

  private readonly inner: IMultimodalMemoryStore;
  private readonly bitsPerDim: number;

  constructor(inner: IMultimodalMemoryStore, bitsPerDim = 2) {
    if (!inner) throw new Error("inner required");
    if (bitsPerDim < 1 || bitsPerDim > 8)
      throw new RangeError("bitsPerDim must be 1..8");
    this.inner = inner;
    this.bitsPerDim = bitsPerDim;
  }

  async addAsync(entry: MultimodalMemoryEntry): Promise<void> {
    if (!entry) throw new Error("entry required");
    const rewritten =
      entry.embedding != null && entry.embedding.length > 1
        ? this.compress(entry)
        : entry;
    return this.inner.addAsync(rewritten);
  }

  async getByHashAsync(sourceSha256: string): Promise<MultimodalMemoryEntry | null> {
    const got = await this.inner.getByHashAsync(sourceSha256);
    return got === null ? null : rehydrateMultimodal(got);
  }

  reinforceAsync(sourceSha256: string): Promise<void> {
    return this.inner.reinforceAsync(sourceSha256);
  }

  async searchAsync(
    queryEmbedding: number[] | null,
    topK = 5,
  ): Promise<readonly MultimodalMemoryEntry[]> {
    const all = await this.inner.getRecentAsync(Number.MAX_SAFE_INTEGER);
    const rehydrated = all.map(rehydrateMultimodal);
    if (queryEmbedding == null) return rehydrated.slice(0, topK);

    return rehydrated
      .filter((e) => e.embedding != null && e.embedding.length > 0)
      .map((e) => ({ entry: e, score: cosineScore(queryEmbedding, e.embedding!) }))
      .sort((x, y) => y.score - x.score)
      .slice(0, topK)
      .map((t) => t.entry);
  }

  async getRecentAsync(count = 10): Promise<readonly MultimodalMemoryEntry[]> {
    const recent = await this.inner.getRecentAsync(count);
    return recent.map(rehydrateMultimodal);
  }

  pruneOlderThanAsync(cutoff: Date): Promise<number> {
    return this.inner.pruneOlderThanAsync(cutoff);
  }

  countAsync(): Promise<number> {
    return this.inner.countAsync();
  }

  private compress(entry: MultimodalMemoryEntry): MultimodalMemoryEntry {
    const tags: Record<string, string> = entry.tags ? { ...entry.tags } : {};
    tags[COMPRESSED_TAG_KEY] = EmbeddingPayloadCodec.encodeBase64(entry.embedding!, this.bitsPerDim);

    return {
      id: entry.id,
      recordedAtUtc: entry.recordedAtUtc,
      modality: entry.modality,
      caption: entry.caption,
      embedding: undefined,
      sourceSha256: entry.sourceSha256,
      sourceMimeType: entry.sourceMimeType,
      sourceByteCount: entry.sourceByteCount,
      sourceUri: entry.sourceUri,
      widthPx: entry.widthPx,
      heightPx: entry.heightPx,
      durationMs: entry.durationMs,
      referenceCount: entry.referenceCount,
      tags,
    };
  }
}

function rehydrateMultimodal(e: MultimodalMemoryEntry): MultimodalMemoryEntry {
  if (e.embedding != null && e.embedding.length > 0) return e;
  const b64 = e.tags?.[COMPRESSED_TAG_KEY];
  if (b64 === undefined) return e;
  try {
    const floats = EmbeddingPayloadCodec.decodeBase64(b64);
    return {
      id: e.id,
      recordedAtUtc: e.recordedAtUtc,
      modality: e.modality,
      caption: e.caption,
      embedding: floats,
      sourceSha256: e.sourceSha256,
      sourceMimeType: e.sourceMimeType,
      sourceByteCount: e.sourceByteCount,
      sourceUri: e.sourceUri,
      widthPx: e.widthPx,
      heightPx: e.heightPx,
      durationMs: e.durationMs,
      referenceCount: e.referenceCount,
      tags: e.tags,
    };
  } catch {
    return e;
  }
}
