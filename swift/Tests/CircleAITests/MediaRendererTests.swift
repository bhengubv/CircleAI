import XCTest
@testable import CircleAI

/// The renderer end to end: layers, z-order, motion and text layout.
final class MediaRendererTests: XCTestCase {

    private let size = RenderSize(width: 16, height: 16)

    private func rawLayer(_ c: Rgba32, rect: NormRect = .full, z: Int = 0,
                          motion: Motion? = nil, opacity: Double = 1.0,
                          fit: ContentFit = .fill) -> ImageLayer {
        let px: [UInt8] = [c.r, c.g, c.b, c.a]
        return ImageLayer(source: .raw(rgba: px, width: 1, height: 1),
                          rect: rect, fit: fit, opacity: opacity, motion: motion, zOrder: z)
    }

    private func render(_ spec: MediaSpec, at fraction: Double = 0) -> PixelBuffer {
        ManagedMediaRenderer().renderStill(spec, posterFraction: fraction)!
    }

    // MARK: - Composition

    func testTheBackgroundIsPaintedFirst() {
        let out = render(MediaSpec.still(size: size, background: .black))
        XCTAssertEqual(out.pixel(x: 8, y: 8), Rgba32.black)
    }

    func testALayerIsDrawnOverTheBackground() {
        let out = render(MediaSpec.still(size: size, background: .black,
                                         images: [rawLayer(.white)]))
        XCTAssertEqual(out.pixel(x: 8, y: 8), Rgba32.white)
    }

    // Higher z draws LAST, so it wins.
    func testTheHigherZOrderWins() {
        let out = render(MediaSpec.still(
            size: size, background: .black,
            images: [rawLayer(.white, z: 10), rawLayer(Rgba32.rgb(255, 0, 0), z: 20)]))
        XCTAssertEqual(out.pixel(x: 8, y: 8), Rgba32.rgb(255, 0, 0))
    }

    // Two layers at the SAME depth keep the caller order, so a spec renders
    // the same way twice.
    func testEqualZOrderKeepsTheOrderTheCallerListed() {
        let first = render(MediaSpec.still(
            size: size, background: .black,
            images: [rawLayer(.white, z: 5), rawLayer(Rgba32.rgb(0, 255, 0), z: 5)]))
        let again = render(MediaSpec.still(
            size: size, background: .black,
            images: [rawLayer(.white, z: 5), rawLayer(Rgba32.rgb(0, 255, 0), z: 5)]))
        XCTAssertEqual(first.pixel(x: 8, y: 8), Rgba32.rgb(0, 255, 0))
        XCTAssertEqual(first.pixel(x: 8, y: 8), again.pixel(x: 8, y: 8))
    }

    func testAFullyTransparentLayerIsSkipped() {
        let out = render(MediaSpec.still(size: size, background: .black,
                                         images: [rawLayer(.white, opacity: 0)]))
        XCTAssertEqual(out.pixel(x: 8, y: 8), Rgba32.black)
    }

    // A layer drawn only into part of the canvas leaves the rest alone.
    func testALayerIsConfinedToItsRectangle() {
        let out = render(MediaSpec.still(
            size: size, background: .black,
            images: [rawLayer(.white, rect: NormRect(x: 0, y: 0, w: 0.5, h: 0.5))]))
        XCTAssertEqual(out.pixel(x: 2, y: 2), Rgba32.white)
        XCTAssertEqual(out.pixel(x: 14, y: 14), Rgba32.black)
    }

    // MARK: - Motion across frames

    func testAFadeInStartsInvisibleAndEndsVisible() {
        let spec = MediaSpec(size: size, background: .black,
                             images: [rawLayer(.white, motion: .fadeIn)],
                             duration: 1, frameRate: 5)
        let frames = ManagedMediaRenderer().frames(spec)
        XCTAssertEqual(frames.count, 5)
        XCTAssertEqual(frames.first!.pixel(x: 8, y: 8), Rgba32.black, "first frame is fully faded out")
        XCTAssertEqual(frames.last!.pixel(x: 8, y: 8), Rgba32.white, "last frame is fully in")
    }

