// MultimodalTests.swift
// Exercises the multimodal memory pipeline: HeuristicMultimodalCaptioner,
// InMemoryMultimodalMemoryStore, and the MultimodalMemoryIngester (dedup +
// caption + persist). Mirrors CircleAI.Tests.MultimodalMemoryTests and the
// verified TS multimodal.test.ts. Bytes are synthesised inline.

import XCTest
@testable import CircleAI

// FakeRichCaptioner — only handles Image, returns a rich caption + embedding.
private struct FakeRichCaptioner: IMultimodalCaptioner {
    func canCaption(modality: MediaModality, mimeType: String?) -> Bool { modality == .image }
    func caption(modality: MediaModality, sourceBytes: [UInt8], mimeType: String?) async throws -> CaptionResult {
        CaptionResult(caption: "A blue sky with two clouds.", embedding: [0.1, 0.2, 0.3], widthPx: 1920, heightPx: 1080)
    }
}

final class MultimodalTests: XCTestCase {

    private func fakeJpeg(_ extraBytes: Int = 100) -> [UInt8] {
        var buf = [UInt8](repeating: 0, count: 2 + extraBytes)
        buf[0] = 0xFF; buf[1] = 0xD8
        for i in 2..<buf.count { buf[i] = UInt8(i % 251) }
        return buf
    }

    private func fakePng(_ extraBytes: Int = 100) -> [UInt8] {
        var buf = [UInt8](repeating: 0, count: 4 + extraBytes)
        buf[0] = 0x89; buf[1] = 0x50; buf[2] = 0x4E; buf[3] = 0x47
        for i in 4..<buf.count { buf[i] = UInt8(i % 251) }
        return buf
    }

    private func wireIngester(_ custom: IMultimodalCaptioner? = nil)
        -> (ingester: MultimodalMemoryIngester, store: InMemoryMultimodalMemoryStore) {
        let store = InMemoryMultimodalMemoryStore()
        let captioners: [IMultimodalCaptioner] = custom != nil
            ? [custom!, HeuristicMultimodalCaptioner()]
            : [HeuristicMultimodalCaptioner()]
        return (try! MultimodalMemoryIngester(captioners: captioners, store: store), store)
    }

    // ── HeuristicMultimodalCaptioner ──────────────────────────────────────

    func testAlwaysCanCaption() {
        let c = HeuristicMultimodalCaptioner()
        XCTAssertTrue(c.canCaption(modality: .image, mimeType: "image/jpeg"))
        XCTAssertTrue(c.canCaption(modality: .audio, mimeType: nil))
        XCTAssertTrue(c.canCaption(modality: .video, mimeType: "video/mp4"))
        XCTAssertTrue(c.canCaption(modality: .textDocument, mimeType: "application/pdf"))
    }

    func testDetectsJpegMagicNoEmbedding() async throws {
        let c = HeuristicMultimodalCaptioner()
        let r = try await c.caption(modality: .image, sourceBytes: fakeJpeg(), mimeType: nil)
        XCTAssertTrue(r.caption.contains("image/jpeg"))
        XCTAssertNil(r.embedding)
    }

    func testDetectsPngGifWavPdfMagic() async throws {
        let c = HeuristicMultimodalCaptioner()
        let png = try await c.caption(modality: .image, sourceBytes: fakePng(), mimeType: nil)
        XCTAssertTrue(png.caption.contains("image/png"))
        let gif = try await c.caption(modality: .image, sourceBytes: [0x47, 0x49, 0x46, 0x38], mimeType: nil)
        XCTAssertTrue(gif.caption.contains("image/gif"))
        let wav = try await c.caption(modality: .audio, sourceBytes: [0x52, 0x49, 0x46, 0x46], mimeType: nil)
        XCTAssertTrue(wav.caption.contains("audio/wav"))
        let pdf = try await c.caption(modality: .textDocument, sourceBytes: [0x25, 0x50, 0x44, 0x46], mimeType: nil)
        XCTAssertTrue(pdf.caption.contains("application/pdf"))
    }

    func testFallsBackToOctetStream() async throws {
        let c = HeuristicMultimodalCaptioner()
        let r = try await c.caption(modality: .audio, sourceBytes: [1, 2, 3, 4], mimeType: nil)
        XCTAssertTrue(r.caption.contains("application/octet-stream"))
    }

    func testUsesDeclaredMime() async throws {
        let c = HeuristicMultimodalCaptioner()
        let r = try await c.caption(modality: .image, sourceBytes: fakePng(), mimeType: "image/heic")
        XCTAssertTrue(r.caption.contains("image/heic"))
    }

