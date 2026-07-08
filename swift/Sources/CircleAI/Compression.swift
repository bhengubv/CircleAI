// Compression.swift
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
// rotation → per-coordinate Lloyd-Max quantise → bit-pack. Decode reverses it.
//
// Numeric fidelity notes (why this round-trips bit-for-bit with C#):
//   • SplitMix64 state is a native UInt64; wrapping arithmetic uses &+ / &* .
//   • Every place C# stores a `float` (norm, matrix cells, centroids, deltas) we
//     use Swift `Float` (32-bit native) so the FP32 rounding matches.
//   • The norm is accumulated in Double then narrowed to Float, exactly as C#.
//   • The wire format writes uint32 / float32 little-endian by explicit byte
//     assembly (Float.bitPattern gives the FP32 bits), same as
//     BinaryPrimitives.Write*LittleEndian.

import Foundation

// MARK: - BitPacker — CircleAI.Core.Compression.BitPacker

/// Bit-packing primitives for arbitrary widths (1..16 bits/index).
public enum BitPacker {
    /// Packs `indices` at `bitsPerIndex` into a new byte array. Indices are
    /// written least-significant-bit first.
    public static func pack(_ indices: [UInt16], bitsPerIndex: Int) -> [UInt8] {
        validateWidth(bitsPerIndex)
        let totalBits = indices.count * bitsPerIndex
        var packed = [UInt8](repeating: 0, count: (totalBits + 7) / 8)

        var bitPos = 0
        for i in 0..<indices.count {
            let value = UInt32(indices[i])
            if bitsPerIndex < 16 && value >= (UInt32(1) << bitsPerIndex) {
                fatalError("Index \(value) at position \(i) exceeds \(bitsPerIndex)-bit range.")
            }

            var remaining = bitsPerIndex
            var byteIdx = bitPos >> 3
            var bitOffset = bitPos & 7

            while remaining > 0 {
                let take = min(remaining, 8 - bitOffset)
                let shift = bitsPerIndex - remaining
                let chunk = UInt8((value >> shift) & ((UInt32(1) << take) - 1))
                packed[byteIdx] |= UInt8((UInt32(chunk) << bitOffset) & 0xFF)

                remaining -= take
                bitOffset = 0
                byteIdx += 1
            }
            bitPos += bitsPerIndex
        }
        return packed
    }

    /// Unpacks `count` indices of `bitsPerIndex` each from `packed`.
    public static func unpack(_ packed: [UInt8], count: Int, bitsPerIndex: Int) -> [UInt16] {
        validateWidth(bitsPerIndex)
        let requiredBytes = (count * bitsPerIndex + 7) / 8
        if packed.count < requiredBytes {
            fatalError("Packed buffer too small: need \(requiredBytes) bytes, got \(packed.count).")
        }

        var result = [UInt16](repeating: 0, count: count)
        var bitPos = 0
        for i in 0..<count {
            var remaining = bitsPerIndex
            var byteIdx = bitPos >> 3
            var bitOffset = bitPos & 7
            var value: UInt32 = 0

            while remaining > 0 {
                let take = min(remaining, 8 - bitOffset)
                let shift = bitsPerIndex - remaining
                let chunk = (UInt32(packed[byteIdx]) >> bitOffset) & ((UInt32(1) << take) - 1)
                value |= chunk << shift

                remaining -= take
                bitOffset = 0
                byteIdx += 1
            }
            result[i] = UInt16(value & 0xFFFF)
            bitPos += bitsPerIndex
        }
        return result
    }

    private static func validateWidth(_ bitsPerIndex: Int) {
        precondition(bitsPerIndex >= 1 && bitsPerIndex <= 16, "Bits per index must be 1..16.")
    }
}

// MARK: - SeededGaussian — SplitMix64 + Box-Muller (internal SeededGaussian in C#)

/// Deterministic Gaussian sampler — Box-Muller over a seeded SplitMix64 PRNG.
/// Hand-rolled (not `Random`) so output is reproducible across platforms and
/// byte-identical with the C# `SeededGaussian`.
final class SeededGaussian {
    private var state: UInt64
    private var hasSpare = false
    private var spare: Double = 0

    init(seed: UInt64) {
        state = seed == 0 ? 0xDEAD_BEEF_CAFE_BABE : seed
    }

    func sample() -> Double {
        if hasSpare { hasSpare = false; return spare }

        // Two uniforms in (0, 1].
        var u: Double
        repeat { u = nextUniform() } while u <= 1e-300
        let v = nextUniform()
        let magnitude = (-2.0 * Foundation.log(u)).squareRoot()
        let angle = 2.0 * Double.pi * v
        spare = magnitude * Foundation.sin(angle)
        hasSpare = true
        return magnitude * Foundation.cos(angle)
    }

