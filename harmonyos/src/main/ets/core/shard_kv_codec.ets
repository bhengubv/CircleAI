/**
 * core/shard_kv_codec
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// core/shard_kv_codec.ts
//
// Port of CircleAI.Core.Compression.ShardKvCodec + ShardCompressedFrame.
//
// (3.3.0) Shard-style KV cache compression: compress K via per-layer online
// PCA + Hadamard rotation, and compress V via product vector quantisation.
//
// Wire format (byte-identical with C#):
//   CompressedK: bytes [0..3]  = scale  (float32 little-endian)
//                bytes [4..]   = kRank signed int8 quantised components
//   CompressedV: 1/2/4 byte little-endian unsigned index into the V codebook,
//                width chosen by codebook size (<=256 → 1, <=65536 → 2, else 4).
//
// Float fidelity: every C# `float` site is narrowed through Math.fround so the
// FP32 rounding matches. The scale is written via DataView.setFloat32(LE), the
// exact analogue of BinaryPrimitives.WriteSingleLittleEndian.

import { DotNetRandom } from "./dotnet_random";

/** (3.3.0) Encoded shard KV pair (compressed K + compressed V). */
export interface ShardCompressedFrame {
  readonly compressedK: Uint8Array;
  readonly compressedV: Uint8Array;
  readonly kPrincipalAxes: Float32Array;
  readonly kOriginalDim: number;
  readonly vOriginalDim: number;
}

/** Rounds a JS number to a signed 32-bit integer (C# `(int)` truncation semantics). */
function toInt32(value: number): number {
  return value | 0;
}

/**
 * C# `Math.Round(double)` uses banker's rounding (round-half-to-even) by
 * default. ShardKvCodec relies on `(int)Math.Round(projected/scale)`, so we
 * reproduce round-half-to-even here.
 */
function mathRoundHalfEven(value: number): number {
  const floor = Math.floor(value);
  const diff = value - floor;
  if (diff < 0.5) return floor;
  if (diff > 0.5) return floor + 1;
  // Exactly .5 — round to even.
  return floor % 2 === 0 ? floor : floor + 1;
}

function clamp(value: number, min: number, max: number): number {
  if (value < min) return min;
  if (value > max) return max;
  return value;
}

/**
 * (3.3.0) Online-PCA-on-K + VQ-on-V KV compressor. Stateless across frames —
 * the host re-trains the PCA basis with {@link observeK} when desired, and uses
 * the current basis to encode subsequent frames.
 */
export class ShardKvCodec {
  private readonly kDim: number;
  private readonly kRank: number;
  private readonly vDim: number;
  private readonly vCodewords: number;
  private readonly vCodebook: Float32Array[];
  private readonly hadamardScratch: Float32Array;
  private readonly kCenter: Float32Array;
  /** Row-major (kRank × kDim) PCA axes. */
  private readonly kAxes: Float32Array;
  private samples = 0;

  /**
   * @param kDim K-vector dimensionality (e.g. 128 for a typical attention head).
   * @param kRank Number of principal components to keep on K (e.g. 32).
   * @param vDim V-vector dimensionality.
   * @param vCodewords Number of VQ codewords for V (must be a power of 2 > 1).
   * @param vCodebookSeed Seed for the deterministic initial codebook.
   */
  constructor(
    kDim: number,
    kRank: number,
    vDim: number,
    vCodewords: number,
    vCodebookSeed = 0,
  ) {
    if (kDim <= 0) throw new RangeError("kDim");
    if (kRank <= 0 || kRank > kDim) throw new RangeError("kRank");
    if (vDim <= 0) throw new RangeError("vDim");
    if (vCodewords <= 1 || (vCodewords & (vCodewords - 1)) !== 0) {
      throw new RangeError(
        "Codeword count must be a power of two greater than 1.",
      );
    }
    this.kDim = kDim;
    this.kRank = kRank;
    this.vDim = vDim;
    this.vCodewords = vCodewords;
    this.kCenter = new Float32Array(kDim);
    this.kAxes = new Float32Array(kRank * kDim);
    this.vCodebook = ShardKvCodec.seedCodebook(vDim, vCodewords, vCodebookSeed);
    this.hadamardScratch = new Float32Array(ShardKvCodec.pow2Ceil(kDim));

    // Initialise PCA axes to identity-top-rank for sane defaults before training.
    for (let r = 0; r < kRank; r++) {
      this.kAxes[r * kDim + r] = 1;
    }
  }

  /** (3.3.0) Number of K samples used to update the PCA centre. */
  get samplesObserved(): number {
    return this.samples;
  }

