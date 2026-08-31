// MediaRendering.swift
//
// The managed rendering pipeline: colours, layout, motion and the spec that
// describes one still or one clip.
//
// Ported from src/CircleAI.Media/Rendering.

import Foundation

// MARK: - Colour

public enum ColourError: Error, CustomStringConvertible, Equatable {
    case unrecognised(String)
    case invalidHexDigit(Character)
    public var description: String {
        switch self {
        case .unrecognised(let hex):
            return "Unrecognised colour \(hex). Use #RGB, #RRGGBB or #RRGGBBAA."
        case .invalidHexDigit(let c):
            return "Invalid hex digit \(c)."
        }
    }
}

public struct Rgba32: Sendable, Equatable, Hashable {
    public let r: UInt8
    public let g: UInt8
    public let b: UInt8
    public let a: UInt8

    public init(_ r: UInt8, _ g: UInt8, _ b: UInt8, _ a: UInt8) {
        self.r = r; self.g = g; self.b = b; self.a = a
    }

    public static let transparent = Rgba32(0, 0, 0, 0)
    public static let black = Rgba32(0, 0, 0, 255)
    public static let white = Rgba32(255, 255, 255, 255)

    public static func rgb(_ r: UInt8, _ g: UInt8, _ b: UInt8) -> Rgba32 { Rgba32(r, g, b, 255) }

    public func withAlpha(_ a: UInt8) -> Rgba32 { Rgba32(r, g, b, a) }

    /// #RGB, #RRGGBB or #RRGGBBAA, with or without the hash.
    ///
    /// Three-digit form DUPLICATES each nibble, so #f00 is ff0000, not f00000 -
    /// halving it instead would make every short colour darker than written.
    public static func hex(_ hex: String) throws -> Rgba32 {
        let s = hex.hasPrefix("#") ? String(hex.dropFirst()) : hex
        let chars = Array(s)

        func nib(_ c: Character) throws -> UInt8 {
            switch c {
            case "0"..."9": return UInt8(c.asciiValue! - 48)
            case "a"..."f": return UInt8(c.asciiValue! - 97 + 10)
            case "A"..."F": return UInt8(c.asciiValue! - 65 + 10)
            default: throw ColourError.invalidHexDigit(c)
            }
        }
        func dup(_ c: Character) throws -> UInt8 { let v = try nib(c); return (v << 4) | v }
        func hex2(_ i: Int) throws -> UInt8 { try (nib(chars[i]) << 4) | nib(chars[i + 1]) }

        switch chars.count {
        case 3: return try Rgba32(dup(chars[0]), dup(chars[1]), dup(chars[2]), 255)
        case 6: return try Rgba32(hex2(0), hex2(2), hex2(4), 255)
        case 8: return try Rgba32(hex2(0), hex2(2), hex2(4), hex2(6))
        default: throw ColourError.unrecognised(hex)
        }
    }
}

// MARK: - Geometry

public struct RenderSize: Sendable, Equatable, Hashable {
    public let width: Int
    public let height: Int
    public init(width: Int, height: Int) {
        self.width = width
        self.height = height
    }

    public static let square1080 = RenderSize(width: 1080, height: 1080)
    public static let portrait1080x1920 = RenderSize(width: 1080, height: 1920)
    public static let landscape1920x1080 = RenderSize(width: 1920, height: 1080)
    public static let preview540x960 = RenderSize(width: 540, height: 960)

    public var pixelCount: Int64 { Int64(width) * Int64(height) }
}

/// A rectangle in 0...1 of the canvas, so a spec is resolution-independent.
public struct NormRect: Sendable, Equatable {
    public let x: Double
    public let y: Double
    public let w: Double
    public let h: Double
    public init(x: Double, y: Double, w: Double, h: Double) {
        self.x = x; self.y = y; self.w = w; self.h = h
    }
    public static let full = NormRect(x: 0, y: 0, w: 1, h: 1)
}

public struct NormVec: Sendable, Equatable {
    public let x: Double
    public let y: Double
    public init(x: Double = 0, y: Double = 0) {
        self.x = x
        self.y = y
    }
    public static let zero = NormVec()
}

public enum ContentFit: Int, Sendable, Equatable {
    case fill = 0
    case contain
    case cover
}

public enum TextAlign: Int, Sendable, Equatable {
    case left = 0
    case center
    case right
}

public enum EasingKind: Int, Sendable, Equatable {
    case linear = 0
    case easeIn
    case easeOut
    case easeInOut
}

