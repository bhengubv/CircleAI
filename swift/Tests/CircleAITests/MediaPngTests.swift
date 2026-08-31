import XCTest
@testable import CircleAI

/// The PNG and APNG chunk layout.
final class MediaPngTests: XCTestCase {

    private func solid(_ w: Int, _ h: Int, _ c: Rgba32) -> PixelBuffer {
        let b = PixelBuffer(width: w, height: h)!
        RasterCanvas(buffer: b).clear(c)
        return b
    }

    func testTheSignatureIsThePngOne() {
        XCTAssertEqual(PngWriter.signature, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
    }

    // The CRC covers the TYPE as well as the data - the part that is easy to
    // miss and produces a file every decoder rejects.
    func testAChunkCrcCoversItsTypeAndItsData() {
        let c = PngWriter.chunk(type: "IEND", data: [])
        XCTAssertEqual(Array(c[0..<4]), [0, 0, 0, 0], "length")
        XCTAssertEqual(Array(c[4..<8]), Array("IEND".utf8))
        let crc = (UInt32(c[8]) << 24) | (UInt32(c[9]) << 16) | (UInt32(c[10]) << 8) | UInt32(c[11])
        XCTAssertEqual(crc, Crc32.compute(Array("IEND".utf8)))
        // The published IEND CRC.
        XCTAssertEqual(crc, 0xAE42_6082)
    }

    func testTheHeaderDeclaresEightBitRgba() {
        let h = PngWriter.ihdr(width: 1080, height: 1920)
        XCTAssertEqual(h.count, 13)
        XCTAssertEqual(Array(h[0..<4]), [0, 0, 0x04, 0x38])   // 1080
        XCTAssertEqual(Array(h[4..<8]), [0, 0, 0x07, 0x80])   // 1920
        XCTAssertEqual(h[8], 8, "bit depth")
        XCTAssertEqual(h[9], 6, "colour type: truecolour + alpha")
    }

    // Every row is prefixed with its filter byte; the format requires it.
    func testEveryScanlineCarriesItsFilterByte() {
        let raw = PngWriter.filteredScanlines(solid(2, 3, .white))
        XCTAssertEqual(raw.count, 3 * (1 + 2 * 4))
        XCTAssertEqual(raw[0], 0)
        XCTAssertEqual(raw[9], 0)
        XCTAssertEqual(raw[18], 0)
    }

    func testAStillPngHasTheRightChunksInOrder() {
        let png = Array(PngWriter.encode(solid(2, 2, .white)))
        XCTAssertEqual(Array(png[0..<8]), PngWriter.signature)
        let text = String(decoding: png, as: UTF8.self)
        XCTAssertTrue(text.contains("IHDR"))
        XCTAssertTrue(text.contains("IDAT"))
        XCTAssertTrue(text.contains("IEND"))
        XCTAssertLessThan(text.range(of: "IHDR")!.lowerBound, text.range(of: "IDAT")!.lowerBound)
    }

    func testAnAnimatedPngDeclaresItsFrameCountAndLoop() throws {
        let frames = (0..<3).map { _ in solid(2, 2, .white) }
        let clip = try AnimatedPngEncoder.instance.encode(
            frames: frames, options: ClipEncodeOptions(size: RenderSize(width: 2, height: 2),
                                                       frameRate: 12, frameCount: 3))
        XCTAssertEqual(clip.frameCount, 3)
        XCTAssertEqual(clip.mimeType, "image/apng")
        XCTAssertEqual(clip.size, RenderSize(width: 2, height: 2))

        let text = String(decoding: Array(clip.bytes), as: UTF8.self)
        XCTAssertTrue(text.contains("acTL"))
        XCTAssertTrue(text.contains("fcTL"))
        XCTAssertTrue(text.contains("fdAT"))
    }

    // Frame 0 is the DEFAULT image and uses IDAT, not fdAT, so a viewer that
    // knows nothing about APNG still shows it.
    func testTheFirstFrameIsAPlainIdat() throws {
        let clip = try AnimatedPngEncoder.instance.encode(
            frames: [solid(2, 2, .white), solid(2, 2, .black)],
            options: ClipEncodeOptions(size: RenderSize(width: 2, height: 2),
                                       frameRate: 12, frameCount: 2))
        let text = String(decoding: Array(clip.bytes), as: UTF8.self)
        XCTAssertLessThan(text.range(of: "IDAT")!.lowerBound, text.range(of: "fdAT")!.lowerBound)
        XCTAssertEqual(text.components(separatedBy: "IDAT").count - 1, 1,
                       "only the default image is an IDAT")
    }

    func testFramesOfDifferentSizesAreRefused() {
        XCTAssertThrowsError(try AnimatedPngEncoder.instance.encode(
            frames: [solid(2, 2, .white), solid(4, 4, .white)],
            options: ClipEncodeOptions(size: RenderSize(width: 2, height: 2),
                                       frameRate: 12, frameCount: 2))) { e in
            XCTAssertEqual(e as? ApngError, .mismatchedFrameSize)
        }
    }

    func testNoFramesProducesAnEmptyClipRatherThanAnInvalidFile() throws {
        let clip = try AnimatedPngEncoder.instance.encode(
            frames: [], options: ClipEncodeOptions(size: .square1080, frameRate: 12, frameCount: 0))
        XCTAssertEqual(clip.frameCount, 0)
        XCTAssertTrue(clip.bytes.isEmpty)
    }

    // delay is a FRACTION: 1 over the frame rate, so 12 fps needs no rounding.
    func testTheFrameDelayIsOneOverTheFrameRate() {
        var seq: UInt32 = 0
        let f = AnimatedPngEncoder.fctl(seq: &seq, w: 2, h: 2, delayDen: 12)
        XCTAssertEqual(f.count, 26)
        XCTAssertEqual(Int(f[20]) << 8 | Int(f[21]), 1, "delay numerator")
        XCTAssertEqual(Int(f[22]) << 8 | Int(f[23]), 12, "delay denominator")
        XCTAssertEqual(f[24], 0, "dispose NONE")
        XCTAssertEqual(f[25], 0, "blend SOURCE")
        XCTAssertEqual(seq, 1, "the sequence number advances")
    }
}
