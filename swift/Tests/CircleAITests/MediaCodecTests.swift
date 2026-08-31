import XCTest
@testable import CircleAI

/// Checksums and zlib framing.
final class MediaCodecTests: XCTestCase {

    func solid(_ w: Int, _ h: Int, _ c: Rgba32) -> PixelBuffer {
        let b = PixelBuffer(width: w, height: h)!
        RasterCanvas(buffer: b).clear(c)
        return b
    }

    // The published CRC-32 of "123456789".
    func testCrc32MatchesTheKnownVector() {
        XCTAssertEqual(Crc32.compute(Array("123456789".utf8)), 0xCBF4_3926)
        XCTAssertEqual(Crc32.compute([]), 0)
    }

    // The published Adler-32 of "Wikipedia".
    func testAdler32MatchesTheKnownVector() {
        XCTAssertEqual(Adler32.compute(Array("Wikipedia".utf8)), 0x11E6_0398)
        // Empty input is 1, not 0 - the low sum starts at one.
        XCTAssertEqual(Adler32.compute([]), 1)
    }

    func testTheZlibHeaderIsTheStandardDeflateOne() {
        let z = ZlibStored.compress([1, 2, 3])
        XCTAssertEqual(z[0], 0x78)
        XCTAssertEqual(z[1], 0x01)
    }

    // LEN and its ONE-COMPLEMENT is what a decoder checks, and the easy thing
    // to get wrong.
    func testAStoredBlockCarriesLenAndItsComplement() {
        let z = ZlibStored.compress([0xAA, 0xBB, 0xCC])
        XCTAssertEqual(z[2], 1, "final block flag")
        let len = Int(z[3]) | (Int(z[4]) << 8)
        let nlen = Int(z[5]) | (Int(z[6]) << 8)
        XCTAssertEqual(len, 3)
        XCTAssertEqual(nlen, (~3) & 0xFFFF)
        XCTAssertEqual(Array(z[7..<10]), [0xAA, 0xBB, 0xCC])
    }

    func testTheTrailingAdlerIsOfTheUncompressedBytes() {
        let payload: [UInt8] = Array("hello".utf8)
        let z = ZlibStored.compress(payload)
        let tail = Array(z.suffix(4))
        let adler = (UInt32(tail[0]) << 24) | (UInt32(tail[1]) << 16)
                  | (UInt32(tail[2]) << 8) | UInt32(tail[3])
        XCTAssertEqual(adler, Adler32.compute(payload))
    }

    // A payload past 65535 has to split into several blocks, and only the last
    // may be marked final.
    func testALargePayloadSplitsIntoBlocksWithOneFinalFlag() {
        let big = [UInt8](repeating: 7, count: 70_000)
        let z = ZlibStored.compress(big)

        var finals = 0, offset = 2, total = 0
        while offset + 5 <= z.count - 4 {
            let isFinal = z[offset]
            let len = Int(z[offset + 1]) | (Int(z[offset + 2]) << 8)
            if isFinal == 1 { finals += 1 }
            total += len
            offset += 5 + len
        }
        XCTAssertEqual(finals, 1)
        XCTAssertEqual(total, 70_000)
    }

    func testEmptyInputStillProducesAValidStream() {
        let z = ZlibStored.compress([])
        XCTAssertEqual(z[0], 0x78)
        XCTAssertEqual(z.count, 2 + 5 + 4)
    }
}
