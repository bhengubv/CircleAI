// Compression.kt
//
// TurboQuant embedding compression + the compressed store decorators. Kotlin
// port of the C# reference, mirroring the verified TypeScript pilot
// (memory/compression.ts) 1:1, so a payload encoded by any language in the SDK
// decodes byte-identically in every other:
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
//   • The SplitMix64 PRNG state is a native 64-bit ULong (Kotlin ULong).
//   • Every place C# stores a `float` (norm, matrix cells, centroids, the
//     Rotate/Unrotate accumulator, deltas) we use Kotlin's native 32-bit Float
//     so the FP32 rounding matches. The Rotate/Unrotate accumulators are Float,
//     matching the C# `float sum` (each += rounds to FP32).
//   • The wire format writes float32 / uint32 little-endian via a
//     LITTLE_ENDIAN ByteBuffer, same as BinaryPrimitives.WriteSingleLittleEndian
//     / WriteUInt32LittleEndian. Base64 via java.util.Base64.

package com.bhengubv.circleai.memory.brain

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.Base64
import java.util.concurrent.ConcurrentHashMap

// ---------------------------------------------------------------------------
// BitPacker — CircleAI.Core.Compression.BitPacker
// ---------------------------------------------------------------------------

/** Bit-packing primitives for arbitrary widths (1..16 bits/index). */
object BitPacker {
    /**
     * Packs [indices] at [bitsPerIndex] into a new byte array. Indices are
     * written least-significant-bit first.
     */
    fun pack(indices: IntArray, bitsPerIndex: Int): ByteArray {
        validateWidth(bitsPerIndex)
        val totalBits = indices.size * bitsPerIndex
        val packed = ByteArray((totalBits + 7) ushr 3)

        var bitPos = 0
        for (i in indices.indices) {
            val value = indices[i]
            if (bitsPerIndex < 16 && value >= (1 shl bitsPerIndex)) {
                throw IllegalArgumentException(
                    "Index $value at position $i exceeds $bitsPerIndex-bit range.",
                )
            }

            var remaining = bitsPerIndex
            var byteIdx = bitPos ushr 3
            var bitOffset = bitPos and 7

            while (remaining > 0) {
                val take = minOf(remaining, 8 - bitOffset)
                val shift = bitsPerIndex - remaining
                val chunk = (value ushr shift) and ((1 shl take) - 1)
                packed[byteIdx] = (packed[byteIdx].toInt() or ((chunk shl bitOffset) and 0xFF)).toByte()

                remaining -= take
                bitOffset = 0
                byteIdx++
            }
            bitPos += bitsPerIndex
        }
        return packed
    }

    /** Convenience overload for ushort-style indices held in an [IntArray]. */
    fun pack(indices: List<Int>, bitsPerIndex: Int): ByteArray =
        pack(indices.toIntArray(), bitsPerIndex)

    /** Unpacks [count] indices of [bitsPerIndex] each from [packed]. */
    fun unpack(packed: ByteArray, count: Int, bitsPerIndex: Int): IntArray {
        validateWidth(bitsPerIndex)
        val requiredBytes = (count * bitsPerIndex + 7) ushr 3
        if (packed.size < requiredBytes) {
            throw IllegalArgumentException(
                "Packed buffer too small: need $requiredBytes bytes, got ${packed.size}.",
            )
        }

        val result = IntArray(count)
        var bitPos = 0
        for (i in 0 until count) {
            var remaining = bitsPerIndex
            var byteIdx = bitPos ushr 3
            var bitOffset = bitPos and 7
            var value = 0

            while (remaining > 0) {
                val take = minOf(remaining, 8 - bitOffset)
                val shift = bitsPerIndex - remaining
                val chunk = ((packed[byteIdx].toInt() and 0xFF) ushr bitOffset) and ((1 shl take) - 1)
                value = value or (chunk shl shift)

                remaining -= take
                bitOffset = 0
                byteIdx++
            }
            result[i] = value and 0xFFFF
            bitPos += bitsPerIndex
        }
        return result
    }

    private fun validateWidth(bitsPerIndex: Int) {
        if (bitsPerIndex < 1 || bitsPerIndex > 16) {
            throw IndexOutOfBoundsException("Bits per index must be 1..16.")
        }
    }
}

// ---------------------------------------------------------------------------
// SeededGaussian — SplitMix64 + Box-Muller (internal SeededGaussian in C#)
// ---------------------------------------------------------------------------

/**
 * Deterministic Gaussian sampler — Box-Muller over a seeded SplitMix64 PRNG.
 * Hand-rolled (not java.util.Random) so output is reproducible across platforms
 * and byte-identical with the C# `SeededGaussian`. State is a native 64-bit
 * ULong; sampling is Double, matching C#.
 */
