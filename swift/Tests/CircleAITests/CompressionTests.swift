// CompressionTests.swift
//
// Exercises the TurboQuant codec + the compressed store decorators. Mirrors the
// verified TS compression.test.ts and pins the cross-language wire format
// against ground-truth captured from the C# codec. The encoded payload — the
// thing that is persisted and shared across devices/languages — must be
// BYTE-IDENTICAL with C#.

import XCTest
@testable import CircleAI

final class CompressionTests: XCTestCase {

    // ── Helpers (mirror the C# / TS test helpers) ─────────────────────────

    /// Deterministic Mulberry32 PRNG so vectors are reproducible across runs.
    /// Matches the TS mulberry32 bit-for-bit (all math in UInt32).
    private func mulberry32(_ seed: UInt32) -> () -> Double {
        var a = seed
        return {
            a = a &+ 0x6D2B_79F5
            var t = a
            t = (t ^ (t >> 15)) &* (1 | t)
            t = (t &+ ((t ^ (t >> 7)) &* (61 | t))) ^ t
            return Double((t ^ (t >> 14))) / 4_294_967_296.0
        }
    }

    private func randomUnit(_ dim: Int, _ seed: UInt32) -> [Float] {
        let rng = mulberry32(seed)
        var v = [Float](repeating: 0, count: dim)
        var sumSq = 0.0
        for i in 0..<dim {
            let x = rng() * 2 - 1
            v[i] = Float(x)
            sumSq += Double(v[i]) * Double(v[i])
        }
        let inv = Float(1.0 / sumSq.squareRoot())
        for i in 0..<dim { v[i] *= inv }
        return v
    }

    private func cosine(_ a: [Float], _ b: [Float]) -> Double {
        var dot = 0.0, magA = 0.0, magB = 0.0
        for i in 0..<a.count {
            dot += Double(a[i]) * Double(b[i])
            magA += Double(a[i]) * Double(a[i])
            magB += Double(b[i]) * Double(b[i])
        }
        let denom = magA.squareRoot() * magB.squareRoot()
        return denom < 1e-30 ? 0 : dot / denom
    }

    private func hex(_ b: [UInt8]) -> String {
        b.map { String(format: "%02x", $0) }.joined()
    }

    // ══════════════════════════════════════════════════════════════════════
    // Cross-language parity — ground truth captured from the C# codec.
    // If these break, the wire format has diverged from every other SDK language.
    // ══════════════════════════════════════════════════════════════════════

    func testBitPackerMatchesCSharp() {
        XCTAssertEqual(hex(BitPacker.pack([0, 3, 1, 2, 3, 0, 2, 1], bitsPerIndex: 2)), "9c63")
        XCTAssertEqual(hex(BitPacker.pack([0, 7, 3, 5, 1, 6, 2, 4], bitsPerIndex: 3)), "f81a8b")
        XCTAssertEqual(hex(BitPacker.pack([15, 0, 8, 7, 1, 14, 9, 6], bitsPerIndex: 4)), "0f78e169")
    }

    func testCodebookCentroidsMatchCSharp() {
        let cb = BetaLloydMaxCodebook.get(bits: 2, dim: 8)
        XCTAssertEqual(cb.centroids.map { Double($0) }, [
            -0.5048246383666992, -0.15792210400104523, 0.15792210400104523, 0.5048246383666992,
        ])
        let cb4 = BetaLloydMaxCodebook.get(bits: 4, dim: 16)
        XCTAssertEqual(cb4.centroids.map { Double($0) }, [
            -0.6039019227027893, -0.4742901921272278, -0.37855634093284607, -0.2978082597255707,
            -0.2253989577293396, -0.1580331176519394, -0.09372113645076752, -0.031065061688423157,
            0.031065061688423157, 0.09372113645076752, 0.1580331176519394, 0.2253989577293396,
            0.2978082597255707, 0.37855634093284607, 0.4742901921272278, 0.6039019227027893,
        ])
    }

