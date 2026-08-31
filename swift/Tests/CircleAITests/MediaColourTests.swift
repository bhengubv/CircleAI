import XCTest
@testable import CircleAI

/// Colour parsing, geometry and motion.
final class MediaColourTests: XCTestCase {

    // MARK: - Hex

    func testSixDigitHexIsParsed() throws {
        XCTAssertEqual(try Rgba32.hex("#2196F3"), Rgba32(0x21, 0x96, 0xF3, 255))
        XCTAssertEqual(try Rgba32.hex("2196f3"), Rgba32(0x21, 0x96, 0xF3, 255))
    }

    // Three-digit form DUPLICATES each nibble: #f00 is ff0000, not f00000.
    // Halving it instead would make every short colour darker than written.
    func testThreeDigitHexDuplicatesEachNibble() throws {
        XCTAssertEqual(try Rgba32.hex("#f00"), Rgba32(255, 0, 0, 255))
        XCTAssertEqual(try Rgba32.hex("#abc"), Rgba32(0xAA, 0xBB, 0xCC, 255))
    }

    func testEightDigitHexCarriesAlpha() throws {
        XCTAssertEqual(try Rgba32.hex("#2196F380"), Rgba32(0x21, 0x96, 0xF3, 0x80))
    }

    func testAMalformedColourIsRefusedNotGuessed() {
        XCTAssertThrowsError(try Rgba32.hex("#12345")) { e in
            XCTAssertEqual(e as? ColourError, .unrecognised("#12345"))
        }
        XCTAssertThrowsError(try Rgba32.hex("#12345g"))
        XCTAssertThrowsError(try Rgba32.hex(""))
    }

    func testTheNamedColoursAreWhatTheySay() {
        XCTAssertEqual(Rgba32.black, Rgba32(0, 0, 0, 255))
        XCTAssertEqual(Rgba32.white, Rgba32(255, 255, 255, 255))
        XCTAssertEqual(Rgba32.transparent.a, 0)
        XCTAssertEqual(Rgba32.rgb(1, 2, 3), Rgba32(1, 2, 3, 255))
        XCTAssertEqual(Rgba32.white.withAlpha(128), Rgba32(255, 255, 255, 128))
    }

    // MARK: - Sizes

    func testTheStandardSizesAreTheOnesSocialPlatformsWant() {
        XCTAssertEqual(RenderSize.square1080, RenderSize(width: 1080, height: 1080))
        XCTAssertEqual(RenderSize.portrait1080x1920.height, 1920)
        XCTAssertEqual(RenderSize.landscape1920x1080.width, 1920)
        XCTAssertEqual(RenderSize.square1080.pixelCount, 1_166_400)
    }

    // MARK: - Easing

    func testEasingCurvesStartAtZeroAndEndAtOne() {
        for kind: EasingKind in [.linear, .easeIn, .easeOut, .easeInOut] {
            XCTAssertEqual(Easing.apply(kind, 0), 0, accuracy: 1e-9, "\(kind)")
            XCTAssertEqual(Easing.apply(kind, 1), 1, accuracy: 1e-9, "\(kind)")
        }
    }

    // The three curves must differ at the midpoint, or they are the same curve.
    func testTheCurvesDifferInTheMiddle() {
        XCTAssertEqual(Easing.apply(.linear, 0.5), 0.5, accuracy: 1e-9)
        XCTAssertEqual(Easing.apply(.easeIn, 0.5), 0.25, accuracy: 1e-9)
        XCTAssertEqual(Easing.apply(.easeOut, 0.5), 0.75, accuracy: 1e-9)
        XCTAssertEqual(Easing.apply(.easeInOut, 0.5), 0.5, accuracy: 1e-9)
        // easeInOut is slower than linear early and faster late.
        XCTAssertLessThan(Easing.apply(.easeInOut, 0.25), 0.25)
        XCTAssertGreaterThan(Easing.apply(.easeInOut, 0.75), 0.75)
    }

