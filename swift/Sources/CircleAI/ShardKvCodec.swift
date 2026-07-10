// ShardKvCodec.swift
//
// Port of CircleAI.Core.Compression.ShardKvCodec (C#).
//
// (3.3.0) Shard-style KV cache compression. Compress K via per-layer online
// PCA + Hadamard rotation, and compress V via product vector quantisation.
// An alternative to the TurboQuant codec — same KV bytes, different math.
//
// WIRE FORMAT — byte-matched to the C# implementation:
//   CompressedK: [0..3]  = scale as float32 little-endian
//                [4..]   = kRank signed-int8 quantised projected components
//   CompressedV: 1, 2, or 4 bytes little-endian codeword index
//                (1 byte if vCodewords <= 256, 2 if <= 65536, else 4)
//   KPrincipalAxes: kRank*kDim row-major float32 axes (materialised per frame)
//
// The V codebook is seeded with a deterministic RNG. To byte-match which
// codeword index C# selects for a given (K, V) pair, the codebook values must
// match C#'s `System.Random(seed)`. `DotNetRandom` below is a faithful port of
// the .NET seeded Random (Knuth subtractive generator), so `SeedCodebook`
// produces byte-identical codebooks to the C# `SeedCodebook`.

import Foundation

// MARK: - DotNetRandom — faithful port of System.Random(int seed)

/// Reproduces `System.Random(int Seed)` from .NET (the Knuth subtractive
/// random number generator). Only the surface used by ShardKvCodec is ported:
/// the seeded constructor and `nextDouble()`. Values are bit-for-bit identical
/// to the .NET reference implementation for the same seed.
struct DotNetRandom {
    private static let MBIG: Int32 = Int32.max          // 2147483647
    private static let MSEED: Int32 = 161803398
    private static let MZ: Int32 = 0

    private var seedArray = [Int32](repeating: 0, count: 56)
    private var inext: Int = 0
    private var inextp: Int = 0

    init(seed: Int32) {
        // subtraction with Int32.min guarded exactly as .NET does.
        let subtractend: Int32 = (seed == Int32.min) ? Int32.max : Int32(abs(Int(seed)))
        var mj = DotNetRandom.MSEED &- subtractend
        seedArray[55] = mj
        var mk: Int32 = 1

        var ii = 0
        for i in 1..<55 {
            ii = (21 &* i) % 55
            seedArray[ii] = mk
            mk = mj &- mk
            if mk < 0 { mk = mk &+ DotNetRandom.MBIG }
            mj = seedArray[ii]
        }

        for _ in 1..<5 {
            for i in 1..<56 {
                seedArray[i] = seedArray[i] &- seedArray[1 + (i + 30) % 55]
                if seedArray[i] < 0 { seedArray[i] = seedArray[i] &+ DotNetRandom.MBIG }
            }
        }

        inext = 0
        inextp = 21
    }

    /// Mirrors .NET `InternalSample()`.
    private mutating func internalSample() -> Int32 {
        var locINext = inext
        var locINextp = inextp
        locINext += 1
        if locINext >= 56 { locINext = 1 }
        locINextp += 1
        if locINextp >= 56 { locINextp = 1 }

        var retVal = seedArray[locINext] &- seedArray[locINextp]
        if retVal == DotNetRandom.MBIG { retVal &-= 1 }
        if retVal < 0 { retVal = retVal &+ DotNetRandom.MBIG }

        seedArray[locINext] = retVal
        inext = locINext
        inextp = locINextp
        return retVal
    }

    /// Mirrors .NET `Sample()` == `NextDouble()`.
    mutating func nextDouble() -> Double {
        return Double(internalSample()) * (1.0 / Double(DotNetRandom.MBIG))
    }
}

// MARK: - ShardCompressedFrame — CircleAI.Core.Compression.ShardCompressedFrame

/// (3.3.0) Encoded shard KV pair (compressed K + compressed V).
public struct ShardCompressedFrame: Sendable, Equatable {
    public let compressedK: [UInt8]
    public let compressedV: [UInt8]
    public let kPrincipalAxes: [Float]
    public let kOriginalDim: Int
    public let vOriginalDim: Int