    func testEncodesEightDimToExactCSharpPayload() {
        let v8: [Float] = [0.1, -0.2, 0.3, -0.4, 0.5, -0.6, 0.7, -0.8]
        // Byte-identical to what CircleAI.Memory.Compression emits.
        XCTAssertEqual(EmbeddingPayloadCodec.encodeBase64(v8, bitsPerDim: 2), "VFEzAQIAAAAIAAAAEdK2P9B5")
        XCTAssertEqual(EmbeddingPayloadCodec.encodeBase64(v8, bitsPerDim: 4), "VFEzAQQAAAAIAAAAEdK2PzPHpV4=")
        XCTAssertEqual(hex(EmbeddingPayloadCodec.encode(v8, bitsPerDim: 2)), "54513301020000000800000011d2b63fd079")
        XCTAssertEqual(hex(EmbeddingPayloadCodec.encode(v8, bitsPerDim: 4)), "54513301040000000800000011d2b63f33c7a55e")
    }

    func testStoresExactCSharpNorm() {
        let v8: [Float] = [0.1, -0.2, 0.3, -0.4, 0.5, -0.6, 0.7, -0.8]
        XCTAssertEqual(Double(TurboQuantCodec.encode(v8, bitsPerDim: 2).norm), 1.4282857179641724)
    }

    func testEncodesTinyFourDimToExactCSharpLayout() {
        let v4: [Float] = [1, 2, 3, 4]
        XCTAssertEqual(hex(EmbeddingPayloadCodec.encode(v4, bitsPerDim: 2)), "5451330102000000040000006f45af409c")
        XCTAssertEqual(EmbeddingPayloadCodec.encodeBase64(v4, bitsPerDim: 2), "VFEzAQIAAAAEAAAAb0WvQJw=")
        XCTAssertEqual(Double(TurboQuantCodec.encode(v4, bitsPerDim: 2).norm), 5.4772257804870605)
    }

    func testRotationMatrixRow0Dim8MatchesCSharp() {
        let row0 = Array(OrthogonalRotation.getMatrix(8)[0..<8]).map { Double($0) }
        XCTAssertEqual(row0, [
            0.32915404438972473, -0.15729351341724396, -0.6576523184776306, 0.4990078806877136,
            -0.2985365092754364, -0.17185114324092865, 0.024059195071458817, 0.2572260797023773,
        ])
    }

    // ══════════════════════════════════════════════════════════════════════
    // BitPacker
    // ══════════════════════════════════════════════════════════════════════

    func testBitPackerRoundTrips() {
        for bits in [1, 2, 3, 4, 8] {
            let maxV = (1 << bits) - 1
            let rng = mulberry32(UInt32(123 + bits))
            var indices = [UInt16](repeating: 0, count: 256)
            for i in 0..<indices.count { indices[i] = UInt16(Int(rng() * Double(maxV + 1))) }

            let packed = BitPacker.pack(indices, bitsPerIndex: bits)
            let unpacked = BitPacker.unpack(packed, count: indices.count, bitsPerIndex: bits)

            XCTAssertEqual(unpacked.count, indices.count)
            XCTAssertEqual(unpacked, indices, "round-trip failed at \(bits) bits")
        }
    }

    func testBitPackerByteCountSpec() {
        let indices = [UInt16](repeating: 0, count: 1536)
        XCTAssertEqual(BitPacker.pack(indices, bitsPerIndex: 2).count, 384)
    }

    // ══════════════════════════════════════════════════════════════════════
    // OrthogonalRotation
    // ══════════════════════════════════════════════════════════════════════

    func testRotationPreservesL2Norm() {
        let dim = 64
        let v = randomUnit(dim, 42)
        var r = [Float](repeating: 0, count: dim)
        OrthogonalRotation.rotate(dim, v, &r)
        var sqA = 0.0, sqR = 0.0
        for i in 0..<dim {
            sqA += Double(v[i]) * Double(v[i])
            sqR += Double(r[i]) * Double(r[i])
        }
        XCTAssertLessThan(abs(sqR.squareRoot() - sqA.squareRoot()), 1e-3)
    }

    func testRotateThenUnrotateRecoversInput() {
        let dim = 64
        let v = randomUnit(dim, 7)
        var r = [Float](repeating: 0, count: dim)
        var v2 = [Float](repeating: 0, count: dim)
        OrthogonalRotation.rotate(dim, v, &r)
        OrthogonalRotation.unrotate(dim, r, &v2)
        for i in 0..<dim { XCTAssertLessThan(abs(v2[i] - v[i]), 1e-3) }
    }

    func testRotationDeterministicAcrossCalls() {
        let a = OrthogonalRotation.getMatrix(32)
        let b = OrthogonalRotation.getMatrix(32)
        XCTAssertEqual(a, b) // deterministic + cached
    }