// MARK: - Motion

/// How one layer moves across the clip. Fractions are of the whole clip, so a
/// spec does not care how many frames it ends up being rendered at.
public struct Motion: Sendable, Equatable {
    public let startFraction: Double
    public let endFraction: Double
    public let fromOpacity: Double
    public let toOpacity: Double
    public let fromScale: Double
    public let toScale: Double
    public let fromTranslate: NormVec
    public let toTranslate: NormVec
    public let easing: EasingKind

    public init(startFraction: Double = 0.0, endFraction: Double = 1.0,
                fromOpacity: Double = 1.0, toOpacity: Double = 1.0,
                fromScale: Double = 1.0, toScale: Double = 1.0,
                fromTranslate: NormVec = .zero, toTranslate: NormVec = .zero,
                easing: EasingKind = .linear) {
        self.startFraction = startFraction
        self.endFraction = endFraction
        self.fromOpacity = fromOpacity
        self.toOpacity = toOpacity
        self.fromScale = fromScale
        self.toScale = toScale
        self.fromTranslate = fromTranslate
        self.toTranslate = toTranslate
        self.easing = easing
    }

    public static let none = Motion()
    public static let fadeIn = Motion(startFraction: 0.0, endFraction: 0.25,
                                      fromOpacity: 0.0, toOpacity: 1.0, easing: .easeOut)
    public static let fadeOut = Motion(startFraction: 0.75, endFraction: 1.0,
                                       fromOpacity: 1.0, toOpacity: 0.0, easing: .easeIn)
    public static let kenBurns = Motion(fromScale: 1.0, toScale: 1.12,
                                        toTranslate: NormVec(x: 0.03, y: 0.02), easing: .easeInOut)
}

public enum Easing {
    public static func lerp(_ a: Double, _ b: Double, _ t: Double) -> Double { a + (b - a) * t }

    public static func apply(_ kind: EasingKind, _ t: Double) -> Double {
        switch kind {
        case .easeIn: return t * t
        case .easeOut: return 1.0 - (1.0 - t) * (1.0 - t)
        case .easeInOut: return t * t * (3.0 - 2.0 * t)   // smoothstep
        case .linear: return t
        }
    }

    /// Where a motion is at global progress `g`. A zero-length window snaps at
    /// its end rather than dividing by zero.
    public static func evaluate(_ m: Motion?, at g: Double)
        -> (opacity: Double, scale: Double, translate: NormVec) {
        guard let m else { return (1.0, 1.0, .zero) }
        let span = m.endFraction - m.startFraction
        let local = span <= 0.0
            ? (g >= m.endFraction ? 1.0 : 0.0)
            : max(0.0, min(1.0, (g - m.startFraction) / span))
        let e = apply(m.easing, local)
        return (lerp(m.fromOpacity, m.toOpacity, e),
                lerp(m.fromScale, m.toScale, e),
                NormVec(x: lerp(m.fromTranslate.x, m.toTranslate.x, e),
                        y: lerp(m.fromTranslate.y, m.toTranslate.y, e)))
    }
}

// MARK: - The spec

public enum ImageSource: Sendable, Equatable {
    case raw(rgba: [UInt8], width: Int, height: Int)
    case encoded(bytes: Data, mimeHint: String?)
}

public struct ImageLayer: Sendable, Equatable {
    public let source: ImageSource
    public let rect: NormRect
    public let fit: ContentFit
    public let opacity: Double
    public let motion: Motion?
    public let zOrder: Int
    public let id: String?

    public init(source: ImageSource, rect: NormRect, fit: ContentFit = .cover,
                opacity: Double = 1.0, motion: Motion? = nil, zOrder: Int = 0, id: String? = nil) {
        self.source = source
        self.rect = rect
        self.fit = fit
        self.opacity = opacity
        self.motion = motion
        self.zOrder = zOrder
        self.id = id
    }
}

public struct TextOverlay: Sendable, Equatable {
    public let text: String
    public let rect: NormRect
    public let fontHeightFraction: Double
    public let color: Rgba32
    public let align: TextAlign
    public let boxColor: Rgba32
    public let letterSpacingFraction: Double
    public let lineSpacingFraction: Double
    public let motion: Motion?
    public let zOrder: Int
    public let id: String?

