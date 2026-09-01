// MediaRealPngTests.swift
//
// THE INFLATE IS THE RISK, AND OUR OWN ENCODER CANNOT EXERCISE IT. PngWriter
// emits STORED deflate blocks, so a round trip through our own PNG proves the
// chunk framing and the filters and nothing at all about Huffman decoding.
// Every real PNG in the world — every photo a person hands this app — is
// dynamic Huffman, which is the one path our own files never take.
//
// So this fixture was compressed by a DIFFERENT encoder (zlib level 9) and its
// expected pixels were recorded before compression. Block type is 2: dynamic
// Huffman, with a code-length alphabet and back-references.

import XCTest
@testable import CircleAI

final class MediaRealPngTests: XCTestCase {

    /// 23x17 RGBA, compressed by an encoder that is not ours.
    private static let realPngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAABcAAAARCAYAAAA2cze9AAAE0UlEQVR42g2QQWjfdBzF/+qydV3t" +
            "au1C1aU0sWgpGdQ5lhkLmUFGUTMqVBJqwGFEHIwwJGJ/l/FlRPQwyIIQNHgYQmAqhECGl6AIShDi" +
            "IdnJQ4QJQoIHGbl4EZ6/w+O9y3t8eLPZbEYnZjOIXDLPKneNy+TZ4u5weTz73BlXyHPEPeXKeC64" +
            "V1w1zx33nmvgeeI+m0kznJAegSg9SrL0GFTpCDRJIFM6Cks6BkeaI086Dl+aB5NOUCgtIJIeRyot" +
            "UiadRCEtoZKeoFpaRic9iV5aoUE6hUkS+bjOx/VHSdSPQNaPQtXnSNPnYeoLsPRFcvQlePoyfH2F" +
            "mC4i1FcR6U9Tqp9Gpq+h0Nep0hXU+gY6/Tnq9U0M+hYmXaXZzOa32I9BtI9Cto+Tai9As0/CtJfJ" +
            "sk/BsVfh2c+Qb6+B2TJCe4Mi+3mk9hYy+wwV9jYq+0XU9nnq7JfQ2zsYbIMm2+TkAScPjkAM5kgO" +
            "FqAGS9CCFTKDVVjBaTjBOnnBBvxgEyxQKQy2EQXnkAYXKAt2UAQXUQWvUh3sogveQB/s0RDsYwoc" +
            "Ph7z8VggMZ6HHJ+EGq+QFj8FM16DFT9LTrwJLz4DPz5LLNYQxjuI4lcojS8hi19HEe9RFb+FOj5A" +
            "F79Dffwehvgqptjnt+T8lvwoxHwBcr5Mar4KLV+DmW+QlW/Bybfh5efJz3fAchNhvktRfhlpvo8s" +
            "P6Aiv4Iqfx91fo26/EP0+SGG/AZNecjJG07eHIPYLJLcnILanIbWPEtmswWreQFOc4G8xoDfXAJr" +
            "LAqbfUTN20ibdylrrqJorqNqPqa6uYGu+QR9c4uG5nNMTcrHRz4+zpE4LkEeV6GO66SNmzDHbVjj" +
            "BXLGi/DGXfjjHrHRQTheQTR+QOl4Hdl4iGIkqsbPUI+30Y1fUD/ewTDexTQW/BaB3yIchygsQxae" +
            "IVXYgCacgSmcJ0sw4Ai78IQ3yRcOwAQPoXCNIuEjpMINZMKnVAi3UQlfoha+pk74Dr1wD4PwA01C" +
            "zckVTq7MQ1RWSFbWoCqb0JSzZCo7sJRLcJQ98pQD+Mp7YIpPoXKISLmJVLlFmZKgUO6gUr6lWrmH" +
            "TvkRvfIrDcp9TErPxw0+bpwg0RAhGzJUQyXN0GAaJizDIsdw4BkefMMnZjCERojIiCg1UmRGhsIo" +
            "qDIq1EaNzuioN3oMxoDJmPgtLr/FXYDorkJ2N0h1t6G5OzDdXbLcfTjuFXjuNfLdQzA3ROjepsj9" +
            "Cql7F5l7jwr3J1Tub6jd36lz/0LvPsTg/keTO8fJGSdnj0NkT5PMnofKzkFjr5DJLsNib8NhH5DH" +
            "PoLPboKxiEL2FSL2DVL2PWXsZxSsRcX+oJr9jY79i54doYE9gYmt8fGEjyeLJCanISdbUJMLpCWX" +
            "YCb7sJJ3yUmuw0tuwE9uEUtShMldRMn3lCa/IEvuo0geUJX8gzr5D10yT32yiiF5DlNyjt9S8lvK" +
            "kxDLNcjlGVLLHWjl6zDLA7LKq3DKQ3jlp+SXCViZISzvUVT+jLS8j6z8k4ryIaoSqMtF6koJfali" +
            "KF+mqXyNk7ecvF2C2K6T3G5DbS9Ca/fIbK/Aaq/DaYm89jb89g5YW1DY/oSobZG2DyhrH6Lg/apd" +
            "orpdR8f7fXuRhnYPE+//D2HvGrhwdOJGAAAAAElFTkSuQmCC"

