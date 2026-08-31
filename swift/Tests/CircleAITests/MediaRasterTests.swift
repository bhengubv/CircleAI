import XCTest
@testable import CircleAI

/// The canvas: blending, scaling and text layout.
final class MediaRasterTests: XCTestCase {

    private func solid(_ w: Int, _ h: Int, _ c: Rgba32) -> PixelBuffer {
        let b = PixelBuffer(width: w, height: h)!
        RasterCanvas(buffer: b).clear(c)
        return b
    }

    // MARK: - Buffer

    func testABufferIsTransparentUntilItIsCleared() {
        let b = PixelBuffer(width: 2, height: 2)!
        XCTAssertEqual(b.stride, 8)
        XCTAssertEqual(b.pixel(x: 0, y: 0), Rgba32(0, 0, 0, 0))
    }

    func testBadDimensionsAreRefused() {
        XCTAssertNil(PixelBuffer(width: 0, height: 4))
        XCTAssertNil(PixelBuffer(width: 4, height: -1))
        XCTAssertNil(PixelBuffer(width: 2, height: 2, pixels: [0, 0, 0]))
    }

    func testOutOfBoundsReadsReturnNothingRatherThanCrashing() {
        let b = solid(2, 2, .white)
        XCTAssertNil(b.pixel(x: -1, y: 0))
        XCTAssertNil(b.pixel(x: 2, y: 0))
    }

    // MARK: - Clear and fill

    func testClearPaintsEveryPixel() {
        let b = solid(3, 3, Rgba32(10, 20, 30, 255))
        for y in 0..<3 {
            for x in 0..<3 { XCTAssertEqual(b.pixel(x: x, y: y), Rgba32(10, 20, 30, 255)) }
        }
    }

    func testFillRectIsClippedToTheCanvas() {
        let b = PixelBuffer(width: 4, height: 4)!
        let c = RasterCanvas(buffer: b)
        c.clear(.black)
        c.fillRect(x0: -2, y0: -2, w: 4, h: 4, color: .white)
        XCTAssertEqual(b.pixel(x: 0, y: 0), Rgba32.white)
        XCTAssertEqual(b.pixel(x: 2, y: 2), Rgba32.black)
    }

    func testAFullyTransparentFillDoesNothing() {
        let b = solid(2, 2, .black)
        RasterCanvas(buffer: b).fillRect(x0: 0, y0: 0, w: 2, h: 2, color: .white, opacity: 0)
        XCTAssertEqual(b.pixel(x: 0, y: 0), Rgba32.black)
    }

    // MARK: - Blending

    func testHalfOpaqueWhiteOverBlackIsMidGrey() {
        let b = solid(1, 1, .black)
        RasterCanvas(buffer: b).fillRect(x0: 0, y0: 0, w: 1, h: 1, color: .white, opacity: 0.5)
        let p = b.pixel(x: 0, y: 0)!
        XCTAssertEqual(Int(p.r), 128, accuracy: 1)
        XCTAssertEqual(p.a, 255)
    }

    // The result is UNPREMULTIPLIED. Skipping the divide by the output alpha
    // darkens everything drawn over transparency.
    func testDrawingOverTransparencyKeepsTheColourAtFullStrength() {
        let b = PixelBuffer(width: 1, height: 1)!   // fully transparent
        RasterCanvas(buffer: b).fillRect(x0: 0, y0: 0, w: 1, h: 1, color: .white, opacity: 0.5)
        let p = b.pixel(x: 0, y: 0)!
        XCTAssertEqual(p.r, 255, "colour must not be darkened by the alpha")
        XCTAssertEqual(Int(p.a), 128, accuracy: 1)
    }

    func testStackingTwoHalfLayersApproachesFullOpacity() {
        let b = PixelBuffer(width: 1, height: 1)!
        let c = RasterCanvas(buffer: b)
        c.fillRect(x0: 0, y0: 0, w: 1, h: 1, color: .white, opacity: 0.5)
        c.fillRect(x0: 0, y0: 0, w: 1, h: 1, color: .white, opacity: 0.5)
        XCTAssertEqual(Int(b.pixel(x: 0, y: 0)!.a), 191, accuracy: 1)
    }