    public init(
        compressedK: [UInt8],
        compressedV: [UInt8],
        kPrincipalAxes: [Float],
        kOriginalDim: Int,
        vOriginalDim: Int
    ) {
        self.compressedK = compressedK
        self.compressedV = compressedV
        self.kPrincipalAxes = kPrincipalAxes
        self.kOriginalDim = kOriginalDim
        self.vOriginalDim = vOriginalDim
    }
}

// MARK: - ShardKvCodec — CircleAI.Core.Compression.ShardKvCodec

/// (3.3.0) Online-PCA-on-K + VQ-on-V KV compressor.
/// Stateless across frames — the host re-trains the PCA basis with `observeK`
/// when desired, and uses the current basis to encode subsequent frames.
///
/// Confined to a single thread by the caller (matches the C# class which is
/// not internally synchronised); marked `@unchecked Sendable` so it can be
/// stored across `async` boundaries when the caller serialises access.
public final class ShardKvCodec: @unchecked Sendable {
    public enum CodecError: Error, Sendable {
        case argumentOutOfRange(String)
        case dimensionMismatch(String)
    }

    private let kDim: Int
    private let kRank: Int
    private let vDim: Int
    private let vCodewords: Int
    private var vCodebook: [[Float]]
    private var hadamardScratch: [Float]
    private var kCenter: [Float]
    // Row-major (kRank x kDim) PCA axes.
    private var kAxes: [Float]
    private var samples: Int64 = 0

    /// - Parameters:
    ///   - kDim: K-vector dimensionality (e.g. 128 for a typical attention head).
    ///   - kRank: Number of principal components to keep on K (e.g. 32).
    ///   - vDim: V-vector dimensionality.
    ///   - vCodewords: Number of VQ codewords for V (must be a power of 2 > 1).
    ///   - vCodebookSeed: Seed for the deterministic initial codebook.
    public init(kDim: Int, kRank: Int, vDim: Int, vCodewords: Int, vCodebookSeed: Int32 = 0) throws {
        if kDim <= 0 { throw CodecError.argumentOutOfRange("kDim") }
        if kRank <= 0 || kRank > kDim { throw CodecError.argumentOutOfRange("kRank") }
        if vDim <= 0 { throw CodecError.argumentOutOfRange("vDim") }
        if vCodewords <= 1 || (vCodewords & (vCodewords - 1)) != 0 {
            throw CodecError.argumentOutOfRange("vCodewords: must be a power of two greater than 1.")
        }

        self.kDim = kDim
        self.kRank = kRank
        self.vDim = vDim
        self.vCodewords = vCodewords
        self.kCenter = [Float](repeating: 0, count: kDim)
        self.kAxes = [Float](repeating: 0, count: kRank * kDim)
        self.vCodebook = ShardKvCodec.seedCodebook(dim: vDim, count: vCodewords, seed: vCodebookSeed)
        self.hadamardScratch = [Float](repeating: 0, count: ShardKvCodec.pow2Ceil(kDim))

        // Initialise PCA axes to identity-top-rank for sane defaults before training.
        for r in 0..<kRank {
            kAxes[r * kDim + r] = 1
        }
    }

    /// (3.3.0) Number of K samples used to update the PCA centre.
    public var samplesObserved: Int64 { samples }

    /// (3.3.0) Update the online K mean estimate with this sample.
    public func observeK(_ k: [Float]) throws {
        if k.count != kDim { throw CodecError.dimensionMismatch("Input dim mismatch") }
        samples += 1
        let n = Float(samples)
        for i in 0..<kDim {
            // Running mean.
            kCenter[i] += (k[i] - kCenter[i]) / n
        }
    }

    /// (3.3.0) Replace the current PCA axes with `axes` (row-major kRank x kDim).
    public func setPrincipalAxes(_ axes: [Float]) throws {
        if axes.count != kRank * kDim {
            throw CodecError.dimensionMismatch("Axes shape must be (kRank, kDim).")
        }
        kAxes = axes
    }