private class SeededGaussian(seed: ULong) {
    private var state: ULong = if (seed == 0UL) 0xDEADBEEFCAFEBABEUL else seed
    private var hasSpare = false
    private var spare = 0.0

    fun sample(): Double {
        if (hasSpare) {
            hasSpare = false
            return spare
        }

        // Two uniforms in (0, 1].
        var u: Double
        do { u = nextUniform() } while (u <= 1e-300)
        val v = nextUniform()
        val magnitude = Math.sqrt(-2.0 * Math.log(u))
        val angle = 2.0 * Math.PI * v
        spare = magnitude * Math.sin(angle)
        hasSpare = true
        return magnitude * Math.cos(angle)
    }

    private fun nextUniform(): Double {
        // SplitMix64 step (native 64-bit unsigned arithmetic).
        state += 0x9E3779B97F4A7C15UL
        var z = state
        z = (z xor (z shr 30)) * 0xBF58476D1CE4E5B9UL
        z = (z xor (z shr 27)) * 0x94D049BB133111EBUL
        z = z xor (z shr 31)
        // Convert top 53 bits to a double in [0, 1).
        return (z shr 11).toLong().toDouble() * (1.0 / (1L shl 53).toDouble())
    }
}

// ---------------------------------------------------------------------------
// OrthogonalRotation — CircleAI.Core.Compression.OrthogonalRotation
// ---------------------------------------------------------------------------

/**
 * Deterministic random orthogonal rotation matrix for a given dimension.
 * Constructed via QR (modified Gram-Schmidt) of a seeded Gaussian matrix, then
 * sign-corrected. Cached per dimension (construction is O(d^3)).
 */
object OrthogonalRotation {
    /**
     * Fixed seed shared across every CircleAI process so the rotation is
     * portable: compress on device A, decode on device B works identically.
     */
    const val ROTATION_SEED: ULong = 0xC1C1EA10C1C1EA10UL

    private val cache = ConcurrentHashMap<Int, FloatArray>()

    /**
     * Returns the dim×dim orthogonal matrix in row-major layout (length
     * dim*dim). Cached after the first call for a given dimension.
     */
    fun getMatrix(dim: Int): FloatArray {
        if (dim <= 0) throw IndexOutOfBoundsException("dim must be positive")
        return cache.getOrPut(dim) { buildMatrix(dim) }
    }

    /** output[i] = Σ R[i,j] * vector[j]. */
    fun rotate(dim: Int, vector: FloatArray, output: FloatArray) {
        require(vector.size == dim) { "vector length must equal dim." }
        require(output.size == dim) { "output length must equal dim." }
        val matrix = getMatrix(dim)
        for (i in 0 until dim) {
            var sum = 0f
            val rowStart = i * dim
            for (j in 0 until dim) sum += matrix[rowStart + j] * vector[j]
            output[i] = sum
        }
    }

    /**
     * Inverse rotation — multiplies the TRANSPOSE of the rotation matrix by
     * [vector]. The transpose of an orthogonal matrix is its inverse.
     */
    fun unrotate(dim: Int, vector: FloatArray, output: FloatArray) {
        require(vector.size == dim) { "vector length must equal dim." }
        require(output.size == dim) { "output length must equal dim." }
        val matrix = getMatrix(dim)
        for (i in 0 until dim) {
            var sum = 0f
            for (j in 0 until dim) sum += matrix[j * dim + i] * vector[j]
            output[i] = sum
        }
    }

    private fun buildMatrix(dim: Int): FloatArray {
        // 1. Generate a seeded Gaussian matrix G (dim × dim).
        val gauss = DoubleArray(dim * dim)
        val rng = SeededGaussian(ROTATION_SEED)
        for (i in gauss.indices) gauss[i] = rng.sample()

        // 2. QR decomposition via modified Gram-Schmidt.
        val q = modifiedGramSchmidt(gauss, dim)

        // 3. Sign-correct columns so Q is deterministic.
        signCorrectColumns(q, dim)

        // 4. Convert to row-major float32.
        val result = FloatArray(dim * dim)
        for (i in result.indices) result[i] = q[i].toFloat()
        return result
    }