    // MARK: - Image fit

    func testFillStretchesToTheWholeRectangle() {
        let src = solid(2, 1, .white)
        let dst = PixelBuffer(width: 4, height: 4)!
        let c = RasterCanvas(buffer: dst)
        c.clear(.black)
        c.drawImage(src, destX: 0, destY: 0, destW: 4, destH: 4, fit: .fill)
        XCTAssertEqual(dst.pixel(x: 0, y: 0), Rgba32.white)
        XCTAssertEqual(dst.pixel(x: 3, y: 3), Rgba32.white)
    }

    // Contain leaves bars; cover fills and crops. That difference is the whole
    // reason both exist.
    func testContainLeavesBarsAndCoverDoesNot() {
        let wide = solid(4, 1, .white)

        let contained = PixelBuffer(width: 4, height: 4)!
        let c1 = RasterCanvas(buffer: contained)
        c1.clear(.black)
        c1.drawImage(wide, destX: 0, destY: 0, destW: 4, destH: 4, fit: .contain)
        XCTAssertEqual(contained.pixel(x: 0, y: 0), Rgba32.black, "top must stay bare")
        XCTAssertEqual(contained.pixel(x: 2, y: 2), Rgba32.white, "middle must be covered")

        let covered = PixelBuffer(width: 4, height: 4)!
        let c2 = RasterCanvas(buffer: covered)
        c2.clear(.black)
        c2.drawImage(wide, destX: 0, destY: 0, destW: 4, destH: 4, fit: .cover)
        XCTAssertEqual(covered.pixel(x: 0, y: 0), Rgba32.white, "cover must fill the corner")
    }

    // Cover must CROP, not spill over its neighbours.
    func testCoverIsClippedToItsDestinationRectangle() {
        let wide = solid(8, 1, .white)
        let dst = PixelBuffer(width: 8, height: 8)!
        let c = RasterCanvas(buffer: dst)
        c.clear(.black)
        c.drawImage(wide, destX: 0, destY: 0, destW: 4, destH: 4, fit: .cover)
        XCTAssertEqual(dst.pixel(x: 6, y: 2), Rgba32.black, "must not paint outside the rect")
    }

    func testADegenerateDrawIsANoOp() {
        let b = solid(2, 2, .black)
        let c = RasterCanvas(buffer: b)
        c.drawImage(solid(2, 2, .white), destX: 0, destY: 0, destW: 0, destH: 4, fit: .fill)
        c.drawImage(solid(2, 2, .white), destX: 0, destY: 0, destW: 4, destH: 4, fit: .fill, opacity: 0)
        XCTAssertEqual(b.pixel(x: 0, y: 0), Rgba32.black)
    }

    // MARK: - Sampling

    func testBilinearInterpolatesBetweenCorners() {
        XCTAssertEqual(RasterCanvas.bilinear(0, 255, 0, 255, 0.5, 0.0), 128)
        XCTAssertEqual(RasterCanvas.bilinear(0, 0, 255, 255, 0.0, 0.5), 128)
        XCTAssertEqual(RasterCanvas.bilinear(0, 255, 0, 255, 0.0, 0.0), 0)
        XCTAssertEqual(RasterCanvas.bilinear(0, 255, 0, 255, 1.0, 0.0), 255)
    }

    // Clamped at the edges, so a sample never wraps to the far side.
    func testSamplingOutsideTheImageClampsRatherThanWraps() {
        let b = PixelBuffer(width: 2, height: 1)!
        let c = RasterCanvas(buffer: b)
        c.fillRect(x0: 0, y0: 0, w: 1, h: 1, color: .white)   // left white, right clear
        let left = RasterCanvas.sample(b, -10, 0)
        XCTAssertEqual(left.r, 255)
    }
}
