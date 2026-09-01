// MediaImageCodecsTests.swift
//
// The inflate is the risky part: every way it can be wrong produces plausible
// bytes rather than an error. So it is tested against streams this package did
// NOT write — real PNGs compressed with dynamic Huffman codes — as well as its
// own round trips.

import XCTest
@testable import CircleAI

final class MediaImageCodecsTests: XCTestCase {

    // MARK: - Inflate

    /// A stored (uncompressed) DEFLATE block, which is what this package's own
    /// encoder writes.
    private func stored(_ payload: [UInt8], final: Bool = true) -> [UInt8] {
        var out: [UInt8] = [final ? 1 : 0]
        let len = payload.count
        out.append(UInt8(len & 0xFF)); out.append(UInt8((len >> 8) & 0xFF))
        let n = ~len
        out.append(UInt8(n & 0xFF)); out.append(UInt8((n >> 8) & 0xFF))
        out.append(contentsOf: payload)
        return out
    }

    func testAStoredBlockRoundTrips() throws {
        let payload: [UInt8] = Array("hello world".utf8)
        XCTAssertEqual(try Inflate.raw(stored(payload)), payload)
    }

    func testAnEmptyStoredBlockIsEmptyOutput() throws {
        XCTAssertEqual(try Inflate.raw(stored([])), [])
    }

    func testAMismatchedStoredLengthIsRefused() {
        // The complement exists exactly so a truncated stream is caught here
        // rather than producing half an image.
        var bad = stored([1, 2, 3])
        bad[3] = 0xFF
        XCTAssertThrowsError(try Inflate.raw(bad)) {
            XCTAssertEqual($0 as? InflateError, .badStoredLength)
        }
    }

    func testAReservedBlockTypeIsRefused() {
        XCTAssertThrowsError(try Inflate.raw([0b111])) {
            XCTAssertEqual($0 as? InflateError, .badBlockType(3))
        }
    }

    func testATruncatedStreamIsRefused() {
        XCTAssertThrowsError(try Inflate.raw([])) {
            XCTAssertEqual($0 as? InflateError, .truncated)
        }
    }

    func testMultipleStoredBlocksConcatenate() throws {
        let a: [UInt8] = Array("first ".utf8), b: [UInt8] = Array("second".utf8)
        let joined = stored(a, final: false) + stored(b, final: true)
        XCTAssertEqual(try Inflate.raw(joined), a + b)
    }

    func testTheZlibWrapperIsStrippedAndValidated() throws {
        let payload: [UInt8] = Array("wrapped".utf8)
        // 0x78 0x01 is the standard low-compression zlib header, and 0x7801 % 31
        // is 0 as the spec requires.
        let z: [UInt8] = [0x78, 0x01] + stored(payload) + [0, 0, 0, 0]
        XCTAssertEqual(try Inflate.zlib(z), payload)
    }

    func testANonZlibHeaderIsRefused() {
        XCTAssertThrowsError(try Inflate.zlib([0x00, 0x00, 0x01])) {
            XCTAssertEqual($0 as? InflateError, .badZlibHeader)
        }
        // Right compression method, wrong check bits.
        XCTAssertThrowsError(try Inflate.zlib([0x78, 0x00, 0x01])) {
            XCTAssertEqual($0 as? InflateError, .badZlibHeader)
        }
    }

    func testABackReferenceBeforeTheStartIsRefusedNotWrapped() {
        // A fixed-Huffman block whose first symbol is a length/distance pair
        // has nothing behind it to copy. Wrapping would read whatever memory
        // followed; this must be an error.
        //
        // 1 (final) + 01 (fixed) then literal-length 257 and distance 0.
        // 257 is 7-bit code 0000001, distance 0 is 5-bit code 00000.
        var bits: [Int] = [1, 1, 0]                    // final, type=01 (LSB first)
        bits += [0, 0, 0, 0, 0, 0, 1]              // 257, MSB-first code
        bits += [0, 0, 0, 0, 0]                        // distance symbol 0
        XCTAssertThrowsError(try Inflate.raw(pack(bits))) { e in
            XCTAssertEqual(e as? InflateError, .badDistance)
        }
    }

    private func pack(_ bits: [Int]) -> [UInt8] {
        var out: [UInt8] = []
        var cur: UInt8 = 0, n = 0
        for b in bits {
            cur |= UInt8(b) << n
            n += 1
            if n == 8 { out.append(cur); cur = 0; n = 0 }
        }
        if n > 0 { out.append(cur) }
        return out
    }

