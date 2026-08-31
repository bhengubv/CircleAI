// MediaRaster.swift
//
// The pixels: a buffer, a 5x7 bitmap font, and a canvas that can clear, fill,
// draw a scaled image and lay out wrapped text.
//
// Ported from src/CircleAI.Media/Rendering.

import Foundation

// MARK: - The buffer

public enum PixelBufferError: Error, CustomStringConvertible, Equatable {
    case badDimensions(width: Int, height: Int)
    case wrongLength(expected: Int, got: Int)
    public var description: String {
        switch self {
        case .badDimensions(let w, let h): return "width and height must be positive (got \(w)x\(h))."
        case .wrongLength(let e, let g): return "pixels length must equal width*height*4 (\(e), got \(g))."
        }
    }
}

/// Straight RGBA8, row-major, no padding.
public final class PixelBuffer: @unchecked Sendable {
    public let width: Int
    public let height: Int
    public private(set) var pixels: [UInt8]

    public var stride: Int { width * 4 }

    public init?(width: Int, height: Int) {
        guard width > 0, height > 0 else { return nil }
        self.width = width
        self.height = height
        self.pixels = [UInt8](repeating: 0, count: width * height * 4)
    }

    public init?(width: Int, height: Int, pixels: [UInt8]) {
        guard width > 0, height > 0, pixels.count == width * height * 4 else { return nil }
        self.width = width
        self.height = height
        self.pixels = pixels
    }

    func index(_ x: Int, _ y: Int) -> Int { (y * width + x) * 4 }

    public func pixel(x: Int, y: Int) -> Rgba32? {
        guard x >= 0, y >= 0, x < width, y < height else { return nil }
        let i = index(x, y)
        return Rgba32(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3])
    }

    func write(_ i: Int, _ value: UInt8) { pixels[i] = value }
}

// MARK: - The font

/// A 5x7 pixel font with no external file. Lower case folds to upper - the
/// glyph table has one case, so a mixed-case caption still renders.
public final class BitmapFont: @unchecked Sendable {
    public static let cols = 5
    public static let rows = 7

    public static let `default` = BitmapFont()

    private let glyphs: [Character: [String]]

    private init() { glyphs = BitmapFont.build() }

    public func hasGlyph(_ c: Character) -> Bool { glyphs[BitmapFont.fold(c)] != nil }

    public func isPixelOn(_ c: Character, col: Int, row: Int) -> Bool {
        guard col >= 0, col < Self.cols, row >= 0, row < Self.rows else { return false }
        guard let g = glyphs[Self.fold(c)], row < g.count else { return false }
        let line = Array(g[row])
        return col < line.count && line[col] == "#"
    }

    static func fold(_ c: Character) -> Character {
        guard let a = c.asciiValue, a >= 97, a <= 122 else { return c }
        return Character(UnicodeScalar(a - 32))
    }