    /**
     * Modified Gram-Schmidt QR. Returns Q (orthonormal columns) in row-major
     * flat layout. The input [g] is not reused after this call.
     */
    private fun modifiedGramSchmidt(g: DoubleArray, dim: Int): DoubleArray {
        val q = DoubleArray(dim * dim)

        for (j in 0 until dim) {
            // Copy column j of g into a working vector.
            for (i in 0 until dim) q[i * dim + j] = g[i * dim + j]

            // Subtract projections onto already-processed columns.
            for (k in 0 until j) {
                var dot = 0.0
                for (i in 0 until dim) dot += q[i * dim + j] * q[i * dim + k]
                for (i in 0 until dim) q[i * dim + j] -= dot * q[i * dim + k]
            }

            // Normalise column j.
            var norm = 0.0
            for (i in 0 until dim) norm += q[i * dim + j] * q[i * dim + j]
            norm = Math.sqrt(norm)
            if (norm < 1e-15) {
                throw IllegalStateException(
                    "Gram-Schmidt produced a near-zero column at j=$j (dim=$dim). " +
                        "This is statistically impossible for a Gaussian matrix; check the RNG seed.",
                )
            }
            val inv = 1.0 / norm
            for (i in 0 until dim) q[i * dim + j] *= inv
        }
        return q
    }

    private fun signCorrectColumns(q: DoubleArray, dim: Int) {
        for (j in 0 until dim) {
            // Diagonal-based sign convention: ensure q[j,j] >= 0.
            val diag = q[j * dim + j]
            if (diag < 0.0) {
                for (i in 0 until dim) q[i * dim + j] = -q[i * dim + j]
            }
        }
    }
}

// ---------------------------------------------------------------------------
// BetaLloydMaxCodebook — CircleAI.Core.Compression.BetaLloydMaxCodebook
// ---------------------------------------------------------------------------

/**
 * A Lloyd-Max codebook for Beta((d-1)/2,(d-1)/2) on [-1, 1]. [boundaries] has
 * length 2^bits-1; [centroids] has length 2^bits.
 */
class BetaCodebook(
    val boundaries: FloatArray,
    val centroids: FloatArray,
)

/** Computes / caches Lloyd-Max codebooks for Beta((d-1)/2,(d-1)/2). */
object BetaLloydMaxCodebook {
    private val cache = ConcurrentHashMap<Long, BetaCodebook>()

    private fun key(bits: Int, dim: Int): Long = (bits.toLong() shl 32) or (dim.toLong() and 0xFFFFFFFFL)

    /**
     * Returns the codebook for the given bit width and dimension, computing it
     * on first request. Cached by (bits, dim).
     */
    fun get(bits: Int, dim: Int): BetaCodebook {
        if (bits < 1 || bits > 8) throw IndexOutOfBoundsException("bits must be in 1..8.")
        if (dim <= 1) throw IndexOutOfBoundsException("dim must be > 1.")
        return cache.getOrPut(key(bits, dim)) { computeCodebook(bits, dim) }
    }

    /**
     * Returns the bin index for [value] against [boundaries] (linear scan — for
     * small codebooks this beats a branch-heavy binary search).
     */
    fun binFor(value: Float, boundaries: FloatArray): Int {
        for (i in boundaries.indices) {
            if (value < boundaries[i]) return i
        }
        return boundaries.size
    }

    private fun computeCodebook(
        bits: Int,
        dim: Int,
        maxIter: Int = 200,
        tol: Double = 1e-12,
    ): BetaCodebook {
        val a = (dim - 1.0) / 2.0
        val nLevels = 1 shl bits

        // Initial centroids: evenly spaced across ±3σ of the Beta-on-[-1,1].
        val std = Math.sqrt((2.0 * a) / ((2.0 * a + 1.0) * 4.0 * a))
        val spread = 3.0 * std
        var centroids = DoubleArray(nLevels)
        for (i in 0 until nLevels) centroids[i] = -spread + (2.0 * spread * i) / (nLevels - 1)

        for (iter in 0 until maxIter) {
            // Boundaries = midpoints between adjacent centroids.
            val boundaries = DoubleArray(nLevels - 1)
            for (i in 0 until nLevels - 1) boundaries[i] = (centroids[i] + centroids[i + 1]) / 2.0

            val edges = DoubleArray(nLevels + 1)
            edges[0] = -1.0
            for (i in boundaries.indices) edges[i + 1] = boundaries[i]
            edges[nLevels] = 1.0

            val newCentroids = DoubleArray(nLevels)
            for (i in 0 until nLevels) {
                val lo = edges[i]
                val hi = edges[i + 1]
                val cdfLo = betaCdfSymmetric(a, (lo + 1.0) / 2.0)
                val cdfHi = betaCdfSymmetric(a, (hi + 1.0) / 2.0)
                val prob = cdfHi - cdfLo

                if (prob < 1e-15) {
                    newCentroids[i] = centroids[i]
                } else {
                    val mean = adaptiveSimpson(
                        { x -> (x * betaPdfSymmetric(a, (x + 1.0) / 2.0)) / 2.0 },
                        lo,
                        hi,
                        1e-14,
                        50,
                    )
                    newCentroids[i] = mean / prob
                }
            }

            var maxChange = 0.0
            for (i in 0 until nLevels) maxChange = maxOf(maxChange, Math.abs(centroids[i] - newCentroids[i]))
            centroids = newCentroids

            if (maxChange < tol) break
        }

        val finalBoundaries = FloatArray(nLevels - 1)
        for (i in 0 until nLevels - 1) finalBoundaries[i] = ((centroids[i] + centroids[i + 1]) / 2.0).toFloat()
        val finalCentroids = FloatArray(nLevels)
        for (i in 0 until nLevels) finalCentroids[i] = centroids[i].toFloat()
        return BetaCodebook(finalBoundaries, finalCentroids)
    }

