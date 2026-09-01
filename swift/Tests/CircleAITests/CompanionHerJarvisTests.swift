// CompanionHerJarvisTests.swift

import XCTest
@testable import CircleAI

final class CompanionHerJarvisTests: XCTestCase {

    // MARK: - Episodic memory

    private func episode(_ id: String, _ title: String, _ content: String = "") -> EpisodeRecord {
        EpisodeRecord(id: id, at: Date(timeIntervalSince1970: 0),
                      title: title, contentJson: content)
    }

    func testRecallRanksByHowMuchTheQueryOverlaps() async throws {
        let m = TfEpisodicMemory()
        try await m.record(episode("a", "hospital visit", "we went to the hospital"))
        try await m.record(episode("b", "shopping", "we went to the shop"))

        let hits = try await m.recall(query: "hospital")
        XCTAssertEqual(hits.map(\.id), ["a"])
    }

    func testTheTitleIsIndexedNotJustTheContent() async throws {
        // The title is the part a person actually remembers; leaving it out
        // makes "the hospital thing" find nothing.
        let m = TfEpisodicMemory()
        try await m.record(episode("a", "hospital visit", "{}"))
        let hoisted1 = try await m.recall(query: "hospital").map(\.id)
        XCTAssertEqual(hoisted1, ["a"])
    }

    func testMoreOverlapOutranksLess() async throws {
        let m = TfEpisodicMemory()
        try await m.record(episode("few", "hospital", "one mention"))
        try await m.record(episode("many", "hospital hospital", "hospital hospital again"))
        let hoisted2 = try await m.recall(query: "hospital").map(\.id)
        XCTAssertEqual(hoisted2, ["many", "few"])
    }

    func testEqualScoresComeBackInTheSameOrderEveryTime() async throws {
        // Without a tie-break a caller taking the top 1 gets a different answer
        // on each run and cannot reproduce anything.
        let m = TfEpisodicMemory()
        for id in ["c", "a", "b", "d"] { try await m.record(episode(id, "hospital")) }

        let first = try await m.recall(query: "hospital").map(\.id)
        for _ in 0..<5 {
            let hoisted3 = try await m.recall(query: "hospital").map(\.id)
            XCTAssertEqual(hoisted3, first)
        }
        XCTAssertEqual(first, ["a", "b", "c", "d"])
    }

    func testTakeLimitsTheResults() async throws {
        let m = TfEpisodicMemory()
        for id in ["a", "b", "c"] { try await m.record(episode(id, "hospital")) }
        let hoisted4 = try await m.recall(query: "hospital", take: 2).count
        XCTAssertEqual(hoisted4, 2)
    }

    func testANonPositiveTakeIsRefusedRatherThanReturningNothing() async {
        // Silently returning nothing looks identical to "no matches", which is
        // a completely different thing.
        let m = TfEpisodicMemory()
        do {
            _ = try await m.recall(query: "x", take: 0)
            XCTFail("must refuse")
        } catch {
            XCTAssertNotNil(error as? HerJarvisError)
        }
    }

    func testAnEpisodeWithNoIdIsRefused() async {
        let m = TfEpisodicMemory()
        do {
            try await m.record(episode("  ", "title"))
            XCTFail("must refuse")
        } catch {
            XCTAssertNotNil(error as? HerJarvisError)
        }
    }

    func testRecordingTheSameIdTwiceReplacesRatherThanDuplicates() async throws {
        let m = TfEpisodicMemory()
        try await m.record(episode("a", "hospital"))
        try await m.record(episode("a", "hospital", "updated"))
        let hits = try await m.recall(query: "hospital")
        XCTAssertEqual(hits.count, 1)
        XCTAssertEqual(hits[0].contentJson, "updated")
    }

    func testAQueryWithNothingUsableInItMatchesNothing() async throws {
        let m = TfEpisodicMemory()
        try await m.record(episode("a", "hospital"))
        let hoisted5 = try await m.recall(query: "").isEmpty
        XCTAssertTrue(hoisted5)
        let hoisted6 = try await m.recall(query: "!!! ??").isEmpty
        XCTAssertTrue(hoisted6)
        let hoisted7 = try await m.recall(query: "a").isEmpty
        XCTAssertTrue(hoisted7, "one-character terms are dropped")
    }