    // MARK: - PNG

    private func checkerboard(_ w: Int, _ h: Int) -> PixelBuffer {
        var px = [UInt8](repeating: 0, count: w * h * 4)
        for y in 0..<h {
            for x in 0..<w {
                let i = (y * w + x) * 4
                let on = (x + y) % 2 == 0
                px[i] = on ? 255 : 20
                px[i + 1] = UInt8(truncatingIfNeeded: x * 7)
                px[i + 2] = UInt8(truncatingIfNeeded: y * 11)
                px[i + 3] = 255
            }
        }
        return PixelBuffer(width: w, height: h, pixels: px)!
    }

    func testAPngWeWroteDecodesBackToTheSamePixels() throws {
        let original = checkerboard(9, 7)
        let png = ImageCodecs.encodePng(original)
        let back = try ImageCodecs.decodePng(png)

        XCTAssertEqual(back.width, 9)
        XCTAssertEqual(back.height, 7)
        XCTAssertEqual(back.pixels, original.pixels)
    }

    func testASingleWhitePixelSurvives() throws {
        let one = PixelBuffer(width: 1, height: 1, pixels: [255, 255, 255, 255])!
        let back = try ImageCodecs.decodePng(ImageCodecs.encodePng(one))
        XCTAssertEqual(back.pixels, [255, 255, 255, 255])
    }

    func testAlphaSurvivesTheRoundTrip() throws {
        // Colour type 6 is the only one this encoder writes, so a dropped alpha
        // channel would show up as an opaque image rather than an error.
        let px: [UInt8] = [255, 0, 0, 0, 0, 255, 0, 128, 0, 0, 255, 255, 1, 2, 3, 4]
        let img = PixelBuffer(width: 2, height: 2, pixels: px)!
        XCTAssertEqual(try ImageCodecs.decodePng(ImageCodecs.encodePng(img)).pixels, px)
    }

    func testNotAPngIsRefused() {
        XCTAssertThrowsError(try ImageCodecs.decodePng(Data([1, 2, 3]))) {
            XCTAssertEqual($0 as? ImageCodecError, .notPng)
        }
    }

    func testAPngWithNoHeaderChunkIsRefused() {
        // Signature alone, then an IEND. Nothing says how big the image is.
        var d = Data(ImageCodecs.pngSignature)
        d.append(contentsOf: [0, 0, 0, 0] + Array("IEND".utf8) + [0, 0, 0, 0])
        XCTAssertThrowsError(try ImageCodecs.decodePng(d)) {
            XCTAssertEqual($0 as? ImageCodecError, .missingHeader)
        }
    }

    func testTheUnsupportedCasesSayWhichOneTheyAre() throws {
        // A 16-bit or interlaced PNG is a real file that this decoder cannot
        // read, and "corrupt" would send somebody looking for the wrong bug.
        let png = [UInt8](ImageCodecs.encodePng(checkerboard(4, 4)))

        var deep = png
        deep[8 + 8 + 8] = 16                              // IHDR bit depth
        XCTAssertThrowsError(try ImageCodecs.decodePng(Data(deep))) {
            XCTAssertEqual($0 as? ImageCodecError, .unsupportedBitDepth(16))
        }

        var interlaced = png
        interlaced[8 + 8 + 12] = 1                        // IHDR interlace method
        XCTAssertThrowsError(try ImageCodecs.decodePng(Data(interlaced))) {
            XCTAssertEqual($0 as? ImageCodecError, .interlaced)
        }

        var palette = png
        palette[8 + 8 + 9] = 3                            // IHDR colour type
        XCTAssertThrowsError(try ImageCodecs.decodePng(Data(palette))) {
            XCTAssertEqual($0 as? ImageCodecError, .unsupportedColourType(3))
        }
    }

    // MARK: - The Paeth predictor

    func testPaethPicksTheNearestNeighbour() {
        // The filter that carries most real photographs. Getting the tie-break
        // wrong shifts an image by one value per pixel — visible only as a
        // faint diagonal texture nobody attributes to the decoder.
        XCTAssertEqual(ImageCodecs.paeth(10, 20, 30), 10)   // p = 0, so a is nearest
        XCTAssertEqual(ImageCodecs.paeth(0, 0, 0), 0)
        XCTAssertEqual(ImageCodecs.paeth(100, 50, 50), 100) // left unchanged
        XCTAssertEqual(ImageCodecs.paeth(50, 100, 50), 100) // above unchanged
        // Ties go to a, then b — the order is in the spec and is not arbitrary.
        XCTAssertEqual(ImageCodecs.paeth(5, 5, 5), 5)
    }