    private func nextUniform() -> Double {
        // SplitMix64 step (native UInt64, wrapping arithmetic).
        state = state &+ 0x9E37_79B9_7F4A_7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        z = z ^ (z >> 31)
        // Convert top 53 bits to a double in [0, 1).
        return Double(z >> 11) * (1.0 / Double(UInt64(1) << 53))
    }
}

// MARK: - OrthogonalRotation — CircleAI.Core.Compression.OrthogonalRotation

/// Deterministic random orthogonal rotation matrix for a given dimension.
/// Constructed via QR (modified Gram-Schmidt) of a seeded Gaussian matrix, then
/// sign-corrected. Cached per dimension (construction is O(d^3)).
public enum OrthogonalRotation {
    /// Fixed seed shared across every CircleAI process so the rotation is
    /// portable: compress on device A, decode on device B works identically.
    public static let rotationSeed: UInt64 = 0xC1C1_EA10_C1C1_EA10

    private static let cache = RotationCache()

    /// Returns the dim×dim orthogonal matrix in row-major layout (length dim*dim).
    /// Cached after the first call for a given dimension.
    public static func getMatrix(_ dim: Int) -> [Float] {
        precondition(dim > 0, "dim must be positive")
        return cache.getOrAdd(dim) { buildMatrix(dim) }
    }

    /// output[i] = Σ R[i,j] * vector[j].
    public static func rotate(_ dim: Int, _ vector: [Float], _ output: inout [Float]) {
        precondition(vector.count == dim, "vector length must equal dim.")
        precondition(output.count == dim, "output length must equal dim.")
        let matrix = getMatrix(dim)
        for i in 0..<dim {
            var sum: Float = 0
            let rowStart = i * dim
            for j in 0..<dim { sum += matrix[rowStart + j] * vector[j] }
            output[i] = sum
        }
    }

    /// Inverse rotation — multiplies the TRANSPOSE of the rotation matrix by
    /// `vector`. The transpose of an orthogonal matrix is its inverse.
    public static func unrotate(_ dim: Int, _ vector: [Float], _ output: inout [Float]) {
        precondition(vector.count == dim, "vector length must equal dim.")
        precondition(output.count == dim, "output length must equal dim.")
        let matrix = getMatrix(dim)
        for i in 0..<dim {
            var sum: Float = 0
            for j in 0..<dim { sum += matrix[j * dim + i] * vector[j] }
            output[i] = sum
        }
    }

    private static func buildMatrix(_ dim: Int) -> [Float] {
        // 1. Generate a seeded Gaussian matrix G (dim × dim).
        var gauss = [Double](repeating: 0, count: dim * dim)
        let rng = SeededGaussian(seed: rotationSeed)
        for i in 0..<gauss.count { gauss[i] = rng.sample() }

        // 2. QR decomposition via modified Gram-Schmidt.
        var q = modifiedGramSchmidt(&gauss, dim)

        // 3. Sign-correct columns so Q is deterministic.
        signCorrectColumns(&q, dim)

        // 4. Convert to row-major float32.
        var result = [Float](repeating: 0, count: dim * dim)
        for i in 0..<result.count { result[i] = Float(q[i]) }
        return result
    }

    /// Modified Gram-Schmidt QR. Returns Q (orthonormal columns) in row-major
    /// flat layout. `g` is not reused after this call.
    private static func modifiedGramSchmidt(_ g: inout [Double], _ dim: Int) -> [Double] {
        var q = [Double](repeating: 0, count: dim * dim)

        for j in 0..<dim {
            // Copy column j of g into a working vector.
            for i in 0..<dim { q[i * dim + j] = g[i * dim + j] }

            // Subtract projections onto already-processed columns.
            for k in 0..<j {
                var dot = 0.0
                for i in 0..<dim { dot += q[i * dim + j] * q[i * dim + k] }
                for i in 0..<dim { q[i * dim + j] -= dot * q[i * dim + k] }
            }

            // Normalise column j.
            var norm = 0.0
            for i in 0..<dim { norm += q[i * dim + j] * q[i * dim + j] }
            norm = norm.squareRoot()
            if norm < 1e-15 {
                fatalError("Gram-Schmidt produced a near-zero column at j=\(j) (dim=\(dim)). " +
                    "This is statistically impossible for a Gaussian matrix; check the RNG seed.")
            }
            let inv = 1.0 / norm
            for i in 0..<dim { q[i * dim + j] *= inv }
        }
        return q
    }

    private static func signCorrectColumns(_ q: inout [Double], _ dim: Int) {
        for j in 0..<dim {
            // Diagonal-based sign convention: ensure q[j,j] >= 0.
            let diag = q[j * dim + j]
            if diag < 0.0 {
                for i in 0..<dim { q[i * dim + j] = -q[i * dim + j] }
            }
        }
    }
}

