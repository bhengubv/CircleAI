// MediaCodecs.swift
//
// PNG and APNG writing: CRC-32, Adler-32, zlib framing, filtered scanlines and
// the chunk layout.
//
// Ported from src/CircleAI.Media/Rendering.
//
// COMPRESSION: the C# hands the deflate to the BCL ZLibStream. Swift has no
// cross-platform zlib (Compression is Apple-only, and this package targets
// watchOS and Linux too), so this writes STORED deflate blocks - valid zlib
// with a real Adler-32, accepted by every decoder, just uncompressed. That is
// a size trade, not a correctness one, and it is stated here rather than
// discovered later.

import Foundation

// MARK: - Checksums

public enum Crc32 {
    private static let table: [UInt32] = {
        (0..<256).map { i -> UInt32 in
            var c = UInt32(i)
            for _ in 0..<8 { c = (c & 1) != 0 ? (0xEDB8_8320 ^ (c >> 1)) : (c >> 1) }
            return c
        }
    }()

    public static func update(_ crc: UInt32, _ bytes: [UInt8]) -> UInt32 {
        var c = crc
        for b in bytes { c = table[Int((c ^ UInt32(b)) & 0xFF)] ^ (c >> 8) }
        return c
    }

    public static func compute(_ bytes: [UInt8]) -> UInt32 {
        update(0xFFFF_FFFF, bytes) ^ 0xFFFF_FFFF
    }
}

public enum Adler32 {
    /// Two rolling sums mod 65521, the largest prime below 2^16.
    public static func compute(_ bytes: [UInt8]) -> UInt32 {
        var a: UInt32 = 1, b: UInt32 = 0
        for byte in bytes {
            a = (a + UInt32(byte)) % 65521
            b = (b + a) % 65521
        }
        return (b << 16) | a
    }
}

// MARK: - zlib framing

public enum ZlibStored {
    /// A zlib stream whose deflate payload is stored (uncompressed) blocks.
    ///
    /// Each block carries a 5-byte header: one bit for final, two for the type
    /// (00 = stored), then LEN and its ONE-COMPLEMENT - which is what a decoder
    /// checks, and the easy thing to get wrong.
    public static func compress(_ data: [UInt8]) -> [UInt8] {
        var out: [UInt8] = [0x78, 0x01]   // CMF/FLG: deflate, 32K window, no dict

        let maxBlock = 65535
        if data.isEmpty {
            out += [0x01, 0x00, 0x00, 0xFF, 0xFF]
        } else {
            var offset = 0
            while offset < data.count {
                let len = min(maxBlock, data.count - offset)
                let isFinal: UInt8 = (offset + len >= data.count) ? 1 : 0
                out.append(isFinal)
                out.append(UInt8(len & 0xFF))
                out.append(UInt8((len >> 8) & 0xFF))
                let nlen = ~len & 0xFFFF
                out.append(UInt8(nlen & 0xFF))
                out.append(UInt8((nlen >> 8) & 0xFF))
                out += data[offset..<(offset + len)]
                offset += len
            }
        }

        // Adler-32 of the UNCOMPRESSED data, big-endian.
        let adler = Adler32.compute(data)
        out += [UInt8((adler >> 24) & 0xFF), UInt8((adler >> 16) & 0xFF),
                UInt8((adler >> 8) & 0xFF), UInt8(adler & 0xFF)]
        return out
    }
}

// MARK: - PNG

public enum PngWriter {
    public static let signature: [UInt8] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]

    /// Every row is prefixed with its filter byte. Filter 0 (None) keeps this
    /// deterministic and cheap; the format requires the byte either way.
    public static func filteredScanlines(_ image: PixelBuffer) -> [UInt8] {
        var out: [UInt8] = []
        out.reserveCapacity(image.height * (1 + image.stride))
        for y in 0..<image.height {
            out.append(0)
            let start = y * image.stride
            out += image.pixels[start..<(start + image.stride)]
        }
        return out
    }

    /// length, type, data, CRC - and the CRC covers the TYPE as well as the
    /// data, which is the part that is easy to miss.
    public static func chunk(type: String, data: [UInt8]) -> [UInt8] {
        let typeBytes = Array(type.utf8)
        var out: [UInt8] = []
        let len = UInt32(data.count)
        out += [UInt8((len >> 24) & 0xFF), UInt8((len >> 16) & 0xFF),
                UInt8((len >> 8) & 0xFF), UInt8(len & 0xFF)]
        out += typeBytes
        out += data
        let crc = Crc32.compute(typeBytes + data)
        out += [UInt8((crc >> 24) & 0xFF), UInt8((crc >> 16) & 0xFF),
                UInt8((crc >> 8) & 0xFF), UInt8(crc & 0xFF)]
        return out
    }

    public static func ihdr(width: Int, height: Int) -> [UInt8] {
        var d: [UInt8] = []
        d += be32(UInt32(width))
        d += be32(UInt32(height))
        d += [8, 6, 0, 0, 0]   // 8-bit, truecolour+alpha, deflate, no filter, no interlace
        return d
    }

    /// A complete single-frame PNG.
    public static func encode(_ image: PixelBuffer) -> Data {
        var out = signature
        out += chunk(type: "IHDR", data: ihdr(width: image.width, height: image.height))
        out += chunk(type: "IDAT", data: ZlibStored.compress(filteredScanlines(image)))
        out += chunk(type: "IEND", data: [])
        return Data(out)
    }

    static func be32(_ v: UInt32) -> [UInt8] {
        [UInt8((v >> 24) & 0xFF), UInt8((v >> 16) & 0xFF), UInt8((v >> 8) & 0xFF), UInt8(v & 0xFF)]
    }

    static func be16(_ v: UInt16) -> [UInt8] { [UInt8((v >> 8) & 0xFF), UInt8(v & 0xFF)] }
}