    // ── Beta(a, a) PDF / CDF on [0, 1] ─────────────────────────────────────
    // The "Symmetric" suffix is a reminder that we always use shape Beta(a, a).

    private fun betaPdfSymmetric(a: Double, x: Double): Double {
        if (x <= 0.0 || x >= 1.0) return 0.0
        // f(x) = x^(a-1) * (1-x)^(a-1) / B(a, a); log-space for stability at large a.
        val logPdf = (a - 1.0) * Math.log(x) +
            (a - 1.0) * Math.log(1.0 - x) -
            logBeta(a, a)
        return Math.exp(logPdf)
    }

    private fun betaCdfSymmetric(a: Double, x: Double): Double {
        if (x <= 0.0) return 0.0
        if (x >= 1.0) return 1.0
        return regularizedIncompleteBeta(a, a, x)
    }

    private fun logBeta(a: Double, b: Double): Double =
        logGamma(a) + logGamma(b) - logGamma(a + b)

    // Lanczos coefficients for g = 7.
    private val LANCZOS_G7 = doubleArrayOf(
        0.99999999999980993, 676.5203681218851, -1259.1392167224028,
        771.32342877765313, -176.61502916214059, 12.507343278686905,
        -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
    )

    /** log Γ(x) for x > 0 via the Lanczos approximation (g = 7, n = 9). */
    private fun logGamma(x0: Double): Double {
        if (x0 < 0.5) {
            // Reflection: Γ(x)Γ(1-x) = π/sin(πx)
            return Math.log(Math.PI / Math.sin(Math.PI * x0)) - logGamma(1.0 - x0)
        }
        val x = x0 - 1.0
        val t = x + 7.5
        var sum = LANCZOS_G7[0]
        for (i in 1 until LANCZOS_G7.size) sum += LANCZOS_G7[i] / (x + i)
        return 0.5 * Math.log(2.0 * Math.PI) + (x + 0.5) * Math.log(t) - t + Math.log(sum)
    }

    /** Regularised incomplete beta function I_x(a, b) (Numerical Recipes 6.4). */
    private fun regularizedIncompleteBeta(a: Double, b: Double, x: Double): Double {
        if (x < 0.0 || x > 1.0) throw IndexOutOfBoundsException("x must be in [0, 1].")
        if (x == 0.0 || x == 1.0) return x

        val bt = Math.exp(
            logGamma(a + b) -
                logGamma(a) -
                logGamma(b) +
                a * Math.log(x) +
                b * Math.log(1.0 - x),
        )
        return if (x < (a + 1.0) / (a + b + 2.0)) {
            (bt * betaContinuedFraction(a, b, x)) / a
        } else {
            1.0 - (bt * betaContinuedFraction(b, a, 1.0 - x)) / b
        }
    }

    private fun betaContinuedFraction(a: Double, b: Double, x: Double): Double {
        val maxIter = 200
        val eps = 3e-15
        val fpmin = 1e-300

        val qab = a + b
        val qap = a + 1.0
        val qam = a - 1.0
        var c = 1.0
        var d = 1.0 - (qab * x) / qap
        if (Math.abs(d) < fpmin) d = fpmin
        d = 1.0 / d
        var h = d

        for (m in 1..maxIter) {
            val m2 = 2 * m
            var aa = (m * (b - m) * x) / ((qam + m2) * (a + m2))
            d = 1.0 + aa * d
            if (Math.abs(d) < fpmin) d = fpmin
            c = 1.0 + aa / c
            if (Math.abs(c) < fpmin) c = fpmin
            d = 1.0 / d
            h *= d * c

            aa = (-(a + m) * (qab + m) * x) / ((a + m2) * (qap + m2))
            d = 1.0 + aa * d
            if (Math.abs(d) < fpmin) d = fpmin
            c = 1.0 + aa / c
            if (Math.abs(c) < fpmin) c = fpmin
            d = 1.0 / d
            val delta = d * c
            h *= delta
            if (Math.abs(delta - 1.0) < eps) return h
        }
        return h // best effort if no convergence
    }

