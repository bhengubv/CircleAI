// MediaImageCodecs.swift
//
// Decoding a person's OWN photo, and the fail-closed defaults for the parts of
// the rendering seam that need a device.
//
// WHY THERE IS AN INFLATE IN HERE. The C# side gets DEFLATE from the BCL's
// ZLibStream for free. Swift has no zlib in its standard library, and linking
// the system one would make this package depend on a C module that is spelled
// differently on every platform it has to run on. The algorithm is fixed by RFC
// 1951 and about two hundred lines, so it is written out — which also means the
// numbers are identical everywhere rather than whatever the host's zlib build
// happens to do.
//
// JPEG IS DELIBERATELY NOT DECODED. A real JPEG decoder is a different order of
// work, and every platform already ships one. A host wires it behind the decoder
// seam; until it does, a JPEG fails with a message that says exactly that rather
// than producing a grey rectangle.
//
// Ported from src/CircleAI.Media/Rendering/ImageCodecs.cs, Contracts.cs and
// NullImplementations.cs.

import Foundation

// MARK: - DEFLATE

public enum InflateError: Error, CustomStringConvertible, Equatable {
    case truncated
    case badBlockType(Int)
    case badStoredLength
    case badHuffmanCode
    case badZlibHeader
    case badDistance

    public var description: String {
        switch self {
        case .truncated: return "compressed stream ended mid-symbol"
        case .badBlockType(let t): return "reserved DEFLATE block type \(t)"
        case .badStoredLength: return "stored block length does not match its complement"
        case .badHuffmanCode: return "no Huffman code matches the next bits"
        case .badZlibHeader: return "not a zlib stream"
        case .badDistance: return "back-reference points before the start of the output"
        }
    }
}

/// RFC 1951 DEFLATE, and the RFC 1950 zlib wrapper around it.
public enum Inflate {

    /// Strips the two-byte zlib header (and refuses a preset dictionary, which
    /// PNG never uses) and inflates the rest. The trailing Adler-32 is not
    /// checked: a PNG already carries a CRC-32 per chunk, and failing an image
    /// twice over says nothing new.
    public static func zlib(_ data: [UInt8]) throws -> [UInt8] {
        guard data.count >= 2 else { throw InflateError.truncated }
        let cmf = data[0], flg = data[1]
        // Low nibble 8 is DEFLATE; the checksum makes the pair a multiple of 31.
        guard cmf & 0x0F == 8, (Int(cmf) << 8 | Int(flg)) % 31 == 0, flg & 0x20 == 0 else {
            throw InflateError.badZlibHeader
        }
        return try raw(Array(data[2...]))
    }

    public static func raw(_ data: [UInt8]) throws -> [UInt8] {
        var reader = BitReader(data)
        var out: [UInt8] = []
        out.reserveCapacity(data.count * 4)

        while true {
            let final = try reader.bits(1)
            let type = try reader.bits(2)

            switch type {
            case 0:
                reader.alignToByte()
                let len = try reader.bits(16)
                let nlen = try reader.bits(16)
                guard len == (~nlen & 0xFFFF) else { throw InflateError.badStoredLength }
                try out.append(contentsOf: reader.bytes(len))

            case 1:
                try block(&reader, &out, literals: Huffman.fixedLiterals,
                          distances: Huffman.fixedDistances)

            case 2:
                let (lit, dist) = try dynamicTables(&reader)
                try block(&reader, &out, literals: lit, distances: dist)

            default:
                throw InflateError.badBlockType(type)
            }

            if final == 1 { break }
        }
        return out
    }

    // Lengths 257..285 and distances 0..29, straight from RFC 1951's tables.
    private static let lengthBase = [
        3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
        35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258]
    private static let lengthExtra = [
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
        3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0]
    private static let distanceBase = [
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
        257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145,
        8193, 12289, 16385, 24577]
    private static let distanceExtra = [
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
        7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13]