    /// The pixels that encoder was handed, recorded before compression.
    private static let expectedPixels: [UInt8] = [0, 0, 0, 128, 11, 0, 0, 255, 22, 0, 0, 255, 33, 0, 0, 128, 44, 0, 0, 255, 55, 0, 0, 255, 66, 0, 0, 128, 77, 0, 0, 255, 88, 0, 0, 255, 99, 0, 0, 128, 110, 0, 0, 255, 121, 0, 0, 255, 132, 0, 0, 128, 143, 0, 0, 255, 154, 0, 0, 255, 165, 0, 0, 128, 176, 0, 0, 255, 187, 0, 0, 255, 198, 0, 0, 128, 209, 0, 0, 255, 220, 0, 0, 255, 231, 0, 0, 128, 242, 0, 0, 255, 0, 29, 0, 255, 11, 29, 1, 255, 22, 29, 2, 128, 33, 29, 3, 255, 44, 29, 4, 255, 55, 29, 5, 128, 66, 29, 6, 255, 77, 29, 7, 255, 88, 29, 8, 128, 99, 29, 9, 255, 110, 29, 10, 255, 121, 29, 11, 128, 132, 29, 12, 255, 143, 29, 13, 255, 154, 29, 14, 128, 165, 29, 15, 255, 176, 29, 16, 255, 187, 29, 17, 128, 198, 29, 18, 255, 209, 29, 19, 255, 220, 29, 20, 128, 231, 29, 21, 255, 242, 29, 22, 255, 0, 58, 0, 255, 11, 58, 2, 128, 22, 58, 4, 255, 33, 58, 6, 255, 44, 58, 8, 128, 55, 58, 10, 255, 66, 58, 12, 255, 77, 58, 14, 128, 88, 58, 16, 255, 99, 58, 18, 255, 110, 58, 20, 128, 121, 58, 22, 255, 132, 58, 24, 255, 143, 58, 26, 128, 154, 58, 28, 255, 165, 58, 30, 255, 176, 58, 32, 128, 187, 58, 34, 255, 198, 58, 36, 255, 209, 58, 38, 128, 220, 58, 40, 255, 231, 58, 42, 255, 242, 58, 44, 128, 0, 87, 0, 128, 11, 87, 3, 255, 22, 87, 6, 255, 33, 87, 9, 128, 44, 87, 12, 255, 55, 87, 15, 255, 66, 87, 18, 128, 77, 87, 21, 255, 88, 87, 24, 255, 99, 87, 27, 128, 110, 87, 30, 255, 121, 87, 33, 255, 132, 87, 36, 128, 143, 87, 39, 255, 154, 87, 42, 255, 165, 87, 45, 128, 176, 87, 48, 255, 187, 87, 51, 255, 198, 87, 54, 128, 209, 87, 57, 255, 220, 87, 60, 255, 231, 87, 63, 128, 242, 87, 66, 255, 0, 116, 0, 255, 11, 116, 4, 255, 22, 116, 8, 128, 33, 116, 12, 255, 44, 116, 16, 255, 55, 116, 20, 128, 66, 116, 24, 255, 77, 116, 28, 255, 88, 116, 32, 128, 99, 116, 36, 255, 110, 116, 40, 255, 121, 116, 44, 128, 132, 116, 48, 255, 143, 116, 52, 255, 154, 116, 56, 128, 165, 116, 60, 255, 176, 116, 64, 255, 187, 116, 68, 128, 198, 116, 72, 255, 209, 116, 76, 255, 220, 116, 80, 128, 231, 116, 84, 255, 242, 116, 88, 255, 0, 145, 0, 255, 11, 145, 5, 128, 22, 145, 10, 255, 33, 145, 15, 255, 44, 145, 20, 128, 55, 145, 25, 255, 66, 145, 30, 255, 77, 145, 35, 128, 88, 145, 40, 255, 99, 145, 45, 255, 110, 145, 50, 128, 121, 145, 55, 255, 132, 145, 60, 255, 143, 145, 65, 128, 154, 145, 70, 255, 165, 145, 75, 255, 176, 145, 80, 128, 187, 145, 85, 255, 198, 145, 90, 255, 209, 145, 95, 128, 220, 145, 100, 255, 231, 145, 105, 255, 242, 145, 110, 128, 0, 174, 0, 128, 11, 174, 6, 255, 22, 174, 12, 255, 33, 174, 18, 128, 44, 174, 24, 255, 55, 174, 30, 255, 66, 174, 36, 128, 77, 174, 42, 255, 88, 174, 48, 255, 99, 174, 54, 128, 110, 174, 60, 255, 121, 174, 66, 255, 132, 174, 72, 128, 143, 174, 78, 255, 154, 174, 84, 255, 165, 174, 90, 128, 176, 174, 96, 255, 187, 174, 102, 255, 198, 174, 108, 128, 209, 174, 114, 255, 220, 174, 120, 255, 231, 174, 126, 128, 242, 174, 132, 255, 0, 203, 0, 255, 11, 203, 7, 255, 22, 203, 14, 128, 33, 203, 21, 255, 44, 203, 28, 255, 55, 203, 35, 128, 66, 203, 42, 255, 77, 203, 49, 255, 88, 203, 56, 128, 99, 203, 63, 255, 110, 203, 70, 255, 121, 203, 77, 128, 132, 203, 84, 255, 143, 203, 91, 255, 154, 203, 98, 128, 165, 203, 105, 255, 176, 203, 112, 255, 187, 203, 119, 128, 198, 203, 126, 255, 209, 203, 133, 255, 220, 203, 140, 128, 231, 203, 147, 255, 242, 203, 154, 255, 0, 232, 0, 255, 11, 232, 8, 128, 22, 232, 16, 255, 33, 232, 24, 255, 44, 232, 32, 128, 55, 232, 40, 255, 66, 232, 48, 255, 77, 232, 56, 128, 88, 232, 64, 255, 99, 232, 72, 255, 110, 232, 80, 128, 121, 232, 88, 255, 132, 232, 96, 255, 143, 232, 104, 128, 154, 232, 112, 255, 165, 232, 120, 255, 176, 232, 128, 128, 187, 232, 136, 255, 198, 232, 144, 255, 209, 232, 152, 128, 220, 232, 160, 255, 231, 232, 168, 255, 242, 232, 176, 128, 0, 5, 0, 128, 11, 5, 9, 255, 22, 5, 18, 255, 33, 5, 27, 128, 44, 5, 36, 255, 55, 5, 45, 255, 66, 5, 54, 128, 77, 5, 63, 255, 88, 5, 72, 255, 99, 5, 81, 128, 110, 5, 90, 255, 121, 5, 99, 255, 132, 5, 108, 128, 143, 5, 117, 255, 154, 5, 126, 255, 165, 5, 135, 128, 176, 5, 144, 255, 187, 5, 153, 255, 198, 5, 162, 128, 209, 5, 171, 255, 220, 5, 180, 255, 231, 5, 189, 128, 242, 5, 198, 255, 0, 34, 0, 255, 11, 34, 10, 255, 22, 34, 20, 128, 33, 34, 30, 255, 44, 34, 40, 255, 55, 34, 50, 128, 66, 34, 60, 255, 77, 34, 70, 255, 88, 34, 80, 128, 99, 34, 90, 255, 110, 34, 100, 255, 121, 34, 110, 128, 132, 34, 120, 255, 143, 34, 130, 255, 154, 34, 140, 128, 165, 34, 150, 255, 176, 34, 160, 255, 187, 34, 170, 128, 198, 34, 180, 255, 209, 34, 190, 255, 220, 34, 200, 128, 231, 34, 210, 255, 242, 34, 220, 255, 0, 63, 0, 255, 11, 63, 11, 128, 22, 63, 22, 255, 33, 63, 33, 255, 44, 63, 44, 128, 55, 63, 55, 255, 66, 63, 66, 255, 77, 63, 77, 128, 88, 63, 88, 255, 99, 63, 99, 255, 110, 63, 110, 128, 121, 63, 121, 255, 132, 63, 132, 255, 143, 63, 143, 128, 154, 63, 154, 255, 165, 63, 165, 255, 176, 63, 176, 128, 187, 63, 187, 255, 198, 63, 198, 255, 209, 63, 209, 128, 220, 63, 220, 255, 231, 63, 231, 255, 242, 63, 242, 128, 0, 92, 0, 128, 11, 92, 12, 255, 22, 92, 24, 255, 33, 92, 36, 128, 44, 92, 48, 255, 55, 92, 60, 255, 66, 92, 72, 128, 77, 92, 84, 255, 88, 92, 96, 255, 99, 92, 108, 128, 110, 92, 120, 255, 121, 92, 132, 255, 132, 92, 144, 128, 143, 92, 156, 255, 154, 92, 168, 255, 165, 92, 180, 128, 176, 92, 192, 255, 187, 92, 204, 255, 198, 92, 216, 128, 209, 92, 228, 255, 220, 92, 240, 255, 231, 92, 252, 128, 242, 92, 8, 255, 0, 121, 0, 255, 11, 121, 13, 255, 22, 121, 26, 128, 33, 121, 39, 255, 44, 121, 52, 255, 55, 121, 65, 128, 66, 121, 78, 255, 77, 121, 91, 255, 88, 121, 104, 128, 99, 121, 117, 255, 110, 121, 130, 255, 121, 121, 143, 128, 132, 121, 156, 255, 143, 121, 169, 255, 154, 121, 182, 128, 165, 121, 195, 255, 176, 121, 208, 255, 187, 121, 221, 128, 198, 121, 234, 255, 209, 121, 247, 255, 220, 121, 4, 128, 231, 121, 17, 255, 242, 121, 30, 255, 0, 150, 0, 255, 11, 150, 14, 128, 22, 150, 28, 255, 33, 150, 42, 255, 44, 150, 56, 128, 55, 150, 70, 255, 66, 150, 84, 255, 77, 150, 98, 128, 88, 150, 112, 255, 99, 150, 126, 255, 110, 150, 140, 128, 121, 150, 154, 255, 132, 150, 168, 255, 143, 150, 182, 128, 154, 150, 196, 255, 165, 150, 210, 255, 176, 150, 224, 128, 187, 150, 238, 255, 198, 150, 252, 255, 209, 150, 10, 128, 220, 150, 24, 255, 231, 150, 38, 255, 242, 150, 52, 128, 0, 179, 0, 128, 11, 179, 15, 255, 22, 179, 30, 255, 33, 179, 45, 128, 44, 179, 60, 255, 55, 179, 75, 255, 66, 179, 90, 128, 77, 179, 105, 255, 88, 179, 120, 255, 99, 179, 135, 128, 110, 179, 150, 255, 121, 179, 165, 255, 132, 179, 180, 128, 143, 179, 195, 255, 154, 179, 210, 255, 165, 179, 225, 128, 176, 179, 240, 255, 187, 179, 255, 255, 198, 179, 14, 128, 209, 179, 29, 255, 220, 179, 44, 255, 231, 179, 59, 128, 242, 179, 74, 255, 0, 208, 0, 255, 11, 208, 16, 255, 22, 208, 32, 128, 33, 208, 48, 255, 44, 208, 64, 255, 55, 208, 80, 128, 66, 208, 96, 255, 77, 208, 112, 255, 88, 208, 128, 128, 99, 208, 144, 255, 110, 208, 160, 255, 121, 208, 176, 128, 132, 208, 192, 255, 143, 208, 208, 255, 154, 208, 224, 128, 165, 208, 240, 255, 176, 208, 0, 255, 187, 208, 16, 128, 198, 208, 32, 255, 209, 208, 48, 255, 220, 208, 64, 128, 231, 208, 80, 255, 242, 208, 96, 255]