    func testMarksFallbackAndByteCount() async throws {
        let c = HeuristicMultimodalCaptioner()
        let bytes = fakeJpeg()
        let r = try await c.caption(modality: .image, sourceBytes: bytes, mimeType: nil)
        XCTAssertTrue(r.caption.contains("no captioner wired"))
        XCTAssertTrue(r.caption.contains("\(bytes.count) bytes"))
    }

    func testModalityLabelPerKind() async throws {
        let c = HeuristicMultimodalCaptioner()
        let img = try await c.caption(modality: .image, sourceBytes: fakeJpeg(), mimeType: nil)
        let aud = try await c.caption(modality: .audio, sourceBytes: fakeJpeg(), mimeType: "audio/wav")
        let vid = try await c.caption(modality: .video, sourceBytes: fakeJpeg(), mimeType: "video/mp4")
        let doc = try await c.caption(modality: .textDocument, sourceBytes: fakeJpeg(), mimeType: "application/pdf")
        XCTAssertTrue(img.caption.hasPrefix("[Image"))
        XCTAssertTrue(aud.caption.hasPrefix("[Audio"))
        XCTAssertTrue(vid.caption.hasPrefix("[Video"))
        XCTAssertTrue(doc.caption.hasPrefix("[Document"))
    }

    // ── Ingester — happy path ─────────────────────────────────────────────

    func testFirstTimeAddsEntryNotDeduplicated() async throws {
        let (ingester, store) = wireIngester()
        let bytes = fakeJpeg()
        let r = try await ingester.ingest(modality: .image, sourceBytes: bytes, mimeType: "image/jpeg")

        XCTAssertFalse(r.wasDeduplicated)
        let n = try await store.count()
        XCTAssertEqual(n, 1)
        XCTAssertEqual(r.entry.sourceByteCount, bytes.count)
        XCTAssertEqual(r.entry.sourceMimeType, "image/jpeg")
        XCTAssertFalse(r.entry.sourceSha256.trimmingCharacters(in: .whitespaces).isEmpty)
    }

    func testSecondTimeDeduplicatesAndReinforces() async throws {
        let (ingester, store) = wireIngester()
        let bytes = fakeJpeg()
        let first = try await ingester.ingest(modality: .image, sourceBytes: bytes, mimeType: "image/jpeg")
        let second = try await ingester.ingest(modality: .image, sourceBytes: bytes, mimeType: "image/jpeg")

        XCTAssertFalse(first.wasDeduplicated)
        XCTAssertTrue(second.wasDeduplicated)
        let n = try await store.count()
        XCTAssertEqual(n, 1)
        XCTAssertEqual(first.entry.sourceSha256, second.entry.sourceSha256)
        // second.entry is the pre-reinforce snapshot (refCount 1); the stored
        // record is now 2 — assert via a fresh read, matching TS semantics.
        let stored = try await store.getByHash(first.entry.sourceSha256)
        XCTAssertEqual(stored?.referenceCount, 2)
    }

    func testDifferentBytesProduceDistinctEntries() async throws {
        let (ingester, store) = wireIngester()
        let ra = try await ingester.ingest(modality: .image, sourceBytes: fakeJpeg(50))
        let rb = try await ingester.ingest(modality: .image, sourceBytes: fakeJpeg(60))
        XCTAssertNotEqual(ra.entry.sourceSha256, rb.entry.sourceSha256)
        let n = try await store.count()
        XCTAssertEqual(n, 2)
    }

    func testEmptyBytesThrow() async {
        let (ingester, _) = wireIngester()
        do {
            _ = try await ingester.ingest(modality: .image, sourceBytes: [])
            XCTFail("expected empty bytes to throw")
        } catch {
            // expected
        }
    }

    func testRecordsSourceUriAndTags() async throws {
        let (ingester, _) = wireIngester()
        let bytes = fakePng()
        let r = try await ingester.ingest(
            modality: .image, sourceBytes: bytes, mimeType: "image/png",
            sourceUri: "file:///photos/IMG_001.png", tags: ["location": "home", "person": "alex"])
        XCTAssertEqual(r.entry.sourceUri, "file:///photos/IMG_001.png")
        XCTAssertEqual(r.entry.tags?["location"], "home")
        XCTAssertEqual(r.entry.tags?["person"], "alex")
    }

    func testHexLowerSha256() async throws {
        let (ingester, _) = wireIngester()
        let r = try await ingester.ingest(modality: .image, sourceBytes: fakeJpeg(0))
        let sha = r.entry.sourceSha256
        XCTAssertEqual(sha.count, 64)
        XCTAssertNotNil(sha.range(of: "^[0-9a-f]{64}$", options: .regularExpression))
    }

