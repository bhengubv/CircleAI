import XCTest
@testable import CircleAI

/// Adverts, wrong shapes and the contract types.
final class MeshOffloadAdvertTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_782_896_400)

    private func ad(tier: DeviceTier = .tablet, latency: Int? = 35) -> MeshCapabilityAdvertisement {
        MeshCapabilityAdvertisement(peerId: "p1", modelId: "m", freeKvTokens: 4096, tier: tier,
                                    contextWindowTokens: 8192, advertisedAtUtc: now,
                                    latencyHintMs: latency)
    }

    // An advert has NO destination - it is for whoever is listening.
    func testAnAdvertIsUnaddressed() throws {
        let payload = try MeshOffloadWire.encodeAdvert(sourceNodeId: "p1", MeshAdvertEnvelope(ad()))
        XCTAssertNil(payload.destinationId)
        XCTAssertEqual(payload.priority, .normal)
        XCTAssertEqual(payload.contentType, MeshOffloadWire.advertContentType)
        XCTAssertEqual(payload.metadata[MeshOffloadWire.correlationMetaKey], "p1")
    }

    func testAnAdvertRoundTripsBackIntoAnAdvertisement() throws {
        let original = ad(tier: .desktop)
        let payload = try MeshOffloadWire.encodeAdvert(sourceNodeId: "p1",
                                                       MeshAdvertEnvelope(original))
        let back = MeshOffloadWire.decodeAdvert(payload)!.toAdvertisement()
        XCTAssertEqual(back.peerId, "p1")
        XCTAssertEqual(back.tier, .desktop)
        XCTAssertEqual(back.freeKvTokens, 4096)
        XCTAssertEqual(back.latencyHintMs, 35)
        XCTAssertEqual(back.advertisedAtUtc.timeIntervalSince1970,
                       original.advertisedAtUtc.timeIntervalSince1970, accuracy: 0.001)
    }

    // A tier from a newer build must not take the whole advert down with it.
    func testAnUnknownTierLandsOnPhoneRatherThanFailing() {
        let env = MeshAdvertEnvelope(peerId: "p", modelId: "m", freeKvTokens: 1, tier: 99,
                                     contextWindowTokens: 1, advertisedAtUtc: now, latencyHintMs: nil)
        XCTAssertEqual(env.toAdvertisement().tier, .phone)
    }

    func testAnAdvertWithNoLatencyHintRoundTripsAsNil() throws {
        let payload = try MeshOffloadWire.encodeAdvert(sourceNodeId: "p",
                                                       MeshAdvertEnvelope(ad(latency: nil)))
        XCTAssertNil(MeshOffloadWire.decodeAdvert(payload)?.latencyHintMs)
    }

    // MARK: - Wrong shapes

    // The three content types exist so a decoder never has to guess.
    func testTheThreeContentTypesAreDistinct() {
        let all = Set([MeshOffloadWire.requestContentType,
                       MeshOffloadWire.replyContentType,
                       MeshOffloadWire.advertContentType])
        XCTAssertEqual(all.count, 3)
    }

    func testDecodingTheWrongShapeReturnsNilRatherThanThrowing() throws {
        let advert = try MeshOffloadWire.encodeAdvert(sourceNodeId: "p", MeshAdvertEnvelope(ad()))
        XCTAssertNil(MeshOffloadWire.decodeRequest(advert))
        XCTAssertNil(MeshOffloadWire.decodeReply(advert))
    }

    func testGarbageDecodesToNil() {
        let junk = NetworkPayload(id: "x", sourceId: nil, destinationId: nil,
                                  data: Data("not json".utf8), priority: .normal, ttl: nil,
                                  contentType: MeshOffloadWire.requestContentType,
                                  metadata: [:], createdAt: now)
        XCTAssertNil(MeshOffloadWire.decodeRequest(junk))
    }

    // MARK: - Contract shapes

    func testATurnNeedsAModelId() {
        XCTAssertNil(OffloadTurn.create(modelId: "  ", prompt: "x"))
        XCTAssertNotNil(OffloadTurn.create(modelId: "m", prompt: "x"))
    }

    func testAFailureCarriesItsReasonAndNoOutput() {
        let r = OffloadResult.fail("nobody home", servedBy: .none, elapsedMilliseconds: 12)
        XCTAssertFalse(r.success)
        XCTAssertEqual(r.outputText, "")
        XCTAssertEqual(r.outputTokenCount, 0)
        XCTAssertEqual(r.failureReason, "nobody home")
        XCTAssertEqual(r.elapsedMilliseconds, 12)
    }

    func testTheNullFallbackAdmitsItCannotServe() async throws {
        let t = OffloadTurn.create(modelId: "m", prompt: "x")!
        let r = try await NullLocalInferenceFallback.instance.complete(t)
        XCTAssertFalse(r.success)
        XCTAssertEqual(r.servedBy, .none)
        XCTAssertTrue(r.failureReason!.contains("cannot serve locally"))
    }
}