    public init(text: String, rect: NormRect, fontHeightFraction: Double = 0.08,
                color: Rgba32 = .transparent, align: TextAlign = .center,
                boxColor: Rgba32 = .transparent, letterSpacingFraction: Double = 0.2,
                lineSpacingFraction: Double = 0.35, motion: Motion? = nil,
                zOrder: Int = 100, id: String? = nil) {
        self.text = text
        self.rect = rect
        self.fontHeightFraction = fontHeightFraction
        self.color = color
        self.align = align
        self.boxColor = boxColor
        self.letterSpacingFraction = letterSpacingFraction
        self.lineSpacingFraction = lineSpacingFraction
        self.motion = motion
        self.zOrder = zOrder
        self.id = id
    }
}

public struct HtmlTemplateSource: Sendable, Equatable {
    public let html: String
    public let tokens: [String: String]?
    public init(html: String, tokens: [String: String]? = nil) {
        self.html = html
        self.tokens = tokens
    }
}

public struct MediaSpec: Sendable, Equatable {
    public let size: RenderSize
    public let background: Rgba32
    public let images: [ImageLayer]
    public let texts: [TextOverlay]
    /// Zero or less means a still.
    public let duration: TimeInterval
    public let frameRate: Int
    public let html: HtmlTemplateSource?

    public init(size: RenderSize, background: Rgba32, images: [ImageLayer] = [],
                texts: [TextOverlay] = [], duration: TimeInterval = 0,
                frameRate: Int = 12, html: HtmlTemplateSource? = nil) {
        self.size = size
        self.background = background
        self.images = images
        self.texts = texts
        self.duration = duration
        self.frameRate = frameRate
        self.html = html
    }

    public var isStill: Bool { duration <= 0 }

    /// At least one frame, always. A 0.01s clip is still a frame, not nothing.
    public var frameCount: Int {
        isStill ? 1 : max(1, Int((duration * Double(max(1, frameRate))).rounded(.toNearestOrAwayFromZero)))
    }

    public static func still(size: RenderSize, background: Rgba32,
                             images: [ImageLayer] = [], texts: [TextOverlay] = []) -> MediaSpec {
        MediaSpec(size: size, background: background, images: images, texts: texts,
                  duration: 0, frameRate: 1)
    }

    /// Replaces {{key}} with its value. A key with no token is left alone
    /// rather than blanked - a half-substituted template is easier to diagnose
    /// than one with holes in it.
    public static func applyTokens(_ template: String, _ tokens: [String: String]?) -> String {
        guard let tokens, !tokens.isEmpty else { return template }
        var out = template
        for (k, v) in tokens {
            out = out.replacingOccurrences(of: "{{\(k)}}", with: v)
        }
        return out
    }
}

// MARK: - The renderer

/// NAMING: VisionOnnx already owns IImageDecoder, which decodes to packed
/// RGB24 for a model. This one decodes to an RGBA PixelBuffer for compositing -
/// a different contract, so it carries a different name.
public protocol IMediaImageDecoder: Sendable {
    var backendId: String { get }
    func decode(_ bytes: Data, mimeHint: String?) -> PixelBuffer?
}

/// No decoder is wired. Encoded layers are skipped rather than drawn as
/// garbage - a raw layer still renders, so a spec built from pixels works on
/// any build.
public struct NullMediaImageDecoder: IMediaImageDecoder {
    public static let instance = NullMediaImageDecoder()
    public init() {}
    public var backendId: String { "null" }
    public func decode(_ bytes: Data, mimeHint: String?) -> PixelBuffer? { nil }
}

public protocol IMediaRenderer: Sendable {
    var backendId: String { get }
    func renderStill(_ spec: MediaSpec, posterFraction: Double) -> PixelBuffer?
    func frames(_ spec: MediaSpec) -> [PixelBuffer]
    func renderClip(_ spec: MediaSpec, encoder: any IVideoEncoder) throws -> EncodedClip
}

/// Composes a spec onto a raster canvas, frame by frame.
public struct ManagedMediaRenderer: IMediaRenderer {
    private let decoder: any IMediaImageDecoder
    private let font: BitmapFont

    public init(decoder: (any IMediaImageDecoder)? = nil, font: BitmapFont? = nil) {
        self.decoder = decoder ?? NullMediaImageDecoder.instance
        self.font = font ?? BitmapFont.default
    }

    public var backendId: String { "managed" }

    public func renderStill(_ spec: MediaSpec, posterFraction: Double = 0.0) -> PixelBuffer? {
        compose(spec, at: min(1.0, max(0.0, posterFraction)), layers: decodeLayers(spec))
    }