    func testTermsAreCaseFoldedAndPunctuationSplits() {
        // One-character tokens are dropped because "a" and "I" match everything
        // and rank nothing.
        let tf = TfEpisodicMemory.termFrequency("Hospital, hospital! a b12 x")
        XCTAssertEqual(tf["hospital"], 2)
        XCTAssertEqual(tf["b12"], 1)
        XCTAssertNil(tf["a"])
        XCTAssertNil(tf["x"])
    }

    func testScoreIsTheDotProductAndAnUnknownDocumentScoresZero() {
        XCTAssertEqual(TfEpisodicMemory.score(["a": 2, "b": 1], ["a": 3, "b": 4]), 10)
        XCTAssertEqual(TfEpisodicMemory.score(["a": 1], nil), 0)
        XCTAssertEqual(TfEpisodicMemory.score(["a": 1], ["z": 9]), 0)
    }

    // MARK: - Identity sync

    func testPullReturnsOnlyWhatCameAfterTheCursor() async throws {
        // A puller that has seen cursor 1 must not be handed it again, or a
        // device syncs the same change twice.
        let s = JsonIdentitySync()
        try await s.push(deltaJson: "{\"a\":1}")
        try await s.push(deltaJson: "{\"b\":2}")

        let all = try await s.pull(sinceCursor: "0")
        XCTAssertTrue(all.contains("{\"a\":1}"))
        XCTAssertTrue(all.contains("{\"b\":2}"))
        XCTAssertTrue(all.contains("\"cursor\":2"))

        let afterFirst = try await s.pull(sinceCursor: "1")
        XCTAssertFalse(afterFirst.contains("{\"a\":1}"))
        XCTAssertTrue(afterFirst.contains("{\"b\":2}"))
    }

    func testTheCursorIsMonotonicAndNeverReused() async throws {
        let s = JsonIdentitySync()
        for i in 1...5 {
            try await s.push(deltaJson: "{\"n\":\(i)}")
            XCTAssertEqual(s.currentCursor, Int64(i))
        }
    }

    func testPullingWithNothingNewReturnsTheCursorUnchanged() async throws {
        // "Nothing new" and "the log was reset" look identical from a payload
        // alone, which is why the cursor is echoed.
        let s = JsonIdentitySync()
        try await s.push(deltaJson: "{}")
        let out = try await s.pull(sinceCursor: "1")
        XCTAssertTrue(out.contains("\"cursor\":1"))
        XCTAssertTrue(out.contains("\"deltas\":[]"))
    }

    func testAFirstTimePullerSendingNothingUsableStartsFromZero() async throws {
        // Refusing an unparseable cursor would make the FIRST sync the one that
        // fails, which is the worst one to fail.
        let s = JsonIdentitySync()
        try await s.push(deltaJson: "{\"a\":1}")
        for cursor in ["", "null", "not a number", "-1"] {
            let hoisted8 = try await s.pull(sinceCursor: cursor).contains("{\"a\":1}")
            XCTAssertTrue(hoisted8, cursor)
        }
    }

    func testDeltasAreSplicedInRawNotReEncoded() async throws {
        // Re-encoding would turn each delta into a JSON string CONTAINING JSON,
        // and the puller would have to decode twice.
        let s = JsonIdentitySync()
        try await s.push(deltaJson: "{\"nested\":{\"x\":1}}")
        let out = try await s.pull(sinceCursor: "0")
        XCTAssertTrue(out.contains("\"deltas\":[{\"nested\":{\"x\":1}}]"))
        XCTAssertFalse(out.contains("\\\""))
    }

    func testTheResultIsValidJson() async throws {
        let s = JsonIdentitySync()
        try await s.push(deltaJson: "{\"a\":1}")
        try await s.push(deltaJson: "{\"b\":[1,2]}")
        let out = try await s.pull(sinceCursor: "0")

        let parsed = try JSONSerialization.jsonObject(with: Data(out.utf8)) as? [String: Any]
        XCTAssertEqual(parsed?["cursor"] as? Int, 2)
        XCTAssertEqual((parsed?["deltas"] as? [Any])?.count, 2)
    }