    // ══════════════════════════════════════════════════════════════════════
    // BetaLloydMaxCodebook
    // ══════════════════════════════════════════════════════════════════════

    func testCodebookSizes() {
        for (bits, dim) in [(1, 16), (2, 64), (3, 128), (4, 256)] {
            let cb = BetaLloydMaxCodebook.get(bits: bits, dim: dim)
            let n = 1 << bits
            XCTAssertEqual(cb.centroids.count, n)
            XCTAssertEqual(cb.boundaries.count, n - 1)
        }
    }

    func testCodebookCentroidsMonotonic() {
        let cb = BetaLloydMaxCodebook.get(bits: 4, dim: 128)
        for i in 1..<cb.centroids.count { XCTAssertGreaterThan(cb.centroids[i], cb.centroids[i - 1]) }
    }

    func testBinForRoundTrips() {
        let cb = BetaLloydMaxCodebook.get(bits: 2, dim: 64)
        for i in 0..<cb.boundaries.count {
            let justBefore = cb.boundaries[i] - 1e-6
            let justAfter = cb.boundaries[i] + 1e-6
            XCTAssertEqual(BetaLloydMaxCodebook.binFor(justBefore, cb.boundaries), UInt16(i))
            XCTAssertEqual(BetaLloydMaxCodebook.binFor(justAfter, cb.boundaries), UInt16(i + 1))
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // TurboQuantCodec end-to-end
    // ══════════════════════════════════════════════════════════════════════

    func testRoundTripPreservesGeometry() {
        for (dim, bits, floor) in [(64, 4, 0.99), (128, 4, 0.99), (256, 3, 0.96), (512, 2, 0.85)] {
            let v = randomUnit(dim, 42)
            let reconstructed = TurboQuantCodec.roundTrip(v, bitsPerDim: bits)
            XCTAssertEqual(reconstructed.count, dim)
            let cos = cosine(v, reconstructed)
            XCTAssertGreaterThanOrEqual(cos, floor, "dim=\(dim) bits=\(bits): cos \(cos) below floor \(floor)")
        }
    }

    func testZeroVectorRoundTripsToZeros() {
        let z = [Float](repeating: 0, count: 64)
        let r = TurboQuantCodec.roundTrip(z, bitsPerDim: 2)
        for x in r { XCTAssertEqual(x, 0) }
    }

    func testPayloadSizeMatchesSpec() {
        XCTAssertEqual(TurboQuantCodec.payloadByteCount(dim: 1536, bitsPerDim: 2), 384)
    }

    func testCompressionRatioExceeds15() {
        let ratio = TurboQuantCodec.compressionRatio(dim: 1536, bitsPerDim: 2)
        XCTAssertGreaterThan(ratio, 15.0)
        XCTAssertEqual(ratio, 15.835051546391753)
    }

    func testEncodeDeterministicAcrossRuns() {
        let v = randomUnit(128, 7)
        let a = TurboQuantCodec.encode(v, bitsPerDim: 3)
        let b = TurboQuantCodec.encode(v, bitsPerDim: 3)
        XCTAssertEqual(a.norm, b.norm)
        XCTAssertEqual(a.packedIndices, b.packedIndices)
    }

    func testPreservesInnerProductBetweenCorrelated() {
        let dim = 128
        let a = randomUnit(dim, 1)
        let b = randomUnit(dim, 2)
        var blended = [Float](repeating: 0, count: dim)
        for i in 0..<dim { blended[i] = 0.7 * a[i] + 0.3 * b[i] }
        var bn = 0.0
        for i in 0..<dim { bn += Double(blended[i]) * Double(blended[i]) }
        let invN = Float(1.0 / bn.squareRoot())
        for i in 0..<dim { blended[i] *= invN }

        let trueCos = cosine(a, blended)
        let aHat = TurboQuantCodec.roundTrip(a, bitsPerDim: 4)
        let blendHat = TurboQuantCodec.roundTrip(blended, bitsPerDim: 4)
        let reconCos = cosine(aHat, blendHat)
        XCTAssertLessThanOrEqual(abs(reconCos - trueCos), 0.05, "true=\(trueCos) recon=\(reconCos)")
    }

    // ══════════════════════════════════════════════════════════════════════
    // EmbeddingPayloadCodec
    // ══════════════════════════════════════════════════════════════════════

    func testPayloadRoundTripPreservesCosine() throws {
        let v = randomUnit(128, 42)
        let encoded = EmbeddingPayloadCodec.encode(v, bitsPerDim: 4)
        let decoded = try EmbeddingPayloadCodec.decode(encoded)
        XCTAssertGreaterThanOrEqual(cosine(v, decoded), 0.99)
    }

    func testDetectsOwnHeader() {
        let encoded = EmbeddingPayloadCodec.encode(randomUnit(64, 1), bitsPerDim: 2)
        XCTAssertTrue(EmbeddingPayloadCodec.isEncoded(encoded))
        XCTAssertFalse(EmbeddingPayloadCodec.isEncoded([0, 1, 2]))
    }

    func testRejectsTooShortPayload() {
        XCTAssertThrowsError(try EmbeddingPayloadCodec.decode([1, 2, 3]))
    }

    func testRejectsPayloadWithoutMagic() {
        let bad = [UInt8](repeating: 0, count: 20) // right length, wrong magic
        XCTAssertThrowsError(try EmbeddingPayloadCodec.decode(bad))
    }

    func testBase64RoundTripPreservesCosine() throws {
        let v = randomUnit(64, 7)
        let b64 = EmbeddingPayloadCodec.encodeBase64(v, bitsPerDim: 3)
        let back = try EmbeddingPayloadCodec.decodeBase64(b64)
        XCTAssertGreaterThanOrEqual(cosine(v, back), 0.96)
    }

    func testPayloadAt2BitsMuchSmaller() {
        let v = randomUnit(1536, 42)
        let encoded = EmbeddingPayloadCodec.encode(v, bitsPerDim: 2)
        let ratio = Double(v.count * 4) / Double(encoded.count)
        XCTAssertGreaterThan(ratio, 12.0)
    }

    // ══════════════════════════════════════════════════════════════════════
    // CompressedEpisodicMemoryStore
    // ══════════════════════════════════════════════════════════════════════

    private func episodic(
        userText: String = "u", assistantText: String = "a",
        at: Date = Date(timeIntervalSince1970: 1_767_225_600),
        embedding: [Float]? = nil
    ) -> EpisodicMemoryEntry {
        EpisodicMemoryEntry(recordedAt: at, userText: userText, assistantText: assistantText, embedding: embedding)
    }

    func testEpisodicStoresCompressedTagNotFloatArray() async throws {
        let inner = InMemoryEpisodicStore()
        let outer = CompressedEpisodicMemoryStore(inner: inner, bitsPerDim: 2)
        try await outer.add(episodic(userText: "hello", assistantText: "hi", embedding: randomUnit(128, 1)))

        let rawRecent = try await inner.getRecent(count: 1)
        XCTAssertEqual(rawRecent.count, 1)
        XCTAssertNil(rawRecent[0].embedding)
        XCTAssertNotNil(rawRecent[0].tags)
        XCTAssertNotNil(rawRecent[0].tags?[compressedTagKey])
    }

    func testEpisodicGetRecentRehydrates() async throws {
        let inner = InMemoryEpisodicStore()
        let outer = CompressedEpisodicMemoryStore(inner: inner, bitsPerDim: 4)
        let original = randomUnit(64, 1)
        try await outer.add(episodic(embedding: original))

        let got = try await outer.getRecent(count: 1)
        XCTAssertEqual(got.count, 1)
        XCTAssertNotNil(got[0].embedding)
        XCTAssertGreaterThanOrEqual(cosine(original, got[0].embedding!), 0.99)
    }

    func testEpisodicSearchRanksByCosine() async throws {
        let inner = InMemoryEpisodicStore()
        let outer = CompressedEpisodicMemoryStore(inner: inner, bitsPerDim: 4)
        let v1 = randomUnit(64, 1)
        let v2 = randomUnit(64, 2)
        try await outer.add(episodic(userText: "near", embedding: v1))
        try await outer.add(episodic(userText: "far", embedding: v2))

        let results = try await outer.search(queryEmbedding: v1, topK: 2)
        XCTAssertEqual(results.count, 2)
        XCTAssertEqual(results[0].userText, "near")
    }

    func testEpisodicSearchNullQueryReturnsRecency() async throws {
        let inner = InMemoryEpisodicStore()
        let outer = CompressedEpisodicMemoryStore(inner: inner, bitsPerDim: 4)
        try await outer.add(episodic(
            userText: "old", at: Date(timeIntervalSince1970: 1_767_225_600), embedding: randomUnit(32, 1)))
        try await outer.add(episodic(
            userText: "new", at: Date(timeIntervalSince1970: 1_780_000_000), embedding: randomUnit(32, 2)))
        let results = try await outer.search(queryEmbedding: nil, topK: 1)
        XCTAssertEqual(results.count, 1)
        XCTAssertEqual(results[0].userText, "new")
    }

    func testEpisodicEntryWithoutEmbeddingPassesThrough() async throws {
        let inner = InMemoryEpisodicStore()
        let outer = CompressedEpisodicMemoryStore(inner: inner)
        try await outer.add(episodic(userText: "u", assistantText: "a"))
        let raw = try await inner.getRecent(count: 1)
        XCTAssertEqual(raw.count, 1)
        XCTAssertNil(raw[0].embedding)
        XCTAssertTrue(raw[0].tags == nil || raw[0].tags?[compressedTagKey] == nil)
    }

    func testEpisodicExposesTagKeyConstant() {
        XCTAssertEqual(CompressedEpisodicMemoryStore.compressedTagKey, "x-tq-embedding")
    }

    // ══════════════════════════════════════════════════════════════════════
    // CompressedMultimodalMemoryStore
    // ══════════════════════════════════════════════════════════════════════

    func testMultimodalRoundTripsEmbeddingAndMetadata() async throws {
        let inner = InMemoryMultimodalMemoryStore()
        let outer = CompressedMultimodalMemoryStore(inner: inner, bitsPerDim: 4)
        // 4-bit ≥ 0.99 is a statistical bound; seed 42 clears it comfortably.
        let emb = randomUnit(128, 42)
        try await outer.add(makeMultimodalMemoryEntry(
            modality: .image, caption: "a sunny beach", embedding: emb,
            sourceSha256: "deadbeef", widthPx: 1920, heightPx: 1080))

        let got = try await outer.getByHash("deadbeef")
        XCTAssertNotNil(got)
        XCTAssertEqual(got?.caption, "a sunny beach")
        XCTAssertEqual(got?.widthPx, 1920)
        XCTAssertEqual(got?.heightPx, 1080)
        XCTAssertNotNil(got?.embedding)
        XCTAssertGreaterThanOrEqual(cosine(emb, got!.embedding!), 0.99)
    }

    func testMultimodalInnerSeesNullEmbeddingPlusTag() async throws {
        let inner = InMemoryMultimodalMemoryStore()
        let outer = CompressedMultimodalMemoryStore(inner: inner)
        try await outer.add(makeMultimodalMemoryEntry(caption: "x", embedding: randomUnit(64, 1), sourceSha256: "abc"))

        let raw = try await inner.getByHash("abc")
        XCTAssertNotNil(raw)
        XCTAssertNil(raw?.embedding)
        XCTAssertNotNil(raw?.tags?[compressedTagKey])
    }

    func testMultimodalSearchRanksByCosine() async throws {
        let inner = InMemoryMultimodalMemoryStore()
        let outer = CompressedMultimodalMemoryStore(inner: inner, bitsPerDim: 4)
        let v1 = randomUnit(64, 1)
        let v2 = randomUnit(64, 2)
        try await outer.add(makeMultimodalMemoryEntry(caption: "near", embedding: v1, sourceSha256: "a"))
        try await outer.add(makeMultimodalMemoryEntry(caption: "far", embedding: v2, sourceSha256: "b"))

        let results = try await outer.search(queryEmbedding: v1, topK: 2)
        XCTAssertEqual(results.count, 2)
        XCTAssertEqual(results[0].caption, "near")
    }

    func testMultimodalReinforceAndCountThroughDecorator() async throws {
        let inner = InMemoryMultimodalMemoryStore()
        let outer = CompressedMultimodalMemoryStore(inner: inner, bitsPerDim: 4)
        try await outer.add(makeMultimodalMemoryEntry(caption: "x", embedding: randomUnit(32, 1), sourceSha256: "x"))
        try await outer.reinforce("x")
        let got = try await outer.getByHash("x")
        XCTAssertEqual(got?.referenceCount, 2)
        let n = try await outer.count()
        XCTAssertEqual(n, 1)
    }
}
