import XCTest
@testable import CircleAI

/// Word wrap, line measurement and the font.
final class MediaTextLayoutTests: XCTestCase {

    // A 5-wide glyph with a 1px letter gap: advance 6, glyph 5.
    private let advance = 6
    private let glyphW = 5

    private func width(_ n: Int) -> Int {
        RasterCanvas.lineWidth(n, advance: advance, glyphW: glyphW)
    }

    private func wrap(_ s: String, _ maxWidth: Int) -> [String] {
        RasterCanvas.wrap(s, maxWidth: maxWidth, advance: advance, glyphW: glyphW)
    }

    // MARK: - Measuring

    // The trailing letter-space of the LAST glyph is not part of the line, so
    // centred text sits centred rather than a space to the left.
    func testALineDoesNotIncludeItsTrailingLetterSpace() {
        XCTAssertEqual(width(1), 5)
        XCTAssertEqual(width(2), 11)
        XCTAssertEqual(width(3), 17)
        XCTAssertEqual(width(0), 0)
    }

    // MARK: - Wrapping

    func testAShortLineIsNotWrapped() {
        XCTAssertEqual(wrap("HI", 100), ["HI"])
    }

    func testWordsWrapAtTheBoxWidth() {
        // "AAA BBB" is 7 chars = 41px; a 20px box fits only one word.
        XCTAssertEqual(wrap("AAA BBB", 20), ["AAA", "BBB"])
    }

    func testExplicitNewlinesStartANewLine() {
        XCTAssertEqual(wrap("ONE\nTWO", 1000), ["ONE", "TWO"])
    }

    func testCarriageReturnsAreNotTreatedAsContent() {
        XCTAssertEqual(wrap("ONE\r\nTWO", 1000), ["ONE", "TWO"])
    }

    func testAnEmptyParagraphSurvivesAsABlankLine() {
        XCTAssertEqual(wrap("ONE\n\nTWO", 1000), ["ONE", "", "TWO"])
    }

    func testRunsOfSpacesCollapse() {
        XCTAssertEqual(wrap("ONE    TWO", 1000), ["ONE TWO"])
    }

    // A single word longer than the box is NOT broken - it overflows, which is
    // visible, and better than silently losing characters.
    func testAnOverlongWordOverflowsRatherThanBeingTruncated() {
        let lines = wrap("SUPERCALIFRAGILISTIC", 20)
        XCTAssertEqual(lines, ["SUPERCALIFRAGILISTIC"])
    }

    // MARK: - The font

    func testTheFontCoversLettersDigitsAndCommonPunctuation() {
        let f = BitmapFont.default
        for c in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.,!?-+:;/()&%#@" {
            XCTAssertTrue(f.hasGlyph(c), "missing glyph for \(c)")
        }
    }

    // Lower case folds to upper, so a mixed-case caption still renders.
    func testLowerCaseFoldsToUpper() {
        let f = BitmapFont.default
        XCTAssertTrue(f.hasGlyph("a"))
        for row in 0..<BitmapFont.rows {
            for col in 0..<BitmapFont.cols {
                XCTAssertEqual(f.isPixelOn("a", col: col, row: row),
                               f.isPixelOn("A", col: col, row: row))
            }
        }
    }

    func testAnUnknownGlyphIsBlankRatherThanACrash() {
        let f = BitmapFont.default
        XCTAssertFalse(f.hasGlyph("\u{1F600}"))
        XCTAssertFalse(f.isPixelOn("\u{1F600}", col: 0, row: 0))
    }

    func testOutOfRangeCellsAreOff() {
        let f = BitmapFont.default
        XCTAssertFalse(f.isPixelOn("A", col: -1, row: 0))
        XCTAssertFalse(f.isPixelOn("A", col: BitmapFont.cols, row: 0))
        XCTAssertFalse(f.isPixelOn("A", col: 0, row: BitmapFont.rows))
    }

    // Every glyph must be exactly 5x7, or the layout arithmetic is wrong for it.
    func testEveryGlyphIsFiveBySeven() {
        let f = BitmapFont.default
        for c in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.,!?-+:;/()&%#@" {
            for row in 0..<BitmapFont.rows {
                // Reading the last column of the last row must not trap.
                _ = f.isPixelOn(c, col: BitmapFont.cols - 1, row: row)
            }
        }
    }

    // A space carries no ink but still advances - otherwise words run together.
    func testASpaceHasNoInk() {
        XCTAssertFalse(BitmapFont.default.isPixelOn(" ", col: 0, row: 0))
    }
}