// MARK: - APNG

public struct EncodedClip: Sendable, Equatable {
    public let bytes: Data
    public let mimeType: String
    public let frameCount: Int
    public let size: RenderSize
    public let frameRate: Int
    public let backendId: String

    public init(bytes: Data, mimeType: String, frameCount: Int, size: RenderSize,
                frameRate: Int, backendId: String) {
        self.bytes = bytes
        self.mimeType = mimeType
        self.frameCount = frameCount
        self.size = size
        self.frameRate = frameRate
        self.backendId = backendId
    }
}

public struct ClipEncodeOptions: Sendable, Equatable {
    public let size: RenderSize
    public let frameRate: Int
    public let frameCount: Int
    /// 0 means loop forever, which is what a caller almost always wants.
    public let loopCount: Int

    public init(size: RenderSize, frameRate: Int, frameCount: Int, loopCount: Int = 0) {
        self.size = size
        self.frameRate = frameRate
        self.frameCount = frameCount
        self.loopCount = loopCount
    }
}

public enum ApngError: Error, CustomStringConvertible, Equatable {
    case mismatchedFrameSize
    public var description: String {
        "All APNG frames must share the first frame dimensions."
    }
}

public protocol IVideoEncoder: Sendable {
    var backendId: String { get }
    var outputMimeType: String { get }
    func encode(frames: [PixelBuffer], options: ClipEncodeOptions) throws -> EncodedClip
}

/// Writes an animated PNG. Chosen over a video container because it needs no
/// codec, no licence and no platform decoder - every browser and every gallery
/// already opens one.
public struct AnimatedPngEncoder: IVideoEncoder {
    public static let instance = AnimatedPngEncoder()
    public init() {}

    public var backendId: String { "apng" }
    public var outputMimeType: String { "image/apng" }

    public func encode(frames: [PixelBuffer], options: ClipEncodeOptions) throws -> EncodedClip {
        let delayDen = min(65535, max(1, options.frameRate <= 0 ? 12 : options.frameRate))
        let loop = max(0, options.loopCount)

        guard let first = frames.first else {
            return EncodedClip(bytes: Data(), mimeType: outputMimeType, frameCount: 0,
                               size: options.size, frameRate: options.frameRate, backendId: backendId)
        }
        let w = first.width, h = first.height
        for f in frames where f.width != w || f.height != h { throw ApngError.mismatchedFrameSize }

        var out = PngWriter.signature
        out += PngWriter.chunk(type: "IHDR", data: PngWriter.ihdr(width: w, height: h))

        // acTL knows the frame count up front here, so nothing needs patching
        // afterwards the way the streaming C# version does.
        var actl = PngWriter.be32(UInt32(frames.count))
        actl += PngWriter.be32(UInt32(loop))
        out += PngWriter.chunk(type: "acTL", data: actl)

        var seq: UInt32 = 0

        // Frame 0 is the DEFAULT image: fcTL then IDAT, not fdAT. A viewer
        // that knows nothing about APNG shows exactly this frame.
        out += PngWriter.chunk(type: "fcTL",
                               data: Self.fctl(seq: &seq, w: w, h: h, delayDen: delayDen))
        out += PngWriter.chunk(type: "IDAT",
                               data: ZlibStored.compress(PngWriter.filteredScanlines(first)))

        for frame in frames.dropFirst() {
            out += PngWriter.chunk(type: "fcTL",
                                   data: Self.fctl(seq: &seq, w: w, h: h, delayDen: delayDen))
            // fdAT carries the sequence number ahead of the same zlib payload
            // an IDAT would hold, and the number keeps counting across both
            // chunk kinds.
            var fdat = PngWriter.be32(seq)
            seq += 1
            fdat += ZlibStored.compress(PngWriter.filteredScanlines(frame))
            out += PngWriter.chunk(type: "fdAT", data: fdat)
        }

        out += PngWriter.chunk(type: "IEND", data: [])

        return EncodedClip(bytes: Data(out), mimeType: outputMimeType, frameCount: frames.count,
                           size: RenderSize(width: w, height: h),
                           frameRate: options.frameRate, backendId: backendId)
    }

    /// delay is a FRACTION: numerator 1 over the frame rate, so 12 fps is 1/12
    /// of a second per frame and no rounding is needed.
    static func fctl(seq: inout UInt32, w: Int, h: Int, delayDen: Int) -> [UInt8] {
        var f = PngWriter.be32(seq)
        seq += 1
        f += PngWriter.be32(UInt32(w))
        f += PngWriter.be32(UInt32(h))
        f += PngWriter.be32(0)                        // x offset
        f += PngWriter.be32(0)                        // y offset
        f += PngWriter.be16(1)                        // delay_num
        f += PngWriter.be16(UInt16(delayDen))         // delay_den
        f += [0, 0]                                   // dispose NONE, blend SOURCE
        return f
    }
}