    /// (3.3.0) Replace the V codebook.
    public func setVCodebook(_ codebook: [[Float]]) throws {
        if codebook.count != vCodewords {
            throw CodecError.dimensionMismatch("Codebook size mismatch.")
        }
        for i in 0..<codebook.count {
            if codebook[i].count != vDim { throw CodecError.dimensionMismatch("Codeword dim mismatch.") }
            vCodebook[i] = codebook[i]
        }
    }

    /// (3.3.0) Encode one (K, V) pair.
    public func encode(k: [Float], v: [Float]) throws -> ShardCompressedFrame {
        if k.count != kDim { throw CodecError.dimensionMismatch("K dim mismatch") }
        if v.count != vDim { throw CodecError.dimensionMismatch("V dim mismatch") }

        // K: centre → Hadamard → project to top-rank principal axes → quantise to int8.
        var centred = [Float](repeating: 0, count: kDim)
        for i in 0..<kDim { centred[i] = k[i] - kCenter[i] }
        applyHadamardInPlace(&centred)

        var projected = [Float](repeating: 0, count: kRank)
        for r in 0..<kRank {
            var dot: Float = 0
            let base = r * kDim
            for i in 0..<kDim { dot += centred[i] * kAxes[base + i] }
            projected[r] = dot
        }

        // Find scale that fits all components into int8 dynamic range.
        var maxAbs: Float = 1e-9
        for r in 0..<kRank { maxAbs = max(maxAbs, abs(projected[r])) }
        let scale = maxAbs / 127

        var encodedK = [UInt8](repeating: 0, count: kRank + 4) // +4 for the scale (float32 LE)
        writeSingleLittleEndian(&encodedK, offset: 0, value: scale)
        for r in 0..<kRank {
            // Match C# exactly: the division is float/float, whose float result
            // is then widened to double for `Math.Round`, which uses banker's
            // rounding (ToEven). Divide in Float, widen, round `.toNearestOrEven`.
            let ratioF: Float = projected[r] / scale
            var q = Int(Double(ratioF).rounded(.toNearestOrEven))
            q = min(127, max(-127, q))
            encodedK[4 + r] = UInt8(bitPattern: Int8(q))
        }

        // V: nearest-codeword VQ → encode index in ⌈log2(codewords)⌉ bits.
        var bestIdx = 0
        var bestDist = Float.greatestFiniteMagnitude
        for c in 0..<vCodewords {
            var d: Float = 0
            let word = vCodebook[c]
            for i in 0..<vDim {
                let diff = v[i] - word[i]
                d += diff * diff
            }
            if d < bestDist { bestDist = d; bestIdx = c }
        }

        // Encode index as little-endian uint (1, 2, or 4 bytes depending on codebook size).
        let idxBytes = vCodewords <= 256 ? 1 : (vCodewords <= 65536 ? 2 : 4)
        var encodedV = [UInt8](repeating: 0, count: idxBytes)
        switch idxBytes {
        case 1: encodedV[0] = UInt8(bestIdx & 0xFF)
        case 2:
            encodedV[0] = UInt8(bestIdx & 0xFF)
            encodedV[1] = UInt8((bestIdx >> 8) & 0xFF)
        default:
            encodedV[0] = UInt8(bestIdx & 0xFF)
            encodedV[1] = UInt8((bestIdx >> 8) & 0xFF)
            encodedV[2] = UInt8((bestIdx >> 16) & 0xFF)
            encodedV[3] = UInt8((bestIdx >> 24) & 0xFF)
        }

        // Materialise the PCA axes once in the frame so the decoder can stand alone.
        var axesFlat = [Float](repeating: 0, count: kRank * kDim)
        for r in 0..<kRank {
            for i in 0..<kDim {
                axesFlat[r * kDim + i] = kAxes[r * kDim + i]
            }
        }
        return ShardCompressedFrame(
            compressedK: encodedK,
            compressedV: encodedV,
            kPrincipalAxes: axesFlat,
            kOriginalDim: kDim,
            vOriginalDim: vDim
        )
    }

