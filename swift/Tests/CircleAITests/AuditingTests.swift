// AuditingTests.swift
//
// Exercises the ported CircleAI.Core.Auditing surface: CircleAIAuditEntry /
// CircleAIAuditQuery DTOs, NoopAuditLog (drops + empty query), LoggerAuditLog
// (emits a structured line through an injected logger), and the CircleAIAuditing
// ambient default with setDefault / resetToNoop.

import XCTest
@testable import CircleAI

// Captures the structured messages LoggerAuditLog emits.
private final class CapturingLogger: ICircleAILogger, @unchecked Sendable {
    private let lock = NSLock()
    private(set) var lines: [String] = []
    func logInformation(_ message: String) {
        lock.lock(); lines.append(message); lock.unlock()
    }
}

// A custom sink used to verify CircleAIAuditing.setDefault routing.
private final class RecordingAuditLog: ICircleAIAuditLog, @unchecked Sendable {
    private let lock = NSLock()
    private(set) var recorded: [CircleAIAuditEntry] = []
    func record(_ entry: CircleAIAuditEntry) async {
        lock.lock(); recorded.append(entry); lock.unlock()
    }
    func query(_ query: CircleAIAuditQuery) -> AsyncStream<CircleAIAuditEntry> {
        let snapshot: [CircleAIAuditEntry]
        lock.lock(); snapshot = recorded; lock.unlock()
        return AsyncStream { cont in
            for e in snapshot { cont.yield(e) }
            cont.finish()
        }
    }
}

final class AuditingTests: XCTestCase {

    private func sampleEntry(outcome: String = "success") -> CircleAIAuditEntry {
        CircleAIAuditEntry(
            at: Date(timeIntervalSince1970: 1_700_000_000),
            component: "JsonPersonaProvider",
            operation: "GetAsync",
            outcome: outcome,
            tenantId: "t1",
            uhidIdentityId: "u1",
            correlationId: "corr-1",
            durationMs: 12.5,
            errorType: outcome == "success" ? nil : "InvalidOperationException",
            errorCode: nil,
            payloadSha256Hex: nil)
    }

    func testNoopAuditLogDropsAndReturnsEmpty() async {
        let log = NoopAuditLog.instance
        await log.record(sampleEntry())
        var count = 0
        for await _ in log.query(CircleAIAuditQuery()) { count += 1 }
        XCTAssertEqual(count, 0)
    }

    func testLoggerAuditLogEmitsStructuredLine() async {
        let logger = CapturingLogger()
        let log = LoggerAuditLog(logger: logger)
        await log.record(sampleEntry(outcome: "failure"))
        XCTAssertEqual(logger.lines.count, 1)
        let line = logger.lines[0]
        XCTAssertTrue(line.contains("JsonPersonaProvider.GetAsync"))
        XCTAssertTrue(line.contains("failure"))
        XCTAssertTrue(line.contains("tenant=t1"))
        XCTAssertTrue(line.contains("uhid=u1"))
        XCTAssertTrue(line.contains("corr=corr-1"))
        XCTAssertTrue(line.contains("error=InvalidOperationException"))
    }

    func testLoggerAuditLogQueryIsEmpty() async {
        let log = LoggerAuditLog(logger: CapturingLogger())
        var count = 0
        for await _ in log.query(CircleAIAuditQuery(component: "X")) { count += 1 }
        XCTAssertEqual(count, 0)
    }

    func testAuditEntryDefaults() {
        let e = CircleAIAuditEntry(
            at: Date(timeIntervalSince1970: 0),
            component: "C", operation: "O", outcome: "success")
        XCTAssertNil(e.tenantId)
        XCTAssertNil(e.uhidIdentityId)
        XCTAssertEqual(e.durationMs, 0)
        XCTAssertNil(e.payloadSha256Hex)
    }

    func testAuditQueryDefaultMaxItems() {
        XCTAssertEqual(CircleAIAuditQuery().maxItems, 1000)
        XCTAssertEqual(CircleAIAuditQuery(maxItems: 7).maxItems, 7)
    }

    func testAmbientDefaultSetAndReset() async {
        // Defaults to Noop.
        XCTAssertTrue(CircleAIAuditing.default is NoopAuditLog)

        let sink = RecordingAuditLog()
        CircleAIAuditing.setDefault(sink)
        defer { CircleAIAuditing.resetToNoop() }

        await CircleAIAuditing.default.record(sampleEntry())
        XCTAssertEqual(sink.recorded.count, 1)

        // Query round-trips through the custom sink.
        var seen = 0
        for await _ in CircleAIAuditing.default.query(CircleAIAuditQuery()) { seen += 1 }
        XCTAssertEqual(seen, 1)

        CircleAIAuditing.resetToNoop()
        XCTAssertTrue(CircleAIAuditing.default is NoopAuditLog)
    }
}