    // ── Adaptive Simpson integration ───────────────────────────────────────

    private fun adaptiveSimpson(
        f: (Double) -> Double,
        a: Double,
        b: Double,
        tol: Double,
        maxDepth: Int,
    ): Double {
        val mid = (a + b) / 2.0
        val fa = f(a)
        val fb = f(b)
        val fm = f(mid)
        val whole = ((b - a) / 6.0) * (fa + 4.0 * fm + fb)
        return adaptiveSimpsonRec(f, a, b, fa, fb, fm, whole, tol, maxDepth)
    }

    private fun adaptiveSimpsonRec(
        f: (Double) -> Double,
        a: Double,
        b: Double,
        fa: Double,
        fb: Double,
        fm: Double,
        whole: Double,
        tol: Double,
        depth: Int,
    ): Double {
        val mid = (a + b) / 2.0
        val m1 = (a + mid) / 2.0
        val m2 = (mid + b) / 2.0
        val fm1 = f(m1)
        val fm2 = f(m2)
        val left = ((mid - a) / 6.0) * (fa + 4.0 * fm1 + fm)
        val right = ((b - mid) / 6.0) * (fm + 4.0 * fm2 + fb)
        val refined = left + right

        if (depth == 0 || Math.abs(refined - whole) < 15.0 * tol) {
            return refined + (refined - whole) / 15.0
        }
        return adaptiveSimpsonRec(f, a, mid, fa, fm, fm1, left, tol / 2.0, depth - 1) +
            adaptiveSimpsonRec(f, mid, b, fm, fb, fm2, right, tol / 2.0, depth - 1)
    }
}

// ---------------------------------------------------------------------------
// TurboQuantCodec — CircleAI.Core.Compression.TurboQuantCodec
// ---------------------------------------------------------------------------

/**
 * Output of [TurboQuantCodec.encode].
 * - [norm]: L2 norm of the original vector — needed to reconstruct magnitude.
 * - [packedIndices]: bit-packed Lloyd-Max bin indices, one per dimension.
 */
class TurboQuantPayload(
    val norm: Float,
    val packedIndices: ByteArray,
)

/** TurboQuant encoder / decoder. */
object TurboQuantCodec {
    /**
     * Encodes a float vector at [bitsPerDim] bits per dimension. Higher bits =
     * better fidelity, larger payload. Typical: 2 bits (16×), 3 bits (~10×).
     */
    fun encode(vector: FloatArray, bitsPerDim: Int): TurboQuantPayload {
        require(vector.size > 1) { "Vector must have length > 1." }
        if (bitsPerDim < 1 || bitsPerDim > 8) throw IndexOutOfBoundsException("bitsPerDim must be 1..8.")

        val dim = vector.size

        // 1. Norm. Accumulate in double, cast the sqrt to float (matches C#).
        var sumSq = 0.0
        for (i in 0 until dim) sumSq += vector[i].toDouble() * vector[i]
        val norm = Math.sqrt(sumSq).toFloat()

        // Edge case — zero vector. Round-trip preserves the all-zero shape.
        if (norm < 1e-20f) {
            val allZeros = ByteArray((dim * bitsPerDim + 7) ushr 3)
            return TurboQuantPayload(0f, allZeros)
        }

        // 2. Unit-normalise (FP32).
        val unit = FloatArray(dim)
        val invNorm = 1f / norm
        for (i in 0 until dim) unit[i] = vector[i] * invNorm

        // 3. Rotate.
        val rotated = FloatArray(dim)
        OrthogonalRotation.rotate(dim, unit, rotated)

        // 4. Quantize per-coordinate.
        val codebook = BetaLloydMaxCodebook.get(bitsPerDim, dim)
        val indices = IntArray(dim)
        for (i in 0 until dim) indices[i] = BetaLloydMaxCodebook.binFor(rotated[i], codebook.boundaries)

        // 5. Pack.
        val packed = BitPacker.pack(indices, bitsPerDim)
        return TurboQuantPayload(norm, packed)
    }

    /**
     * Decodes a TurboQuant payload back into the original-magnitude vector
     * (modulo quantization error).
     */
    fun decode(payload: TurboQuantPayload, dim: Int, bitsPerDim: Int): FloatArray {
        if (dim <= 1) throw IndexOutOfBoundsException("dim must be > 1")
        if (bitsPerDim < 1 || bitsPerDim > 8) throw IndexOutOfBoundsException("bitsPerDim must be 1..8")

        val result = FloatArray(dim)
        if (payload.norm == 0f) return result // all zeros

        // 1. Unpack indices.
        val indices = BitPacker.unpack(payload.packedIndices, dim, bitsPerDim)

        // 2. Map indices → centroids (rotated-space reconstruction).
        val rotated = FloatArray(dim)
        val centroids = BetaLloydMaxCodebook.get(bitsPerDim, dim).centroids
        for (i in 0 until dim) rotated[i] = centroids[indices[i]]

        // 3. Inverse rotation.
        val unit = FloatArray(dim)
        OrthogonalRotation.unrotate(dim, rotated, unit)

        // 4. Scale by stored norm (FP32).
        val scale = payload.norm
        for (i in 0 until dim) result[i] = unit[i] * scale
        return result
    }

