import XCTest
@testable import CircleAI

/// The Aho-Corasick keyword graph: several wake phrases watched at once.
final class VoiceKwsGraphTests: XCTestCase {

    private func graph(_ tokens: [[Int]], phrases: [String]) -> KwsContextGraph {
        KwsContextGraph(tokenIds: tokens, contextScore: 1.5, acThreshold: 0.25, phrases: phrases)
    }

    /// Walk a token sequence from the root and collect the phrases completed.
    private func walk(_ g: KwsContextGraph, _ tokens: [Int]) -> [String] {
        var state = g.root
        var hits: [String] = []
        for t in tokens {
            let step = g.forwardOneStep(state, token: t)
            state = step.state
            if let m = step.matched { hits.append(m.phrase) }
        }
        return hits
    }

    // MARK: - Matching

    func testASinglePhraseIsMatched() {
        let g = graph([[1, 2, 3]], phrases: ["hey b"])
        XCTAssertEqual(walk(g, [1, 2, 3]), ["hey b"])
    }

    func testAPartialWalkMatchesNothing() {
        let g = graph([[1, 2, 3]], phrases: ["hey b"])
        XCTAssertTrue(walk(g, [1, 2]).isEmpty)
    }

    func testTwoUnrelatedPhrasesBothMatch() {
        let g = graph([[1, 2], [7, 8]], phrases: ["one", "two"])
        XCTAssertEqual(walk(g, [1, 2, 7, 8]), ["one", "two"])
    }

    // The fail links are what make this work: after a dead end the walk
    // resumes at the longest proper suffix rather than restarting.
    func testAFalseStartDoesNotLoseTheRealMatch() {
        let g = graph([[1, 2, 3]], phrases: ["hey b"])
        XCTAssertEqual(walk(g, [1, 1, 2, 3]), ["hey b"])
    }

    func testJunkBetweenAttemptsIsIgnored() {
        let g = graph([[1, 2]], phrases: ["p"])
        XCTAssertEqual(walk(g, [9, 9, 1, 2]), ["p"])
    }

    // When two phrases END on the SAME token the longer one is what is
    // reported - forwardOneStep prefers the node itself over its output link.
    // The output link is still built, which is what lets a shorter phrase fire
    // when it ends somewhere the longer one does not.
    func testTheLongerPhraseWinsWhenBothEndTogether() {
        // "b" is tokens [2,3]; "a b" is tokens [1,2,3].
        let g = graph([[2, 3], [1, 2, 3]], phrases: ["b", "a b"])
        XCTAssertEqual(walk(g, [1, 2, 3]), ["a b"])

        // ...and the suffix link is present on that end node.
        let end = g.root.next[1]!.next[2]!.next[3]!
        XCTAssertEqual(end.output?.phrase, "b")
    }

    // The suffix phrase DOES fire on its own.
    func testTheShorterPhraseFiresWhenItStandsAlone() {
        let g = graph([[2, 3], [1, 2, 3]], phrases: ["b", "a b"])
        XCTAssertEqual(walk(g, [9, 2, 3]), ["b"])
    }

    // MARK: - Shadowing

    // A phrase whose PREFIX is itself a complete phrase can never fire, and
    // somebody needs to be told which one.
    func testAShadowedPhraseIsReportedNotSilentlyDropped() {
        let g = graph([[1, 2], [1, 2, 3]], phrases: ["short", "short and long"])
        XCTAssertEqual(g.shadowedPhrases.count, 1)
        XCTAssertEqual(g.shadowedPhrases[0].phrase, "short and long")
        XCTAssertEqual(g.shadowedPhrases[0].shadowedBy, "short")
    }

    func testUnrelatedPhrasesShadowNothing() {
        let g = graph([[1, 2], [7, 8]], phrases: ["one", "two"])
        XCTAssertTrue(g.shadowedPhrases.isEmpty)
    }

    func testAShadowedPhraseIsNamedByIndexWhenUnnamed() {
        let g = KwsContextGraph(tokenIds: [[1, 2], [1, 2, 3]],
                                contextScore: 1.0, acThreshold: 0.2)
        XCTAssertEqual(g.shadowedPhrases.first?.phrase, "#1")
    }

    // MARK: - Scores

