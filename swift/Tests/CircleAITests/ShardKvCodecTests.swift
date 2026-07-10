// ShardKvCodecTests.swift
//
// Exercises ShardKvCodec (port of CircleAI.Core.Compression.ShardKvCodec) plus
// the DotNetRandom PRNG it seeds the V codebook with. The encoded frame is a
// cross-language wire format, so the byte layout (float32-LE scale + int8
// components; LE codeword index) and the codebook PRNG are pinned against
// ground-truth captured from .NET's System.Random(0).

import XCTest
@testable import CircleAI

final class ShardKvCodecTests: XCTestCase {

    // ── DotNetRandom parity ───────────────────────────────────────────────

    func testDotNetRandomSeed0MatchesGroundTruth() {
        // Ground-truth from .NET `new Random(0)`.NextDouble() ×8.
        let expected: [Double] = [
            0.7262432699679598,
            0.8173253595909687,
            0.7680226893946634,
            0.5581611914365372,
            0.2060331540210327,
            0.5588847946184151,
            0.9060270660119257,
            0.44217787331071584,
        ]
        var rng = DotNetRandom(seed: 0)
        for e in expected {
            XCTAssertEqual(rng.nextDouble(), e, accuracy: 1e-15)
        }
    }

    func testSeedCodebookDeterministicAndInRange() {
        let cb = ShardKvCodec.seedCodebook(dim: 4, count: 4, seed: 0)
        XCTAssertEqual(cb.count, 4)
        XCTAssertEqual(cb[0].count, 4)
        // First six generated floats (2*NextDouble-1) as Float32, ground-truth.
        let expectFirstSix: [Float] = [
            0.4524865448474884,
            0.634650707244873,
            0.5360453724861145,
            0.11632238328456879,
            -0.5879337191581726,
            0.11776959151029587,
        ]
        let flat = cb[0] + cb[1]  // first 8 floats span these 6 and more
        for i in 0..<6 {
            XCTAssertEqual(flat[i], expectFirstSix[i], accuracy: 1e-6)
        }
        for word in cb { for x in word { XCTAssert(x >= -1 && x <= 1) } }

        // Re-seeding yields identical codebook.
        let cb2 = ShardKvCodec.seedCodebook(dim: 4, count: 4, seed: 0)
        XCTAssertEqual(cb, cb2)
    }

    // ── Construction guards ───────────────────────────────────────────────

    func testConstructorRejectsBadArgs() {
        XCTAssertThrowsError(try ShardKvCodec(kDim: 0, kRank: 1, vDim: 2, vCodewords: 2))
        XCTAssertThrowsError(try ShardKvCodec(kDim: 4, kRank: 5, vDim: 2, vCodewords: 2)) // kRank > kDim
        XCTAssertThrowsError(try ShardKvCodec(kDim: 4, kRank: 2, vDim: 0, vCodewords: 2))
        XCTAssertThrowsError(try ShardKvCodec(kDim: 4, kRank: 2, vDim: 2, vCodewords: 3)) // not power of two
        XCTAssertThrowsError(try ShardKvCodec(kDim: 4, kRank: 2, vDim: 2, vCodewords: 1)) // must be > 1
        XCTAssertNoThrow(try ShardKvCodec(kDim: 8, kRank: 4, vDim: 4, vCodewords: 16))
    }

    // ── Wire format ───────────────────────────────────────────────────────

    func testEncodeWireLayout() throws {
        let codec = try ShardKvCodec(kDim: 8, kRank: 4, vDim: 4, vCodewords: 16)
        let k: [Float] = [0.5, -0.25, 0.1, 0.9, -0.4, 0.3, 0.0, 0.7]
        let v: [Float] = [0.2, -0.3, 0.4, -0.1]
        let frame = try codec.encode(k: k, v: v)

        // CompressedK = 4-byte scale + kRank int8 components.
        XCTAssertEqual(frame.compressedK.count, 4 + 4)
        // Scale reads back as a positive float32-LE.
        let scaleBits = UInt32(frame.compressedK[0])
            | (UInt32(frame.compressedK[1]) << 8)
            | (UInt32(frame.compressedK[2]) << 16)
            | (UInt32(frame.compressedK[3]) << 24)
        let scale = Float(bitPattern: scaleBits)
        XCTAssert(scale > 0)

        // vCodewords = 16 <= 256, so index is a single byte in [0, 16).
        XCTAssertEqual(frame.compressedV.count, 1)
        XCTAssert(frame.compressedV[0] < 16)

        // Axes materialised: kRank * kDim floats.
        XCTAssertEqual(frame.kPrincipalAxes.count, 4 * 8)
        XCTAssertEqual(frame.kOriginalDim, 8)
        XCTAssertEqual(frame.vOriginalDim, 4)
    }