  /** (3.3.0) Update the online K mean estimate with this sample. */
  observeK(k: ArrayLike<number>): void {
    if (k.length !== this.kDim)
      throw new Error("Input dim mismatch");
    this.samples++;
    for (let i = 0; i < this.kDim; i++) {
      // Running mean, kept in FP32 like the C# float[] _kCenter.
      this.kCenter[i] = Math.fround(
        this.kCenter[i] + Math.fround((k[i] - this.kCenter[i]) / this.samples),
      );
    }
  }

  /**
   * (3.3.0) Replace the current PCA axes with `axes` (row-major kRank × kDim).
   * Caller computes axes offline (full SVD/PCA on observed K) or in batch.
   */
  setPrincipalAxes(axes: ArrayLike<number>): void {
    if (axes.length !== this.kRank * this.kDim) {
      throw new Error("Axes shape must be (kRank, kDim).");
    }
    for (let i = 0; i < axes.length; i++) this.kAxes[i] = Math.fround(axes[i]);
  }

  /** (3.3.0) Replace the V codebook with `codebook`. */
  setVCodebook(codebook: ArrayLike<ArrayLike<number>>): void {
    if (codebook.length !== this.vCodewords) {
      throw new Error("Codebook size mismatch.");
    }
    for (let i = 0; i < codebook.length; i++) {
      const word = codebook[i];
      if (word.length !== this.vDim)
        throw new Error("Codeword dim mismatch.");
      for (let j = 0; j < this.vDim; j++)
        this.vCodebook[i][j] = Math.fround(word[j]);
    }
  }

  /** (3.3.0) Encode one (K, V) pair. */
  encode(k: ArrayLike<number>, v: ArrayLike<number>): ShardCompressedFrame {
    if (k.length !== this.kDim) throw new Error("K dim mismatch");
    if (v.length !== this.vDim) throw new Error("V dim mismatch");

    // K: centre → Hadamard → project to top-rank principal axes → int8.
    const centred = new Float32Array(this.kDim);
    for (let i = 0; i < this.kDim; i++)
      centred[i] = Math.fround(k[i] - this.kCenter[i]);
    this.applyHadamardInPlace(centred);

    const projected = new Float32Array(this.kRank);
    for (let r = 0; r < this.kRank; r++) {
      let dot = 0;
      const rowStart = r * this.kDim;
      for (let i = 0; i < this.kDim; i++)
        dot = Math.fround(dot + Math.fround(centred[i] * this.kAxes[rowStart + i]));
      projected[r] = dot;
    }

    // Find scale that fits all components into int8 dynamic range.
    let maxAbs = Math.fround(1e-9);
    for (let r = 0; r < this.kRank; r++)
      maxAbs = Math.max(maxAbs, Math.abs(projected[r]));
    const scale = Math.fround(maxAbs / 127);

    const encodedK = new Uint8Array(this.kRank + 4); // +4 for the float32 scale
    new DataView(encodedK.buffer).setFloat32(0, scale, true);
    for (let r = 0; r < this.kRank; r++) {
      let q = toInt32(mathRoundHalfEven(Math.fround(projected[r] / scale)));
      q = clamp(q, -127, 127);
      // (byte)((sbyte)q) — store two's-complement low byte.
      encodedK[4 + r] = q & 0xff;
    }

    // V: nearest-codeword VQ → encode index in ⌈log2(codewords)⌉ bits.
    let bestIdx = 0;
    let bestDist = Number.MAX_VALUE;
    for (let c = 0; c < this.vCodewords; c++) {
      let d = 0;
      const word = this.vCodebook[c];
      for (let i = 0; i < this.vDim; i++) {
        const diff = Math.fround(v[i] - word[i]);
        d = Math.fround(d + Math.fround(diff * diff));
      }
      if (d < bestDist) {
        bestDist = d;
        bestIdx = c;
      }
    }

    // Encode index as little-endian uint (1, 2, or 4 bytes).
    const idxBytes = ShardKvCodec.indexByteWidth(this.vCodewords);
    const encodedV = new Uint8Array(idxBytes);
    const vView = new DataView(encodedV.buffer);
    switch (idxBytes) {
      case 1:
        encodedV[0] = bestIdx & 0xff;
        break;
      case 2:
        vView.setUint16(0, bestIdx & 0xffff, true);
        break;
      case 4:
        vView.setUint32(0, bestIdx >>> 0, true);
        break;
    }

    // Materialise the PCA axes into the frame so the decoder can stand alone.
    const axesFlat = new Float32Array(this.kRank * this.kDim);
    for (let r = 0; r < this.kRank; r++) {
      const rowStart = r * this.kDim;
      for (let i = 0; i < this.kDim; i++) {
        axesFlat[rowStart + i] = this.kAxes[rowStart + i];
      }
    }

    return {
      compressedK: encodedK,
      compressedV: encodedV,
      kPrincipalAxes: axesFlat,
      kOriginalDim: this.kDim,
      vOriginalDim: this.vDim,
    };
  }