    private static func block(_ reader: inout BitReader, _ out: inout [UInt8],
                              literals: Huffman, distances: Huffman) throws {
        while true {
            let sym = try literals.decode(&reader)
            if sym == 256 { return }

            if sym < 256 {
                out.append(UInt8(sym))
                continue
            }

            let li = sym - 257
            guard li < lengthBase.count else { throw InflateError.badHuffmanCode }
            let length = lengthBase[li] + (try reader.bits(lengthExtra[li]))

            let dsym = try distances.decode(&reader)
            guard dsym < distanceBase.count else { throw InflateError.badHuffmanCode }
            let distance = distanceBase[dsym] + (try reader.bits(distanceExtra[dsym]))

            guard distance <= out.count else { throw InflateError.badDistance }

            // Copied ONE BYTE AT A TIME on purpose: a back-reference may overlap
            // the bytes it is producing (distance 1, length 100 is a run), and a
            // block copy would read the pre-copy contents.
            var from = out.count - distance
            for _ in 0..<length {
                out.append(out[from])
                from += 1
            }
        }
    }

    /// Reads the code-length alphabet, then the two real alphabets it encodes.
    private static func dynamicTables(_ reader: inout BitReader) throws -> (Huffman, Huffman) {
        let hlit = try reader.bits(5) + 257
        let hdist = try reader.bits(5) + 1
        let hclen = try reader.bits(4) + 4

        // The code-length code lengths arrive in this order, not in numeric
        // order — the common ones first so the rare ones can be omitted.
        let order = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15]
        var clLengths = [Int](repeating: 0, count: 19)
        for i in 0..<hclen { clLengths[order[i]] = try reader.bits(3) }

        let clTable = try Huffman(lengths: clLengths)

        var lengths = [Int]()
        lengths.reserveCapacity(hlit + hdist)
        while lengths.count < hlit + hdist {
            let sym = try clTable.decode(&reader)
            switch sym {
            case 0...15:
                lengths.append(sym)
            case 16:
                guard let last = lengths.last else { throw InflateError.badHuffmanCode }
                let n = 3 + (try reader.bits(2))
                lengths.append(contentsOf: [Int](repeating: last, count: n))
            case 17:
                let n = 3 + (try reader.bits(3))
                lengths.append(contentsOf: [Int](repeating: 0, count: n))
            case 18:
                let n = 11 + (try reader.bits(7))
                lengths.append(contentsOf: [Int](repeating: 0, count: n))
            default:
                throw InflateError.badHuffmanCode
            }
        }
        guard lengths.count >= hlit + hdist else { throw InflateError.truncated }

        return (try Huffman(lengths: Array(lengths[0..<hlit])),
                try Huffman(lengths: Array(lengths[hlit..<(hlit + hdist)])))
    }

    /// Least-significant-bit-first, which is DEFLATE's order and the opposite of
    /// the Huffman codes it carries — those are most-significant first. Getting
    /// the two the same way round produces plausible garbage rather than an
    /// error, which is why they are read by two separate methods here.
    struct BitReader {
        private let data: [UInt8]
        private var pos = 0
        private var bit = 0

        init(_ data: [UInt8]) { self.data = data }

        mutating func bits(_ n: Int) throws -> Int {
            var value = 0
            for i in 0..<n {
                guard pos < data.count else { throw InflateError.truncated }
                let b = (Int(data[pos]) >> bit) & 1
                value |= b << i
                bit += 1
                if bit == 8 { bit = 0; pos += 1 }
            }
            return value
        }

        /// One bit, most-significant first within a Huffman code.
        mutating func codeBit() throws -> Int { try bits(1) }

        mutating func alignToByte() {
            if bit != 0 { bit = 0; pos += 1 }
        }

        mutating func bytes(_ n: Int) throws -> [UInt8] {
            guard pos + n <= data.count else { throw InflateError.truncated }
            defer { pos += n }
            return Array(data[pos..<(pos + n)])
        }
    }

    /// Canonical Huffman, decoded bit by bit against per-length counts. Slower
    /// than a lookup table and small enough to read, which is the trade a codec
    /// that runs a handful of times per image should make.
    struct Huffman {
        private let counts: [Int]        // codes of each length
        private let symbols: [Int]       // symbols ordered by (length, symbol)

        init(lengths: [Int]) throws {
            var counts = [Int](repeating: 0, count: 16)
            for l in lengths where l > 0 { counts[l] += 1 }

            var offsets = [Int](repeating: 0, count: 16)
            for l in 1..<15 { offsets[l + 1] = offsets[l] + counts[l] }

            var symbols = [Int](repeating: 0, count: lengths.filter { $0 > 0 }.count)
            for (sym, l) in lengths.enumerated() where l > 0 {
                symbols[offsets[l]] = sym
                offsets[l] += 1
            }

            self.counts = counts
            self.symbols = symbols
        }

        func decode(_ reader: inout Inflate.BitReader) throws -> Int {
            var code = 0, first = 0, index = 0
            for length in 1...15 {
                code |= try reader.codeBit()
                let count = counts[length]
                if code - first < count { return symbols[index + (code - first)] }
                index += count
                first = (first + count) << 1
                code <<= 1
            }
            throw InflateError.badHuffmanCode
        }

        /// RFC 1951's fixed literal alphabet: 8-bit codes for 0-143 and 280-287,
        /// 9-bit for 144-255, 7-bit for the end-of-block run 256-279.
        static let fixedLiterals: Huffman = {
            var lengths = [Int](repeating: 8, count: 288)
            for i in 144...255 { lengths[i] = 9 }
            for i in 256...279 { lengths[i] = 7 }
            return try! Huffman(lengths: lengths)
        }()

        /// Thirty distances, all five bits.
        static let fixedDistances: Huffman = {
            try! Huffman(lengths: [Int](repeating: 5, count: 30))
        }()
    }
}

