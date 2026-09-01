// TailPrimitivesTests.swift

import XCTest
@testable import CircleAI

final class TailPrimitivesTests: XCTestCase {

    // MARK: - Version vectors

    private func vv(_ d: [String: Int64]) -> VersionVector { VersionVector(clocks: d) }

    func testAnAbsentClockIsZeroNotMissing() {
        // A replica that has never been heard from has simply made no changes.
        XCTAssertEqual(vv(["a": 3]).clock(for: "b"), 0)
        XCTAssertEqual(VersionVector().clock(for: "anything"), 0)
    }

    func testMergeIsThePairwiseMaximum() {
        let m = SyncReconciliation.merge(vv(["a": 3, "b": 1]), vv(["b": 7, "c": 2]))
        XCTAssertEqual(m.clocks, ["a": 3, "b": 7, "c": 2])
    }

    func testMergeIsCommutativeAndIdempotent() {
        // Two devices that have never met still have to agree on the result.
        let a = vv(["a": 3, "b": 1]), b = vv(["b": 7, "c": 2])
        XCTAssertEqual(SyncReconciliation.merge(a, b), SyncReconciliation.merge(b, a))
        XCTAssertEqual(SyncReconciliation.merge(a, a), a)
    }

    func testMergingWithNothingChangesNothing() {
        let a = vv(["a": 3])
        XCTAssertEqual(SyncReconciliation.merge(a, VersionVector()), a)
    }

    func testDominationNeedsSomethingStrictlyGreater() {
        // Without that half, two IDENTICAL vectors would each dominate the
        // other, and a caller using this to pick a winner would pick both.
        let a = vv(["a": 3, "b": 1])
        XCTAssertFalse(SyncReconciliation.aDominatesB(a, a))
        XCTAssertTrue(SyncReconciliation.aDominatesB(vv(["a": 4, "b": 1]), a))
    }

    func testNeitherSideDominatesAConcurrentEdit() {
        // The case the whole vector exists for: two devices each ahead of the
        // other on their own clock. Nobody wins, and the caller must merge.
        let a = vv(["a": 2, "b": 0]), b = vv(["a": 0, "b": 2])
        XCTAssertFalse(SyncReconciliation.aDominatesB(a, b))
        XCTAssertFalse(SyncReconciliation.aDominatesB(b, a))
    }

    func testDominationTreatsAMissingClockAsZero() {
        XCTAssertTrue(SyncReconciliation.aDominatesB(vv(["a": 1, "b": 1]), vv(["a": 1])))
        XCTAssertFalse(SyncReconciliation.aDominatesB(vv(["a": 1]), vv(["a": 1, "b": 1])))
    }

    func testLastWriterWinsAndTiesGoToTheFirstArgument() {
        // Two devices writing in the same millisecond is ordinary on a mesh, so
        // the rule is stated rather than left to whichever comparison won.
        let t = Date(timeIntervalSince1970: 1_000)
        let later = t.addingTimeInterval(1)

        XCTAssertEqual(SyncReconciliation.lastWriterWins((t, "old"), (later, "new")).value, "new")
        XCTAssertEqual(SyncReconciliation.lastWriterWins((later, "new"), (t, "old")).value, "new")
        XCTAssertEqual(SyncReconciliation.lastWriterWins((t, "first"), (t, "second")).value, "first")
    }

    func testAVersionVectorRoundTripsThroughJson() throws {
        let a = vv(["a": 3, "b": 7])
        let back = try JSONDecoder().decode(VersionVector.self, from: try JSONEncoder().encode(a))
        XCTAssertEqual(back, a)
    }

    // MARK: - Language registry

    func testTheRegistryFindsEveryKnownLanguage() {
        let r = DefaultLanguageRegistry()
        XCTAssertEqual(r.getAll().count, KnownLanguages.all.count)
        for t in KnownLanguages.all {
            XCTAssertEqual(r.getByBcpTag(t.bcpTag)?.bcpTag, t.bcpTag)
            XCTAssertTrue(r.isSupported(t.bcpTag))
        }
    }