    // MARK: - Knowledge graph

    private func node(_ id: String) -> KnowledgeNode {
        KnowledgeNode(id: id, kind: "person", name: id)
    }

    func testNeighboursWalksTheOutEdges() async throws {
        let g = AdjacencyPersonalKnowledgeGraph()
        try await g.upsert(node: node("thabo"))
        try await g.upsert(node: node("nandi"))
        try await g.upsert(relation: KnowledgeRelation(fromId: "thabo", toId: "nandi",
                                                       relation: "sister"))
        let out = try await g.neighbours(of: "thabo")
        XCTAssertEqual(out.map(\.id), ["nandi"])
    }

    func testEdgesAreDirected() async throws {
        let g = AdjacencyPersonalKnowledgeGraph()
        try await g.upsert(node: node("a"))
        try await g.upsert(node: node("b"))
        try await g.upsert(relation: KnowledgeRelation(fromId: "a", toId: "b", relation: "knows"))
        let hoisted9 = try await g.neighbours(of: "b").isEmpty
        XCTAssertTrue(hoisted9)
    }

    func testTheSameRelationAssertedTwiceIsOneEdge() async throws {
        // Appending would make a node appear twice in its own neighbour list
        // for no reason a caller could see.
        let g = AdjacencyPersonalKnowledgeGraph()
        try await g.upsert(node: node("a"))
        try await g.upsert(node: node("b"))
        for _ in 0..<3 {
            try await g.upsert(relation: KnowledgeRelation(fromId: "a", toId: "b",
                                                           relation: "knows"))
        }
        let hoisted10 = try await g.neighbours(of: "a").count
        XCTAssertEqual(hoisted10, 1)
    }

    func testTwoDifferentRelationsToTheSameNodeAreBothKept() async throws {
        let g = AdjacencyPersonalKnowledgeGraph()
        try await g.upsert(node: node("a"))
        try await g.upsert(node: node("b"))
        try await g.upsert(relation: KnowledgeRelation(fromId: "a", toId: "b", relation: "knows"))
        try await g.upsert(relation: KnowledgeRelation(fromId: "a", toId: "b", relation: "works_with"))
        let hoisted11 = try await g.neighbours(of: "a").count
        XCTAssertEqual(hoisted11, 2)
    }

    func testAnEdgeToANodeThatDoesNotExistYetIsSkippedNotAHole() async throws {
        // A graph assembled from two sources routinely has edges arriving before
        // their nodes.
        let g = AdjacencyPersonalKnowledgeGraph()
        try await g.upsert(node: node("a"))
        try await g.upsert(relation: KnowledgeRelation(fromId: "a", toId: "ghost",
                                                       relation: "knows"))
        let hoisted12 = try await g.neighbours(of: "a").isEmpty
        XCTAssertTrue(hoisted12)

        // And it appears as soon as the node arrives.
        try await g.upsert(node: node("ghost"))
        let hoisted13 = try await g.neighbours(of: "a").map(\.id)
        XCTAssertEqual(hoisted13, ["ghost"])
    }

    func testUpsertingANodeReplacesIt() async throws {
        let g = AdjacencyPersonalKnowledgeGraph()
        try await g.upsert(node: KnowledgeNode(id: "a", kind: "person", name: "old"))
        try await g.upsert(node: KnowledgeNode(id: "a", kind: "person", name: "new"))
        try await g.upsert(node: node("b"))
        try await g.upsert(relation: KnowledgeRelation(fromId: "b", toId: "a", relation: "knows"))
        let hoisted14 = try await g.neighbours(of: "b").first?.name
        XCTAssertEqual(hoisted14, "new")
    }

    func testBlankIdsAreRefused() async {
        let g = AdjacencyPersonalKnowledgeGraph()
        do {
            try await g.upsert(node: KnowledgeNode(id: " ", kind: "x", name: "y"))
            XCTFail("must refuse")
        } catch { XCTAssertNotNil(error as? HerJarvisError) }

        do {
            _ = try await g.neighbours(of: "")
            XCTFail("must refuse")
        } catch { XCTAssertNotNil(error as? HerJarvisError) }
    }