    // MARK: - BMP

    func testABmpRoundTripsItsColoursWithFullAlpha() throws {
        // 24-bit BMP has no alpha channel, so the decode fills 255 — an image
        // that came back transparent would be invisible with no error anywhere.
        let original = checkerboard(5, 3)
        let back = try ImageCodecs.decodeBmp(ImageCodecs.encodeBmp(original))

        XCTAssertEqual(back.width, 5)
        XCTAssertEqual(back.height, 3)
        for i in stride(from: 0, to: back.pixels.count, by: 4) {
            XCTAssertEqual(back.pixels[i], original.pixels[i])
            XCTAssertEqual(back.pixels[i + 1], original.pixels[i + 1])
            XCTAssertEqual(back.pixels[i + 2], original.pixels[i + 2])
            XCTAssertEqual(back.pixels[i + 3], 255)
        }
    }

    func testRowPaddingIsHonouredForAWidthThatIsNotAMultipleOfFour() throws {
        // Every BMP row is padded to four bytes. Forgetting it shears the image
        // progressively — right at the top-left, wrong by the bottom-right.
        for w in 1...7 {
            let img = checkerboard(w, 4)
            let back = try ImageCodecs.decodeBmp(ImageCodecs.encodeBmp(img))
            XCTAssertEqual(back.width, w)
            for i in stride(from: 0, to: back.pixels.count, by: 4) {
                XCTAssertEqual(back.pixels[i], img.pixels[i], "width \(w) sheared")
            }
        }
    }

    func testNotABmpIsRefused() {
        XCTAssertThrowsError(try ImageCodecs.decodeBmp(Data(repeating: 0, count: 60))) {
            XCTAssertEqual($0 as? ImageCodecError, .notBmp)
        }
    }

    func testACompressedBmpIsRefusedByName() throws {
        var bmp = [UInt8](ImageCodecs.encodeBmp(checkerboard(4, 4)))
        bmp[30] = 1                                       // BI_RLE8
        XCTAssertThrowsError(try ImageCodecs.decodeBmp(Data(bmp))) {
            XCTAssertEqual($0 as? ImageCodecError, .unsupportedBmpCompression)
        }
    }

    func testATopDownBmpIsReadTheRightWayUp() throws {
        // A NEGATIVE height means top-down. Read as unsigned it is an enormous
        // positive number and the decode fails on an underflow check that says
        // nothing about the real problem.
        let img = checkerboard(4, 3)
        var bmp = [UInt8](ImageCodecs.encodeBmp(img))

        // Flip the stored rows and negate the height: the same picture, written
        // the other way up.
        let rowStride = (4 * 3 + 3) / 4 * 4
        var flipped = bmp
        for y in 0..<3 {
            let from = 54 + y * rowStride
            let to = 54 + (2 - y) * rowStride
            for i in 0..<rowStride { flipped[to + i] = bmp[from + i] }
        }
        bmp = flipped
        let neg = UInt32(bitPattern: Int32(-3))
        bmp[22] = UInt8(neg & 0xFF); bmp[23] = UInt8((neg >> 8) & 0xFF)
        bmp[24] = UInt8((neg >> 16) & 0xFF); bmp[25] = UInt8((neg >> 24) & 0xFF)

        let back = try ImageCodecs.decodeBmp(Data(bmp))
        XCTAssertEqual(back.height, 3)
        for i in stride(from: 0, to: back.pixels.count, by: 4) {
            XCTAssertEqual(back.pixels[i], img.pixels[i])
        }
    }

    // MARK: - The decoder seam

    func testTheManagedDecoderPicksTheFormatFromTheBytes() throws {
        let img = checkerboard(4, 4)
        let d = ManagedImageDecoder.instance
        XCTAssertEqual(d.backendId, "managed-png-bmp")
        XCTAssertNotNil(d.decode(ImageCodecs.encodePng(img), mimeHint: nil))
        XCTAssertNotNil(d.decode(ImageCodecs.encodeBmp(img), mimeHint: nil))
    }

