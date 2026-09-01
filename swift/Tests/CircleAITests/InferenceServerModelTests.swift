// InferenceServerModelTests.swift
//
// These are wire shapes, so the tests are about the WIRE: the exact keys, not
// the Swift property names. A renamed key does not fail anything at compile
// time — the field simply arrives as nil in every existing client.

import XCTest
@testable import CircleAI

final class InferenceServerModelTests: XCTestCase {

    private func keys<T: Encodable>(_ value: T) throws -> Set<String> {
        let data = try JSONEncoder().encode(value)
        let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        return Set(obj?.keys ?? [:].keys)
    }

    // MARK: - The wire keys

    func testDiagnosticsUsesTheServersSnakeCaseKeys() throws {
        XCTAssertEqual(try keys(DiagnosticsResponse()), [
            "server_version", "uptime_seconds", "started_at", "loaded_models", "counters",
        ], "absent optionals are omitted; the rest are the server's spelling")
    }

    func testEveryOptionalSectionAppearsUnderItsOwnKeyWhenPresent() throws {
        let full = DiagnosticsResponse(
            hostProfile: HostProfileDto(),
            backendSelection: BackendSelectionDto(),
            nativeRuntime: NativeRuntimePathsDto())
        let k = try keys(full)
        XCTAssertTrue(k.contains("host_profile"))
        XCTAssertTrue(k.contains("backend_selection"))
        XCTAssertTrue(k.contains("native_runtime"))
    }

    func testLoadedModelKeysMatchTheOpenAiShapedClients() throws {
        XCTAssertEqual(try keys(LoadedModelInfo(id: "qwen")),
                       ["id", "object", "owned_by", "supports_streaming"])
    }

    func testLoadedModelDefaultsAreTheOnesClientsExpect() {
        let m = LoadedModelInfo(id: "qwen")
        XCTAssertEqual(m.object, "model")
        XCTAssertEqual(m.ownedBy, "circleai")
        XCTAssertTrue(m.supportsStreaming)
    }

    func testHostProfileKeysAreAllSnakeCase() throws {
        let k = try keys(HostProfileDto(gpuVendor: "amd", gpuModel: "x",
                                        gpuVramBytes: 1, npuVendor: "y", npuModel: "z"))
        for key in k {
            XCTAssertEqual(key, key.lowercased(), key)
            XCTAssertFalse(key.contains("-"), key)
        }
        XCTAssertTrue(k.contains("logical_cores"))
        XCTAssertTrue(k.contains("gpu_vram_bytes"))
        XCTAssertTrue(k.contains("npu_vendor"))
    }

    func testCounterKeysAreTheServersSpelling() throws {
        XCTAssertEqual(try keys(CounterSnapshot()),
                       ["total_requests", "active_requests",
                        "rejected_requests", "failed_requests"])
    }

    func testNativeRuntimeKeepsTheMnnbridgeSpellingWithNoUnderscore() throws {
        // "mnnbridge_path", not "mnn_bridge_path". It is what the server sends
        // and it is the kind of thing a tidy-up would silently change.
        let k = try keys(NativeRuntimePathsDto())
        XCTAssertTrue(k.contains("mnnbridge_path"))
        XCTAssertTrue(k.contains("mnnbridge_loaded"))
        XCTAssertFalse(k.contains("mnn_bridge_path"))
    }

    func testTheTwoRuntimeErrorsStayApart() throws {
        // A runtime that flattened and failed to preload is a different problem
        // from one that never unpacked; one "error" field loses which stage.
        let d = NativeRuntimePathsDto(flattenError: "no space", preloadError: nil)
        let k = try keys(d)
        XCTAssertTrue(k.contains("flatten_error"))
        XCTAssertFalse(k.contains("preload_error"), "an absent error is omitted")
    }

    func testHealthDefaultsToOk() throws {
        XCTAssertEqual(HealthResponse().status, "ok")
        XCTAssertEqual(try keys(HealthResponse()), ["status", "at"])
    }

    // MARK: - Round trips

    func testDiagnosticsRoundTripsThroughItsOwnJson() throws {
        let original = DiagnosticsResponse(
            serverVersion: "1.1.0",
            uptimeSeconds: 42.5,
            startedAt: Date(timeIntervalSince1970: 1_700_000_000),
            loadedModels: [LoadedModelInfo(id: "qwen"), LoadedModelInfo(id: "kimi")],
            hostProfile: HostProfileDto(os: "Linux", arch: "arm64", logicalCores: 8,
                                        ramBytes: 8_000_000_000),
            backendSelection: BackendSelectionDto(backend: "mnn", tier: "cpu",
                                                  rationale: "no GPU present"),
            counters: CounterSnapshot(totalRequests: 10, activeRequests: 1,
                                      rejectedRequests: 2, failedRequests: 3),
            nativeRuntime: NativeRuntimePathsDto(rid: "linux-arm64", mnnBridgeLoaded: true))

        let data = try JSONEncoder().encode(original)
        let back = try JSONDecoder().decode(DiagnosticsResponse.self, from: data)
        XCTAssertEqual(back, original)
    }

    func testTheRationaleSurvivesBecauseItIsTheUsefulField() throws {
        // "Which backend" without "why" turns every performance question into a
        // guess.
        let s = BackendSelectionDto(backend: "cpu", tier: "fallback",
                                    rationale: "GPU present but VRAM below the model's floor")
        let back = try JSONDecoder().decode(BackendSelectionDto.self,
                                           from: try JSONEncoder().encode(s))
        XCTAssertEqual(back.rationale, s.rationale)
    }

