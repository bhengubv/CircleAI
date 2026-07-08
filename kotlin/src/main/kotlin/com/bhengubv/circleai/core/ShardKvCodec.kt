// ShardKvCodec.kt
//
// Kotlin port of CircleAI.Core.Compression.ShardKvCodec (+ ShardCompressedFrame).
//
// (3.3.0) Shard-style KV cache compression: compress K via per-layer online PCA
// + Hadamard rotation, and compress V via product vector quantisation.
//
// WIRE FORMAT (byte-identical to C#):
//   CompressedK: [0..3] scale as float32 little-endian, then kRank signed int8
//                (one per principal component).
//   CompressedV: 1, 2, or 4 bytes little-endian holding the nearest-codeword
//                index (width depends on vCodewords: <=256 → 1, <=65536 → 2,
//                else 4).
//   KPrincipalAxes: kRank*kDim row-major float32 (materialised so the decoder
//                   can stand alone).
//
// Cross-language byte-parity of the seeded default codebook requires the SAME
// pseudo-random sequence as .NET's System.Random(seed). DotNetRandom below
// reimplements the .NET Framework/Core legacy subtractive PRNG (the algorithm
// `new Random(seed)` uses) exactly, so SeedCodebook produces an identical
// codebook and thus identical VQ indices for identical (K, V) inputs.

package com.bhengubv.circleai.core

import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.abs
import kotlin.math.max

// ---------------------------------------------------------------------------
// DotNetRandom — byte-faithful reimplementation of System.Random(seed)
// ---------------------------------------------------------------------------

/**
 * Reproduces the .NET legacy [System.Random] subtractive generator so a seeded
 * codebook is byte-identical across the C# and Kotlin codecs.
 *
 * Algorithm: Knuth's subtractive random number generator (Numerical Recipes),
 * exactly as shipped in .NET Framework and used by .NET Core / .NET for the
 * seeded (`new Random(int)`) constructor.
 */
internal class DotNetRandom(seed: Int) {
    private val seedArray = IntArray(56)
    private var inext = 0
    private var inextp = 0

    init {
        val mbig = Int.MAX_VALUE // 2147483647
        val mseed = 161803398
        val subtraction = if (seed == Int.MIN_VALUE) Int.MAX_VALUE else abs(seed)
        var mj = mseed - subtraction
        seedArray[55] = mj
        var mk = 1
        var ii = 0
        for (i in 1..54) {
            ii = (21 * i) % 55
            seedArray[ii] = mk
            mk = mj - mk
            if (mk < 0) mk += mbig
            mj = seedArray[ii]
        }
        for (k in 1..4) {
            for (i in 1..55) {
                seedArray[i] -= seedArray[1 + (i + 30) % 55]
                if (seedArray[i] < 0) seedArray[i] += mbig
            }
        }
        inext = 0
        inextp = 21
    }

    /** Internal sample in [0.0, 1.0), matching .NET Random.Sample(). */
    private fun sample(): Double {
        // InternalSample() * (1.0 / MBIG)
        return internalSample() * (1.0 / Int.MAX_VALUE.toDouble())
    }

    private fun internalSample(): Int {
        val mbig = Int.MAX_VALUE
        var locINext = inext
        var locINextp = inextp
        if (++locINext >= 56) locINext = 1
        if (++locINextp >= 56) locINextp = 1
        var retVal = seedArray[locINext] - seedArray[locINextp]
        if (retVal == mbig) retVal--
        if (retVal < 0) retVal += mbig
        seedArray[locINext] = retVal
        inext = locINext
        inextp = locINextp
        return retVal
    }

    /** Equivalent to .NET Random.NextDouble(). */
    fun nextDouble(): Double = sample()
}

// ---------------------------------------------------------------------------
// ShardCompressedFrame — CircleAI.Core.Compression.ShardCompressedFrame
// ---------------------------------------------------------------------------

/** (3.3.0) Encoded shard KV pair (compressed K + compressed V). */
class ShardCompressedFrame(
    val compressedK: ByteArray,
    val compressedV: ByteArray,
    val kPrincipalAxes: FloatArray,
    val kOriginalDim: Int,
    val vOriginalDim: Int,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ShardCompressedFrame) return false
        return compressedK.contentEquals(other.compressedK) &&
            compressedV.contentEquals(other.compressedV) &&
            kPrincipalAxes.contentEquals(other.kPrincipalAxes) &&
            kOriginalDim == other.kOriginalDim &&
            vOriginalDim == other.vOriginalDim
    }

    override fun hashCode(): Int {
        var result = compressedK.contentHashCode()
        result = 31 * result + compressedV.contentHashCode()
        result = 31 * result + kPrincipalAxes.contentHashCode()
        result = 31 * result + kOriginalDim
        result = 31 * result + vOriginalDim
        return result
    }
}

// ---------------------------------------------------------------------------
// ShardKvCodec — CircleAI.Core.Compression.ShardKvCodec
// ---------------------------------------------------------------------------