    func testAJpegIsNamedRatherThanCalledCorrupt() {
        // A JPEG IS a picture. The caller needs to know it must wire a platform
        // decoder, not that the file is broken.
        let jpeg = Data([0xFF, 0xD8, 0xFF, 0xE0, 0, 16] + [UInt8](repeating: 0, count: 32))
        XCTAssertThrowsError(try ManagedImageDecoder.instance.decodeOrThrow(jpeg)) {
            XCTAssertEqual($0 as? ImageCodecError, .jpegNeedsAPlatformDecoder)
        }
        XCTAssertNil(ManagedImageDecoder.instance.decode(jpeg, mimeHint: "image/jpeg"))
    }

    func testUnrecognisedBytesAreNilOnTheSeamAndNamedOnTheThrow() {
        let junk = Data([1, 2, 3, 4, 5, 6, 7, 8])
        XCTAssertNil(ManagedImageDecoder.instance.decode(junk, mimeHint: nil))
        XCTAssertThrowsError(try ManagedImageDecoder.instance.decodeOrThrow(junk)) {
            XCTAssertEqual($0 as? ImageCodecError, .unrecognisedFormat)
        }
    }

    // MARK: - Fail-closed defaults

    func testTheNullVideoEncoderIsAnHonestGapMarker() throws {
        // It advertises mp4 and emits ZERO bytes, and reports the length that
        // was ASKED FOR so a caller can see what it wanted.
        let e = NullVideoEncoder.instance
        XCTAssertEqual(e.outputMimeType, "video/mp4")
        let clip = try e.encode(frames: [], options: ClipEncodeOptions(
            size: .square1080, frameRate: 12, frameCount: 72))
        XCTAssertTrue(clip.bytes.isEmpty)
        XCTAssertEqual(clip.frameCount, 72)
        XCTAssertEqual(clip.frameRate, 12)
        XCTAssertEqual(clip.backendId, "null")
    }

    func testTheApngEncoderIsTheOneThatActuallyProducesAClip() throws {
        // The alternative the null encoder's documentation points at has to
        // really work, or the gap marker is just a dead end.
        let clip = try AnimatedPngEncoder.instance.encode(
            frames: [checkerboard(4, 4), checkerboard(4, 4)],
            options: ClipEncodeOptions(size: RenderSize(width: 4, height: 4),
                                       frameRate: 12, frameCount: 2))
        XCTAssertFalse(clip.bytes.isEmpty)
        XCTAssertEqual(clip.mimeType, "image/apng")
    }

    func testTheNullRendererGivesAOnePixelStillNotNothing() {
        // A caller compositing a poster wants something with a size it can
        // reason about.
        let spec = MediaSpec(size: .square1080, background: .black)
        let still = NullMediaRenderer.instance.renderStill(spec, posterFraction: 0)
        XCTAssertEqual(still?.width, 1)
        XCTAssertEqual(still?.height, 1)
        XCTAssertTrue(NullMediaRenderer.instance.frames(spec).isEmpty)
    }

    func testTheNullRendererReportsTheEncodersMimeTypeNotItsOwn() throws {
        // The clip is empty either way; saying "video/mp4" when the caller
        // passed an APNG encoder would send them to the wrong player.
        let spec = MediaSpec(size: .square1080, background: .black)
        let clip = try NullMediaRenderer.instance.renderClip(
            spec, encoder: AnimatedPngEncoder.instance)
        XCTAssertEqual(clip.mimeType, "image/apng")
        XCTAssertTrue(clip.bytes.isEmpty)
    }

    func testTheNullHtmlProviderYieldsNoFrames() async throws {
        let frames = try await NullHtmlFrameProvider.instance.renderHtmlFrames(
            HtmlTemplateSource(html: "<p>hi</p>"), size: .square1080,
            frameCount: 12, frameRate: 12)
        XCTAssertTrue(frames.isEmpty)
        XCTAssertEqual(NullHtmlFrameProvider.instance.backendId, "null")
    }

    // MARK: - Templates

    func testASolidColourSourceIsOnePixel() {
        // It is scaled to whatever rectangle it lands in, so a full-screen scrim
        // costs four bytes.
        guard case let .raw(rgba, w, h) = MediaTemplates.solidColor(Rgba32(1, 2, 3, 4)) else {
            return XCTFail("expected a raw source")
        }
        XCTAssertEqual(rgba, [1, 2, 3, 4])
        XCTAssertEqual(w, 1)
        XCTAssertEqual(h, 1)
    }