    /** Convenience: encode then decode, returning the reconstruction. */
    fun roundTrip(vector: FloatArray, bitsPerDim: Int): FloatArray {
        val encoded = encode(vector, bitsPerDim)
        return decode(encoded, vector.size, bitsPerDim)
    }

    /**
     * Bytes-per-vector required at the given dim and bitsPerDim (excluding the
     * 4-byte norm header).
     */
    fun payloadByteCount(dim: Int, bitsPerDim: Int): Int = (dim * bitsPerDim + 7) ushr 3

    /** Compression ratio vs raw FP32 (vector bytes / encoded bytes incl. norm). */
    fun compressionRatio(dim: Int, bitsPerDim: Int): Double {
        val raw = dim * 4
        val encoded = payloadByteCount(dim, bitsPerDim) + 4 /* norm */
        return raw.toDouble() / encoded
    }
}

// ---------------------------------------------------------------------------
// EmbeddingPayloadCodec — CircleAI.Memory.Compression.EmbeddingPayloadCodec
// ---------------------------------------------------------------------------
//
// Wire format (binary):
//   bytes [0..3]   = magic "TQ3\1" (0x54 0x51 0x33 0x01)
//   bytes [4..7]   = bit-width as uint32 little-endian
//   bytes [8..11]  = dimension as uint32 little-endian
//   bytes [12..15] = norm as float32 little-endian
//   bytes [16..]   = packed indices
// Base64-encoded for tag storage. Bit-width + dim are embedded so callers can
// decode without out-of-band metadata.

/**
 * Encodes and decodes TurboQuant-compressed embeddings as binary blobs suitable
 * for persistence (e.g. in a tag value).
 */
object EmbeddingPayloadCodec {
    /** Magic header bytes that identify a TurboQuant-encoded blob ("TQ3\1"). */
    val MAGIC: ByteArray = byteArrayOf(0x54, 0x51, 0x33, 0x01)

    /**
     * Encodes [vector] at [bitsPerDim] bits per coordinate into a
     * self-describing byte payload.
     */
    fun encode(vector: FloatArray, bitsPerDim: Int): ByteArray {
        require(vector.size > 1) { "Vector must have length > 1." }

        val payload = TurboQuantCodec.encode(vector, bitsPerDim)
        val buf = ByteArray(MAGIC.size + 4 + 4 + 4 + payload.packedIndices.size)
        val bb = ByteBuffer.wrap(buf).order(ByteOrder.LITTLE_ENDIAN)
        bb.put(MAGIC)
        bb.putInt(bitsPerDim)
        bb.putInt(vector.size)
        bb.putFloat(payload.norm)
        bb.put(payload.packedIndices)
        return buf
    }

    /** Decodes a byte payload produced by [encode] back into a float array. */
    fun decode(bytes: ByteArray): FloatArray {
        if (bytes.size < MAGIC.size + 12) throw IllegalArgumentException("Payload too short.")
        if (!hasMagic(bytes)) {
            throw IllegalArgumentException("Magic header missing — not a TurboQuant payload.")
        }

        val bb = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        bb.position(MAGIC.size)
        val bitsPerDim = bb.int
        val dim = bb.int
        val norm = bb.float
        val packed = ByteArray(bytes.size - bb.position())
        bb.get(packed)
        val payload = TurboQuantPayload(norm, packed)
        return TurboQuantCodec.decode(payload, dim, bitsPerDim)
    }

    /** True when the byte span begins with the TurboQuant magic header. */
    fun isEncoded(bytes: ByteArray): Boolean = bytes.size >= MAGIC.size && hasMagic(bytes)

    /** Convenience: encode + base64-stringify for tag-style storage. */
    fun encodeBase64(vector: FloatArray, bitsPerDim: Int): String =
        Base64.getEncoder().encodeToString(encode(vector, bitsPerDim))

    /** Convenience: base64-decode + decode. */
    fun decodeBase64(base64: String): FloatArray = decode(Base64.getDecoder().decode(base64))

