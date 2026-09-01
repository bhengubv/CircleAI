//! Rendering: a raster canvas, a bitmap font, and PNG in both directions.
//!
//! WHY THIS IS HAND-WRITTEN. Every image library worth having is either GPL, a
//! native blob per architecture, or both, and an app that must ship as ONE APK
//! cannot carry four architectures of a decoder to draw a chart.
//!
//! WHAT IS DIFFERENT IN RUST: no `zlib` dependency is assumed, because adding
//! one adds a C build to every target this crate has to reach. So the encoder
//! emits DEFLATE STORED BLOCKS - valid deflate, no compression - which every
//! PNG decoder accepts. The file is larger, and that is the honest trade: a
//! correct PNG everywhere beats a smaller one that only builds on some targets.
//! A host with a compressor passes one in and gets the smaller file.
//!
//! THE THREE THAT ARE ALWAYS GOT WRONG, and are not here:
//!
//!   * Compositing in STRAIGHT alpha needs the divide by the output alpha.
//!     Without it every soft edge drawn onto transparency gets a dark rim.
//!
//!   * PNG is big-endian in its framing and DEFLATE is little-endian inside it,
//!     and the Huffman codes inside THAT are MSB-first. Three byte orders in one
//!     file.
//!
//!   * The Paeth tie-break is ordered - left, then above, then upper-left.
//!     Reversing it decodes most images correctly and a few with coloured
//!     streaks, which is the worst way for a bug to behave.

use std::collections::HashMap;

// ─────────────────────────────────────────────────────────────────────────────
// Geometry and colour

/// A colour, STRAIGHT (not premultiplied).
///
/// Straight because it is what an author types and what a PNG stores;
/// premultiplying at the edge of the compositor and dividing back out is where
/// the rounding lives, and it lives there once.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Rgba32 {
    pub r: u8,
    pub g: u8,
    pub b: u8,
    pub a: u8,
}

impl Rgba32 {
    pub const TRANSPARENT: Self = Self { r: 0, g: 0, b: 0, a: 0 };
    pub const BLACK: Self = Self { r: 0, g: 0, b: 0, a: 255 };
    pub const WHITE: Self = Self { r: 255, g: 255, b: 255, a: 255 };

    pub const fn new(r: u8, g: u8, b: u8, a: u8) -> Self {
        Self { r, g, b, a }
    }

    /// Accepts `#rgb`, `#rgba`, `#rrggbb` and `#rrggbbaa`.
    ///
    /// Alpha LAST, matching CSS. A reader that assumes `#aarrggbb` gets a fully
    /// opaque colour with the wrong red, which looks like a palette mistake
    /// rather than a parsing one.
    pub fn from_hex(text: &str) -> Option<Self> {
        let s = text.trim().trim_start_matches('#');
        let expanded: String = match s.len() {
            3 | 4 => s.chars().flat_map(|c| [c, c]).collect(),
            6 | 8 => s.to_string(),
            _ => return None,
        };
        let full = if expanded.len() == 6 {
            format!("{expanded}ff")
        } else {
            expanded
        };
        let byte = |i: usize| u8::from_str_radix(&full[i..i + 2], 16).ok();
        Some(Self::new(byte(0)?, byte(2)?, byte(4)?, byte(6)?))
    }

    pub fn to_hex(self) -> String {
        format!("#{:02x}{:02x}{:02x}{:02x}", self.r, self.g, self.b, self.a)
    }

    pub fn with_alpha(self, a: u8) -> Self {
        Self { a, ..self }
    }
}

/// A point in 0..1 of the frame.
///
/// NORMALISED so a spec renders at any size. An overlay placed at pixel 240 is
/// centred on one phone and off the edge of another.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct NormVec {
    pub x: f32,
    pub y: f32,
}

/// A rectangle in 0..1 of the frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NormRect {
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
}

impl Default for NormRect {
    fn default() -> Self {
        Self { x: 0.0, y: 0.0, width: 1.0, height: 1.0 }
    }
}

impl NormRect {
    pub const FULL: Self = Self { x: 0.0, y: 0.0, width: 1.0, height: 1.0 };

    /// Rounds the EDGES, not the size.
    ///
    /// Rounding x and width separately lets two rectangles that share an edge
    /// end up a pixel apart, which shows as a hairline seam.
    pub fn scaled(&self, width: u32, height: u32) -> (i32, i32, i32, i32) {
        let x0 = (self.x * width as f32).round() as i32;
        let y0 = (self.y * height as f32).round() as i32;
        let x1 = ((self.x + self.width) * width as f32).round() as i32;
        let y1 = ((self.y + self.height) * height as f32).round() as i32;
        (x0, y0, x1 - x0, y1 - y0)
    }
}

/// Output pixel size.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RenderSize {
    pub width: u32,
    pub height: u32,
}

impl RenderSize {
    pub const SQUARE: Self = Self { width: 1080, height: 1080 };
    pub const STORY: Self = Self { width: 1080, height: 1920 };
    pub const LANDSCAPE: Self = Self { width: 1920, height: 1080 };

    pub fn new(width: u32, height: u32) -> Option<Self> {
        (width > 0 && height > 0).then_some(Self { width, height })
    }

    pub fn aspect(&self) -> f32 {
        self.width as f32 / self.height as f32
    }
}

impl Default for RenderSize {
    fn default() -> Self {
        Self::SQUARE
    }
}

/// How an image fills a box it does not match.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ContentFit {
    /// Whole image visible, box may show through. The safe default: nothing is
    /// lost, and what a caller did not intend is empty space rather than a
    /// cropped face.
    #[default]
    Contain,
    Cover,
    Stretch,
    None,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum TextAlign {
    #[default]
    Left,
    Center,
    Right,
}

/// How a motion interpolates.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum EasingKind {
    #[default]
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
}

/// CLAMPED, because a caller that computes `t` from a frame index off by one
/// would otherwise extrapolate - an ease-out past 1.0 overshoots and the layer
/// jumps back.
pub fn ease(kind: EasingKind, t: f32) -> f32 {
    let c = t.clamp(0.0, 1.0);
    match kind {
        EasingKind::Linear => c,
        EasingKind::EaseIn => c * c,
        EasingKind::EaseOut => 1.0 - (1.0 - c) * (1.0 - c),
        EasingKind::EaseInOut => {
            if c < 0.5 {
                2.0 * c * c
            } else {
                1.0 - 2.0 * (1.0 - c) * (1.0 - c)
            }
        }
    }
}

/// A layer moving between two rectangles over the clip.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Motion {
    pub start: NormRect,
    pub end: NormRect,
    pub easing: EasingKind,
}