    /// The first frame is at progress 0 and the last at 1, so a fade-in starts
    /// fully transparent and a fade-out ends fully gone.
    public func frames(_ spec: MediaSpec) -> [PixelBuffer] {
        let n = spec.frameCount
        let decoded = decodeLayers(spec)
        return (0..<n).compactMap { i in
            let g = n <= 1 ? 0.0 : Double(i) / Double(n - 1)
            return compose(spec, at: g, layers: decoded)
        }
    }

    public func renderClip(_ spec: MediaSpec, encoder: any IVideoEncoder) throws -> EncodedClip {
        let options = ClipEncodeOptions(size: spec.size, frameRate: max(1, spec.frameRate),
                                        frameCount: spec.frameCount)
        return try encoder.encode(frames: frames(spec), options: options)
    }

    private func decodeLayers(_ spec: MediaSpec) -> [(layer: ImageLayer, pixels: PixelBuffer)] {
        spec.images.compactMap { layer in
            let px: PixelBuffer?
            switch layer.source {
            case .raw(let rgba, let w, let h):
                px = PixelBuffer(width: w, height: h, pixels: rgba)
            case .encoded(let bytes, let hint):
                px = decoder.decode(bytes, mimeHint: hint)
            }
            return px.map { (layer, $0) }
        }
    }

    private func compose(_ spec: MediaSpec, at g: Double,
                         layers: [(layer: ImageLayer, pixels: PixelBuffer)]) -> PixelBuffer? {
        guard let canvas = RasterCanvas(width: spec.size.width, height: spec.size.height) else {
            return nil
        }
        canvas.clear(spec.background)

        // A STABLE sort by z-order: two layers at the same depth keep the order
        // the caller listed them in, so a spec renders the same way twice.
        for (layer, pixels) in layers.enumerated()
            .sorted(by: { ($0.element.layer.zOrder, $0.offset) < ($1.element.layer.zOrder, $1.offset) })
            .map(\.element) {
            let m = Easing.evaluate(layer.motion, at: g)
            let opacity = layer.opacity * m.opacity
            if opacity <= 0 { continue }
            let r = Self.placeRect(layer.rect, spec.size, scale: m.scale, translate: m.translate)
            canvas.drawImage(pixels, destX: r.x, destY: r.y, destW: r.w, destH: r.h,
                             fit: layer.fit, opacity: opacity)
        }

        for overlay in spec.texts.enumerated()
            .sorted(by: { ($0.element.zOrder, $0.offset) < ($1.element.zOrder, $1.offset) })
            .map(\.element) {
            if overlay.text.isEmpty { continue }
            let m = Easing.evaluate(overlay.motion, at: g)
            if m.opacity <= 0 { continue }
            let r = Self.placeRect(overlay.rect, spec.size, scale: 1.0, translate: m.translate)

            // A fully transparent colour means "not set", so it becomes white
            // rather than invisible ink.
            let color = overlay.color.a == 0 ? Rgba32.white : overlay.color
            let fontPx = max(BitmapFont.rows,
                             Int((overlay.fontHeightFraction * Double(spec.size.height)).rounded()))

            canvas.drawText(font: font, text: overlay.text,
                            rx: Int(r.x.rounded()), ry: Int(r.y.rounded()),
                            rw: Int(r.w.rounded()), rh: Int(r.h.rounded()),
                            pixelHeight: fontPx, color: color, align: overlay.align,
                            box: overlay.boxColor,
                            letterSpacingFraction: overlay.letterSpacingFraction,
                            lineSpacingFraction: overlay.lineSpacingFraction,
                            opacity: m.opacity)
        }
        return canvas.buffer
    }

    /// Scale is applied about the rectangle CENTRE, not its origin, so a
    /// Ken Burns zoom pushes out evenly instead of sliding down and right.
    static func placeRect(_ rect: NormRect, _ size: RenderSize,
                          scale: Double, translate: NormVec)
        -> (x: Double, y: Double, w: Double, h: Double) {
        var x = rect.x * Double(size.width)
        var y = rect.y * Double(size.height)
        var w = rect.w * Double(size.width)
        var h = rect.h * Double(size.height)

        let cx = x + w / 2.0, cy = y + h / 2.0
        w *= scale; h *= scale
        x = cx - w / 2.0; y = cy - h / 2.0

        x += translate.x * Double(size.width)
        y += translate.y * Double(size.height)
        return (x, y, w, h)
    }
}