    private fun hasMagic(bytes: ByteArray): Boolean =
        bytes[0] == MAGIC[0] && bytes[1] == MAGIC[1] && bytes[2] == MAGIC[2] && bytes[3] == MAGIC[3]
}

// ---------------------------------------------------------------------------
// Shared cosine (double accumulation) + tag key
// ---------------------------------------------------------------------------

/**
 * Cosine similarity matching the C# stores' internal CosineSimilarity.Score
 * (double accumulation, double.Epsilon guard). Distinct from the brain
 * EpisodicStore's dot-product cosine (which assumes pre-normalised vectors).
 */
private fun cosineScoreDouble(a: FloatArray, b: FloatArray): Float {
    if (a.size != b.size) return 0f
    var dot = 0.0
    var magA = 0.0
    var magB = 0.0
    for (i in a.indices) {
        dot += a[i].toDouble() * b[i]
        magA += a[i].toDouble() * a[i]
        magB += b[i].toDouble() * b[i]
    }
    val denom = Math.sqrt(magA) * Math.sqrt(magB)
    return if (denom < Double.MIN_VALUE) 0f else (dot / denom).toFloat()
}

/** Tag key under which the compressed embedding is stored. */
const val COMPRESSED_TAG_KEY: String = "x-tq-embedding"

// ---------------------------------------------------------------------------
// CompressedEpisodicMemoryStore — CircleAI.Memory.Compression
// ---------------------------------------------------------------------------

/**
 * Wraps any [IEpisodicStore] and stores its embeddings in TurboQuant-compressed
 * form. Default 2 bits per dim (~16× shrink).
 *
 * The inner store sees `embedding = null`; the compressed base64 payload lives
 * in the entry's tags under [COMPRESSED_TAG_KEY]. Reads rehydrate the embedding
 * by decoding the tag, and search rebuilds embeddings on the read path so cosine
 * ranking works against the reconstructed vectors.
 */
class CompressedEpisodicMemoryStore(
    private val inner: IEpisodicStore,
    private val bitsPerDim: Int = 2,
) : IEpisodicStore {

    init {
        if (bitsPerDim < 1 || bitsPerDim > 8) throw IndexOutOfBoundsException("bitsPerDim must be 1..8")
    }

    override suspend fun addAsync(entry: EpisodicEntry) {
        val emb = entry.embedding
        val rewritten = if (emb != null && emb.size > 1) {
            EpisodicEntry(
                id = entry.id,
                userText = entry.userText,
                assistantText = entry.assistantText,
                recordedAtUtc = entry.recordedAtUtc,
                appContext = entry.appContext,
                embedding = null, // dropped — lives in tags
                tags = copyTagsWithCompressed(entry.tags, emb),
            )
        } else {
            entry
        }
        inner.addAsync(rewritten)
    }

    override suspend fun searchAsync(queryEmbedding: FloatArray?, topK: Int): List<EpisodicEntry> {
        // The inner store sees embedding = null on every entry, so we cannot
        // defer to its cosine ranking. Load recent, rehydrate, then rank here.
        val all = inner.getRecentAsync(Int.MAX_VALUE)
        val rehydrated = all.map { rehydrate(it) }

        if (queryEmbedding == null) return rehydrated.take(topK)

        return rehydrated
            .filter { it.embedding != null && it.embedding.isNotEmpty() }
            .map { it to cosineScoreDouble(queryEmbedding, it.embedding!!) }
            .sortedByDescending { it.second }
            .take(topK)
            .map { it.first }
    }

    override suspend fun getRecentAsync(count: Int): List<EpisodicEntry> {
        val recent = inner.getRecentAsync(count)
        return recent.map { rehydrate(it) }
    }

    override suspend fun countAsync(): Int = inner.countAsync()

    override suspend fun pruneOlderThanAsync(cutoff: java.time.Instant): Int =
        inner.pruneOlderThanAsync(cutoff)

    private fun copyTagsWithCompressed(src: Map<String, String>?, embedding: FloatArray): Map<String, String> {
        val dict = LinkedHashMap<String, String>()
        if (src != null) dict.putAll(src)
        dict[COMPRESSED_TAG_KEY] = EmbeddingPayloadCodec.encodeBase64(embedding, bitsPerDim)
        return dict
    }

    companion object {
        /** Tag key under which the compressed embedding is stored. */
        const val CompressedTagKey: String = COMPRESSED_TAG_KEY

        private fun rehydrate(e: EpisodicEntry): EpisodicEntry {
            if (e.embedding != null && e.embedding.isNotEmpty()) return e // never compressed
            val b64 = e.tags?.get(COMPRESSED_TAG_KEY) ?: return e
            return try {
                val floats = EmbeddingPayloadCodec.decodeBase64(b64)
                EpisodicEntry(
                    id = e.id,
                    userText = e.userText,
                    assistantText = e.assistantText,
                    recordedAtUtc = e.recordedAtUtc,
                    appContext = e.appContext,
                    embedding = floats,
                    tags = e.tags,
                )
            } catch (_: Throwable) {
                // Malformed tag — return entry as-is so the caller can still see it.
                e
            }
        }
    }
}