    func testAnUnknownNodeHasNoNeighboursRatherThanFailing() async throws {
        let g = AdjacencyPersonalKnowledgeGraph()
        let hoisted15 = try await g.neighbours(of: "nobody").isEmpty
        XCTAssertTrue(hoisted15)
    }

    // MARK: - Live world knowledge

    private func fact(_ topic: String, _ body: String = "{}") -> WorldFact {
        WorldFact(topic: topic, summaryJson: body, at: Date(timeIntervalSince1970: 0))
    }

    func testASubscriberReceivesFactsOnItsTopic() async throws {
        let k = TopicLiveWorldKnowledge()
        let stream = k.subscribe(topics: ["load-shedding"])

        k.publish(fact("load-shedding", "{\"stage\":4}"))

        var iterator = stream.makeAsyncIterator()
        let received = await iterator.next()
        XCTAssertEqual(received?.summaryJson, "{\"stage\":4}")
    }

    func testFactsOnOtherTopicsAreNotDelivered() async throws {
        let k = TopicLiveWorldKnowledge()
        let stream = k.subscribe(topics: ["load-shedding"])

        k.publish(fact("weather"))
        k.publish(fact("load-shedding", "{\"mine\":true}"))

        var iterator = stream.makeAsyncIterator()
        let received = await iterator.next()
        XCTAssertEqual(received?.summaryJson, "{\"mine\":true}")
    }

    func testOneSubscriptionCanCoverSeveralTopics() async throws {
        let k = TopicLiveWorldKnowledge()
        let stream = k.subscribe(topics: ["a", "b"])
        k.publish(fact("a", "{\"first\":1}"))
        k.publish(fact("b", "{\"second\":2}"))

        var iterator = stream.makeAsyncIterator()
        let one = await iterator.next()
        let two = await iterator.next()
        XCTAssertEqual([one?.summaryJson, two?.summaryJson],
                       ["{\"first\":1}", "{\"second\":2}"])
    }

    func testEverySubscriberToATopicGetsTheFact() async throws {
        let k = TopicLiveWorldKnowledge()
        let a = k.subscribe(topics: ["t"])
        let b = k.subscribe(topics: ["t"])
        k.publish(fact("t", "{\"n\":1}"))

        var ia = a.makeAsyncIterator(), ib = b.makeAsyncIterator()
        let ra = await ia.next(), rb = await ib.next()
        XCTAssertEqual(ra?.summaryJson, "{\"n\":1}")
        XCTAssertEqual(rb?.summaryJson, "{\"n\":1}")
    }

    func testAFactWithNoSubscribersIsDroppedNotBuffered() {
        // The alternative is an unbounded buffer per topic filled by a feed that
        // runs whether anyone is listening, which on a phone is a memory leak
        // with a schedule.
        let k = TopicLiveWorldKnowledge()
        XCTAssertEqual(k.subscriberCount(topic: "quiet"), 0)
        for _ in 0..<10_000 { k.publish(fact("quiet")) }
        XCTAssertEqual(k.subscriberCount(topic: "quiet"), 0)
    }

    func testSubscriberCountDistinguishesQuietFromUnheard() {
        // "The feed is quiet" and "nobody is listening and every fact is being
        // dropped" look identical from outside without this.
        let k = TopicLiveWorldKnowledge()
        XCTAssertEqual(k.subscriberCount(topic: "t"), 0)
        let s = k.subscribe(topics: ["t"])
        XCTAssertEqual(k.subscriberCount(topic: "t"), 1)
        _ = s
    }

    func testEndingASubscriptionRemovesIt() async {
        let k = TopicLiveWorldKnowledge()
        do {
            let s = k.subscribe(topics: ["t"])
            XCTAssertEqual(k.subscriberCount(topic: "t"), 1)
            var i = s.makeAsyncIterator()
            k.publish(fact("t"))
            _ = await i.next()
        }
        // The stream and its iterator are gone; the broker must not keep the
        // continuation alive and go on yielding into nothing.
        try? await Task.sleep(nanoseconds: 30_000_000)
        XCTAssertEqual(k.subscriberCount(topic: "t"), 0)
    }
}