// MARK: - Image codecs

public enum ImageCodecError: Error, CustomStringConvertible, Equatable {
    case notPng
    case notBmp
    case corruptChunk
    case missingHeader
    case unsupportedBitDepth(Int)
    case interlaced
    case unsupportedColourType(Int)
    case unknownFilter(Int)
    case scanlineUnderflow
    case unsupportedBmpCompression
    case unsupportedBmpBitDepth(Int)
    case invalidDimensions
    case jpegNeedsAPlatformDecoder
    case unrecognisedFormat

    public var description: String {
        switch self {
        case .notPng: return "Not a PNG stream."
        case .notBmp: return "Not a BMP stream."
        case .corruptChunk: return "Corrupt PNG chunk."
        case .missingHeader: return "PNG missing IHDR."
        case .unsupportedBitDepth(let d):
            return "Unsupported PNG bit depth \(d) (managed decoder handles 8-bit only)."
        case .interlaced: return "Interlaced PNG is not supported in managed code."
        case .unsupportedColourType(let c): return "Unsupported PNG colour type \(c)."
        case .unknownFilter(let f): return "Unknown PNG filter \(f)."
        case .scanlineUnderflow: return "PNG scanline data underflow."
        case .unsupportedBmpCompression: return "Only uncompressed BMP (BI_RGB) is supported."
        case .unsupportedBmpBitDepth(let d): return "Unsupported BMP bit depth \(d)."
        case .invalidDimensions: return "Invalid image dimensions."
        case .jpegNeedsAPlatformDecoder:
            return "JPEG decoding needs a platform decoder (Android BitmapFactory / "
                 + "CoreGraphics) wired through the image-decoder seam."
        case .unrecognisedFormat:
            return "Unrecognised image format; managed decoder supports PNG and BMP."
        }
    }
}

public enum ImageCodecs {

    public static let pngSignature: [UInt8] = [137, 80, 78, 71, 13, 10, 26, 10]

    public static func encodePng(_ image: PixelBuffer) -> Data { PngWriter.encode(image) }

    // MARK: PNG