// ---------------------------------------------------------------------------
// CompressedMultimodalMemoryStore — CircleAI.Memory.Compression
// ---------------------------------------------------------------------------

/**
 * Wraps any [IMultimodalMemoryStore] and stores its embeddings in
 * TurboQuant-compressed form. Same wire format + tag key as the episodic
 * decorator.
 */
class CompressedMultimodalMemoryStore(
    private val inner: IMultimodalMemoryStore,
    private val bitsPerDim: Int = 2,
) : IMultimodalMemoryStore {

    init {
        if (bitsPerDim < 1 || bitsPerDim > 8) throw IndexOutOfBoundsException("bitsPerDim must be 1..8")
    }

    override suspend fun addAsync(entry: MultimodalMemoryEntry) {
        val emb = entry.embedding
        val rewritten = if (emb != null && emb.size > 1) compress(entry, emb) else entry
        inner.addAsync(rewritten)
    }

    override suspend fun getByHashAsync(sourceSha256: String): MultimodalMemoryEntry? {
        val got = inner.getByHashAsync(sourceSha256) ?: return null
        return rehydrate(got)
    }

    override suspend fun reinforceAsync(sourceSha256: String) = inner.reinforceAsync(sourceSha256)

    override suspend fun searchAsync(queryEmbedding: FloatArray?, topK: Int): List<MultimodalMemoryEntry> {
        val all = inner.getRecentAsync(Int.MAX_VALUE)
        val rehydrated = all.map { rehydrate(it) }
        if (queryEmbedding == null) return rehydrated.take(topK)

        return rehydrated
            .filter { it.embedding != null && it.embedding.isNotEmpty() }
            .map { it to cosineScoreDouble(queryEmbedding, it.embedding!!) }
            .sortedByDescending { it.second }
            .take(topK)
            .map { it.first }
    }

    override suspend fun getRecentAsync(count: Int): List<MultimodalMemoryEntry> {
        val recent = inner.getRecentAsync(count)
        return recent.map { rehydrate(it) }
    }

    override suspend fun pruneOlderThanAsync(cutoff: java.time.Instant): Int =
        inner.pruneOlderThanAsync(cutoff)

    override suspend fun countAsync(): Int = inner.countAsync()

    private fun compress(entry: MultimodalMemoryEntry, embedding: FloatArray): MultimodalMemoryEntry {
        val tags = LinkedHashMap<String, String>()
        entry.tags?.let { tags.putAll(it) }
        tags[COMPRESSED_TAG_KEY] = EmbeddingPayloadCodec.encodeBase64(embedding, bitsPerDim)

        return MultimodalMemoryEntry(
            id = entry.id,
            recordedAtUtc = entry.recordedAtUtc,
            modality = entry.modality,
            caption = entry.caption,
            embedding = null,
            sourceSha256 = entry.sourceSha256,
            sourceMimeType = entry.sourceMimeType,
            sourceByteCount = entry.sourceByteCount,
            sourceUri = entry.sourceUri,
            widthPx = entry.widthPx,
            heightPx = entry.heightPx,
            durationMs = entry.durationMs,
            referenceCount = entry.referenceCount,
            tags = tags,
        )
    }

    companion object {
        /** Tag key under which the compressed embedding is stored. */
        const val CompressedTagKey: String = COMPRESSED_TAG_KEY

        private fun rehydrate(e: MultimodalMemoryEntry): MultimodalMemoryEntry {
            if (e.embedding != null && e.embedding.isNotEmpty()) return e
            val b64 = e.tags?.get(COMPRESSED_TAG_KEY) ?: return e
            return try {
                val floats = EmbeddingPayloadCodec.decodeBase64(b64)
                MultimodalMemoryEntry(
                    id = e.id,
                    recordedAtUtc = e.recordedAtUtc,
                    modality = e.modality,
                    caption = e.caption,
                    embedding = floats,
                    sourceSha256 = e.sourceSha256,
                    sourceMimeType = e.sourceMimeType,
                    sourceByteCount = e.sourceByteCount,
                    sourceUri = e.sourceUri,
                    widthPx = e.widthPx,
                    heightPx = e.heightPx,
                    durationMs = e.durationMs,
                    referenceCount = e.referenceCount,
                    tags = e.tags,
                )
            } catch (_: Throwable) {
                e
            }
        }
    }
}