    func testTagLookupIsCaseInsensitive() {
        // A BCP-47 tag is case-insensitive, and "en-za" arriving lower-cased
        // must not read as an unknown language.
        let r = DefaultLanguageRegistry()
        guard let any = KnownLanguages.all.first else { return XCTFail("no languages") }
        XCTAssertNotNil(r.getByBcpTag(any.bcpTag.lowercased()))
        XCTAssertNotNil(r.getByBcpTag(any.bcpTag.uppercased()))
        XCTAssertTrue(r.isSupported(any.bcpTag.uppercased()))
    }

    func testAnUnknownTagIsNilNotAGuess() {
        let r = DefaultLanguageRegistry()
        XCTAssertNil(r.getByBcpTag("xx-QQ"))
        XCTAssertFalse(r.isSupported("xx-QQ"))
    }

    func testEveryLanguageIsReachableFromItsRegion() {
        let r = DefaultLanguageRegistry()
        for t in KnownLanguages.all {
            XCTAssertTrue(r.getForRegion(t.primaryRegion).contains { $0.bcpTag == t.bcpTag },
                          "\(t.bcpTag) is not listed under \(t.primaryRegion)")
        }
    }

    func testRegionLookupIsCaseInsensitiveAndEmptyForNowhere() {
        let r = DefaultLanguageRegistry()
        guard let any = KnownLanguages.all.first else { return XCTFail("no languages") }
        XCTAssertFalse(r.getForRegion(any.primaryRegion.lowercased()).isEmpty)
        XCTAssertTrue(r.getForRegion("QQ").isEmpty)
    }

    func testSouthAfricaCarriesMoreThanOneLanguage() {
        // The eleven official languages are the reason this is a lookup and not
        // a dictionary keyed by region.
        XCTAssertGreaterThan(DefaultLanguageRegistry().getForRegion("ZA").count, 1)
    }

    // MARK: - Null detector

    func testTheNullDetectorSaysUnknownRatherThanGuessingEnglish() async throws {
        // A detector that quietly answers "English" makes every downstream
        // choice wrong in a way that looks like a working system.
        let d = NullLanguageDetector.instance
        let one = try await d.detect(text: "sawubona, unjani na")
        XCTAssertEqual(one.language.bcpTag, LanguageTag.unknown.bcpTag)
        XCTAssertEqual(one.confidence, 0)
        XCTAssertFalse(one.isReliable)
    }

    func testTheNullDetectorReturnsOneUnreliableCandidate() async throws {
        let many = try await NullLanguageDetector.instance.detectMultiple(text: "hello", maxResults: 3)
        XCTAssertEqual(many.count, 1)
        XCTAssertFalse(many[0].isReliable)
    }

    // MARK: - Provider ids

    func testProviderIdsAreLowercaseAndUnique() {
        // A typo in one of these is a provider that is configured, present, and
        // never selected, with nothing anywhere reporting a problem.
        XCTAssertEqual(Set(CloudProviderIds.all).count, CloudProviderIds.all.count)
        for id in CloudProviderIds.all {
            XCTAssertEqual(id, id.lowercased())
            XCTAssertFalse(id.contains(" "))
        }
        XCTAssertEqual(CloudProviderIds.deepSeek, "deepseek")
        XCTAssertEqual(CloudProviderIds.all.count, 7)
    }

    func testGeneratorIdsAreDistinctFromChatProviderIds() {
        // "openai-images" and "openai" are different registrations; collapsing
        // them resolves an image request to a chat model.
        XCTAssertEqual(VisionGeneratorIds.openAi, "openai-images")
        XCTAssertNotEqual(VisionGeneratorIds.openAi, CloudProviderIds.openAi)
        XCTAssertEqual(Set(VisionGeneratorIds.all).count, 2)
    }
}