    public static func decodePng(_ bytes: Data) throws -> PixelBuffer {
        let data = [UInt8](bytes)
        guard data.count >= 8, Array(data[0..<8]) == pngSignature else {
            throw ImageCodecError.notPng
        }

        var pos = 8
        var width = 0, height = 0, colourType = -1, bitDepth = 0, interlace = 0
        var haveHeader = false
        var idat: [UInt8] = []

        while pos + 12 <= data.count {
            let len = Int(be32(data, pos)); pos += 4
            guard len >= 0, pos + 4 + len + 4 <= data.count else {
                throw ImageCodecError.corruptChunk
            }
            let type = Array(data[pos..<(pos + 4)]); pos += 4
            let chunk = Array(data[pos..<(pos + len)])
            pos += len + 4                       // data + CRC (CRC not validated)

            if type == Array("IHDR".utf8) {
                guard chunk.count >= 13 else { throw ImageCodecError.corruptChunk }
                width = Int(be32(chunk, 0))
                height = Int(be32(chunk, 4))
                bitDepth = Int(chunk[8])
                colourType = Int(chunk[9])
                interlace = Int(chunk[12])
                haveHeader = true

                guard width > 0, height > 0 else { throw ImageCodecError.invalidDimensions }
                guard bitDepth == 8 else { throw ImageCodecError.unsupportedBitDepth(bitDepth) }
                guard interlace == 0 else { throw ImageCodecError.interlaced }
                guard [0, 2, 4, 6].contains(colourType) else {
                    throw ImageCodecError.unsupportedColourType(colourType)
                }
            } else if type == Array("IDAT".utf8) {
                idat.append(contentsOf: chunk)
            } else if type == Array("IEND".utf8) {
                break
            }
        }

        guard haveHeader else { throw ImageCodecError.missingHeader }

        let channels = colourType == 0 ? 1 : colourType == 2 ? 3 : colourType == 4 ? 2 : 4
        let raw = try Inflate.zlib(idat)
        let stride = width * channels
        guard raw.count >= height * (stride + 1) else { throw ImageCodecError.scanlineUnderflow }
        guard let out = PixelBuffer(width: width, height: height) else {
            throw ImageCodecError.invalidDimensions
        }

        var cur = [UInt8](repeating: 0, count: stride)
        var prev = [UInt8](repeating: 0, count: stride)
        var px = [UInt8](repeating: 0, count: width * height * 4)

        var ri = 0
        for y in 0..<height {
            let filter = Int(raw[ri]); ri += 1

            for x in 0..<stride {
                let rawv = Int(raw[ri]); ri += 1
                let a = x >= channels ? Int(cur[x - channels]) : 0
                let b = Int(prev[x])
                let c = x >= channels ? Int(prev[x - channels]) : 0

                let value: Int
                switch filter {
                case 0: value = rawv
                case 1: value = rawv + a
                case 2: value = rawv + b
                case 3: value = rawv + ((a + b) >> 1)
                case 4: value = rawv + paeth(a, b, c)
                default: throw ImageCodecError.unknownFilter(filter)
                }
                cur[x] = UInt8(value & 0xFF)
            }

            var di = y * width * 4
            for x in 0..<width {
                let s = x * channels
                let r8, g8, b8, a8: UInt8
                switch colourType {
                case 0: r8 = cur[s]; g8 = cur[s]; b8 = cur[s]; a8 = 255
                case 2: r8 = cur[s]; g8 = cur[s + 1]; b8 = cur[s + 2]; a8 = 255
                case 4: r8 = cur[s]; g8 = cur[s]; b8 = cur[s]; a8 = cur[s + 1]
                default: r8 = cur[s]; g8 = cur[s + 1]; b8 = cur[s + 2]; a8 = cur[s + 3]
                }
                px[di] = r8; px[di + 1] = g8; px[di + 2] = b8; px[di + 3] = a8
                di += 4
            }

            swap(&prev, &cur)
        }

        guard let filled = PixelBuffer(width: width, height: height, pixels: px) else {
            throw ImageCodecError.invalidDimensions
        }
        _ = out
        return filled
    }

    // MARK: BMP

    public static func encodeBmp(_ image: PixelBuffer) -> Data {
        let w = image.width, h = image.height
        // Every BMP row is padded to a four-byte boundary. Forgetting this
        // produces an image that shears progressively — correct at the top-left
        // and wrong by the bottom-right — which reads as a decoder bug.
        let rowStride = (w * 3 + 3) / 4 * 4
        let imageSize = rowStride * h
        let fileSize = 54 + imageSize

        var o = [UInt8](repeating: 0, count: fileSize)
        o[0] = UInt8(ascii: "B"); o[1] = UInt8(ascii: "M")
        putLe32(&o, 2, Int32(fileSize))
        putLe32(&o, 10, 54)
        putLe32(&o, 14, 40)
        putLe32(&o, 18, Int32(w))
        putLe32(&o, 22, Int32(h))            // positive => bottom-up
        putLe16(&o, 26, 1)
        putLe16(&o, 28, 24)
        putLe32(&o, 34, Int32(imageSize))
        putLe32(&o, 38, 2835)
        putLe32(&o, 42, 2835)

        let px = image.pixels
        for y in 0..<h {
            let srcRow = (h - 1 - y) * w * 4
            var dst = 54 + y * rowStride
            for x in 0..<w {
                let s = srcRow + x * 4
                o[dst] = px[s + 2]      // B
                o[dst + 1] = px[s + 1]  // G
                o[dst + 2] = px[s]      // R
                dst += 3
            }
        }
        return Data(o)
    }