  /** (3.3.0) Decode a frame back to approximate K and V. */
  decode(frame: ShardCompressedFrame): { k: Float32Array; v: Float32Array } {
    if (frame === null || frame === undefined)
      throw new Error("frame is required");
    if (frame.kOriginalDim !== this.kDim)
      throw new Error("Codec K-dim does not match frame.");
    if (frame.vOriginalDim !== this.vDim)
      throw new Error("Codec V-dim does not match frame.");

    // K decode: int8 + scale → projected → un-rotate via axes → un-Hadamard → recenter.
    const scale = new DataView(
      frame.compressedK.buffer,
      frame.compressedK.byteOffset,
      frame.compressedK.byteLength,
    ).getFloat32(0, true);

    const projected = new Float32Array(this.kRank);
    for (let r = 0; r < this.kRank; r++) {
      // (sbyte)frame.CompressedK[4 + r] — reinterpret low byte as signed.
      const signed = (frame.compressedK[4 + r] << 24) >> 24;
      projected[r] = Math.fround(signed * scale);
    }

    const k = new Float32Array(this.kDim);
    for (let i = 0; i < this.kDim; i++) {
      let acc = 0;
      for (let r = 0; r < this.kRank; r++) {
        acc = Math.fround(
          acc + Math.fround(projected[r] * frame.kPrincipalAxes[r * this.kDim + i]),
        );
      }
      k[i] = acc;
    }
    this.applyHadamardInPlace(k); // Hadamard is self-inverse (up to scale 1/n).
    for (let i = 0; i < this.kDim; i++)
      k[i] = Math.fround(Math.fround(k[i] / this.kDim) + this.kCenter[i]);

    // V decode: read index, copy codeword.
    const idxBytes = ShardKvCodec.indexByteWidth(this.vCodewords);
    const vView = new DataView(
      frame.compressedV.buffer,
      frame.compressedV.byteOffset,
      frame.compressedV.byteLength,
    );
    let idx = 0;
    switch (idxBytes) {
      case 1:
        idx = frame.compressedV[0];
        break;
      case 2:
        idx = vView.getUint16(0, true);
        break;
      case 4:
        idx = vView.getUint32(0, true) | 0;
        break;
    }
    const v = new Float32Array(this.vDim);
    const word = this.vCodebook[idx];
    for (let i = 0; i < this.vDim; i++) v[i] = word[i];
    return { k, v };
  }

  private applyHadamardInPlace(buffer: Float32Array): void {
    // Fast Walsh-Hadamard transform on the next-power-of-two-sized scratch.
    const n = this.hadamardScratch.length;
    this.hadamardScratch.fill(0);
    const copyLen = Math.min(buffer.length, n);
    for (let i = 0; i < copyLen; i++) this.hadamardScratch[i] = buffer[i];

    for (let h = 1; h < n; h <<= 1) {
      for (let i = 0; i < n; i += h * 2) {
        for (let j = i; j < i + h; j++) {
          const x = this.hadamardScratch[j];
          const y = this.hadamardScratch[j + h];
          this.hadamardScratch[j] = Math.fround(x + y);
          this.hadamardScratch[j + h] = Math.fround(x - y);
        }
      }
    }
    for (let i = 0; i < copyLen; i++) buffer[i] = this.hadamardScratch[i];
  }

  private static pow2Ceil(v: number): number {
    let p = 1;
    while (p < v) p <<= 1;
    return p;
  }

  private static indexByteWidth(vCodewords: number): number {
    return vCodewords <= 256 ? 1 : vCodewords <= 65536 ? 2 : 4;
  }

  private static seedCodebook(
    dim: number,
    count: number,
    seed: number,
  ): Float32Array[] {
    const rng = new DotNetRandom(seed);
    const cb: Float32Array[] = new Array(count);
    for (let c = 0; c < count; c++) {
      const word = new Float32Array(dim);
      for (let i = 0; i < dim; i++) {
        // (float)(rng.NextDouble() * 2.0 - 1.0) — uniform [-1, 1], narrowed to FP32.
        word[i] = Math.fround(rng.nextDouble() * 2.0 - 1.0);
      }
      cb[c] = word;
    }
    return cb;
  }
}