/// Thread-safe per-dimension rotation-matrix cache (mirrors the C#
/// ConcurrentDictionary). Returns the same array instance for a given dim.
final class RotationCache: @unchecked Sendable {
    private let lock = NSLock()
    private var store: [Int: [Float]] = [:]

    func getOrAdd(_ dim: Int, _ make: () -> [Float]) -> [Float] {
        lock.lock()
        if let existing = store[dim] { lock.unlock(); return existing }
        lock.unlock()
        // Compute outside the lock (deterministic; a duplicate compute is harmless).
        let built = make()
        lock.lock()
        if let existing = store[dim] { lock.unlock(); return existing }
        store[dim] = built
        lock.unlock()
        return built
    }
}

// MARK: - BetaLloydMaxCodebook — CircleAI.Core.Compression.BetaLloydMaxCodebook

/// A Lloyd-Max codebook for Beta((d-1)/2,(d-1)/2) on [-1, 1].
/// `boundaries` has length 2^bits-1; `centroids` has length 2^bits.
public struct BetaCodebook: Sendable {
    public let boundaries: [Float]
    public let centroids: [Float]

    public init(boundaries: [Float], centroids: [Float]) {
        self.boundaries = boundaries
        self.centroids = centroids
    }
}

/// Computes / caches Lloyd-Max codebooks for Beta((d-1)/2,(d-1)/2).
public enum BetaLloydMaxCodebook {
    private static let cache = CodebookCache()

    /// Returns the codebook for the given bit width and dimension, computing it
    /// on first request. Cached by (bits, dim).
    public static func get(bits: Int, dim: Int) -> BetaCodebook {
        precondition(bits >= 1 && bits <= 8, "bits must be in 1..8.")
        precondition(dim > 1, "dim must be > 1.")
        return cache.getOrAdd(bits: bits, dim: dim) { computeCodebook(bits: bits, dim: dim) }
    }

    /// Returns the bin index for `value` against `boundaries` (linear scan).
    public static func binFor(_ value: Float, _ boundaries: [Float]) -> UInt16 {
        for i in 0..<boundaries.count {
            if value < boundaries[i] { return UInt16(i) }
        }
        return UInt16(boundaries.count)
    }

    // ── Lloyd-Max iteration ──────────────────────────────────────────────

    private static func computeCodebook(bits: Int, dim: Int, maxIter: Int = 200, tol: Double = 1e-12) -> BetaCodebook {
        let a = (Double(dim) - 1.0) / 2.0
        let nLevels = 1 << bits

        // Initial centroids: evenly spaced across ±3σ of the Beta-on-[-1,1].
        let std = (2.0 * a / ((2.0 * a + 1.0) * 4.0 * a)).squareRoot()
        let spread = 3.0 * std
        var centroids = [Double](repeating: 0, count: nLevels)
        for i in 0..<nLevels {
            centroids[i] = -spread + 2.0 * spread * Double(i) / Double(nLevels - 1)
        }

        for _ in 0..<maxIter {
            // Boundaries = midpoints between adjacent centroids.
            var boundaries = [Double](repeating: 0, count: nLevels - 1)
            for i in 0..<(nLevels - 1) { boundaries[i] = (centroids[i] + centroids[i + 1]) / 2.0 }

            var edges = [Double](repeating: 0, count: nLevels + 1)
            edges[0] = -1.0
            for i in 0..<boundaries.count { edges[i + 1] = boundaries[i] }
            edges[nLevels] = 1.0

            var newCentroids = [Double](repeating: 0, count: nLevels)
            for i in 0..<nLevels {
                let lo = edges[i]
                let hi = edges[i + 1]
                let cdfLo = betaCdfSymmetric(a, (lo + 1.0) / 2.0)
                let cdfHi = betaCdfSymmetric(a, (hi + 1.0) / 2.0)
                let prob = cdfHi - cdfLo

                if prob < 1e-15 {
                    newCentroids[i] = centroids[i]
                } else {
                    let mean = adaptiveSimpson(
                        { x in x * betaPdfSymmetric(a, (x + 1.0) / 2.0) / 2.0 },
                        lo, hi, 1e-14, 50)
                    newCentroids[i] = mean / prob
                }
            }

            var maxChange = 0.0
            for i in 0..<nLevels { maxChange = max(maxChange, abs(centroids[i] - newCentroids[i])) }
            centroids = newCentroids

            if maxChange < tol { break }
        }

        var finalBoundaries = [Float](repeating: 0, count: nLevels - 1)
        for i in 0..<(nLevels - 1) { finalBoundaries[i] = Float((centroids[i] + centroids[i + 1]) / 2.0) }
        var finalCentroids = [Float](repeating: 0, count: nLevels)
        for i in 0..<nLevels { finalCentroids[i] = Float(centroids[i]) }
        return BetaCodebook(boundaries: finalBoundaries, centroids: finalCentroids)
    }

