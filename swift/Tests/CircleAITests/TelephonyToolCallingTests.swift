// TelephonyToolCallingTests.swift
//
// Verifies TelephonyToolCalling.swift: the DefaultToolCallRegistry local +
// webhook dispatch, ordinal-ignore-case tool names, unknown-tool handling,
// webhook body shape (call_id / tool / arguments), non-2xx webhook error
// mapping (with the 240-char truncation + ellipsis), and empty-body → "{}".
// Cross-checked against CircleAI.Telephony.DefaultToolCallRegistry.

import XCTest
import Foundation
@testable import CircleAI

final class TelephonyToolCallingTests: XCTestCase {

    private func makeRegistry() -> (DefaultToolCallRegistry, FakeHttpTransport) {
        let http = FakeHttpTransport()
        return (DefaultToolCallRegistry(http: http), http)
    }

    func testLocalHandlerInvoked() async throws {
        let (reg, _) = makeRegistry()
        let def = TelephonyToolDefinition(name: "getWeather", description: "d", argumentsJsonSchema: "{}")
        try reg.registerLocal(def) { args in
            "{\"echo\":\(args)}"
        }
        XCTAssertEqual(reg.definitions.map(\.name), ["getWeather"])

        let result = await reg.invoke(
            TelephonyToolInvocation(callId: "c1", toolName: "getWeather", argumentsJson: "{\"city\":\"CPT\"}"))
        XCTAssertTrue(result.succeeded)
        XCTAssertEqual(result.callId, "c1")
        XCTAssertEqual(result.resultJson, "{\"echo\":{\"city\":\"CPT\"}}")
        XCTAssertNil(result.error)
    }

    func testToolNameIsCaseInsensitive() async throws {
        let (reg, _) = makeRegistry()
        try reg.registerLocal(
            TelephonyToolDefinition(name: "DoThing", description: "", argumentsJsonSchema: "{}")) { _ in "{}" }
        // Invoke with different casing → still resolves (OrdinalIgnoreCase).
        let result = await reg.invoke(
            TelephonyToolInvocation(callId: "c", toolName: "dothing", argumentsJson: "{}"))
        XCTAssertTrue(result.succeeded)
    }

    func testUnknownToolReturnsError() async throws {
        let (reg, _) = makeRegistry()
        let result = await reg.invoke(
            TelephonyToolInvocation(callId: "c9", toolName: "missing", argumentsJson: "{}"))
        XCTAssertFalse(result.succeeded)
        XCTAssertEqual(result.resultJson, "{}")
        XCTAssertEqual(result.error, "Tool 'missing' is not registered.")
    }

    func testRegisterLocalRejectsBlankName() {
        let (reg, _) = makeRegistry()
        XCTAssertThrowsError(
            try reg.registerLocal(
                TelephonyToolDefinition(name: "  ", description: "", argumentsJsonSchema: "{}")) { _ in "{}" })
    }

    func testRegisterWebhookRejectsRelativeUrl() {
        let (reg, _) = makeRegistry()
        let relative = URL(string: "not-absolute")!
        XCTAssertThrowsError(
            try reg.registerWebhook(
                TelephonyToolDefinition(name: "t", description: "", argumentsJsonSchema: "{}"),
                webhook: relative))
    }

    func testWebhookDispatchBodyShapeAndSuccess() async throws {
        let (reg, http) = makeRegistry()
        http.on(.post, where: { $0 == "https://host/tool" }) { _ in
            .json("{\"ok\":true}")
        }
        try reg.registerWebhook(
            TelephonyToolDefinition(name: "remote", description: "", argumentsJsonSchema: "{}"),
            webhook: URL(string: "https://host/tool")!)

        let result = await reg.invoke(
            TelephonyToolInvocation(callId: "call-7", toolName: "remote", argumentsJson: "{\"x\":1}"))
        XCTAssertTrue(result.succeeded)
        XCTAssertEqual(result.resultJson, "{\"ok\":true}")

        // Body must be { "call_id":"call-7", "tool":"remote", "arguments":{"x":1} }.
        let sent = try XCTUnwrap(http.lastRequest)
        XCTAssertEqual(sent.method, .post)
        XCTAssertEqual(sent.contentType, .json)
        XCTAssertEqual(sent.bodyString, "{\"call_id\":\"call-7\",\"tool\":\"remote\",\"arguments\":{\"x\":1}}")
    }

    func testWebhookEmptyBodyBecomesEmptyObject() async throws {
        let (reg, http) = makeRegistry()
        http.on(.post, where: { _ in true }) { _ in .json("") }
        try reg.registerWebhook(
            TelephonyToolDefinition(name: "r", description: "", argumentsJsonSchema: "{}"),
            webhook: URL(string: "https://h/r")!)
        let result = await reg.invoke(
            TelephonyToolInvocation(callId: "c", toolName: "r", argumentsJson: "{}"))
        XCTAssertTrue(result.succeeded)
        XCTAssertEqual(result.resultJson, "{}")
    }

    func testWebhookNonSuccessMapsToError() async throws {
        let (reg, http) = makeRegistry()
        http.on(.post, where: { _ in true }) { _ in .error(500, "boom") }
        try reg.registerWebhook(
            TelephonyToolDefinition(name: "r", description: "", argumentsJsonSchema: "{}"),
            webhook: URL(string: "https://h/r")!)
        let result = await reg.invoke(
            TelephonyToolInvocation(callId: "c", toolName: "r", argumentsJson: "{}"))
        XCTAssertFalse(result.succeeded)
        XCTAssertEqual(result.resultJson, "{}")
        XCTAssertEqual(result.error, "Webhook 500: boom")
    }

    func testWebhookErrorTruncatesTo240WithEllipsis() async throws {
        let (reg, http) = makeRegistry()
        let longBody = String(repeating: "a", count: 300)
        http.on(.post, where: { _ in true }) { _ in .error(400, longBody) }
        try reg.registerWebhook(
            TelephonyToolDefinition(name: "r", description: "", argumentsJsonSchema: "{}"),
            webhook: URL(string: "https://h/r")!)
        let result = await reg.invoke(
            TelephonyToolInvocation(callId: "c", toolName: "r", argumentsJson: "{}"))
        XCTAssertFalse(result.succeeded)
        // "Webhook 400: " + 240 'a' + "…"
        let expected = "Webhook 400: " + String(repeating: "a", count: 240) + "\u{2026}"
        XCTAssertEqual(result.error, expected)
    }

    func testDefinitionsOrderStableAcrossReplacement() throws {
        let (reg, _) = makeRegistry()
        try reg.registerLocal(TelephonyToolDefinition(name: "a", description: "1", argumentsJsonSchema: "{}")) { _ in "{}" }
        try reg.registerLocal(TelephonyToolDefinition(name: "b", description: "1", argumentsJsonSchema: "{}")) { _ in "{}" }
        // Replace "a" with an updated definition — order + count preserved.
        try reg.registerLocal(TelephonyToolDefinition(name: "a", description: "2", argumentsJsonSchema: "{}")) { _ in "{}" }
        let defs = reg.definitions
        XCTAssertEqual(defs.map(\.name), ["a", "b"])
        XCTAssertEqual(defs.first(where: { $0.name == "a" })?.description, "2")
    }
}