impl Motion {
    pub fn at(&self, t: f32) -> NormRect {
        let e = ease(self.easing, t);
        NormRect {
            x: self.start.x + (self.end.x - self.start.x) * e,
            y: self.start.y + (self.end.y - self.start.y) * e,
            width: self.start.width + (self.end.width - self.start.width) * e,
            height: self.start.height + (self.end.height - self.start.height) * e,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Pixels

/// RGBA8888, top-down, no padding.
///
/// A flat `Vec<u8>` rather than a vector of structs: a 1080x1080 frame is 1.1
/// million pixels, and the flat buffer is what a decoder and an encoder both
/// want anyway.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PixelBuffer {
    pub width: u32,
    pub height: u32,
    pub data: Vec<u8>,
}

impl PixelBuffer {
    pub fn new(width: u32, height: u32, fill: Rgba32) -> Option<Self> {
        if width == 0 || height == 0 {
            return None;
        }
        let count = (width as usize) * (height as usize);
        let data = if fill.a == 0 && fill.r == 0 && fill.g == 0 && fill.b == 0 {
            vec![0u8; count * 4]
        } else {
            [fill.r, fill.g, fill.b, fill.a].repeat(count)
        };
        Some(Self { width, height, data })
    }

    fn index(&self, x: i32, y: i32) -> Option<usize> {
        if x < 0 || y < 0 || x as u32 >= self.width || y as u32 >= self.height {
            return None;
        }
        Some(((y as usize) * (self.width as usize) + x as usize) * 4)
    }

    pub fn get(&self, x: i32, y: i32) -> Rgba32 {
        match self.index(x, y) {
            Some(i) => Rgba32::new(self.data[i], self.data[i + 1], self.data[i + 2], self.data[i + 3]),
            None => Rgba32::TRANSPARENT,
        }
    }

    pub fn set(&mut self, x: i32, y: i32, c: Rgba32) {
        if let Some(i) = self.index(x, y) {
            self.data[i] = c.r;
            self.data[i + 1] = c.g;
            self.data[i + 2] = c.b;
            self.data[i + 3] = c.a;
        }
    }

    /// Source-over in STRAIGHT alpha.
    ///
    /// The divide by the output alpha is the whole point. Without it a
    /// half-transparent white drawn onto transparency comes out mid-grey
    /// instead of white-at-half-alpha, and every soft edge in the frame gets a
    /// dark rim.
    pub fn blend(&mut self, x: i32, y: i32, c: Rgba32) {
        if c.a == 0 {
            return;
        }
        if c.a == 255 {
            self.set(x, y, c);
            return;
        }
        let Some(i) = self.index(x, y) else { return };
        let sa = c.a as f32 / 255.0;
        let da = self.data[i + 3] as f32 / 255.0;
        let out_a = sa + da * (1.0 - sa);
        if out_a <= 0.0 {
            self.data[i..i + 4].fill(0);
            return;
        }
        let mix = |s: u8, d: u8| -> u8 {
            (((s as f32) * sa + (d as f32) * da * (1.0 - sa)) / out_a)
                .round()
                .clamp(0.0, 255.0) as u8
        };
        self.data[i] = mix(c.r, self.data[i]);
        self.data[i + 1] = mix(c.g, self.data[i + 1]);
        self.data[i + 2] = mix(c.b, self.data[i + 2]);
        self.data[i + 3] = (out_a * 255.0).round().clamp(0.0, 255.0) as u8;
    }
}

/// Something a layer draws.
pub trait ImageSource {
    fn decode(&self, decoder: &dyn ImageDecoder) -> Option<PixelBuffer>;
}

/// Pixels already in hand.
#[derive(Debug, Clone)]
pub struct RawImageSource {
    pub buffer: PixelBuffer,
}

impl ImageSource for RawImageSource {
    fn decode(&self, _decoder: &dyn ImageDecoder) -> Option<PixelBuffer> {
        Some(self.buffer.clone())
    }
}

/// Bytes of a PNG.
#[derive(Debug, Clone)]
pub struct EncodedImageSource {
    pub bytes: Vec<u8>,
    pub media_type: String,
}

impl ImageSource for EncodedImageSource {
    fn decode(&self, decoder: &dyn ImageDecoder) -> Option<PixelBuffer> {
        decoder.decode(&self.bytes)
    }
}

/// A layer rendered by a browser engine, when the host has one.
///
/// A SOURCE rather than a renderer, so a spec that names one still renders on a
/// host with no browser - the layer is skipped and the rest of the frame is
/// produced. A missing layer is a worse picture; a failed render is no picture.
pub struct HtmlTemplateSource {
    pub html: String,
    pub css: String,
    pub provider: Option<Box<dyn HtmlFrameProvider + Send + Sync>>,
}

impl ImageSource for HtmlTemplateSource {
    fn decode(&self, _decoder: &dyn ImageDecoder) -> Option<PixelBuffer> {
        self.provider.as_ref()?.render(&self.html, &self.css)
    }
}

/// One image placed in the frame.
pub struct ImageLayer {
    pub source: Box<dyn ImageSource + Send + Sync>,
    pub rect: NormRect,
    pub fit: ContentFit,
    pub opacity: f32,
    pub motion: Option<Motion>,
}

impl ImageLayer {
    pub fn rect_at(&self, t: f32) -> NormRect {
        match self.motion {
            Some(m) => m.at(t),
            None => self.rect,
        }
    }
}

/// Text placed in the frame.
#[derive(Debug, Clone, PartialEq)]
pub struct TextOverlay {
    pub text: String,
    pub at: NormVec,
    pub align: TextAlign,
    /// In FRACTIONS OF FRAME HEIGHT, not points. A point size renders the same
    /// caption legibly at 1080 and unreadably at 320.
    pub size: f32,
    pub colour: Rgba32,
    /// A backing plate. Not decoration: white text over an unknown photograph is
    /// unreadable about half the time.
    pub background: Option<Rgba32>,
    pub padding: f32,
}

/// A whole frame or clip, declaratively.
pub struct MediaSpec {
    pub size: RenderSize,
    pub background: Rgba32,
    pub layers: Vec<ImageLayer>,
    pub overlays: Vec<TextOverlay>,
    pub duration_seconds: f32,
    pub frames_per_second: u32,
}

impl MediaSpec {
    pub fn is_still(&self) -> bool {
        self.duration_seconds <= 0.0
    }

    pub fn frame_count(&self) -> usize {
        if self.is_still() {
            1
        } else {
            ((self.duration_seconds * self.frames_per_second as f32).round() as usize).max(1)
        }
    }
}

impl Default for MediaSpec {
    fn default() -> Self {
        Self {
            size: RenderSize::SQUARE,
            background: Rgba32::BLACK,
            layers: Vec::new(),
            overlays: Vec::new(),
            duration_seconds: 0.0,
            frames_per_second: 30,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The font

/// A 5x7 bitmap font, one entry per glyph, rows separated by `/`.
///
/// Hand-drawn and deliberately small: it exists to label a chart axis and stamp
/// a caption, and it is the only way to put a word on a picture without a font
/// file, a shaper and a licence to check.
const GLYPH_ART: &[(char, &str)] = &[
    (' ', "...../...../...../...../...../...../....."),
    ('0', ".###./#...#/#..##/#.#.#/##..#/#...#/.###."),
    ('1', "..#../.##../..#../..#../..#../..#../.###."),
    ('2', ".###./#...#/....#/...#./..#../.#.../#####"),
    ('3', "####./....#/....#/.###./....#/....#/####."),
    ('4', "...#./..##./.#.#./#..#./#####/...#./...#."),
    ('5', "#####/#..../####./....#/....#/#...#/.###."),
    ('6', ".###./#..../#..../####./#...#/#...#/.###."),
    ('7', "#####/....#/...#./..#../.#.../.#.../.#..."),
    ('8', ".###./#...#/#...#/.###./#...#/#...#/.###."),
    ('9', ".###./#...#/#...#/.####/....#/....#/.###."),
    ('A', ".###./#...#/#...#/#####/#...#/#...#/#...#"),
    ('B', "####./#...#/#...#/####./#...#/#...#/####."),
    ('C', ".###./#...#/#..../#..../#..../#...#/.###."),
    ('D', "####./#...#/#...#/#...#/#...#/#...#/####."),
    ('E', "#####/#..../#..../####./#..../#..../#####"),
    ('F', "#####/#..../#..../####./#..../#..../#...."),
    ('G', ".###./#...#/#..../#.###/#...#/#...#/.###."),
    ('H', "#...#/#...#/#...#/#####/#...#/#...#/#...#"),
    ('I', ".###./..#../..#../..#../..#../..#../.###."),
    ('J', "....#/....#/....#/....#/#...#/#...#/.###."),
    ('K', "#...#/#..#./#.#../##.../#.#../#..#./#...#"),
    ('L', "#..../#..../#..../#..../#..../#..../#####"),
    ('M', "#...#/##.##/#.#.#/#.#.#/#...#/#...#/#...#"),
    ('N', "#...#/##..#/#.#.#/#..##/#...#/#...#/#...#"),
    ('O', ".###./#...#/#...#/#...#/#...#/#...#/.###."),
    ('P', "####./#...#/#...#/####./#..../#..../#...."),
    ('Q', ".###./#...#/#...#/#...#/#.#.#/#..#./.##.#"),
    ('R', "####./#...#/#...#/####./#.#../#..#./#...#"),
    ('S', ".####/#..../#..../.###./....#/....#/####."),
    ('T', "#####/..#../..#../..#../..#../..#../..#.."),
    ('U', "#...#/#...#/#...#/#...#/#...#/#...#/.###."),
    ('V', "#...#/#...#/#...#/#...#/#...#/.#.#./..#.."),
    ('W', "#...#/#...#/#...#/#.#.#/#.#.#/##.##/#...#"),
    ('X', "#...#/#...#/.#.#./..#../.#.#./#...#/#...#"),
    ('Y', "#...#/#...#/.#.#./..#../..#../..#../..#.."),
    ('Z', "#####/....#/...#./..#../.#.../#..../#####"),
    ('.', "...../...../...../...../...../.##../.##.."),
    (',', "...../...../...../...../.##../.##../.#..."),
    ('-', "...../...../...../#####/...../...../....."),
    ('+', "...../..#../..#../#####/..#../..#../....."),
    ('%', "##..#/##..#/...#./..#../.#.../#..##/#..##"),
    (':', "...../.##../.##../...../.##../.##../....."),
    ('/', "....#/...#./...#./..#../.#.../.#.../#...."),
    ('(', "..#../.#.../#..../#..../#..../.#.../..#.."),
    (')', "..#../...#./....#/....#/....#/...#./..#.."),
    ('?', ".###./#...#/....#/...#./..#../...../..#.."),
    ('!', "..#../..#../..#../..#../..#../...../..#.."),
    ('\'', "..#../..#../...../...../...../...../....."),
];

/// The 5x7 font, scaled by WHOLE PIXELS.
///
/// A bitmap glyph resampled to a fractional size grows ragged stems and uneven
/// counters - the artefact that makes a rendered caption look broken rather
/// than small.
#[derive(Debug, Clone)]
pub struct BitmapFont {
    rows: HashMap<char, [u8; Self::GLYPH_HEIGHT]>,
}

impl Default for BitmapFont {
    fn default() -> Self {
        Self::new()
    }
}

impl BitmapFont {
    pub const GLYPH_WIDTH: usize = 5;
    pub const GLYPH_HEIGHT: usize = 7;
    /// One blank column between glyphs, at the same scale as the glyph.
    pub const TRACKING: usize = 1;

    pub fn new() -> Self {
        let mut rows = HashMap::new();
        for (ch, art) in GLYPH_ART {
            let mut bits = [0u8; Self::GLYPH_HEIGHT];
            for (y, line) in art.split('/').enumerate().take(Self::GLYPH_HEIGHT) {
                for (x, c) in line.chars().enumerate().take(Self::GLYPH_WIDTH) {
                    if c == '#' {
                        bits[y] |= 1 << (Self::GLYPH_WIDTH - 1 - x);
                    }
                }
            }
            rows.insert(*ch, bits);
        }
        Self { rows }
    }

    /// Unknown characters fall back to `?`, never to nothing.
    ///
    /// A missing glyph that draws blank turns a caption in a language this font
    /// does not cover into an empty box, and nobody reports an empty box.
    pub fn glyph(&self, ch: char) -> [u8; Self::GLYPH_HEIGHT] {
        let upper = ch.to_ascii_uppercase();
        *self
            .rows
            .get(&upper)
            .or_else(|| self.rows.get(&'?'))
            .unwrap_or(&[0u8; Self::GLYPH_HEIGHT])
    }

    pub fn measure(&self, text: &str, scale: usize) -> (usize, usize) {
        let count = text.chars().count();
        if count == 0 {
            return (0, 0);
        }
        let advance = (Self::GLYPH_WIDTH + Self::TRACKING) * scale;
        (
            advance * count - Self::TRACKING * scale,
            Self::GLYPH_HEIGHT * scale,
        )
    }

    /// At least 1, so text never vanishes at a small size - it goes chunky
    /// instead, which is legible and obviously wrong rather than silently
    /// absent.
    pub fn scale_for_height(&self, pixels: f32) -> usize {
        ((pixels / Self::GLYPH_HEIGHT as f32).round() as usize).max(1)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The canvas

/// Draws onto a `PixelBuffer`.
pub struct RasterCanvas {
    pub buffer: PixelBuffer,
    pub font: BitmapFont,
}

impl RasterCanvas {
    pub fn create(size: RenderSize, background: Rgba32) -> Option<Self> {
        Some(Self {
            buffer: PixelBuffer::new(size.width, size.height, background)?,
            font: BitmapFont::new(),
        })
    }

    /// CLIPPED here rather than per pixel.
    ///
    /// Clipping the loop bounds once instead of testing every pixel is the
    /// difference between a full-frame fill costing a million branch
    /// mispredictions and costing none.
    pub fn fill_rect(&mut self, x: i32, y: i32, width: i32, height: i32, colour: Rgba32) {
        let x0 = x.max(0);
        let y0 = y.max(0);
        let x1 = (x + width).min(self.buffer.width as i32);
        let y1 = (y + height).min(self.buffer.height as i32);
        if x1 <= x0 || y1 <= y0 {
            return;
        }
        for py in y0..y1 {
            for px in x0..x1 {
                if colour.a == 255 {
                    self.buffer.set(px, py, colour);
                } else {
                    self.buffer.blend(px, py, colour);
                }
            }
        }
    }

    /// Bresenham. Integer only - no accumulated float error, so a long axis line
    /// stays straight to its last pixel.
    pub fn draw_line(&mut self, x0: i32, y0: i32, x1: i32, y1: i32, colour: Rgba32) {
        let (mut x, mut y) = (x0, y0);
        let dx = (x1 - x0).abs();
        let dy = -(y1 - y0).abs();
        let sx = if x0 < x1 { 1 } else { -1 };
        let sy = if y0 < y1 { 1 } else { -1 };
        let mut err = dx + dy;
        loop {
            self.buffer.blend(x, y, colour);
            if x == x1 && y == y1 {
                return;
            }
            let e2 = 2 * err;
            if e2 >= dy {
                err += dy;
                x += sx;
            }
            if e2 <= dx {
                err += dx;
                y += sy;
            }
        }
    }

    /// `x`, `y` is the TOP-LEFT of the run, adjusted for alignment. Returns the
    /// measured size so a caller can draw a plate behind it.
    pub fn draw_text(
        &mut self,
        text: &str,
        x: i32,
        y: i32,
        scale: usize,
        colour: Rgba32,
        align: TextAlign,
    ) -> (usize, usize) {
        let measured = self.font.measure(text, scale);
        let mut pen = match align {
            TextAlign::Left => x,
            TextAlign::Center => x - (measured.0 / 2) as i32,
            TextAlign::Right => x - measured.0 as i32,
        };
        let advance = ((BitmapFont::GLYPH_WIDTH + BitmapFont::TRACKING) * scale) as i32;
        for ch in text.chars() {
            let rows = self.font.glyph(ch);
            for (ry, bits) in rows.iter().enumerate() {
                if *bits == 0 {
                    continue;
                }
                for rx in 0..BitmapFont::GLYPH_WIDTH {
                    if bits & (1 << (BitmapFont::GLYPH_WIDTH - 1 - rx)) != 0 {
                        self.fill_rect(
                            pen + (rx * scale) as i32,
                            y + (ry * scale) as i32,
                            scale as i32,
                            scale as i32,
                            colour,
                        );
                    }
                }
            }
            pen += advance;
        }
        measured
    }

    /// Nearest-neighbour, sampled from the DESTINATION.
    ///
    /// Destination-driven so every output pixel is written exactly once -
    /// source-driven scaling leaves unwritten gaps when scaling up, which show
    /// as a grid of holes.
    pub fn draw_image(
        &mut self,
        source: &PixelBuffer,
        rect: (i32, i32, i32, i32),
        fit: ContentFit,
        opacity: f32,
    ) {
        let (dx, dy, dw, dh) = rect;
        if dw <= 0 || dh <= 0 || opacity <= 0.0 {
            return;
        }
        let sw = source.width as i32;
        let sh = source.height as i32;
        let (ox, oy, tw, th) = match fit {
            ContentFit::Stretch => (dx, dy, dw, dh),
            ContentFit::None => (dx, dy, sw, sh),
            _ => {
                let sx = dw as f32 / sw as f32;
                let sy = dh as f32 / sh as f32;
                let s = if matches!(fit, ContentFit::Contain) {
                    sx.min(sy)
                } else {
                    sx.max(sy)
                };
                let tw = ((sw as f32 * s).round() as i32).max(1);
                let th = ((sh as f32 * s).round() as i32).max(1);
                (dx + (dw - tw) / 2, dy + (dh - th) / 2, tw, th)
            }
        };

        let alpha = opacity.clamp(0.0, 1.0);
        // Clipped to BOTH the destination rectangle and the buffer: Cover
        // deliberately overflows its box, and without the first clip it would
        // paint over neighbouring layers.
        let x_from = ox.max(dx).max(0);
        let x_to = (ox + tw).min(dx + dw).min(self.buffer.width as i32);
        let y_from = oy.max(dy).max(0);
        let y_to = (oy + th).min(dy + dh).min(self.buffer.height as i32);
        for py in y_from..y_to {
            let syi = (((py - oy) as i64 * sh as i64) / th as i64).clamp(0, (sh - 1) as i64) as i32;
            for px in x_from..x_to {
                let sxi =
                    (((px - ox) as i64 * sw as i64) / tw as i64).clamp(0, (sw - 1) as i64) as i32;
                let c = source.get(sxi, syi);
                let c = if alpha < 1.0 {
                    c.with_alpha((c.a as f32 * alpha).round() as u8)
                } else {
                    c
                };
                self.buffer.blend(px, py, c);
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PNG

/// The PNG Paeth predictor. `a` left, `b` above, `c` upper-left.
///
/// THE TIE-BREAK IS ORDERED - left, then above, then upper-left - and it is
/// written as `<=` twice for exactly that reason. Reversing it decodes most
/// images correctly and a few with coloured streaks along diagonal edges, which
/// is a bug that survives a test suite and fails on a photograph.
fn paeth(a: i32, b: i32, c: i32) -> i32 {
    let p = a + b - c;
    let (pa, pb, pc) = ((p - a).abs(), (p - b).abs(), (p - c).abs());
    if pa <= pb && pa <= pc {
        a
    } else if pb <= pc {
        b
    } else {
        c
    }
}

fn crc32(bytes: &[u8]) -> u32 {
    let mut table = [0u32; 256];
    for (n, entry) in table.iter_mut().enumerate() {
        let mut c = n as u32;
        for _ in 0..8 {
            c = if c & 1 != 0 { 0xEDB8_8320 ^ (c >> 1) } else { c >> 1 };
        }
        *entry = c;
    }
    let mut c = 0xFFFF_FFFFu32;
    for byte in bytes {
        c = table[((c ^ *byte as u32) & 0xFF) as usize] ^ (c >> 8);
    }
    c ^ 0xFFFF_FFFF
}

/// Adler-32, which is what zlib's trailer carries - NOT CRC-32, which is what
/// PNG's chunks carry. Two different checksums in one file, and swapping them
/// produces a stream every decoder rejects with an error about the wrong one.
fn adler32(bytes: &[u8]) -> u32 {
    let (mut a, mut b) = (1u32, 0u32);
    for byte in bytes {
        a = (a + *byte as u32) % 65521;
        b = (b + a) % 65521;
    }
    (b << 16) | a
}

/// A zlib stream of DEFLATE STORED blocks - valid deflate, no compression.
///
/// LEN and NLEN are LITTLE-endian and NLEN is the ones complement of LEN - a
/// decoder checks that, so getting either wrong is caught immediately rather
/// than producing a subtly wrong image. Blocks cap at 65535 bytes because LEN
/// is 16 bits; a single oversized block truncates the image at 64 KB.
fn zlib_stored(data: &[u8]) -> Vec<u8> {
    const MAX: usize = 0xFFFF;
    let blocks = data.len().div_ceil(MAX).max(1);
    let mut out = Vec::with_capacity(2 + blocks * 5 + data.len() + 4);
    // 0x78 0x01: deflate, 32K window, no preset dictionary. The pair must
    // satisfy (CMF<<8 | FLG) % 31 == 0, which this one does.
    out.extend_from_slice(&[0x78, 0x01]);
    for i in 0..blocks {
        let start = i * MAX;
        let len = (data.len() - start).min(MAX);
        out.push(if i == blocks - 1 { 1 } else { 0 });
        out.extend_from_slice(&(len as u16).to_le_bytes());
        out.extend_from_slice(&(!(len as u16)).to_le_bytes());
        out.extend_from_slice(&data[start..start + len]);
    }
    // Adler-32 is BIG-endian, unlike everything else in the deflate stream.
    out.extend_from_slice(&adler32(data).to_be_bytes());
    out
}

/// Inflates a zlib stream of stored blocks.
///
/// Compressed blocks need a Huffman decoder, which a host's zlib does far better
/// than this would. A file this cannot read is REPORTED, never guessed at - a
/// decoder that returns something plausible for input it did not understand is
/// the worst kind.
fn zlib_inflate_stored(data: &[u8]) -> Result<Vec<u8>, String> {
    if data.len() < 6 {
        return Err("not a zlib stream".into());
    }
    let mut out = Vec::new();
    let mut p = 2usize;
    loop {
        let header = *data.get(p).ok_or("zlib stream ended inside a block header")?;
        p += 1;
        if (header >> 1) & 3 != 0 {
            return Err(
                "this decoder reads stored deflate blocks only; pass a host inflater for compressed PNGs"
                    .into(),
            );
        }
        let len = u16::from_le_bytes([
            *data.get(p).ok_or("short block")?,
            *data.get(p + 1).ok_or("short block")?,
        ]) as usize;
        let nlen = u16::from_le_bytes([
            *data.get(p + 2).ok_or("short block")?,
            *data.get(p + 3).ok_or("short block")?,
        ]);
        p += 4;
        // The complement check is the format's own integrity test. Skipping it
        // is how a misaligned stream reads garbage as a valid block.
        if (len as u16) ^ 0xFFFF != nlen {
            return Err("stored block length is corrupt".into());
        }
        out.extend_from_slice(data.get(p..p + len).ok_or("block runs past the end")?);
        p += len;
        if header & 1 != 0 {
            break;
        }
    }
    Ok(out)
}

/// PNG in and out.
pub struct ImageCodecs;

impl ImageCodecs {
    pub const PNG_SIGNATURE: [u8; 8] = [137, 80, 78, 71, 13, 10, 26, 10];

    /// Length and CRC are BIG-endian, unlike everything inside the compressed
    /// data. Three byte orders in one file, and this is the first two of them.
    pub fn chunk(kind: &[u8; 4], payload: &[u8]) -> Vec<u8> {
        let mut out = Vec::with_capacity(12 + payload.len());
        out.extend_from_slice(&(payload.len() as u32).to_be_bytes());
        out.extend_from_slice(kind);
        out.extend_from_slice(payload);
        let crc = crc32(&out[4..]);
        out.extend_from_slice(&crc.to_be_bytes());
        out
    }

    /// Every scanline is prefixed with a filter byte - 0 here, meaning stored
    /// as-is. Omitting the byte produces a file exactly one byte short per row
    /// that decodes as a diagonal smear.
    pub fn raw_scanlines(buffer: &PixelBuffer) -> Vec<u8> {
        let stride = buffer.width as usize * 4;
        let mut out = Vec::with_capacity(buffer.height as usize * (stride + 1));
        for y in 0..buffer.height as usize {
            out.push(0);
            out.extend_from_slice(&buffer.data[y * stride..(y + 1) * stride]);
        }
        out
    }

    /// Colour type 6 (RGBA), 8 bits, no interlace.
    pub fn encode_png(
        buffer: &PixelBuffer,
        deflate: Option<&dyn Fn(&[u8]) -> Vec<u8>>,
    ) -> Vec<u8> {
        let mut ihdr = Vec::with_capacity(13);
        ihdr.extend_from_slice(&buffer.width.to_be_bytes());
        ihdr.extend_from_slice(&buffer.height.to_be_bytes());
        ihdr.extend_from_slice(&[8, 6, 0, 0, 0]);

        let raw = Self::raw_scanlines(buffer);
        let compressed = match deflate {
            Some(f) => f(&raw),
            None => zlib_stored(&raw),
        };

        let mut out = Vec::new();
        out.extend_from_slice(&Self::PNG_SIGNATURE);
        out.extend_from_slice(&Self::chunk(b"IHDR", &ihdr));
        out.extend_from_slice(&Self::chunk(b"IDAT", &compressed));
        out.extend_from_slice(&Self::chunk(b"IEND", &[]));
        out
    }

    /// Undoes the five filters and returns RGBA.
    ///
    /// Grey and palette images are widened to RGBA here so nothing downstream
    /// has to know about colour types.
    pub fn decode_png(
        data: &[u8],
        inflate: Option<&dyn Fn(&[u8]) -> Result<Vec<u8>, String>>,
    ) -> Result<PixelBuffer, String> {
        if data.len() < 8 || data[..8] != Self::PNG_SIGNATURE {
            return Err("not a PNG: the signature does not match".into());
        }
        let mut p = 8usize;
        let (mut width, mut height, mut colour_type) = (0u32, 0u32, 0u8);
        let mut idat = Vec::new();
        let mut palette: Vec<u8> = Vec::new();
        let mut trns: Vec<u8> = Vec::new();

        while p + 8 <= data.len() {
            let length =
                u32::from_be_bytes([data[p], data[p + 1], data[p + 2], data[p + 3]]) as usize;
            let kind = &data[p + 4..p + 8];
            let payload = data
                .get(p + 8..p + 8 + length)
                .ok_or("a chunk runs past the end of the file")?;
            p += 12 + length;
            match kind {
                b"IHDR" => {
                    width = u32::from_be_bytes([payload[0], payload[1], payload[2], payload[3]]);
                    height = u32::from_be_bytes([payload[4], payload[5], payload[6], payload[7]]);
                    if payload[8] != 8 {
                        return Err(format!("only 8-bit PNG is supported, not {}-bit", payload[8]));
                    }
                    colour_type = payload[9];
                    if payload[12] != 0 {
                        return Err("interlaced PNG is not supported".into());
                    }
                }
                b"PLTE" => palette = payload.to_vec(),
                b"tRNS" => trns = payload.to_vec(),
                // CONCATENATED before inflating. IDAT may be split at any byte,
                // including mid-symbol, so inflating each chunk on its own fails
                // on exactly the large images that need splitting.
                b"IDAT" => idat.extend_from_slice(payload),
                b"IEND" => break,
                _ => {}
            }
        }
        if width == 0 || height == 0 {
            return Err("PNG has no IHDR".into());
        }
        let channels: usize = match colour_type {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            other => return Err(format!("unsupported PNG colour type {other}")),
        };

        let raw = match inflate {
            Some(f) => f(&idat)?,
            None => zlib_inflate_stored(&idat)?,
        };

        let stride = width as usize * channels;
        let mut out =
            PixelBuffer::new(width, height, Rgba32::TRANSPARENT).ok_or("bad dimensions")?;
        let mut previous = vec![0u8; stride];
        let mut pos = 0usize;
        for y in 0..height as usize {
            let f = *raw.get(pos).ok_or("scanline data ran out")?;
            let mut line = raw
                .get(pos + 1..pos + 1 + stride)
                .ok_or("scanline data ran out")?
                .to_vec();
            pos += 1 + stride;
            match f {
                0 => {}
                1 => {
                    for i in channels..stride {
                        line[i] = line[i].wrapping_add(line[i - channels]);
                    }
                }
                2 => {
                    for i in 0..stride {
                        line[i] = line[i].wrapping_add(previous[i]);
                    }
                }
                3 => {
                    for i in 0..stride {
                        let left = if i >= channels { line[i - channels] as u32 } else { 0 };
                        // The average is FLOORED before adding - rounding it up
                        // drifts a level per row and the image ends visibly
                        // lighter at the bottom.
                        line[i] = line[i].wrapping_add(((left + previous[i] as u32) >> 1) as u8);
                    }
                }
                4 => {
                    for i in 0..stride {
                        let left = if i >= channels { line[i - channels] as i32 } else { 0 };
                        let upper_left =
                            if i >= channels { previous[i - channels] as i32 } else { 0 };
                        line[i] = line[i]
                            .wrapping_add(paeth(left, previous[i] as i32, upper_left) as u8);
                    }
                }
                other => return Err(format!("unknown PNG filter {other}")),
            }

            let base = y * width as usize * 4;
            for x in 0..width as usize {
                let s = x * channels;
                let d = base + x * 4;
                match colour_type {
                    6 => out.data[d..d + 4].copy_from_slice(&line[s..s + 4]),
                    2 => {
                        out.data[d..d + 3].copy_from_slice(&line[s..s + 3]);
                        out.data[d + 3] = 255;
                    }
                    0 => {
                        let v = line[s];
                        out.data[d..d + 4].copy_from_slice(&[v, v, v, 255]);
                    }
                    4 => {
                        let v = line[s];
                        out.data[d..d + 4].copy_from_slice(&[v, v, v, line[s + 1]]);
                    }
                    _ => {
                        let idx = line[s] as usize;
                        let rgb = palette.get(idx * 3..idx * 3 + 3).unwrap_or(&[0, 0, 0]);
                        out.data[d..d + 3].copy_from_slice(rgb);
                        out.data[d + 3] = *trns.get(idx).unwrap_or(&255);
                    }
                }
            }
            previous = line;
        }
        Ok(out)
    }
}

/// Turns encoded bytes into pixels.
pub trait ImageDecoder {
    fn can_decode(&self, data: &[u8]) -> bool;
    fn decode(&self, data: &[u8]) -> Option<PixelBuffer>;
}

/// PNG only.
///
/// JPEG is deliberately absent rather than half-written: a decoder that produces
/// something for a JPEG but not the right something is worse than one that says
/// it cannot.
#[derive(Default)]
pub struct ManagedImageDecoder;

impl ImageDecoder for ManagedImageDecoder {
    fn can_decode(&self, data: &[u8]) -> bool {
        data.len() >= 8 && data[..8] == ImageCodecs::PNG_SIGNATURE
    }
    fn decode(&self, data: &[u8]) -> Option<PixelBuffer> {
        if !self.can_decode(data) {
            return None;
        }
        ImageCodecs::decode_png(data, None).ok()
    }
}

/// APNG: a PNG whose first frame is a valid still.
///
/// That ORDER is the entire trick. A viewer that knows nothing about APNG shows
/// the IDAT and stops, so the file degrades to a still image everywhere rather
/// than failing everywhere - which is why this is the animation format for a
/// device that cannot ship a video encoder.
#[derive(Debug, Default)]
pub struct AnimatedPngEncoder {
    /// Zero means forever, per the spec. Not a missing value.
    loops: u32,
    frames: Vec<(PixelBuffer, u16)>,
}

impl AnimatedPngEncoder {
    pub fn new(loops: u32) -> Self {
        Self { loops, frames: Vec::new() }
    }

    pub fn frame_count(&self) -> usize {
        self.frames.len()
    }

    pub fn add_frame(&mut self, buffer: PixelBuffer, delay_ms: u16) -> bool {
        if let Some((first, _)) = self.frames.first() {
            if buffer.width != first.width || buffer.height != first.height {
                return false;
            }
        }
        self.frames.push((buffer, delay_ms.max(1)));
        true
    }

    /// The sequence number spans fcTL AND fdAT and must increase by one across
    /// both. Numbering them separately produces a file every decoder rejects,
    /// and the error it reports names the chunk rather than the counter.
    pub fn encode(&self) -> Option<Vec<u8>> {
        let (first, first_delay) = self.frames.first()?;

        let mut ihdr = Vec::with_capacity(13);
        ihdr.extend_from_slice(&first.width.to_be_bytes());
        ihdr.extend_from_slice(&first.height.to_be_bytes());
        ihdr.extend_from_slice(&[8, 6, 0, 0, 0]);

        let mut actl = Vec::with_capacity(8);
        actl.extend_from_slice(&(self.frames.len() as u32).to_be_bytes());
        actl.extend_from_slice(&self.loops.to_be_bytes());

        let mut seq = 0u32;
        let mut fctl = |delay_ms: u16, seq: &mut u32| -> Vec<u8> {
            let mut payload = Vec::with_capacity(26);
            payload.extend_from_slice(&seq.to_be_bytes());
            payload.extend_from_slice(&first.width.to_be_bytes());
            payload.extend_from_slice(&first.height.to_be_bytes());
            payload.extend_from_slice(&0u32.to_be_bytes());
            payload.extend_from_slice(&0u32.to_be_bytes());
            // Delay is a RATIONAL, numerator over denominator, not milliseconds.
            payload.extend_from_slice(&delay_ms.to_be_bytes());
            payload.extend_from_slice(&1000u16.to_be_bytes());
            payload.extend_from_slice(&[0, 0]);
            *seq += 1;
            ImageCodecs::chunk(b"fcTL", &payload)
        };

        let mut out = Vec::new();
        out.extend_from_slice(&ImageCodecs::PNG_SIGNATURE);
        out.extend_from_slice(&ImageCodecs::chunk(b"IHDR", &ihdr));
        out.extend_from_slice(&ImageCodecs::chunk(b"acTL", &actl));
        out.extend_from_slice(&fctl(*first_delay, &mut seq));
        out.extend_from_slice(&ImageCodecs::chunk(
            b"IDAT",
            &zlib_stored(&ImageCodecs::raw_scanlines(first)),
        ));
        for (buffer, delay) in self.frames.iter().skip(1) {
            out.extend_from_slice(&fctl(*delay, &mut seq));
            let mut fdat = seq.to_be_bytes().to_vec();
            seq += 1;
            fdat.extend_from_slice(&zlib_stored(&ImageCodecs::raw_scanlines(buffer)));
            out.extend_from_slice(&ImageCodecs::chunk(b"fdAT", &fdat));
        }
        out.extend_from_slice(&ImageCodecs::chunk(b"IEND", &[]));
        Some(out)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The renderer

/// How a clip is encoded.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClipEncodeOptions {
    pub frames_per_second: u32,
    pub bitrate_kbps: u32,
    /// The container to try. A host with no encoder ignores it and the renderer
    /// falls back to an APNG.
    pub container: String,
}

impl Default for ClipEncodeOptions {
    fn default() -> Self {
        Self { frames_per_second: 30, bitrate_kbps: 2500, container: "mp4".into() }
    }
}

/// The result of encoding.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EncodedClip {
    pub bytes: Vec<u8>,
    pub media_type: String,
    pub width: u32,
    pub height: u32,
    pub frame_count: usize,
    /// True when this came out as an animated PNG because no video encoder was
    /// available. Carried so a caller can tell a person why the file is large.
    pub fell_back_to_apng: bool,
}

/// Encodes frames into a clip.
pub trait VideoEncoder {
    fn is_available(&self) -> bool;
    fn encode(&self, frames: &[PixelBuffer], options: &ClipEncodeOptions) -> Option<EncodedClip>;
}

/// Encodes nothing and says so.
///
/// The default. It reports unavailable rather than failing, so the renderer
/// takes the APNG path instead of erroring.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullVideoEncoder;

impl VideoEncoder for NullVideoEncoder {
    fn is_available(&self) -> bool {
        false
    }
    fn encode(&self, _frames: &[PixelBuffer], _options: &ClipEncodeOptions) -> Option<EncodedClip> {
        None
    }
}

/// Renders HTML into pixels, when a host has an engine.
pub trait HtmlFrameProvider {
    fn is_available(&self) -> bool;
    fn render(&self, html: &str, css: &str) -> Option<PixelBuffer>;
}

/// Renders nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullHtmlFrameProvider;

impl HtmlFrameProvider for NullHtmlFrameProvider {
    fn is_available(&self) -> bool {
        false
    }
    fn render(&self, _html: &str, _css: &str) -> Option<PixelBuffer> {
        None
    }
}

/// Turns a spec into pixels or a clip.
pub trait MediaRenderer {
    fn render_still(&self, spec: &MediaSpec) -> Option<PixelBuffer>;
    fn render_clip(&self, spec: &MediaSpec, options: &ClipEncodeOptions) -> Option<EncodedClip>;
}

/// Renders a flat background and nothing else.
///
/// Not a failure: a build with no renderer configured should produce a plain
/// card rather than nothing where a picture was expected.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullMediaRenderer;

impl MediaRenderer for NullMediaRenderer {
    fn render_still(&self, spec: &MediaSpec) -> Option<PixelBuffer> {
        PixelBuffer::new(spec.size.width, spec.size.height, spec.background)
    }

    fn render_clip(&self, spec: &MediaSpec, _options: &ClipEncodeOptions) -> Option<EncodedClip> {
        let buffer = self.render_still(spec)?;
        Some(EncodedClip {
            bytes: ImageCodecs::encode_png(&buffer, None),
            media_type: "image/png".into(),
            width: buffer.width,
            height: buffer.height,
            frame_count: 1,
            fell_back_to_apng: true,
        })
    }
}

/// The default renderer: pure Rust, no native dependency.
pub struct ManagedMediaRenderer {
    decoder: Box<dyn ImageDecoder + Send + Sync>,
    encoder: Box<dyn VideoEncoder + Send + Sync>,
    font: BitmapFont,
}

impl Default for ManagedMediaRenderer {
    fn default() -> Self {
        Self {
            decoder: Box::new(ManagedImageDecoder),
            encoder: Box::new(NullVideoEncoder),
            font: BitmapFont::new(),
        }
    }
}

impl ManagedMediaRenderer {
    pub fn new(
        decoder: Box<dyn ImageDecoder + Send + Sync>,
        encoder: Box<dyn VideoEncoder + Send + Sync>,
    ) -> Self {
        Self { decoder, encoder, font: BitmapFont::new() }
    }

    pub fn render_frame(&self, spec: &MediaSpec, t: f32) -> Option<PixelBuffer> {
        let mut canvas = RasterCanvas::create(spec.size, spec.background)?;
        for layer in &spec.layers {
            // A layer that cannot be decoded is SKIPPED, not fatal. One broken
            // image should cost one layer, not the whole picture.
            let Some(source) = layer.source.decode(self.decoder.as_ref()) else {
                continue;
            };
            canvas.draw_image(
                &source,
                layer.rect_at(t).scaled(spec.size.width, spec.size.height),
                layer.fit,
                layer.opacity,
            );
        }
        for overlay in &spec.overlays {
            let scale = self.font.scale_for_height(overlay.size * spec.size.height as f32);
            let (width, height) = self.font.measure(&overlay.text, scale);
            let x = overlay.at.x * spec.size.width as f32;
            let y = overlay.at.y * spec.size.height as f32;
            let top = (y - height as f32 / 2.0).round() as i32;
            if let Some(background) = overlay.background {
                let pad = (overlay.padding * spec.size.height as f32).round() as i32;
                let left = match overlay.align {
                    TextAlign::Left => x.round() as i32,
                    TextAlign::Center => x.round() as i32 - (width / 2) as i32,
                    TextAlign::Right => x.round() as i32 - width as i32,
                };
                canvas.fill_rect(
                    left - pad,
                    top - pad,
                    width as i32 + 2 * pad,
                    height as i32 + 2 * pad,
                    background,
                );
            }
            canvas.draw_text(
                &overlay.text,
                x.round() as i32,
                top,
                scale,
                overlay.colour,
                overlay.align,
            );
        }
        Some(canvas.buffer)
    }
}

impl MediaRenderer for ManagedMediaRenderer {
    fn render_still(&self, spec: &MediaSpec) -> Option<PixelBuffer> {
        self.render_frame(spec, 0.0)
    }

    fn render_clip(&self, spec: &MediaSpec, options: &ClipEncodeOptions) -> Option<EncodedClip> {
        let count = spec.frame_count();
        // `count - 1` in the denominator so the last frame lands exactly on
        // t=1.0. Dividing by `count` stops one frame short and a motion never
        // reaches its end rectangle.
        let frames: Vec<PixelBuffer> = (0..count)
            .filter_map(|i| {
                let t = if count > 1 {
                    i as f32 / (count - 1) as f32
                } else {
                    0.0
                };
                self.render_frame(spec, t)
            })
            .collect();

        if self.encoder.is_available() {
            return self.encoder.encode(&frames, options);
        }
        let mut apng = AnimatedPngEncoder::new(0);
        let delay = (1000 / options.frames_per_second.max(1)).max(1) as u16;
        for frame in frames {
            apng.add_frame(frame, delay);
        }
        Some(EncodedClip {
            bytes: apng.encode()?,
            media_type: "image/apng".into(),
            width: spec.size.width,
            height: spec.size.height,
            frame_count: count,
            fell_back_to_apng: true,
        })
    }
}

/// Ready-made specs for the things people actually ask for.
pub struct MediaTemplates;

impl MediaTemplates {
    /// Wraps by MEASURING, not by character count.
    ///
    /// A fixed character count wraps a line of capitals off the edge and leaves
    /// a line of lowercase half empty, because what fits is ink, not letters.
    pub fn quote_card(text: &str, size: RenderSize, background: Rgba32, ink: Rgba32) -> MediaSpec {
        let font = BitmapFont::new();
        let scale = font.scale_for_height(0.07 * size.height as f32);
        let max_width = (size.width as f32 * 0.86) as usize;
        let mut lines: Vec<String> = Vec::new();
        let mut current = String::new();
        for word in text.split_whitespace() {
            let candidate = if current.is_empty() {
                word.to_string()
            } else {
                format!("{current} {word}")
            };
            if !current.is_empty() && font.measure(&candidate, scale).0 > max_width {
                lines.push(std::mem::take(&mut current));
                current = word.to_string();
            } else {
                current = candidate;
            }
        }
        if !current.is_empty() {
            lines.push(current);
        }

        let step = 0.10f32;
        let top = 0.5 - step * (lines.len().saturating_sub(1)) as f32 / 2.0;
        MediaSpec {
            size,
            background,
            overlays: lines
                .into_iter()
                .enumerate()
                .map(|(i, line)| TextOverlay {
                    text: line,
                    at: NormVec { x: 0.5, y: top + i as f32 * step },
                    align: TextAlign::Center,
                    size: 0.07,
                    colour: ink,
                    background: None,
                    padding: 0.01,
                })
                .collect(),
            ..Default::default()
        }
    }
}

/// What the companion knows about making pictures here.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct MediaDomainContext {
    pub can_encode_video: bool,
    pub can_render_html: bool,
    pub max_render_pixels: u32,
}

impl MediaDomainContext {
    pub fn describe(&self) -> String {
        let mut parts = vec!["still images and animated PNG"];
        if self.can_encode_video {
            parts.push("video");
        }
        if self.can_render_html {
            parts.push("HTML layouts");
        }
        format!("this device can make {}", parts.join(", "))
    }
}

/// Puts the renderer behind a plain request.
pub struct MediaCompanionAdapter {
    renderer: Box<dyn MediaRenderer + Send + Sync>,
    pub context: MediaDomainContext,
}

impl Default for MediaCompanionAdapter {
    fn default() -> Self {
        Self {
            renderer: Box::new(ManagedMediaRenderer::default()),
            context: MediaDomainContext::default(),
        }
    }
}

impl MediaCompanionAdapter {
    pub fn new(
        renderer: Box<dyn MediaRenderer + Send + Sync>,
        context: MediaDomainContext,
    ) -> Self {
        Self { renderer, context }
    }

    pub fn make_quote_card(&self, text: &str) -> Option<Vec<u8>> {
        let spec = MediaTemplates::quote_card(
            text,
            RenderSize::SQUARE,
            Rgba32::from_hex("#2c3e50").unwrap_or(Rgba32::BLACK),
            Rgba32::WHITE,
        );
        Some(ImageCodecs::encode_png(&self.renderer.render_still(&spec)?, None))
    }
}