    // ── Beta(a, a) PDF / CDF on [0, 1] ─────────────────────────────────────

    private static func betaPdfSymmetric(_ a: Double, _ x: Double) -> Double {
        if x <= 0.0 || x >= 1.0 { return 0.0 }
        // f(x) = x^(a-1) * (1-x)^(a-1) / B(a, a); log-space for stability at large a.
        let logPdf = (a - 1.0) * Foundation.log(x)
                   + (a - 1.0) * Foundation.log(1.0 - x)
                   - logBeta(a, a)
        return Foundation.exp(logPdf)
    }

    private static func betaCdfSymmetric(_ a: Double, _ x: Double) -> Double {
        if x <= 0.0 { return 0.0 }
        if x >= 1.0 { return 1.0 }
        return regularizedIncompleteBeta(a, a, x)
    }

    private static func logBeta(_ a: Double, _ b: Double) -> Double {
        logGamma(a) + logGamma(b) - logGamma(a + b)
    }

    // Lanczos coefficients for g = 7.
    private static let lanczosG7: [Double] = [
        0.99999999999980993, 676.5203681218851, -1259.1392167224028,
        771.32342877765313, -176.61502916214059, 12.507343278686905,
        -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
    ]

    /// log Γ(x) for x > 0 via the Lanczos approximation (g = 7, n = 9).
    private static func logGamma(_ x: Double) -> Double {
        if x < 0.5 {
            // Reflection: Γ(x)Γ(1-x) = π/sin(πx)
            return Foundation.log(Double.pi / Foundation.sin(Double.pi * x)) - logGamma(1.0 - x)
        }
        let xm = x - 1.0
        let t = xm + 7.5
        var sum = lanczosG7[0]
        for i in 1..<lanczosG7.count { sum += lanczosG7[i] / (xm + Double(i)) }
        return 0.5 * Foundation.log(2.0 * Double.pi) + (xm + 0.5) * Foundation.log(t) - t + Foundation.log(sum)
    }

    /// Regularised incomplete beta function I_x(a, b) (Numerical Recipes 6.4).
    private static func regularizedIncompleteBeta(_ a: Double, _ b: Double, _ x: Double) -> Double {
        precondition(x >= 0.0 && x <= 1.0, "x must be in [0, 1].")
        if x == 0.0 || x == 1.0 { return x }

        let bt = Foundation.exp(
            logGamma(a + b) - logGamma(a) - logGamma(b)
            + a * Foundation.log(x) + b * Foundation.log(1.0 - x))
        if x < (a + 1.0) / (a + b + 2.0) {
            return bt * betaContinuedFraction(a, b, x) / a
        }
        return 1.0 - bt * betaContinuedFraction(b, a, 1.0 - x) / b
    }

    private static func betaContinuedFraction(_ a: Double, _ b: Double, _ x: Double) -> Double {
        let maxIter = 200
        let eps = 3e-15
        let fpmin = 1e-300

        let qab = a + b
        let qap = a + 1.0
        let qam = a - 1.0
        var c = 1.0
        var d = 1.0 - qab * x / qap
        if abs(d) < fpmin { d = fpmin }
        d = 1.0 / d
        var h = d

        for m in 1...maxIter {
            let m2 = 2 * m
            var aa = Double(m) * (b - Double(m)) * x / ((qam + Double(m2)) * (a + Double(m2)))
            d = 1.0 + aa * d
            if abs(d) < fpmin { d = fpmin }
            c = 1.0 + aa / c
            if abs(c) < fpmin { c = fpmin }
            d = 1.0 / d
            h *= d * c

            aa = -(a + Double(m)) * (qab + Double(m)) * x / ((a + Double(m2)) * (qap + Double(m2)))
            d = 1.0 + aa * d
            if abs(d) < fpmin { d = fpmin }
            c = 1.0 + aa / c
            if abs(c) < fpmin { c = fpmin }
            d = 1.0 / d
            let delta = d * c
            h *= delta
            if abs(delta - 1.0) < eps { return h }
        }
        return h // best effort if no convergence
    }

    // ── Adaptive Simpson integration ───────────────────────────────────────

    private static func adaptiveSimpson(
        _ f: (Double) -> Double, _ a: Double, _ b: Double, _ tol: Double, _ maxDepth: Int
    ) -> Double {
        let mid = (a + b) / 2.0
        let fa = f(a), fb = f(b), fm = f(mid)
        let whole = (b - a) / 6.0 * (fa + 4.0 * fm + fb)
        return adaptiveSimpsonRec(f, a, b, fa, fb, fm, whole, tol, maxDepth)
    }