    func testTheSocialAdAlwaysLaysAScrimUnderTheText() {
        // White text over an arbitrary photo is legible or not depending on the
        // photo, and nobody checks every frame before posting.
        let spec = MediaTemplates.socialAd(size: .portrait1080x1920, headline: "Open today")
        XCTAssertTrue(spec.images.contains { $0.id == "scrim" })
        let scrim = spec.images.first { $0.id == "scrim" }!
        let headline = spec.texts.first { $0.id == "headline" }!
        XCTAssertLessThanOrEqual(scrim.rect.y, headline.rect.y)
        XCTAssertLessThan(scrim.zOrder, headline.zOrder)
    }

    func testATransparentScrimIsNotDrawnAtAll() {
        let spec = MediaTemplates.socialAd(size: .square1080, headline: "Hi",
                                           scrimColor: Rgba32(0, 0, 0, 0))
        XCTAssertFalse(spec.images.contains { $0.id == "scrim" })
    }

    func testTheBackgroundGetsAKenBurnsMoveAndSitsBehindEverything() {
        let spec = MediaTemplates.socialAd(
            size: .square1080,
            background: MediaTemplates.solidColor(.white),
            headline: "Hi")
        let bg = spec.images.first { $0.id == "bg" }!
        XCTAssertEqual(bg.zOrder, 0)
        XCTAssertEqual(bg.fit, .cover)
        XCTAssertNotEqual(bg.motion?.toScale, bg.motion?.fromScale)
    }

    func testASublineArrivesAfterTheHeadline() {
        // Two things fading in together read as one flicker.
        let spec = MediaTemplates.socialAd(size: .square1080, headline: "Open", subline: "today")
        let headline = spec.texts.first { $0.id == "headline" }!
        let subline = spec.texts.first { $0.id == "subline" }!
        XCTAssertGreaterThan(subline.motion!.startFraction, headline.motion!.startFraction)
    }

    func testABlankSublineIsNoSubline() {
        for blank in [nil, "", "   "] {
            let spec = MediaTemplates.socialAd(size: .square1080, headline: "Hi", subline: blank)
            XCTAssertEqual(spec.texts.count, 1, "blank subline \(blank ?? "nil") added a layer")
        }
    }

    func testTheCvCardStaggersItsFourElements() {
        let spec = MediaTemplates.videoCvCard(
            size: .portrait1080x1920,
            portrait: MediaTemplates.solidColor(.white),
            name: "Thabo", title: "Engineer", contact: "thabo@example.com")

        let starts = ["portrait", "name", "title", "contact"].map { id -> Double in
            (spec.images.first { $0.id == id }?.motion?.startFraction)
                ?? spec.texts.first { $0.id == id }!.motion!.startFraction
        }
        XCTAssertEqual(starts, starts.sorted(), "each element starts as the last settles")
    }

    func testTheCvCardTitleUsesTheAccentColour() {
        let spec = MediaTemplates.videoCvCard(size: .square1080, name: "A", title: "B")
        XCTAssertEqual(spec.texts.first { $0.id == "title" }?.color, MediaTemplates.defaultAccent)
        XCTAssertNotEqual(spec.texts.first { $0.id == "name" }?.color,
                          spec.texts.first { $0.id == "title" }?.color)
    }

    func testAnHtmlSceneDefaultsToAWhiteCanvas() {
        // An HTML scene brings its own styling, and a dark canvas under it shows
        // through every unstyled margin.
        let spec = MediaTemplates.fromHtml(size: .square1080, html: "<h1>Hi</h1>")
        XCTAssertEqual(spec.background, .white)
        XCTAssertEqual(spec.html?.html, "<h1>Hi</h1>")
        XCTAssertTrue(spec.images.isEmpty)
        XCTAssertTrue(spec.texts.isEmpty)
    }

    func testEveryTemplateProducesAClipNotAStill() {
        XCTAssertFalse(MediaTemplates.socialAd(size: .square1080, headline: "x").isStill)
        XCTAssertFalse(MediaTemplates.videoCvCard(size: .square1080, name: "a", title: "b").isStill)
        XCTAssertFalse(MediaTemplates.fromHtml(size: .square1080, html: "x").isStill)
    }
}