    public static func decodeBmp(_ bytes: Data) throws -> PixelBuffer {
        let d = [UInt8](bytes)
        guard d.count >= 54, d[0] == UInt8(ascii: "B"), d[1] == UInt8(ascii: "M") else {
            throw ImageCodecError.notBmp
        }

        let dataOffset = Int(le32(d, 10))
        let width = Int(le32(d, 18))
        let rawHeight = Int(le32(d, 22))
        let bpp = Int(le16(d, 28))
        let compression = Int(le32(d, 30))

        guard compression == 0 else { throw ImageCodecError.unsupportedBmpCompression }
        guard bpp == 24 || bpp == 32 else { throw ImageCodecError.unsupportedBmpBitDepth(bpp) }
        guard width > 0 else { throw ImageCodecError.invalidDimensions }

        // A NEGATIVE height means top-down. Read as unsigned it is an enormous
        // positive number and the decode fails on an underflow check that says
        // nothing about the real problem.
        let topDown = rawHeight < 0
        let height = abs(rawHeight)
        guard height > 0 else { throw ImageCodecError.invalidDimensions }

        let bytesPP = bpp / 8
        let rowStride = (width * bytesPP + 3) / 4 * 4
        guard d.count >= dataOffset + rowStride * height else {
            throw ImageCodecError.scanlineUnderflow
        }

        var px = [UInt8](repeating: 0, count: width * height * 4)
        for y in 0..<height {
            let srcRowIndex = topDown ? y : (height - 1 - y)
            let src = dataOffset + srcRowIndex * rowStride
            var dst = y * width * 4
            for x in 0..<width {
                let s = src + x * bytesPP
                px[dst] = d[s + 2]                                  // R
                px[dst + 1] = d[s + 1]                              // G
                px[dst + 2] = d[s]                                  // B
                px[dst + 3] = bytesPP == 4 ? d[s + 3] : 255
                dst += 4
            }
        }

        guard let out = PixelBuffer(width: width, height: height, pixels: px) else {
            throw ImageCodecError.invalidDimensions
        }
        return out
    }

    // MARK: Helpers

    static func paeth(_ a: Int, _ b: Int, _ c: Int) -> Int {
        let p = a + b - c
        let pa = abs(p - a), pb = abs(p - b), pc = abs(p - c)
        if pa <= pb && pa <= pc { return a }
        return pb <= pc ? b : c
    }

    static func looksPng(_ s: [UInt8]) -> Bool {
        s.count >= 8 && s[0] == 0x89 && s[1] == 0x50 && s[2] == 0x4E && s[3] == 0x47
    }

    static func looksBmp(_ s: [UInt8]) -> Bool {
        s.count >= 2 && s[0] == UInt8(ascii: "B") && s[1] == UInt8(ascii: "M")
    }

    static func looksJpeg(_ s: [UInt8]) -> Bool {
        s.count >= 2 && s[0] == 0xFF && s[1] == 0xD8
    }

    private static func be32(_ d: [UInt8], _ i: Int) -> Int32 {
        Int32(bitPattern: UInt32(d[i]) << 24 | UInt32(d[i + 1]) << 16
              | UInt32(d[i + 2]) << 8 | UInt32(d[i + 3]))
    }

    private static func le32(_ d: [UInt8], _ i: Int) -> Int32 {
        Int32(bitPattern: UInt32(d[i]) | UInt32(d[i + 1]) << 8
              | UInt32(d[i + 2]) << 16 | UInt32(d[i + 3]) << 24)
    }

    private static func le16(_ d: [UInt8], _ i: Int) -> Int16 {
        Int16(bitPattern: UInt16(d[i]) | UInt16(d[i + 1]) << 8)
    }

    private static func putLe32(_ o: inout [UInt8], _ i: Int, _ v: Int32) {
        let u = UInt32(bitPattern: v)
        o[i] = UInt8(u & 0xFF); o[i + 1] = UInt8((u >> 8) & 0xFF)
        o[i + 2] = UInt8((u >> 16) & 0xFF); o[i + 3] = UInt8((u >> 24) & 0xFF)
    }

    private static func putLe16(_ o: inout [UInt8], _ i: Int, _ v: Int16) {
        let u = UInt16(bitPattern: v)
        o[i] = UInt8(u & 0xFF); o[i + 1] = UInt8((u >> 8) & 0xFF)
    }
}

/// PNG and BMP, in pure Swift. Everything else is somebody else's decoder.
public struct ManagedImageDecoder: IMediaImageDecoder, Sendable {
    public static let instance = ManagedImageDecoder()
    public init() {}