    private static func adaptiveSimpsonRec(
        _ f: (Double) -> Double, _ a: Double, _ b: Double,
        _ fa: Double, _ fb: Double, _ fm: Double,
        _ whole: Double, _ tol: Double, _ depth: Int
    ) -> Double {
        let mid = (a + b) / 2.0
        let m1 = (a + mid) / 2.0
        let m2 = (mid + b) / 2.0
        let fm1 = f(m1), fm2 = f(m2)
        let left = (mid - a) / 6.0 * (fa + 4.0 * fm1 + fm)
        let right = (b - mid) / 6.0 * (fm + 4.0 * fm2 + fb)
        let refined = left + right

        if depth == 0 || abs(refined - whole) < 15.0 * tol {
            return refined + (refined - whole) / 15.0
        }
        return adaptiveSimpsonRec(f, a, mid, fa, fm, fm1, left, tol / 2.0, depth - 1)
             + adaptiveSimpsonRec(f, mid, b, fm, fb, fm2, right, tol / 2.0, depth - 1)
    }
}

/// Thread-safe (bits, dim) codebook cache (mirrors the C# ConcurrentDictionary).
final class CodebookCache: @unchecked Sendable {
    private let lock = NSLock()
    private var store: [Int: BetaCodebook] = [:]

    // Pack (bits, dim) into a single key. bits is 1..8, dim fits comfortably.
    private func key(_ bits: Int, _ dim: Int) -> Int { (dim << 8) | bits }

    func getOrAdd(bits: Int, dim: Int, _ make: () -> BetaCodebook) -> BetaCodebook {
        let k = key(bits, dim)
        lock.lock()
        if let existing = store[k] { lock.unlock(); return existing }
        lock.unlock()
        let built = make()
        lock.lock()
        if let existing = store[k] { lock.unlock(); return existing }
        store[k] = built
        lock.unlock()
        return built
    }
}

// MARK: - TurboQuantCodec — CircleAI.Core.Compression.TurboQuantCodec

/// Output of `TurboQuantCodec.encode`.
/// - `norm`: L2 norm of the original vector — needed to reconstruct magnitude.
/// - `packedIndices`: bit-packed Lloyd-Max bin indices, one per dimension.
public struct TurboQuantPayload: Sendable {
    public let norm: Float
    public let packedIndices: [UInt8]

    public init(norm: Float, packedIndices: [UInt8]) {
        self.norm = norm
        self.packedIndices = packedIndices
    }
}

/// TurboQuant encoder / decoder.
public enum TurboQuantCodec {
    /// Encodes a float vector at `bitsPerDim` bits per dimension. Higher bits =
    /// better fidelity, larger payload. Typical: 2 bits (16×), 3 bits (~10×).
    public static func encode(_ vector: [Float], bitsPerDim: Int) -> TurboQuantPayload {
        precondition(vector.count > 1, "Vector must have length > 1.")
        precondition(bitsPerDim >= 1 && bitsPerDim <= 8, "bitsPerDim must be 1..8.")

        let dim = vector.count

        // 1. Norm — accumulate in Double, then narrow to Float (matches C#).
        var sumSq = 0.0
        for i in 0..<dim { sumSq += Double(vector[i]) * Double(vector[i]) }
        let norm = Float(sumSq.squareRoot())

        // Edge case — zero vector. Round-trip preserves the all-zero shape.
        if norm < 1e-20 {
            let allZeros = [UInt8](repeating: 0, count: (dim * bitsPerDim + 7) / 8)
            return TurboQuantPayload(norm: 0, packedIndices: allZeros)
        }

        // 2. Unit-normalise (FP32).
        var unit = [Float](repeating: 0, count: dim)
        let invNorm = Float(1) / norm
        for i in 0..<dim { unit[i] = vector[i] * invNorm }

        // 3. Rotate.
        var rotated = [Float](repeating: 0, count: dim)
        OrthogonalRotation.rotate(dim, unit, &rotated)

        // 4. Quantize per-coordinate.
        let codebook = BetaLloydMaxCodebook.get(bits: bitsPerDim, dim: dim)
        var indices = [UInt16](repeating: 0, count: dim)
        for i in 0..<dim { indices[i] = BetaLloydMaxCodebook.binFor(rotated[i], codebook.boundaries) }

        // 5. Pack.
        let packed = BitPacker.pack(indices, bitsPerIndex: bitsPerDim)
        return TurboQuantPayload(norm: norm, packedIndices: packed)
    }