    // MARK: - Options

    func testTheServerDefaultsAreTheDocumentedOnes() {
        let o = InferenceServerOptions()
        XCTAssertEqual(InferenceServerOptions.sectionName, "CircleAIServer")
        XCTAssertEqual(o.maxConcurrentRequests, 16)
        XCTAssertEqual(o.requestTimeoutSeconds, 120)
        XCTAssertTrue(o.runtimeCacheRoot.contains("CircleAI"))
        XCTAssertTrue(o.modelStorageRoot.contains("CircleAI"))
    }

    func testApiKeyAuthIsOnByDefaultAndJwtIsNot() {
        // A JWT scheme that is ON with an empty signing key would accept tokens
        // nobody signed.
        let a = AuthOptions()
        XCTAssertTrue(a.apiKey.enabled)
        XCTAssertFalse(a.jwt.enabled)
        XCTAssertTrue(a.jwt.signingKey.isEmpty)
    }

    func testTheApiKeyHeaderNameIsTheServersOwn() {
        XCTAssertEqual(ApiKeyOptions().headerName, "X-CircleAI-Api-Key")
        XCTAssertTrue(ApiKeyOptions().keys.isEmpty, "no key ships in the box")
    }

    func testOptionsRoundTrip() throws {
        let o = InferenceServerOptions(
            maxConcurrentRequests: 4,
            auth: AuthOptions(apiKey: ApiKeyOptions(keys: ["k1", "k2"]),
                              jwt: JwtOptions(enabled: true, issuer: "circle",
                                              audience: "app", signingKey: "s")))
        let back = try JSONDecoder().decode(InferenceServerOptions.self,
                                            from: try JSONEncoder().encode(o))
        XCTAssertEqual(back, o)
    }

    // MARK: - Auth schemes

    func testTheJwtSchemeIsSpelledBearerBecauseThatIsWhatGoesOnTheWire() {
        XCTAssertEqual(AuthSchemes.jwt, "Bearer")
        XCTAssertEqual(AuthSchemes.apiKey, "ApiKey")
        XCTAssertEqual(AuthSchemes.authenticatedPolicy, "Authenticated")
    }

    func testTheSchemeNamesAreDistinct() {
        // A typo here is an endpoint requiring a policy nothing satisfies, which
        // reads as "authentication is broken".
        let all = [AuthSchemes.jwt, AuthSchemes.apiKey, AuthSchemes.authenticatedPolicy]
        XCTAssertEqual(Set(all).count, 3)
    }

    // MARK: - SSE framing

    func testAChunkIsDataPrefixedAndBlankLineTerminated() {
        let framed = String(decoding: ServerSentEventsWriter.frame(json: "{\"a\":1}"),
                            as: UTF8.self)
        XCTAssertEqual(framed, "data: {\"a\":1}\n\n")
    }

    func testTheTerminatorIsTheOneOpenAiClientsWaitFor() {
        // A stream that just closes leaves those clients hanging until their own
        // timeout.
        XCTAssertEqual(ServerSentEventsWriter.terminator, "data: [DONE]\n\n")
        XCTAssertEqual(String(decoding: ServerSentEventsWriter.terminatorFrame(), as: UTF8.self),
                       "data: [DONE]\n\n")
    }

    func testTheNginxBufferingHeaderIsPresent() {
        // The one that is easy to leave out and impossible to debug: nginx
        // buffers the whole stream by default, so streaming works perfectly in
        // development and arrives all at once, at the end, in production.
        XCTAssertEqual(ServerSentEventsWriter.headers["X-Accel-Buffering"], "no")
        XCTAssertEqual(ServerSentEventsWriter.headers["Content-Type"],
                       "text/event-stream; charset=utf-8")
        XCTAssertTrue(ServerSentEventsWriter.headers["Cache-Control"]!.contains("no-cache"))
        XCTAssertEqual(ServerSentEventsWriter.headers["Connection"], "keep-alive")
    }

    func testAnEncodablePayloadIsFramedAsOneEvent() throws {
        let framed = String(decoding: try ServerSentEventsWriter.frame(HealthResponse(
            status: "ok", at: Date(timeIntervalSince1970: 0))), as: UTF8.self)
        XCTAssertTrue(framed.hasPrefix("data: "))
        XCTAssertTrue(framed.hasSuffix("\n\n"))
        // Exactly ONE event: a payload whose own JSON contained a blank line
        // would be read as two, and an SSE parser would drop half of it.
        XCTAssertEqual(framed.components(separatedBy: "\n\n").count, 2)
    }

    func testEachFrameIsIndependentlyParseable() throws {
        let a = try ServerSentEventsWriter.frame(LoadedModelInfo(id: "one"))
        let b = try ServerSentEventsWriter.frame(LoadedModelInfo(id: "two"))
        var stream = Data(); stream.append(a); stream.append(b)
        stream.append(ServerSentEventsWriter.terminatorFrame())

        let events = String(decoding: stream, as: UTF8.self)
            .components(separatedBy: "\n\n")
            .filter { !$0.isEmpty }
        XCTAssertEqual(events.count, 3)
        XCTAssertEqual(events.last, "data: [DONE]")

        let first = events[0].dropFirst("data: ".count)
        let decoded = try JSONDecoder().decode(LoadedModelInfo.self,
                                               from: Data(first.utf8))
        XCTAssertEqual(decoded.id, "one")
    }
}