    func testTheGraphDefaultIsUsedWhenNoScoreIsGiven() {
        let g = KwsContextGraph(tokenIds: [[1]], contextScore: 2.5,
                                acThreshold: 0.3, phrases: ["p"])
        XCTAssertEqual(g.root.next[1]?.tokenScore, 2.5)
        XCTAssertEqual(g.root.next[1]?.acThreshold, 0.3)
    }

    func testAnExplicitScoreOverridesTheDefault() {
        let g = KwsContextGraph(tokenIds: [[1]], contextScore: 2.5, acThreshold: 0.3,
                                scores: [7.0], phrases: ["p"], acThresholds: [0.9])
        XCTAssertEqual(g.root.next[1]?.tokenScore, 7.0)
        XCTAssertEqual(g.root.next[1]?.acThreshold, 0.9)
    }

    // A shared prefix keeps the HIGHER boost, so one phrase cannot quietly
    // weaken another that starts the same way.
    func testASharedPrefixKeepsTheHigherBoost() {
        let g = KwsContextGraph(tokenIds: [[1, 2], [1, 3]], contextScore: 1.0,
                                acThreshold: 0.2, scores: [1.0, 9.0],
                                phrases: ["low", "high"])
        XCTAssertEqual(g.root.next[1]?.tokenScore, 9.0)
    }

    // Falling back must not re-award the prefix that was already counted: a
    // false start then a clean run must score the same as the clean run plus
    // the one wasted step.
    func testFallingBackDoesNotDoubleCountTheSharedPrefix() {
        let g = graph([[1, 2, 3]], phrases: ["p"])

        func total(_ tokens: [Int]) -> Float {
            var state = g.root
            var sum: Float = 0
            for t in tokens {
                let step = g.forwardOneStep(state, token: t)
                state = step.state
                sum += step.score
            }
            return sum
        }

        // The repeated token scores ZERO: the fallback score is the DIFFERENCE
        // in node score, so a prefix already counted is not counted again.
        XCTAssertEqual(total([1, 1, 2, 3]), total([1, 2, 3]), accuracy: 1e-5)

        let wasted = g.forwardOneStep(g.forwardOneStep(g.root, token: 1).state, token: 1)
        XCTAssertEqual(wasted.score, 0, accuracy: 1e-5)
    }

    // MARK: - End state

    func testIsMatchedReportsTheEndNode() {
        let g = graph([[1, 2]], phrases: ["p"])
        let step = g.forwardOneStep(g.forwardOneStep(g.root, token: 1).state, token: 2)
        let m = g.isMatched(step.state)
        XCTAssertTrue(m.matched)
        XCTAssertEqual(m.state?.phrase, "p")
    }

    func testIsMatchedIsFalseMidPhrase() {
        let g = graph([[1, 2]], phrases: ["p"])
        let step = g.forwardOneStep(g.root, token: 1)
        XCTAssertFalse(g.isMatched(step.state).matched)
    }

    func testTheRootIsItsOwnFallback() {
        let g = graph([[1]], phrases: ["p"])
        XCTAssertEqual(g.root.token, -1)
        // An unknown token from the root goes nowhere and matches nothing.
        let step = g.forwardOneStep(g.root, token: 99)
        XCTAssertNil(step.matched)
    }

    // MARK: - Levels

    func testEachNodeKnowsItsDepth() {
        let g = graph([[1, 2, 3]], phrases: ["p"])
        let a = g.root.next[1]!
        let b = a.next[2]!
        let c = b.next[3]!
        XCTAssertEqual([a.level, b.level, c.level], [1, 2, 3])
        XCTAssertTrue(c.isEnd)
        XCTAssertFalse(b.isEnd)
        XCTAssertEqual(c.prefixLength, 3)
    }

    // MARK: - Audio seams

    func testTheNullPlayerSwallowsAudio() async throws {
        try await NullAudioPlayer.instance.play(pcm: Data([1, 2, 3, 4]), sampleRate: 16_000,
                                                channels: 1, bitsPerSample: 16)
        await NullAudioPlayer.instance.close()
    }

    func testAnExchangeCarriesBothHalves() {
        let e = VoiceExchange(heard: "what is the time",
                              replied: "just after three",
                              at: Date(timeIntervalSince1970: 1_782_896_400))
        XCTAssertEqual(e.heard, "what is the time")
        XCTAssertEqual(e.replied, "just after three")
    }
}