    /// Decodes a TurboQuant payload back into the original-magnitude vector
    /// (modulo quantization error).
    public static func decode(_ payload: TurboQuantPayload, dim: Int, bitsPerDim: Int) -> [Float] {
        precondition(dim > 1, "dim must be > 1")
        precondition(bitsPerDim >= 1 && bitsPerDim <= 8, "bitsPerDim must be 1..8")

        var result = [Float](repeating: 0, count: dim)
        if payload.norm == 0 { return result } // all zeros

        // 1. Unpack indices.
        let indices = BitPacker.unpack(payload.packedIndices, count: dim, bitsPerIndex: bitsPerDim)

        // 2. Map indices → centroids (rotated-space reconstruction).
        var rotated = [Float](repeating: 0, count: dim)
        let centroids = BetaLloydMaxCodebook.get(bits: bitsPerDim, dim: dim).centroids
        for i in 0..<dim { rotated[i] = centroids[Int(indices[i])] }

        // 3. Inverse rotation.
        var unit = [Float](repeating: 0, count: dim)
        OrthogonalRotation.unrotate(dim, rotated, &unit)

        // 4. Scale by stored norm.
        let scale = payload.norm
        for i in 0..<dim { result[i] = unit[i] * scale }
        return result
    }

    /// Convenience: encode then decode, returning the reconstruction.
    public static func roundTrip(_ vector: [Float], bitsPerDim: Int) -> [Float] {
        let encoded = encode(vector, bitsPerDim: bitsPerDim)
        return decode(encoded, dim: vector.count, bitsPerDim: bitsPerDim)
    }

    /// Bytes-per-vector required at the given dim and bitsPerDim (excluding the
    /// 4-byte norm header).
    public static func payloadByteCount(dim: Int, bitsPerDim: Int) -> Int {
        (dim * bitsPerDim + 7) / 8
    }

    /// Compression ratio vs raw FP32 (vector bytes / encoded bytes incl. norm).
    public static func compressionRatio(dim: Int, bitsPerDim: Int) -> Double {
        let raw = dim * 4
        let encoded = payloadByteCount(dim: dim, bitsPerDim: bitsPerDim) + 4 /* norm */
        return Double(raw) / Double(encoded)
    }
}

// MARK: - EmbeddingPayloadCodec — CircleAI.Memory.Compression.EmbeddingPayloadCodec
//
// Wire format (binary):
//   bytes [0..3]   = magic "TQ3\1" (0x54 0x51 0x33 0x01)
//   bytes [4..7]   = bit-width as uint32 little-endian
//   bytes [8..11]  = dimension as uint32 little-endian
//   bytes [12..15] = norm as float32 little-endian
//   bytes [16..]   = packed indices
// Base64-encoded for tag storage. Bit-width + dim are embedded so callers can
// decode without out-of-band metadata.

/// Errors raised by the embedding payload codec.
public enum EmbeddingPayloadError: Error, Sendable {
    /// The payload is shorter than the minimum header.
    case tooShort
    /// The magic header is missing — not a TurboQuant payload.
    case magicMissing
    /// The base64 string could not be decoded.
    case invalidBase64
}

/// Encodes and decodes TurboQuant-compressed embeddings as binary blobs suitable
/// for persistence (e.g. in a tag value).
public enum EmbeddingPayloadCodec {
    /// Magic header bytes that identify a TurboQuant-encoded blob ("TQ3\1").
    public static let magic: [UInt8] = [0x54, 0x51, 0x33, 0x01]

    /// Encodes `vector` at `bitsPerDim` bits per coordinate into a self-describing
    /// byte payload.
    public static func encode(_ vector: [Float], bitsPerDim: Int) -> [UInt8] {
        precondition(vector.count > 1, "Vector must have length > 1.")

        let payload = TurboQuantCodec.encode(vector, bitsPerDim: bitsPerDim)
        var buf = [UInt8]()
        buf.reserveCapacity(magic.count + 4 + 4 + 4 + payload.packedIndices.count)
        buf.append(contentsOf: magic)
        appendUInt32LE(&buf, UInt32(bitsPerDim))
        appendUInt32LE(&buf, UInt32(vector.count))
        appendFloat32LE(&buf, payload.norm)
        buf.append(contentsOf: payload.packedIndices)
        return buf
    }

    /// Decodes a byte payload produced by `encode` back into a float array.
    public static func decode(_ bytes: [UInt8]) throws -> [Float] {
        if bytes.count < magic.count + 12 { throw EmbeddingPayloadError.tooShort }
        if !hasMagic(bytes) { throw EmbeddingPayloadError.magicMissing }

        var o = magic.count
        let bitsPerDim = Int(readUInt32LE(bytes, o)); o += 4
        let dim = Int(readUInt32LE(bytes, o)); o += 4
        let norm = readFloat32LE(bytes, o); o += 4
        let packed = Array(bytes[o...])
        let payload = TurboQuantPayload(norm: norm, packedIndices: packed)
        return TurboQuantCodec.decode(payload, dim: dim, bitsPerDim: bitsPerDim)
    }

