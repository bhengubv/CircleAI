import XCTest
@testable import CircleAI

/// The wire format: three envelopes, three content types, one correlation id.
final class MeshOffloadWireTests: XCTestCase {

    let now = Date(timeIntervalSince1970: 1_782_896_400)

    func turn() -> OffloadTurn {
        OffloadTurn.create(modelId: "qwen-1.5b", prompt: "why is the sky blue",
                           maxOutputTokens: 200, temperature: 0.4, topP: 0.9,
                           stopSequences: ["\n\n"], correlationId: "corr-42", now: now)!
    }

    // MARK: - Requests

    func testARequestSurvivesTheRoundTrip() throws {
        let env = OffloadRequestEnvelope(turn: turn(), replyToNodeId: "me")
        let payload = try MeshOffloadWire.encodeRequest(
            sourceNodeId: "me", destinationPeerId: "them", env)
        XCTAssertEqual(MeshOffloadWire.decodeRequest(payload), env)
    }

    func testARequestIsAddressedAndPrioritised() throws {
        let payload = try MeshOffloadWire.encodeRequest(
            sourceNodeId: "me", destinationPeerId: "them",
            OffloadRequestEnvelope(turn: turn(), replyToNodeId: "me"))
        XCTAssertEqual(payload.sourceId, "me")
        XCTAssertEqual(payload.destinationId, "them")
        XCTAssertEqual(payload.priority, .high)
        XCTAssertEqual(payload.contentType, MeshOffloadWire.requestContentType)
    }

    // The correlation id rides in the metadata as well as the body, so a
    // transport can route a reply without parsing the payload.
    func testTheCorrelationIdIsVisibleWithoutOpeningTheBody() throws {
        let payload = try MeshOffloadWire.encodeRequest(
            sourceNodeId: "me", destinationPeerId: "them",
            OffloadRequestEnvelope(turn: turn(), replyToNodeId: "me"))
        XCTAssertEqual(payload.metadata[MeshOffloadWire.correlationMetaKey], "corr-42")
    }

    func testTheTurnCarriesItsSamplingSettingsAcross() throws {
        let env = OffloadRequestEnvelope(turn: turn(), replyToNodeId: "me")
        let payload = try MeshOffloadWire.encodeRequest(
            sourceNodeId: "me", destinationPeerId: "them", env)
        let back = MeshOffloadWire.decodeRequest(payload)!
        XCTAssertEqual(back.temperature, 0.4)
        XCTAssertEqual(back.topP, 0.9)
        XCTAssertEqual(back.maxOutputTokens, 200)
        XCTAssertEqual(back.stopSequences, ["\n\n"])
        XCTAssertEqual(back.replyToNodeId, "me")
    }

    // MARK: - Replies

    func testAReplySurvivesTheRoundTrip() throws {
        let env = OffloadReplyEnvelope(correlationId: "corr-42", success: true,
                                       outputText: "because of Rayleigh scattering",
                                       outputTokenCount: 5, failureReason: nil,
                                       reasoningText: "thought about it", completedAtUtc: now)
        let payload = try MeshOffloadWire.encodeReply(
            sourceNodeId: "them", destinationNodeId: "me", env)
        XCTAssertEqual(MeshOffloadWire.decodeReply(payload), env)
        XCTAssertEqual(payload.contentType, MeshOffloadWire.replyContentType)
    }

    func testAFailedReplyCarriesItsReason() throws {
        let env = OffloadReplyEnvelope(correlationId: "c", success: false, outputText: "",
                                       outputTokenCount: 0, failureReason: "out of KV",
                                       reasoningText: nil, completedAtUtc: now)
        let payload = try MeshOffloadWire.encodeReply(sourceNodeId: "a", destinationNodeId: "b", env)
        let back = MeshOffloadWire.decodeReply(payload)!
        XCTAssertFalse(back.success)
        XCTAssertEqual(back.failureReason, "out of KV")
        XCTAssertNil(back.reasoningText)
    }
}