    public var backendId: String { "managed-png-bmp" }

    /// The seam's own signature: nil rather than a throw, so an undecodable
    /// layer is SKIPPED and the rest of the composition still renders.
    public func decode(_ bytes: Data, mimeHint: String?) -> PixelBuffer? {
        try? decodeOrThrow(bytes, mimeHint: mimeHint)
    }

    /// The same decode, with the reason.
    ///
    /// Worth having separately because "this is a JPEG and you have not wired a
    /// platform decoder" and "these bytes are not an image" are different
    /// problems with different fixes, and nil cannot tell them apart.
    public func decodeOrThrow(_ bytes: Data, mimeHint: String? = nil) throws -> PixelBuffer {
        let s = [UInt8](bytes.prefix(8))
        if ImageCodecs.looksPng(s) { return try ImageCodecs.decodePng(bytes) }
        if ImageCodecs.looksBmp(s) { return try ImageCodecs.decodeBmp(bytes) }
        // Named rather than lumped in with "unrecognised": a JPEG IS a picture,
        // and the caller needs to know it must wire a platform decoder, not that
        // the file is broken.
        if ImageCodecs.looksJpeg(s) { throw ImageCodecError.jpegNeedsAPlatformDecoder }
        throw ImageCodecError.unrecognisedFormat
    }
}

// MARK: - The HTML seam

/// Captures an HTML scene into frames.
///
/// A seam because rich typography and emoji need a real text engine, and every
/// platform already has one behind a web view. The bitmap font in this package
/// renders a headline; it does not render Devanagari with an emoji in it.
public protocol IHtmlFrameProvider: Sendable {
    var backendId: String { get }
    func renderHtmlFrames(_ html: HtmlTemplateSource, size: RenderSize,
                          frameCount: Int, frameRate: Int) async throws -> [PixelBuffer]
}

// MARK: - Fail-closed defaults
//
// Absence of a real backend yields a deterministic empty result, never a crash.

public struct NullHtmlFrameProvider: IHtmlFrameProvider, Sendable {
    public static let instance = NullHtmlFrameProvider()
    public init() {}
    public var backendId: String { "null" }

    public func renderHtmlFrames(_ html: HtmlTemplateSource, size: RenderSize,
                                 frameCount: Int, frameRate: Int) async throws -> [PixelBuffer] {
        []
    }
}

/// The HONEST GAP MARKER for true video: it advertises "video/mp4" and emits
/// zero bytes.
///
/// A real MP4/H.264 clip needs a genuine encoder, which is not feasible in pure
/// managed code on a low-end phone — the on-device, de-Googled path is AOSP
/// MediaCodec (or FFmpeg) wired in from the hosting layer. For a real clip that
/// this package CAN produce, use the APNG encoder instead.
public struct NullVideoEncoder: IVideoEncoder, Sendable {
    public static let instance = NullVideoEncoder()
    public init() {}

    public var backendId: String { "null" }
    public var outputMimeType: String { "video/mp4" }

    public func encode(frames: [PixelBuffer], options: ClipEncodeOptions) throws -> EncodedClip {
        // Frames are deliberately not consumed — no wasted compositing. The
        // INTENDED length is reported from the options, so a caller can still
        // see what it asked for rather than a clip of zero frames.
        EncodedClip(bytes: Data(), mimeType: "video/mp4", frameCount: options.frameCount,
                    size: options.size, frameRate: options.frameRate, backendId: "null")
    }
}

public struct NullMediaRenderer: IMediaRenderer, Sendable {
    public static let instance = NullMediaRenderer()
    public init() {}

    public var backendId: String { "null" }

    /// A 1x1 buffer, not nil: a caller compositing a poster wants something with
    /// a size it can reason about.
    public func renderStill(_ spec: MediaSpec, posterFraction: Double = 0.0) -> PixelBuffer? {
        PixelBuffer(width: 1, height: 1)
    }

    public func frames(_ spec: MediaSpec) -> [PixelBuffer] { [] }

    public func renderClip(_ spec: MediaSpec, encoder: any IVideoEncoder) throws -> EncodedClip {
        EncodedClip(bytes: Data(), mimeType: encoder.outputMimeType, frameCount: 0,
                    size: spec.size, frameRate: spec.frameRate, backendId: "null")
    }
}
