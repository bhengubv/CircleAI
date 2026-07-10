// DirectivePublisherTests.swift
//
// Validates DirectivePublisher — subscribe / fan-out / unsubscribe semantics
// ported from DirectivePublisher.cs, including idempotent disposal.

import XCTest
import Foundation
@testable import CircleAI

private final class RecordingConsumer: IPeerDirectiveConsumer, @unchecked Sendable {
    private let lock = NSLock()
    private var received: [PeerDirective] = []

    func onDirective(_ directive: PeerDirective) {
        lock.lock(); received.append(directive); lock.unlock()
    }

    var count: Int { lock.lock(); defer { lock.unlock() }; return received.count }
    var last: PeerDirective? { lock.lock(); defer { lock.unlock() }; return received.last }
}

final class DirectivePublisherTests: XCTestCase {

    private func directive(_ node: String = "peer-1") -> PeerDirective {
        PeerDirective(kind: .avoidNode, targetNodeId: node, trustScore: 0.4,
                      threatLevel: .high, reason: "test", duration: nil, issuedAt: Date())
    }

    func testSubscribeIncrementsSubscriberCount() {
        let pub = DirectivePublisher()
        XCTAssertEqual(pub.subscriberCount, 0)
        _ = pub.subscribe(RecordingConsumer())
        XCTAssertEqual(pub.subscriberCount, 1)
    }

    func testPublishFansOutToAllSubscribers() {
        let pub = DirectivePublisher()
        let a = RecordingConsumer(); let b = RecordingConsumer()
        let subA = pub.subscribe(a)
        let subB = pub.subscribe(b)
        pub.publish(directive("peer-9"))
        XCTAssertEqual(a.count, 1)
        XCTAssertEqual(b.count, 1)
        XCTAssertEqual(a.last?.targetNodeId, "peer-9")
        // Keep handles alive to the end.
        _ = subA; _ = subB
    }

    func testDisposeUnsubscribes() {
        let pub = DirectivePublisher()
        let a = RecordingConsumer()
        let sub = pub.subscribe(a)
        XCTAssertEqual(pub.subscriberCount, 1)
        sub.dispose()
        XCTAssertEqual(pub.subscriberCount, 0)
        pub.publish(directive())
        XCTAssertEqual(a.count, 0)
    }

    func testDisposeIsIdempotent() {
        let pub = DirectivePublisher()
        let a = RecordingConsumer()
        let sub = pub.subscribe(a)
        let b = RecordingConsumer()
        _ = pub.subscribe(b)
        sub.dispose()
        sub.dispose() // second dispose must not remove b or throw
        XCTAssertEqual(pub.subscriberCount, 1)
    }

    func testUnsubscribedConsumerReceivesNothingAfterDispose() {
        let pub = DirectivePublisher()
        let a = RecordingConsumer()
        let sub = pub.subscribe(a)
        pub.publish(directive("first"))
        sub.dispose()
        pub.publish(directive("second"))
        XCTAssertEqual(a.count, 1)
        XCTAssertEqual(a.last?.targetNodeId, "first")
    }
}