    private static func build() -> [Character: [String]] {
        // Each entry: 7 strings of exactly 5 characters. # is ink, . is paper.
        [
            "A": [".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
            "B": ["####.", "#...#", "#...#", "####.", "#...#", "#...#", "####."],
            "C": [".###.", "#...#", "#....", "#....", "#....", "#...#", ".###."],
            "D": ["####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####."],
            "E": ["#####", "#....", "#....", "####.", "#....", "#....", "#####"],
            "F": ["#####", "#....", "#....", "####.", "#....", "#....", "#...."],
            "G": [".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".###."],
            "H": ["#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
            "I": ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "#####"],
            "J": ["..###", "...#.", "...#.", "...#.", "#..#.", "#..#.", ".##.."],
            "K": ["#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#"],
            "L": ["#....", "#....", "#....", "#....", "#....", "#....", "#####"],
            "M": ["#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#"],
            "N": ["#...#", "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#"],
            "O": [".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
            "P": ["####.", "#...#", "#...#", "####.", "#....", "#....", "#...."],
            "Q": [".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#"],
            "R": ["####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#"],
            "S": [".####", "#....", "#....", ".###.", "....#", "....#", "####."],
            "T": ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."],
            "U": ["#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
            "V": ["#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#.."],
            "W": ["#...#", "#...#", "#...#", "#.#.#", "#.#.#", "##.##", "#...#"],
            "X": ["#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#"],
            "Y": ["#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.."],
            "Z": ["#####", "....#", "...#.", "..#..", ".#...", "#....", "#####"],
            "0": [".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###."],
            "1": ["..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###."],
            "2": [".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"],
            "3": ["#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###."],
            "4": ["...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."],
            "5": ["#####", "#....", "####.", "....#", "....#", "#...#", ".###."],
            "6": [".###.", "#....", "#....", "####.", "#...#", "#...#", ".###."],
            "7": ["#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..."],
            "8": [".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###."],
            "9": [".###.", "#...#", "#...#", ".####", "....#", "....#", ".###."],
            ".": [".....", ".....", ".....", ".....", ".....", ".##..", ".##.."],
            ",": [".....", ".....", ".....", ".....", ".##..", ".##..", ".#..."],
            "!": ["..#..", "..#..", "..#..", "..#..", "..#..", ".....", "..#.."],
            "?": [".###.", "#...#", "....#", "...#.", "..#..", ".....", "..#.."],
            "\u{27}": ["..#..", "..#..", "..#..", ".....", ".....", ".....", "....."],
            "\u{22}": [".#.#.", ".#.#.", ".#.#.", ".....", ".....", ".....", "....."],
            "-": [".....", ".....", ".....", "#####", ".....", ".....", "....."],
            "+": [".....", "..#..", "..#..", "#####", "..#..", "..#..", "....."],
            ":": [".....", ".##..", ".##..", ".....", ".##..", ".##..", "....."],
            ";": [".....", ".##..", ".##..", ".....", ".##..", ".##..", ".#..."],
            "/": ["....#", "....#", "...#.", "..#..", ".#...", "#....", "#...."],
            "(": ["..##.", ".#...", ".#...", ".#...", ".#...", ".#...", "..##."],
            ")": [".##..", "...#.", "...#.", "...#.", "...#.", "...#.", ".##.."],
            "&": [".##..", "#..#.", "#.#..", ".#...", "#.#.#", "#..#.", ".##.#"],
            "%": ["##..#", "##.#.", "..#..", ".#...", "#..##", "..#.#", "..#.#"],
            "#": [".#.#.", ".#.#.", "#####", ".#.#.", "#####", ".#.#.", ".#.#."],
            "@": [".###.", "#...#", "#.###", "#.#.#", "#.###", "#....", ".###."],
        ]
    }
}

// MARK: - The canvas

/// Source-over compositing onto an RGBA8 buffer.
public final class RasterCanvas: @unchecked Sendable {
    public let buffer: PixelBuffer
    public var width: Int { buffer.width }
    public var height: Int { buffer.height }

    public init(buffer: PixelBuffer) { self.buffer = buffer }

    public convenience init?(width: Int, height: Int) {
        guard let b = PixelBuffer(width: width, height: height) else { return nil }
        self.init(buffer: b)
    }

    public func clear(_ c: Rgba32) {
        var i = 0
        while i < buffer.pixels.count {
            buffer.write(i, c.r)
            buffer.write(i + 1, c.g)
            buffer.write(i + 2, c.b)
            buffer.write(i + 3, c.a)
            i += 4
        }
    }

    public func fillRect(x0: Int, y0: Int, w: Int, h: Int, color c: Rgba32, opacity: Double = 1.0) {
        let a = (Double(c.a) / 255.0) * opacity
        if a <= 0 { return }
        let xs = max(0, x0), ys = max(0, y0)
        let xe = min(width, x0 + w), ye = min(height, y0 + h)
        guard xe > xs, ye > ys else { return }
        for y in ys..<ye {
            for x in xs..<xe { blend(x, y, Int(c.r), Int(c.g), Int(c.b), a) }
        }
    }

    /// Draws `src` into the destination rectangle under the given fit.
    ///
    /// Contain fits the WHOLE image inside and leaves bars; cover fills the
    /// rectangle and crops. Both centre what is left over, which is why the
    /// offsets are halved rather than zeroed.
    public func drawImage(_ src: PixelBuffer, destX: Double, destY: Double,
                          destW: Double, destH: Double, fit: ContentFit, opacity: Double = 1.0) {
        guard src.width > 0, src.height > 0, destW > 0, destH > 0, opacity > 0 else { return }

        var pw = destW, ph = destH, ox = destX, oy = destY
        switch fit {
        case .fill:
            break
        case .contain:
            let s = min(destW / Double(src.width), destH / Double(src.height))
            pw = Double(src.width) * s; ph = Double(src.height) * s
            ox = destX + (destW - pw) / 2.0; oy = destY + (destH - ph) / 2.0
        case .cover:
            let s = max(destW / Double(src.width), destH / Double(src.height))
            pw = Double(src.width) * s; ph = Double(src.height) * s
            ox = destX + (destW - pw) / 2.0; oy = destY + (destH - ph) / 2.0
        }

        // The CLIP is the destination rectangle, not the placed image, so a
        // cover fit crops instead of spilling over its neighbours.
        let cx0 = max(0, Int(destX.rounded(.down)))
        let cy0 = max(0, Int(destY.rounded(.down)))
        let cx1 = min(width, Int((destX + destW).rounded(.up)))
        let cy1 = min(height, Int((destY + destH).rounded(.up)))
        guard cx1 > cx0, cy1 > cy0 else { return }

        for y in cy0..<cy1 {
            let v = ((Double(y) + 0.5) - oy) / ph * Double(src.height)
            if v < 0 || v > Double(src.height) { continue }
            for x in cx0..<cx1 {
                let u = ((Double(x) + 0.5) - ox) / pw * Double(src.width)
                if u < 0 || u > Double(src.width) { continue }
                let s = Self.sample(src, u - 0.5, v - 0.5)
                if s.a <= 0 { continue }
                blend(x, y, s.r, s.g, s.b, (Double(s.a) / 255.0) * opacity)
            }
        }
    }

    // ── Blending and sampling ─────────────────────────────────────────────

    /// Source-over onto a possibly-transparent destination. The result is
    /// UNPREMULTIPLIED, which is why each channel is divided by the output
    /// alpha - skipping that step darkens everything drawn over transparency.
    func blend(_ x: Int, _ y: Int, _ r: Int, _ g: Int, _ b: Int, _ alphaIn: Double) {
        if alphaIn <= 0.0 { return }
        guard x >= 0, y >= 0, x < width, y < height else { return }
        let alpha = min(1.0, alphaIn)

        let idx = (y * width + x) * 4
        let da = Double(buffer.pixels[idx + 3]) / 255.0
        let outA = alpha + da * (1.0 - alpha)
        if outA <= 0.0 {
            for k in 0..<4 { buffer.write(idx + k, 0) }
            return
        }
        let inv = da * (1.0 - alpha)
        buffer.write(idx, Self.clamp255((Double(r) * alpha + Double(buffer.pixels[idx]) * inv) / outA))
        buffer.write(idx + 1, Self.clamp255((Double(g) * alpha + Double(buffer.pixels[idx + 1]) * inv) / outA))
        buffer.write(idx + 2, Self.clamp255((Double(b) * alpha + Double(buffer.pixels[idx + 2]) * inv) / outA))
        buffer.write(idx + 3, Self.clamp255(outA * 255.0))
    }

    static func clamp255(_ v: Double) -> UInt8 {
        if v <= 0 { return 0 }
        if v >= 255 { return 255 }
        return UInt8(v.rounded())
    }

    /// Bilinear, clamped at the edges so a sample never wraps to the far side.
    static func sample(_ src: PixelBuffer, _ fxIn: Double, _ fyIn: Double)
        -> (r: Int, g: Int, b: Int, a: Int) {
        let maxX = Double(src.width - 1), maxY = Double(src.height - 1)
        let fx = min(max(fxIn, 0), maxX)
        let fy = min(max(fyIn, 0), maxY)
        let x0 = Int(fx), y0 = Int(fy)
        let x1 = Double(x0) < maxX ? x0 + 1 : x0
        let y1 = Double(y0) < maxY ? y0 + 1 : y0
        let tx = fx - Double(x0), ty = fy - Double(y0)

        let p = src.pixels, w = src.width
        let i00 = (y0 * w + x0) * 4, i10 = (y0 * w + x1) * 4
        let i01 = (y1 * w + x0) * 4, i11 = (y1 * w + x1) * 4

        func bi(_ o: Int) -> Int {
            Self.bilinear(p[i00 + o], p[i10 + o], p[i01 + o], p[i11 + o], tx, ty)
        }
        return (bi(0), bi(1), bi(2), bi(3))
    }

    static func bilinear(_ c00: UInt8, _ c10: UInt8, _ c01: UInt8, _ c11: UInt8,
                         _ tx: Double, _ ty: Double) -> Int {
        let top = Double(c00) + (Double(c10) - Double(c00)) * tx
        let bottom = Double(c01) + (Double(c11) - Double(c01)) * tx
        let v = top + (bottom - top) * ty
        return Int(min(255, max(0, v.rounded())))
    }
}

// MARK: - Text

public extension RasterCanvas {

    /// Lays out wrapped, aligned text inside a rectangle and draws it.
    ///
    /// The glyph scale is an INTEGER multiple of the 5x7 cell. A fractional
    /// scale would need antialiasing to look like anything, and this pipeline
    /// has none - blocky and crisp beats blurry and grey.
    func drawText(font: BitmapFont, text: String,
                  rx: Int, ry: Int, rw: Int, rh: Int,
                  pixelHeight: Int, color: Rgba32, align: TextAlign,
                  box: Rgba32, letterSpacingFraction: Double, lineSpacingFraction: Double,
                  opacity: Double = 1.0) {
        guard !text.isEmpty, rw > 0, rh > 0, opacity > 0 else { return }

        let scale = max(1, Int((Double(pixelHeight) / Double(BitmapFont.rows)).rounded(.toNearestOrAwayFromZero)))
        let glyphW = BitmapFont.cols * scale
        let glyphH = BitmapFont.rows * scale
        let letter = max(scale, Int((Double(glyphW) * letterSpacingFraction).rounded()))
        let advance = glyphW + letter
        let lineH = glyphH + max(scale, Int((Double(glyphH) * lineSpacingFraction).rounded()))

        let lines = Self.wrap(text, maxWidth: rw, advance: advance, glyphW: glyphW)
        guard !lines.isEmpty else { return }

        // The LAST line contributes only its glyph height, not a full line box,
        // so a block of text is vertically centred on its ink rather than on a
        // trailing gap.
        let totalH = lines.count * lineH - (lineH - glyphH)
        let startY = ry + max(0, (rh - totalH) / 2)

        if box.a > 0 {
            var maxW = 0
            for ln in lines { maxW = max(maxW, Self.lineWidth(ln.count, advance: advance, glyphW: glyphW)) }
            if maxW > 0 {
                let pad = max(scale * 2, glyphW / 2)
                let bx: Int
                switch align {
                case .left: bx = rx
                case .right: bx = rx + rw - maxW
                case .center: bx = rx + (rw - maxW) / 2
                }
                fillRect(x0: bx - pad, y0: startY - pad,
                         w: maxW + pad * 2, h: totalH + pad * 2, color: box, opacity: opacity)
            }
        }

        let inkA = (Double(color.a) / 255.0) * opacity
        var y0 = startY
        for line in lines {
            let lineW = Self.lineWidth(line.count, advance: advance, glyphW: glyphW)
            let x0: Int
            switch align {
            case .left: x0 = rx
            case .right: x0 = rx + rw - lineW
            case .center: x0 = rx + (rw - lineW) / 2
            }
            var cx = x0
            for ch in line where true {
                if ch != " " {
                    for gy in 0..<BitmapFont.rows {
                        for gx in 0..<BitmapFont.cols where font.isPixelOn(ch, col: gx, row: gy) {
                            fillBlock(x0: cx + gx * scale, y0: y0 + gy * scale,
                                      size: scale, color: color, alpha: inkA)
                        }
                    }
                }
                cx += advance
            }
            y0 += lineH
        }
    }

    /// The trailing letter-space of the last glyph is NOT part of the line, so
    /// centred text sits centred rather than a space to the left.
    static func lineWidth(_ charCount: Int, advance: Int, glyphW: Int) -> Int {
        charCount <= 0 ? 0 : charCount * advance - (advance - glyphW)
    }

    /// Greedy word wrap. Explicit newlines start a new line; a single word
    /// longer than the box is NOT broken - it overflows, which is visible, and
    /// better than silently losing characters.
    static func wrap(_ text: String, maxWidth: Int, advance: Int, glyphW: Int) -> [String] {
        var result: [String] = []
        let paragraphs = text.replacingOccurrences(of: "\r", with: "").components(separatedBy: "\n")

        for paragraph in paragraphs {
            let words = paragraph.split(separator: " ", omittingEmptySubsequences: true).map(String.init)
            if words.isEmpty { result.append(""); continue }

            var cur = ""
            for word in words {
                let candidate = cur.isEmpty ? word.count : cur.count + 1 + word.count
                if !cur.isEmpty && lineWidth(candidate, advance: advance, glyphW: glyphW) > maxWidth {
                    result.append(cur)
                    cur = word
                } else {
                    if !cur.isEmpty { cur += " " }
                    cur += word
                }
            }
            result.append(cur)
        }
        return result
    }

    private func fillBlock(x0: Int, y0: Int, size: Int, color c: Rgba32, alpha: Double) {
        for y in y0..<(y0 + size) {
            for x in x0..<(x0 + size) { blend(x, y, Int(c.r), Int(c.g), Int(c.b), alpha) }
        }
    }
}
