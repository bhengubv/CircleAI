import XCTest
@testable import CircleAI

/// Golden snapshots, deterministic ids and the frozen clock.
final class TestingSupportTests: XCTestCase {

    // MARK: - Golden store

    func testWhatIsWrittenComesBack() async throws {
        let s = InMemoryGoldenStore()
        try await s.write("t1", golden: "hello")
        let v = try await s.read("t1")
        XCTAssertEqual(v, "hello")
    }

    func testAnUnknownTestHasNoGolden() async throws {
        let v = try await InMemoryGoldenStore().read("nope")
        XCTAssertNil(v)
    }

    func testATestIdIsRequired() async {
        let s = InMemoryGoldenStore()
        do { _ = try await s.read("  "); XCTFail("expected a refusal") }
        catch let e as TestingError { XCTAssertEqual(e, .missingTestId) }
        catch { XCTFail("wrong error") }
    }

    // MARK: - Comparison

    func testAMatchingSnapshotIsEqualWithNoDiff() async throws {
        let store = InMemoryGoldenStore()
        try await store.write("t", golden: "line one\nline two")
        let d = try await LineDiffSnapshotComparer(store: store).compare("t", actual: "line one\nline two")
        XCTAssertTrue(d.equal)
        XCTAssertNil(d.diff)
    }

    // A missing golden must NOT pass, or every new test passes on day one.
    func testAMissingGoldenIsNotAPass() async throws {
        let d = try await LineDiffSnapshotComparer(store: InMemoryGoldenStore())
            .compare("never-seen", actual: "anything")
        XCTAssertFalse(d.equal)
        XCTAssertEqual(d.diff, "(no golden)")
    }

    func testTheDiffShowsOnlyTheLinesThatChanged() async throws {
        let store = InMemoryGoldenStore()
        try await store.write("t", golden: "same\nold\nsame")
        let d = try await LineDiffSnapshotComparer(store: store).compare("t", actual: "same\nnew\nsame")
        XCTAssertFalse(d.equal)
        XCTAssertEqual(d.diff, "-old\n+new\n")
    }

    func testAShorterActualStillDiffsAgainstTheMissingLines() async throws {
        let store = InMemoryGoldenStore()
        try await store.write("t", golden: "a\nb\nc")
        let d = try await LineDiffSnapshotComparer(store: store).compare("t", actual: "a")
        XCTAssertEqual(d.diff, "-b\n+\n-c\n+\n")
    }

    // Line endings and trailing spaces are editor noise. Without this, every
    // golden file fails the first time it crosses an operating system.
    func testCrlfAndTrailingSpaceAreNotDifferences() async throws {
        let store = InMemoryGoldenStore()
        try await store.write("t", golden: "one\r\ntwo   \r\n")
        let d = try await LineDiffSnapshotComparer(store: store).compare("t", actual: "one\ntwo\n")
        XCTAssertTrue(d.equal)
    }

    func testALoneCarriageReturnIsAlsoALineBreak() {
        XCTAssertEqual(LineDiffSnapshotComparer.normalise("a\rb"), "a\nb")
    }

    // Leading whitespace IS content - only trailing is stripped.
    func testIndentationIsPreserved() async throws {
        let store = InMemoryGoldenStore()
        try await store.write("t", golden: "  indented")
        let d = try await LineDiffSnapshotComparer(store: store).compare("t", actual: "indented")
        XCTAssertFalse(d.equal)
    }

    func testTheNullComparerReportsNotEqualRatherThanPassingEverything() async throws {
        let d = try await NullSnapshotComparer.instance.compare("t", actual: "x")
        XCTAssertFalse(d.equal)
        XCTAssertTrue(d.diff!.contains("no golden store wired"))
    }

    func testTheNullStoreForgetsWhatItIsGiven() async throws {
        try await NullGoldenStore.instance.write("t", golden: "x")
        let v = try await NullGoldenStore.instance.read("t")
        XCTAssertNil(v)
    }

    // MARK: - Deterministic ids

    func testTheSameSeedAlwaysGivesTheSameId() throws {
        XCTAssertEqual(try DeterministicIds.fromSeed("alpha"), try DeterministicIds.fromSeed("alpha"))
        XCTAssertNotEqual(try DeterministicIds.fromSeed("alpha"), try DeterministicIds.fromSeed("beta"))
    }

    // The published FNV-1a 32-bit vector, so a Swift id equals the C# one.
    func testTheHashMatchesTheKnownFnv1aVector() throws {
        XCTAssertEqual(try DeterministicIds.fromSeed("a", prefix: "x"), "x-e40c292c")
        XCTAssertEqual(try DeterministicIds.fromSeed("foobar", prefix: "x"), "x-bf9cf968")
    }

    func testThePrefixIsPartOfTheId() throws {
        XCTAssertTrue(try DeterministicIds.fromSeed("s").hasPrefix("test-"))
        XCTAssertTrue(try DeterministicIds.fromSeed("s", prefix: "case").hasPrefix("case-"))
    }

    func testTheHashIsAlwaysEightHexDigits() throws {
        for s in ["a", "ab", "abc", "the quick brown fox", "\u{1F600}"] {
            let id = try DeterministicIds.fromSeed(s, prefix: "p")
            XCTAssertEqual(id.count, 2 + 8, "wrong length for \(s)")
        }
    }

    func testAnEmptySeedIsRefused() {
        XCTAssertThrowsError(try DeterministicIds.fromSeed("   ")) { e in
            XCTAssertEqual(e as? TestingError, .missingSeed)
        }
    }

    // MARK: - Frozen clock

    func testTheClockOnlyMovesWhenToldTo() {
        let start = Date(timeIntervalSince1970: 1_000_000)
        let c = FrozenClock(start)
        XCTAssertEqual(c.now, start)
        XCTAssertEqual(c.now, start)

        c.advance(by: 60)
        XCTAssertEqual(c.now, start.addingTimeInterval(60))

        c.set(to: start)
        XCTAssertEqual(c.now, start)
    }

    func testTheClockCanBeMovedBackwards() {
        let start = Date(timeIntervalSince1970: 1_000_000)
        let c = FrozenClock(start)
        c.advance(by: -60)
        XCTAssertEqual(c.now, start.addingTimeInterval(-60))
    }
}