    /// True when the byte span begins with the TurboQuant magic header.
    public static func isEncoded(_ bytes: [UInt8]) -> Bool {
        bytes.count >= magic.count && hasMagic(bytes)
    }

    /// Convenience: encode + base64-stringify for tag-style storage.
    public static func encodeBase64(_ vector: [Float], bitsPerDim: Int) -> String {
        Data(encode(vector, bitsPerDim: bitsPerDim)).base64EncodedString()
    }

    /// Convenience: base64-decode + decode.
    public static func decodeBase64(_ base64: String) throws -> [Float] {
        guard let data = Data(base64Encoded: base64) else { throw EmbeddingPayloadError.invalidBase64 }
        return try decode([UInt8](data))
    }

    // ── LE byte helpers (explicit; independent of host endianness) ─────────

    private static func hasMagic(_ bytes: [UInt8]) -> Bool {
        bytes[0] == magic[0] && bytes[1] == magic[1] && bytes[2] == magic[2] && bytes[3] == magic[3]
    }

    private static func appendUInt32LE(_ buf: inout [UInt8], _ value: UInt32) {
        buf.append(UInt8(value & 0xFF))
        buf.append(UInt8((value >> 8) & 0xFF))
        buf.append(UInt8((value >> 16) & 0xFF))
        buf.append(UInt8((value >> 24) & 0xFF))
    }

    private static func appendFloat32LE(_ buf: inout [UInt8], _ value: Float) {
        // Float.bitPattern gives the IEEE-754 FP32 bit representation.
        appendUInt32LE(&buf, value.bitPattern)
    }

    private static func readUInt32LE(_ bytes: [UInt8], _ o: Int) -> UInt32 {
        UInt32(bytes[o])
            | (UInt32(bytes[o + 1]) << 8)
            | (UInt32(bytes[o + 2]) << 16)
            | (UInt32(bytes[o + 3]) << 24)
    }

    private static func readFloat32LE(_ bytes: [UInt8], _ o: Int) -> Float {
        Float(bitPattern: readUInt32LE(bytes, o))
    }
}

// MARK: - Shared cosine + tag key

/// Full cosine (with magnitudes) matching the C# stores' CosineSimilarity.Score.
/// Reuses the module-level `cosineFull` (defined in Consolidation.swift), which
/// accumulates in Double and returns Double.
func compressedCosine(_ a: [Float], _ b: [Float]) -> Double {
    cosineFull(a, b)
}

/// Tag key under which the compressed embedding is stored.
public let compressedTagKey = "x-tq-embedding"

// MARK: - CompressedEpisodicMemoryStore — CircleAI.Memory.Compression

/// Wraps any `IEpisodicMemoryStore` and stores its embeddings in
/// TurboQuant-compressed form. Default 2 bits per dim (~16× shrink).
///
/// The inner store sees `embedding = nil`; the compressed base64 payload lives in
/// the entry's tags under `compressedTagKey`. Reads rehydrate the embedding by
/// decoding the tag, and search rebuilds embeddings on the read path so cosine
/// ranking works against the reconstructed vectors.
public final class CompressedEpisodicMemoryStore: IEpisodicMemoryStore, @unchecked Sendable {
    /// Tag key under which the compressed embedding is stored.
    public static let compressedTagKey = "x-tq-embedding"

    private let inner: IEpisodicMemoryStore
    private let bitsPerDim: Int

    public init(inner: IEpisodicMemoryStore, bitsPerDim: Int = 2) {
        precondition(bitsPerDim >= 1 && bitsPerDim <= 8, "bitsPerDim must be 1..8")
        self.inner = inner
        self.bitsPerDim = bitsPerDim
    }

    public func add(_ entry: EpisodicMemoryEntry) async throws {
        var rewritten = entry
        if let emb = entry.embedding, emb.count > 1 {
            var tags = entry.tags ?? [:]
            tags[CompressedEpisodicMemoryStore.compressedTagKey] =
                EmbeddingPayloadCodec.encodeBase64(emb, bitsPerDim: bitsPerDim)
            rewritten.embedding = nil // dropped — lives in tags
            rewritten.tags = tags
        }
        try await inner.add(rewritten)
    }