    // ── Captioner selection ───────────────────────────────────────────────

    func testPrefersRichCaptioner() async throws {
        let (ingester, _) = wireIngester(FakeRichCaptioner())
        let r = try await ingester.ingest(modality: .image, sourceBytes: fakeJpeg(), mimeType: "image/jpeg")
        XCTAssertEqual(r.entry.caption, "A blue sky with two clouds.")
        XCTAssertNotNil(r.entry.embedding)
        XCTAssertEqual(r.entry.widthPx, 1920)
        XCTAssertEqual(r.entry.heightPx, 1080)
    }

    func testFallsBackToHeuristicWhenRichDeclines() async throws {
        let (ingester, _) = wireIngester(FakeRichCaptioner())
        let r = try await ingester.ingest(modality: .audio, sourceBytes: fakePng(), mimeType: "audio/wav")
        XCTAssertTrue(r.entry.caption.contains("no captioner wired"))
        XCTAssertNil(r.entry.embedding)
    }

    func testRejectsZeroCaptioners() {
        do {
            _ = try MultimodalMemoryIngester(captioners: [], store: InMemoryMultimodalMemoryStore())
            XCTFail("expected zero captioners to throw")
        } catch {
            // expected
        }
    }

    // ── Store: search, prune, recent, reinforce ───────────────────────────

    func testSearchByEmbeddingRanksByCosine() async throws {
        let store = InMemoryMultimodalMemoryStore()
        try await store.add(makeMultimodalMemoryEntry(caption: "near", embedding: [1, 0.1, 0], sourceSha256: "near"))
        try await store.add(makeMultimodalMemoryEntry(caption: "far", embedding: [0, 0, 1], sourceSha256: "far"))

        let ranked = try await store.search(queryEmbedding: [1, 0, 0], topK: 2)
        XCTAssertEqual(ranked[0].sourceSha256, "near")
        XCTAssertEqual(ranked[1].sourceSha256, "far")
    }

    func testSearchNullQueryReturnsMostRecent() async throws {
        let store = InMemoryMultimodalMemoryStore()
        try await store.add(makeMultimodalMemoryEntry(
            recordedAt: Date().addingTimeInterval(-10 * 86400), caption: "older", sourceSha256: "older"))
        try await store.add(makeMultimodalMemoryEntry(
            recordedAt: Date(), caption: "newer", sourceSha256: "newer"))
        let recent = try await store.search(queryEmbedding: nil, topK: 2)
        XCTAssertEqual(recent[0].sourceSha256, "newer")
    }

    func testPruneRemovesOlderThanCutoff() async throws {
        let store = InMemoryMultimodalMemoryStore()
        try await store.add(makeMultimodalMemoryEntry(
            recordedAt: Date().addingTimeInterval(-10 * 86400), caption: "old", sourceSha256: "old"))
        try await store.add(makeMultimodalMemoryEntry(recordedAt: Date(), caption: "new", sourceSha256: "new"))

        let removed = try await store.pruneOlderThan(cutoff: Date().addingTimeInterval(-5 * 86400))
        XCTAssertEqual(removed, 1)
        let n = try await store.count()
        XCTAssertEqual(n, 1)
        let newEntry = try await store.getByHash("new")
        XCTAssertNotNil(newEntry)
        let oldEntry = try await store.getByHash("old")
        XCTAssertNil(oldEntry)
    }

    func testReinforceIncrementsRefCount() async throws {
        let store = InMemoryMultimodalMemoryStore()
        try await store.add(makeMultimodalMemoryEntry(caption: "x", sourceSha256: "x"))
        try await store.reinforce("x")
        try await store.reinforce("x")
        let got = try await store.getByHash("x")
        XCTAssertEqual(got?.referenceCount, 3) // initial 1 + 2 reinforce
    }

    func testReinforceUnknownHashIsNoOp() async throws {
        let store = InMemoryMultimodalMemoryStore()
        try await store.reinforce("missing") // must not throw
        let n = try await store.count()
        XCTAssertEqual(n, 0)
    }

    func testAddWithoutHashThrows() async {
        let store = InMemoryMultimodalMemoryStore()
        do {
            try await store.add(makeMultimodalMemoryEntry(caption: "x", sourceSha256: ""))
            XCTFail("expected add without hash to throw")
        } catch {
            // expected
        }
    }

    func testHashLookupCaseInsensitive() async throws {
        let store = InMemoryMultimodalMemoryStore()
        try await store.add(makeMultimodalMemoryEntry(caption: "x", sourceSha256: "ABCDEF"))
        let got = try await store.getByHash("abcdef")
        XCTAssertNotNil(got)
    }
}