/**
 * (3.3.0) Online-PCA-on-K + VQ-on-V KV compressor.
 *
 * Stateless across frames — the host re-trains the PCA basis with [observeK]
 * when desired, and uses the current basis to encode subsequent frames.
 *
 * @param kDim K-vector dimensionality (e.g. 128 for a typical attention head).
 * @param kRank Number of principal components to keep on K (e.g. 32).
 * @param vDim V-vector dimensionality.
 * @param vCodewords Number of VQ codewords for V (must be a power of 2).
 * @param vCodebookSeed Seed for the deterministic initial codebook.
 */
class ShardKvCodec(
    kDim: Int,
    kRank: Int,
    vDim: Int,
    vCodewords: Int,
    vCodebookSeed: Int = 0,
) {
    private val kDim: Int
    private val kRank: Int
    private val vDim: Int
    private val vCodewords: Int
    private val vCodebook: Array<FloatArray>
    private val hadamardScratch: FloatArray
    private val kCenter: FloatArray
    private val kAxes: Array<FloatArray> // [kRank][kDim]
    private var samplesObservedInternal: Long = 0

    init {
        if (kDim <= 0) throw IndexOutOfBoundsException("kDim")
        if (kRank <= 0 || kRank > kDim) throw IndexOutOfBoundsException("kRank")
        if (vDim <= 0) throw IndexOutOfBoundsException("vDim")
        if (vCodewords <= 1 || (vCodewords and (vCodewords - 1)) != 0) {
            throw IndexOutOfBoundsException(
                "Codeword count must be a power of two greater than 1.",
            )
        }
        this.kDim = kDim
        this.kRank = kRank
        this.vDim = vDim
        this.vCodewords = vCodewords
        kCenter = FloatArray(kDim)
        kAxes = Array(kRank) { FloatArray(kDim) }
        vCodebook = seedCodebook(vDim, vCodewords, vCodebookSeed)
        hadamardScratch = FloatArray(pow2Ceil(kDim))

        // Initialise PCA axes to identity-top-rank for sane defaults before training.
        for (r in 0 until kRank) {
            kAxes[r][r] = 1f
        }
    }

    /** (3.3.0) Number of K samples used to update the PCA centre. */
    val samplesObserved: Long
        get() = samplesObservedInternal

    /** (3.3.0) Update the online K mean estimate with this sample. */
    fun observeK(k: FloatArray) {
        if (k.size != kDim) throw IllegalArgumentException("Input dim mismatch")
        samplesObservedInternal++
        for (i in 0 until kDim) {
            // Running mean.
            kCenter[i] += (k[i] - kCenter[i]) / samplesObservedInternal
        }
    }

    /**
     * (3.3.0) Replace the current PCA axes with [axes]. Caller computes axes
     * offline (full SVD/PCA on observed K) or in batch. Shape is (kRank, kDim).
     */
    fun setPrincipalAxes(axes: Array<FloatArray>) {
        if (axes.size != kRank || axes.any { it.size != kDim }) {
            throw IllegalArgumentException("Axes shape must be (kRank, kDim).")
        }
        for (r in 0 until kRank) {
            System.arraycopy(axes[r], 0, kAxes[r], 0, kDim)
        }
    }

    /** (3.3.0) Replace the V codebook with [codebook]. */
    fun setVCodebook(codebook: Array<FloatArray>) {
        if (codebook.size != vCodewords) {
            throw IllegalArgumentException("Codebook size mismatch.")
        }
        for (i in codebook.indices) {
            if (codebook[i].size != vDim) throw IllegalArgumentException("Codeword dim mismatch.")
            System.arraycopy(codebook[i], 0, vCodebook[i], 0, vDim)
        }
    }

    /** (3.3.0) Encode one (K, V) pair. */
    fun encode(k: FloatArray, v: FloatArray): ShardCompressedFrame {
        if (k.size != kDim) throw IllegalArgumentException("K dim mismatch")
        if (v.size != vDim) throw IllegalArgumentException("V dim mismatch")

        // K: centre → Hadamard → project to top-rank principal axes → quantise to int8.
        val centred = FloatArray(kDim)
        for (i in 0 until kDim) centred[i] = k[i] - kCenter[i]
        applyHadamardInPlace(centred)

        val projected = FloatArray(kRank)
        for (r in 0 until kRank) {
            var dot = 0f
            val axis = kAxes[r]
            for (i in 0 until kDim) dot += centred[i] * axis[i]
            projected[r] = dot
        }

        // Find scale that fits all components into int8 dynamic range.
        var maxAbs = 1e-9f
        for (r in 0 until kRank) maxAbs = max(maxAbs, abs(projected[r]))
        val scale = maxAbs / 127f

        val encodedK = ByteArray(kRank + 4) // +4 for the scale (float32 little-endian)
        ByteBuffer.wrap(encodedK, 0, 4).order(ByteOrder.LITTLE_ENDIAN).putFloat(scale)
        for (r in 0 until kRank) {
            // C# uses `(int)Math.Round(projected[r] / scale)`. Math.Round(double)
            // defaults to MidpointRounding.ToEven (banker's rounding) — Java's
            // Math.rint is exactly round-half-to-even, so this matches byte-for-byte.
            var q = Math.rint((projected[r] / scale).toDouble()).toInt()
            q = q.coerceIn(-127, 127)
            encodedK[4 + r] = q.toByte()
        }

        // V: nearest-codeword VQ → encode index in ⌈log2(codewords)⌉ bits.
        var bestIdx = 0
        var bestDist = Float.MAX_VALUE
        for (c in 0 until vCodewords) {
            var d = 0f
            val word = vCodebook[c]
            for (i in 0 until vDim) {
                val diff = v[i] - word[i]
                d += diff * diff
            }
            if (d < bestDist) {
                bestDist = d
                bestIdx = c
            }
        }

        // Encode index as little-endian uint (1, 2, or 4 bytes depending on codebook size).
        val idxBytes = if (vCodewords <= 256) 1 else if (vCodewords <= 65536) 2 else 4
        val encodedV = ByteArray(idxBytes)
        when (idxBytes) {
            1 -> encodedV[0] = bestIdx.toByte()
            2 -> ByteBuffer.wrap(encodedV).order(ByteOrder.LITTLE_ENDIAN).putShort(bestIdx.toShort())
            4 -> ByteBuffer.wrap(encodedV).order(ByteOrder.LITTLE_ENDIAN).putInt(bestIdx)
        }

        // Materialise the PCA axes once in the frame so the decoder can stand alone.
        val axesFlat = FloatArray(kRank * kDim)
        for (r in 0 until kRank) {
            for (i in 0 until kDim) {
                axesFlat[r * kDim + i] = kAxes[r][i]
            }
        }
        return ShardCompressedFrame(encodedK, encodedV, axesFlat, kDim, vDim)
    }

    /** (3.3.0) Decode a frame back to approximate K and V. */
    fun decode(frame: ShardCompressedFrame): Pair<FloatArray, FloatArray> {
        if (frame.kOriginalDim != kDim) throw IllegalArgumentException("Codec K-dim does not match frame.")
        if (frame.vOriginalDim != vDim) throw IllegalArgumentException("Codec V-dim does not match frame.")

        // K decode: int8 + scale → projected → un-rotate via axes → un-Hadamard → recenter.
        val scale = ByteBuffer.wrap(frame.compressedK, 0, 4).order(ByteOrder.LITTLE_ENDIAN).float
        val projected = FloatArray(kRank)
        for (r in 0 until kRank) {
            projected[r] = frame.compressedK[4 + r].toInt() * scale // Byte is signed in Kotlin (== sbyte)
        }

        val k = FloatArray(kDim)
        for (i in 0 until kDim) {
            var acc = 0f
            for (r in 0 until kRank) {
                acc += projected[r] * frame.kPrincipalAxes[r * kDim + i]
            }
            k[i] = acc
        }
        applyHadamardInPlace(k) // Hadamard is self-inverse (up to scale 1/n).
        for (i in 0 until kDim) k[i] = k[i] / kDim + kCenter[i]

        // V decode: read index, copy codeword.
        val idxBytes = if (vCodewords <= 256) 1 else if (vCodewords <= 65536) 2 else 4
        val idx = when (idxBytes) {
            1 -> frame.compressedV[0].toInt() and 0xFF
            2 -> ByteBuffer.wrap(frame.compressedV).order(ByteOrder.LITTLE_ENDIAN).short.toInt() and 0xFFFF
            4 -> ByteBuffer.wrap(frame.compressedV).order(ByteOrder.LITTLE_ENDIAN).int
            else -> 0
        }
        val vv = FloatArray(vDim)
        System.arraycopy(vCodebook[idx], 0, vv, 0, vDim)
        return Pair(k, vv)
    }

    private fun applyHadamardInPlace(buffer: FloatArray) {
        // Fast Walsh-Hadamard transform on the next-power-of-two-sized scratch.
        val n = hadamardScratch.size
        hadamardScratch.fill(0f)
        System.arraycopy(buffer, 0, hadamardScratch, 0, minOf(buffer.size, n))

        var h = 1
        while (h < n) {
            var i = 0
            while (i < n) {
                for (j in i until i + h) {
                    val x = hadamardScratch[j]
                    val y = hadamardScratch[j + h]
                    hadamardScratch[j] = x + y
                    hadamardScratch[j + h] = x - y
                }
                i += h * 2
            }
            h = h shl 1
        }
        System.arraycopy(hadamardScratch, 0, buffer, 0, minOf(buffer.size, n))
    }

    private companion object {
        fun pow2Ceil(v: Int): Int {
            var p = 1
            while (p < v) p = p shl 1
            return p
        }

        fun seedCodebook(dim: Int, count: Int, seed: Int): Array<FloatArray> {
            val rng = DotNetRandom(seed)
            // NOTE: the codebook is filled row-major, one full row (dim floats)
            // at a time, drawing rng.nextDouble() in the same order as the C#
            // nested for-loop, so the seeded codebook is byte-identical.
            return Array(count) {
                FloatArray(dim) {
                    (rng.nextDouble() * 2.0 - 1.0).toFloat() // uniform [-1, 1]
                }
            }
        }
    }
}