    public func search(queryEmbedding: [Float]?, topK: Int) async throws -> [EpisodicMemoryEntry] {
        // The inner store sees embedding = nil on every entry, so we cannot defer
        // to its cosine ranking. Load recent, rehydrate, then rank here.
        let all = try await inner.getRecent(count: Int.max)
        let rehydrated = all.map { CompressedEpisodicMemoryStore.rehydrate($0) }

        guard let qe = queryEmbedding else {
            return Array(rehydrated.prefix(topK))
        }

        let ranked = rehydrated
            .filter { ($0.embedding?.isEmpty == false) }
            .map { (entry: $0, score: compressedCosine(qe, $0.embedding!)) }
            .sorted { $0.score > $1.score }
        return Array(ranked.prefix(topK).map { $0.entry })
    }

    public func getRecent(count: Int) async throws -> [EpisodicMemoryEntry] {
        let recent = try await inner.getRecent(count: count)
        return recent.map { CompressedEpisodicMemoryStore.rehydrate($0) }
    }

    public func count() async throws -> Int {
        try await inner.count()
    }

    public func pruneOlderThan(cutoff: Date) async throws -> Int {
        try await inner.pruneOlderThan(cutoff: cutoff)
    }

    static func rehydrate(_ e: EpisodicMemoryEntry) -> EpisodicMemoryEntry {
        if let emb = e.embedding, !emb.isEmpty { return e } // never compressed
        guard let b64 = e.tags?[compressedTagKey] else { return e }
        do {
            let floats = try EmbeddingPayloadCodec.decodeBase64(b64)
            var copy = e
            copy.embedding = floats
            return copy
        } catch {
            // Malformed tag — return entry as-is so the caller can still see it.
            return e
        }
    }
}

// MARK: - CompressedMultimodalMemoryStore — CircleAI.Memory.Compression

/// Wraps any `IMultimodalMemoryStore` and stores its embeddings in
/// TurboQuant-compressed form. Same wire format + tag key as the episodic
/// decorator.
public final class CompressedMultimodalMemoryStore: IMultimodalMemoryStore, @unchecked Sendable {
    /// Tag key under which the compressed embedding is stored.
    public static let compressedTagKey = "x-tq-embedding"

    private let inner: IMultimodalMemoryStore
    private let bitsPerDim: Int

    public init(inner: IMultimodalMemoryStore, bitsPerDim: Int = 2) {
        precondition(bitsPerDim >= 1 && bitsPerDim <= 8, "bitsPerDim must be 1..8")
        self.inner = inner
        self.bitsPerDim = bitsPerDim
    }

    public func add(_ entry: MultimodalMemoryEntry) async throws {
        var rewritten = entry
        if let emb = entry.embedding, emb.count > 1 {
            var tags = entry.tags ?? [:]
            tags[CompressedMultimodalMemoryStore.compressedTagKey] =
                EmbeddingPayloadCodec.encodeBase64(emb, bitsPerDim: bitsPerDim)
            rewritten.embedding = nil
            rewritten.tags = tags
        }
        try await inner.add(rewritten)
    }

    public func getByHash(_ sourceSha256: String) async throws -> MultimodalMemoryEntry? {
        guard let got = try await inner.getByHash(sourceSha256) else { return nil }
        return CompressedMultimodalMemoryStore.rehydrate(got)
    }

    public func reinforce(_ sourceSha256: String) async throws {
        try await inner.reinforce(sourceSha256)
    }

    public func search(queryEmbedding: [Float]?, topK: Int) async throws -> [MultimodalMemoryEntry] {
        let all = try await inner.getRecent(count: Int.max)
        let rehydrated = all.map { CompressedMultimodalMemoryStore.rehydrate($0) }

        guard let qe = queryEmbedding else {
            return Array(rehydrated.prefix(topK))
        }

        let ranked = rehydrated
            .filter { ($0.embedding?.isEmpty == false) }
            .map { (entry: $0, score: compressedCosine(qe, $0.embedding!)) }
            .sorted { $0.score > $1.score }
        return Array(ranked.prefix(topK).map { $0.entry })
    }

    public func getRecent(count: Int) async throws -> [MultimodalMemoryEntry] {
        let recent = try await inner.getRecent(count: count)
        return recent.map { CompressedMultimodalMemoryStore.rehydrate($0) }
    }

    public func pruneOlderThan(cutoff: Date) async throws -> Int {
        try await inner.pruneOlderThan(cutoff: cutoff)
    }

    public func count() async throws -> Int {
        try await inner.count()
    }

    static func rehydrate(_ e: MultimodalMemoryEntry) -> MultimodalMemoryEntry {
        if let emb = e.embedding, !emb.isEmpty { return e }
        guard let b64 = e.tags?[compressedTagKey] else { return e }
        do {
            let floats = try EmbeddingPayloadCodec.decodeBase64(b64)
            var copy = e
            copy.embedding = floats
            return copy
        } catch {
            return e
        }
    }
}