    func testAPngCompressedByAnotherEncoderDecodesExactly() throws {
        let data = Data(base64Encoded: Self.realPngBase64)!
        let img = try ImageCodecs.decodePng(data)

        XCTAssertEqual(img.width, 23)
        XCTAssertEqual(img.height, 17)
        XCTAssertEqual(img.pixels, Self.expectedPixels)
    }

    func testTheFixtureReallyIsDynamicHuffmanAndNotAStoredBlock() throws {
        // If this ever becomes a stored block the test above stops proving
        // anything, and it would stop silently.
        let data = [UInt8](Data(base64Encoded: Self.realPngBase64)!)

        var pos = 8
        var idat: [UInt8] = []
        while pos + 12 <= data.count {
            let len = Int(data[pos]) << 24 | Int(data[pos + 1]) << 16
                    | Int(data[pos + 2]) << 8 | Int(data[pos + 3])
            let type = Array(data[(pos + 4)..<(pos + 8)])
            if type == Array("IDAT".utf8) {
                idat = Array(data[(pos + 8)..<(pos + 8 + len)])
                break
            }
            pos += 12 + len
        }
        XCTAssertFalse(idat.isEmpty)

        // Two bytes of zlib header, then BFINAL and a two-bit BTYPE.
        let btype = (Int(idat[2]) >> 1) & 3
        XCTAssertEqual(btype, 2, "fixture must stay dynamic-Huffman compressed")
    }

    func testTheAlphaChannelSurvivesARealCompressedFile() throws {
        // Colour type 6 with a varying alpha: a decoder that silently forced
        // opacity would still produce a picture, just the wrong one.
        let img = try ImageCodecs.decodePng(Data(base64Encoded: Self.realPngBase64)!)
        let alphas = Set(stride(from: 3, to: img.pixels.count, by: 4).map { img.pixels[$0] })
        XCTAssertEqual(alphas, [128, 255])
    }

    func testBackReferencesActuallyOccurInTheFixture() throws {
        // A stream of pure literals would exercise the Huffman tables but never
        // the length/distance copy loop, which is where an overlapping run gets
        // it wrong. The image is a repeating pattern precisely so this happens.
        let data = Data(base64Encoded: Self.realPngBase64)!
        let img = try ImageCodecs.decodePng(data)
        // 23 * 17 * 4 = 1564 raw bytes plus filters, in well under that
        // compressed: only back-references can do that.
        XCTAssertLessThan(data.count, img.pixels.count)
    }
}