    func testIndexWidthGrowsWithCodebook() throws {
        // 256 codewords still fits in 1 byte (<= 256 branch).
        let c256 = try ShardKvCodec(kDim: 4, kRank: 2, vDim: 2, vCodewords: 256)
        let f256 = try c256.encode(k: [0.1, 0.2, 0.3, 0.4], v: [0.5, 0.6])
        XCTAssertEqual(f256.compressedV.count, 1)

        // 512 codewords → 2-byte index.
        let c512 = try ShardKvCodec(kDim: 4, kRank: 2, vDim: 2, vCodewords: 512)
        let f512 = try c512.encode(k: [0.1, 0.2, 0.3, 0.4], v: [0.5, 0.6])
        XCTAssertEqual(f512.compressedV.count, 2)
    }

    // ── Round-trip ────────────────────────────────────────────────────────

    func testEncodeDecodeRoundTripApproximatesV() throws {
        let codec = try ShardKvCodec(kDim: 8, kRank: 8, vDim: 4, vCodewords: 256)
        // Set the V codebook to contain the exact target so VQ picks it back.
        var cb = ShardKvCodec.seedCodebook(dim: 4, count: 256, seed: 7)
        let target: [Float] = [0.11, -0.22, 0.33, -0.44]
        cb[100] = target
        try codec.setVCodebook(cb)

        let k: [Float] = [0.5, -0.25, 0.1, 0.9, -0.4, 0.3, 0.0, 0.7]
        let frame = try codec.encode(k: k, v: target)
        let (dk, dv) = try codec.decode(frame)

        XCTAssertEqual(dk.count, 8)
        // V decodes exactly to the chosen codeword.
        for i in 0..<4 { XCTAssertEqual(dv[i], target[i], accuracy: 1e-6) }
    }

    func testDecodeRejectsDimMismatch() throws {
        let codec = try ShardKvCodec(kDim: 8, kRank: 4, vDim: 4, vCodewords: 16)
        let frame = try codec.encode(
            k: [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8], v: [0.1, 0.2, 0.3, 0.4])
        // A different codec with mismatched dims must reject the frame.
        let other = try ShardKvCodec(kDim: 4, kRank: 2, vDim: 4, vCodewords: 16)
        XCTAssertThrowsError(try other.decode(frame))
    }

    // ── Online mean (observeK) ────────────────────────────────────────────

    func testObserveKUpdatesRunningMean() throws {
        let codec = try ShardKvCodec(kDim: 2, kRank: 2, vDim: 2, vCodewords: 4)
        XCTAssertEqual(codec.samplesObserved, 0)
        try codec.observeK([2, 4])
        try codec.observeK([4, 8])
        XCTAssertEqual(codec.samplesObserved, 2)
        // Encoding centres by the mean [3, 6]; feeding the mean back gives a
        // (near) zero projection so the scale collapses to its 1e-9 floor.
        let frame = try codec.encode(k: [3, 6], v: [0, 0])
        let scaleBits = UInt32(frame.compressedK[0])
            | (UInt32(frame.compressedK[1]) << 8)
            | (UInt32(frame.compressedK[2]) << 16)
            | (UInt32(frame.compressedK[3]) << 24)
        let scale = Float(bitPattern: scaleBits)
        XCTAssert(scale <= 1e-9 + 1e-12)
    }

    func testEncodeRejectsWrongInputDim() throws {
        let codec = try ShardKvCodec(kDim: 4, kRank: 2, vDim: 2, vCodewords: 4)
        XCTAssertThrowsError(try codec.encode(k: [1, 2, 3], v: [1, 2]))
        XCTAssertThrowsError(try codec.encode(k: [1, 2, 3, 4], v: [1, 2, 3]))
        XCTAssertThrowsError(try codec.observeK([1, 2, 3]))
    }
}