    // The first frame is at progress 0 and the LAST at 1 - not one step short.
    func testTheLastFrameReachesFullProgress() {
        let spec = MediaSpec(size: size, background: .black,
                             images: [rawLayer(.white, motion: Motion(fromOpacity: 0, toOpacity: 1))],
                             duration: 1, frameRate: 4)
        let frames = ManagedMediaRenderer().frames(spec)
        XCTAssertEqual(frames.last!.pixel(x: 8, y: 8), Rgba32.white)
    }

    func testAStillProducesExactlyOneFrame() {
        XCTAssertEqual(ManagedMediaRenderer()
            .frames(MediaSpec.still(size: size, background: .black)).count, 1)
    }

    // Scale is applied about the CENTRE, so a zoom pushes out evenly rather
    // than sliding down and to the right.
    func testScaleGrowsAboutTheCentre() {
        let r = ManagedMediaRenderer.placeRect(NormRect(x: 0.25, y: 0.25, w: 0.5, h: 0.5),
                                               RenderSize(width: 100, height: 100),
                                               scale: 2.0, translate: .zero)
        XCTAssertEqual(r.x, 0, accuracy: 1e-9)
        XCTAssertEqual(r.y, 0, accuracy: 1e-9)
        XCTAssertEqual(r.w, 100, accuracy: 1e-9)
        XCTAssertEqual(r.x + r.w / 2, 50, accuracy: 1e-9, "the centre must not move")
    }

    func testTranslateIsInCanvasFractions() {
        let r = ManagedMediaRenderer.placeRect(.full, RenderSize(width: 100, height: 200),
                                               scale: 1.0, translate: NormVec(x: 0.1, y: 0.25))
        XCTAssertEqual(r.x, 10, accuracy: 1e-9)
        XCTAssertEqual(r.y, 50, accuracy: 1e-9)
    }

    // MARK: - Encoded layers with no decoder

    // A raw layer still renders, so a spec built from pixels works on any build.
    func testAnEncodedLayerIsSkippedWhenNoDecoderIsWired() {
        let spec = MediaSpec.still(
            size: size, background: .black,
            images: [ImageLayer(source: .encoded(bytes: Data([1, 2, 3]), mimeHint: "image/png"),
                                rect: .full),
                     rawLayer(.white, z: 1)])
        let out = render(spec)
        XCTAssertEqual(out.pixel(x: 8, y: 8), Rgba32.white)
    }

    // MARK: - Text

    func testTextIsDrawnOntoTheCanvas() {
        let spec = MediaSpec.still(
            size: RenderSize(width: 128, height: 64), background: .black,
            texts: [TextOverlay(text: "HI", rect: .full, fontHeightFraction: 0.5, color: .white)])
        let out = render(spec)
        var inked = 0
        for y in 0..<64 { for x in 0..<128 where out.pixel(x: x, y: y) == Rgba32.white { inked += 1 } }
        XCTAssertGreaterThan(inked, 0, "some ink must have landed")
    }

    // A fully transparent colour means "not set" and becomes white, rather
    // than invisible ink.
    func testUnsetTextColourBecomesWhiteNotInvisible() {
        let spec = MediaSpec.still(
            size: RenderSize(width: 128, height: 64), background: .black,
            texts: [TextOverlay(text: "HI", rect: .full, fontHeightFraction: 0.5)])
        let out = render(spec)
        var inked = 0
        for y in 0..<64 { for x in 0..<128 where out.pixel(x: x, y: y) == Rgba32.white { inked += 1 } }
        XCTAssertGreaterThan(inked, 0)
    }

    func testEmptyTextDrawsNothing() {
        let spec = MediaSpec.still(size: size, background: .black,
                                   texts: [TextOverlay(text: "", rect: .full, color: .white)])
        XCTAssertEqual(render(spec).pixel(x: 8, y: 8), Rgba32.black)
    }
}
