// SpeechVoiceIntentRouterTests.swift
//
// Verifies KeywordVoiceIntentRouter + NullVoiceIntentRouter
// (SpeechVoiceIntentRouter.swift): ordered first-hit matching, named-group
// capture surfacing (trimmed), empty/no-match fallback, and group-name parsing.

import XCTest
@testable import CircleAI

final class SpeechVoiceIntentRouterTests: XCTestCase {

    private func intent(_ name: String, _ pattern: String) throws -> VoiceIntent {
        try VoiceIntent(name: name, pattern: pattern)
    }

    func testGroupNameParsing() {
        XCTAssertEqual(VoiceIntent.parseGroupNames("^play (?<song>.+)$"), ["song"])
        XCTAssertEqual(VoiceIntent.parseGroupNames("(?<a>x)(?<b>y)"), ["a", "b"])
        // Look-behind (?<= and (?<! are NOT capture groups.
        XCTAssertEqual(VoiceIntent.parseGroupNames("(?<=foo)(?<name>bar)"), ["name"])
        // Escaped paren is not a group.
        XCTAssertEqual(VoiceIntent.parseGroupNames("\\(?<not>\\)"), [])
        // Alternate (?'name') syntax.
        XCTAssertEqual(VoiceIntent.parseGroupNames("(?'q'.+)"), ["q"])
    }

    func testFirstMatchingIntentWins() async throws {
        let router = KeywordVoiceIntentRouter(intents: [
            try intent("weather", "weather"),
            try intent("play", "^play (?<song>.+)$"),
        ])
        XCTAssertEqual(router.backendId, "keyword")
        let m = await router.route(transcript: "play bohemian rhapsody")
        XCTAssertEqual(m.intentName, "play")
        XCTAssertEqual(m.transcript, "play bohemian rhapsody")
        XCTAssertEqual(m.captures["song"], "bohemian rhapsody")
    }

    func testOrderingWhenBothMatch() async throws {
        // Both patterns match "play weather report" but "weather" is listed first.
        let router = KeywordVoiceIntentRouter(intents: [
            try intent("weather", "weather"),
            try intent("play", "^play (?<song>.+)$"),
        ])
        let m = await router.route(transcript: "play weather report")
        XCTAssertEqual(m.intentName, "weather")   // first hit wins
        XCTAssertTrue(m.captures.isEmpty)          // "weather" has no named groups
    }

    func testCaptureIsTrimmed() async throws {
        let router = KeywordVoiceIntentRouter(intents: [
            try intent("call", "^call (?<who>.+)$"),
        ])
        // A trailing space inside the capture is trimmed by the router.
        let m = await router.route(transcript: "call   Alice  ")
        XCTAssertEqual(m.intentName, "call")
        XCTAssertEqual(m.captures["who"], "Alice")
    }

    func testEmptyTranscriptReturnsFallbackWithEmptyCaptures() async throws {
        let router = KeywordVoiceIntentRouter(intents: [try intent("x", "x")], fallbackIntentName: "ask-ai")
        let m = await router.route(transcript: "   ")
        XCTAssertEqual(m.intentName, "ask-ai")
        XCTAssertEqual(m.transcript, "")
        XCTAssertTrue(m.captures.isEmpty)
    }

    func testNoMatchReturnsFallback() async throws {
        let router = KeywordVoiceIntentRouter(intents: [try intent("weather", "weather")], fallbackIntentName: "ask-ai")
        let m = await router.route(transcript: "tell me a joke")
        XCTAssertEqual(m.intentName, "ask-ai")
        XCTAssertEqual(m.transcript, "tell me a joke")
        XCTAssertTrue(m.captures.isEmpty)
    }

    func testCustomFallbackName() async throws {
        let router = KeywordVoiceIntentRouter(intents: [], fallbackIntentName: "escalate")
        let m = await router.route(transcript: "anything")
        XCTAssertEqual(m.intentName, "escalate")
    }

    func testNullRouterAlwaysAskAi() async {
        let router = NullVoiceIntentRouter.instance
        XCTAssertEqual(router.backendId, "null")
        let m = await router.route(transcript: "play jazz")
        XCTAssertEqual(m.intentName, "ask-ai")
        XCTAssertEqual(m.transcript, "play jazz")
        XCTAssertTrue(m.captures.isEmpty)
    }
}