    // MARK: - Motion windows

    func testAMotionOnlyMovesInsideItsWindow() {
        let m = Motion(startFraction: 0.25, endFraction: 0.75,
                       fromOpacity: 0.0, toOpacity: 1.0, easing: .linear)
        XCTAssertEqual(Easing.evaluate(m, at: 0.0).opacity, 0.0, accuracy: 1e-9)
        XCTAssertEqual(Easing.evaluate(m, at: 0.25).opacity, 0.0, accuracy: 1e-9)
        XCTAssertEqual(Easing.evaluate(m, at: 0.5).opacity, 0.5, accuracy: 1e-9)
        XCTAssertEqual(Easing.evaluate(m, at: 0.75).opacity, 1.0, accuracy: 1e-9)
        XCTAssertEqual(Easing.evaluate(m, at: 1.0).opacity, 1.0, accuracy: 1e-9)
    }

    // A zero-length window snaps at its end rather than dividing by zero.
    func testAZeroLengthWindowSnapsInsteadOfDividingByZero() {
        let m = Motion(startFraction: 0.5, endFraction: 0.5, fromOpacity: 0.0, toOpacity: 1.0)
        XCTAssertEqual(Easing.evaluate(m, at: 0.49).opacity, 0.0, accuracy: 1e-9)
        XCTAssertEqual(Easing.evaluate(m, at: 0.5).opacity, 1.0, accuracy: 1e-9)
    }

    func testNoMotionMeansFullyVisibleAndUnmoved() {
        let e = Easing.evaluate(nil, at: 0.5)
        XCTAssertEqual(e.opacity, 1.0)
        XCTAssertEqual(e.scale, 1.0)
        XCTAssertEqual(e.translate, .zero)
    }

    func testThePresetsDoWhatTheyAreNamed() {
        XCTAssertEqual(Easing.evaluate(.fadeIn, at: 0.0).opacity, 0.0, accuracy: 1e-9)
        XCTAssertEqual(Easing.evaluate(.fadeIn, at: 0.25).opacity, 1.0, accuracy: 1e-9)
        XCTAssertEqual(Easing.evaluate(.fadeOut, at: 1.0).opacity, 0.0, accuracy: 1e-9)
        XCTAssertGreaterThan(Easing.evaluate(.kenBurns, at: 1.0).scale, 1.0)
    }

    // MARK: - Spec

    // A 0.01s clip is still a frame, not nothing.
    func testFrameCountIsAtLeastOne() {
        XCTAssertEqual(MediaSpec(size: .square1080, background: .black,
                                 duration: 0.01, frameRate: 12).frameCount, 1)
        XCTAssertEqual(MediaSpec(size: .square1080, background: .black,
                                 duration: 2, frameRate: 12).frameCount, 24)
        XCTAssertEqual(MediaSpec(size: .square1080, background: .black,
                                 duration: 0, frameRate: 12).frameCount, 1)
    }

    func testAStillIsAStill() {
        let s = MediaSpec.still(size: .square1080, background: .white)
        XCTAssertTrue(s.isStill)
        XCTAssertEqual(s.frameCount, 1)
    }

    func testTokensAreSubstituted() {
        let out = MediaSpec.applyTokens("Hello {{name}}, you owe {{amount}}.",
                                        ["name": "Nandi", "amount": "R 500.00"])
        XCTAssertEqual(out, "Hello Nandi, you owe R 500.00.")
    }

    // A key with no token is LEFT ALONE - a half-substituted template is easier
    // to diagnose than one with holes in it.
    func testAnUnmatchedTokenIsLeftInPlace() {
        XCTAssertEqual(MediaSpec.applyTokens("Hi {{who}}", ["other": "x"]), "Hi {{who}}")
        XCTAssertEqual(MediaSpec.applyTokens("Hi {{who}}", nil), "Hi {{who}}")
        XCTAssertEqual(MediaSpec.applyTokens("Hi {{who}}", [:]), "Hi {{who}}")
    }
}