    /// (3.3.0) Decode a frame back to approximate K and V.
    public func decode(_ frame: ShardCompressedFrame) throws -> (k: [Float], v: [Float]) {
        if frame.kOriginalDim != kDim { throw CodecError.dimensionMismatch("Codec K-dim does not match frame.") }
        if frame.vOriginalDim != vDim { throw CodecError.dimensionMismatch("Codec V-dim does not match frame.") }

        // K decode: int8 + scale → projected → un-rotate via axes → un-Hadamard → recenter.
        let scale = readSingleLittleEndian(frame.compressedK, offset: 0)
        var projected = [Float](repeating: 0, count: kRank)
        for r in 0..<kRank {
            projected[r] = Float(Int8(bitPattern: frame.compressedK[4 + r])) * scale
        }

        var k = [Float](repeating: 0, count: kDim)
        for i in 0..<kDim {
            var acc: Float = 0
            for r in 0..<kRank {
                acc += projected[r] * frame.kPrincipalAxes[r * kDim + i]
            }
            k[i] = acc
        }
        applyHadamardInPlace(&k) // Hadamard is self-inverse (up to scale 1/n).
        let dimF = Float(kDim)
        for i in 0..<kDim { k[i] = k[i] / dimF + kCenter[i] }

        // V decode: read index, copy codeword.
        let idxBytes = vCodewords <= 256 ? 1 : (vCodewords <= 65536 ? 2 : 4)
        var idx = 0
        switch idxBytes {
        case 1: idx = Int(frame.compressedV[0])
        case 2: idx = Int(frame.compressedV[0]) | (Int(frame.compressedV[1]) << 8)
        default:
            idx = Int(frame.compressedV[0])
                | (Int(frame.compressedV[1]) << 8)
                | (Int(frame.compressedV[2]) << 16)
                | (Int(frame.compressedV[3]) << 24)
        }
        var v = [Float](repeating: 0, count: vDim)
        let word = vCodebook[idx]
        for i in 0..<vDim { v[i] = word[i] }
        return (k, v)
    }

    private func applyHadamardInPlace(_ buffer: inout [Float]) {
        // Fast Walsh-Hadamard transform on the next-power-of-two-sized scratch.
        let n = hadamardScratch.count
        for i in 0..<n { hadamardScratch[i] = 0 }
        let copyCount = min(buffer.count, n)
        for i in 0..<copyCount { hadamardScratch[i] = buffer[i] }

        var h = 1
        while h < n {
            var i = 0
            while i < n {
                for j in i..<(i + h) {
                    let x = hadamardScratch[j]
                    let y = hadamardScratch[j + h]
                    hadamardScratch[j] = x + y
                    hadamardScratch[j + h] = x - y
                }
                i += h * 2
            }
            h <<= 1
        }
        for i in 0..<copyCount { buffer[i] = hadamardScratch[i] }
    }

    private static func pow2Ceil(_ v: Int) -> Int {
        var p = 1
        while p < v { p <<= 1 }
        return p
    }

    static func seedCodebook(dim: Int, count: Int, seed: Int32) -> [[Float]] {
        var rng = DotNetRandom(seed: seed)
        var cb = [[Float]](repeating: [Float](repeating: 0, count: dim), count: count)
        for c in 0..<count {
            var word = [Float](repeating: 0, count: dim)
            for i in 0..<dim {
                word[i] = Float(rng.nextDouble() * 2.0 - 1.0) // uniform [-1, 1]
            }
            cb[c] = word
        }
        return cb
    }
}

// MARK: - little-endian float helpers (bit-exact with BinaryPrimitives)

private func writeSingleLittleEndian(_ buffer: inout [UInt8], offset: Int, value: Float) {
    let bits = value.bitPattern
    buffer[offset]     = UInt8(bits & 0xFF)
    buffer[offset + 1] = UInt8((bits >> 8) & 0xFF)
    buffer[offset + 2] = UInt8((bits >> 16) & 0xFF)
    buffer[offset + 3] = UInt8((bits >> 24) & 0xFF)
}

private func readSingleLittleEndian(_ buffer: [UInt8], offset: Int) -> Float {
    let bits = UInt32(buffer[offset])
        | (UInt32(buffer[offset + 1]) << 8)
        | (UInt32(buffer[offset + 2]) << 16)
        | (UInt32(buffer[offset + 3]) << 24)
    return Float(bitPattern: bits)
}
