/**
 * media/rendering
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// Rendering: a raster canvas, a bitmap font, and PNG in both directions.
//
// WHY THIS IS HAND-WRITTEN. The same reason it is in every other port: an image
// library is either a licence to argue about or a native blob per architecture,
// and a page that must run in a browser, in Node, and inside a WebView cannot
// carry any of them.
//
// WHAT IS DIFFERENT HERE FROM THE OTHER PORTS: there is no zlib to reach for.
// Node has one and a browser does not, and `CompressionStream` is asynchronous
// where this is not. So the encoder emits DEFLATE STORED BLOCKS - valid
// deflate, no compression - which every PNG decoder in the world accepts. The
// file is larger, and that is the honest trade: a correct PNG everywhere beats
// a smaller one that only encodes on one runtime. A host that has a compressor
// can pass one in and get the smaller file.
//
// THE THREE THINGS THAT ARE ALWAYS GOT WRONG, and are not here:
//
//   * Compositing in STRAIGHT alpha needs the divide by the output alpha.
//     Without it every soft edge drawn onto transparency gets a dark rim.
//
//   * PNG is big-endian in its framing and DEFLATE is little-endian inside it,
//     and the Huffman codes inside THAT are MSB-first. Three byte orders in one
//     file, and the stored-block path still has to get two of them right.
//
//   * The Paeth tie-break is ordered - left, then above, then upper-left.
//     Reversing it decodes most images correctly and a few with coloured
//     streaks, which is the worst way for a bug to behave.

// ─────────────────────────────────────────────────────────────────────────────
// Geometry and colour

/**
 * A colour, STRAIGHT (not premultiplied).
 *
 * Straight because it is what an author types and what a PNG stores;
 * premultiplying at the edge of the compositor and dividing back out is where
 * the rounding lives, and it lives there once.
 */
export interface Rgba32 {
  readonly r: number;
  readonly g: number;
  readonly b: number;
  readonly a: number;
}

export const rgba = (r: number, g: number, b: number, a = 255): Rgba32 =>
  Object.freeze({ r: clampByte(r), g: clampByte(g), b: clampByte(b), a: clampByte(a) });

export const TRANSPARENT: Rgba32 = rgba(0, 0, 0, 0);
export const BLACK: Rgba32 = rgba(0, 0, 0, 255);
export const WHITE: Rgba32 = rgba(255, 255, 255, 255);

function clampByte(v: number): number {
  return v < 0 ? 0 : v > 255 ? 255 : Math.round(v);
}

/**
 * Accepts #rgb, #rgba, #rrggbb and #rrggbbaa.
 *
 * Alpha LAST, matching CSS. A reader that assumes #aarrggbb gets a fully opaque
 * colour with the wrong red, which looks like a palette mistake rather than a
 * parsing one. The four-digit shorthand is here because CSS has it, and a
 * palette pasted from a stylesheet is exactly how these arrive.
 */
export function colourFromHex(text: string): Rgba32 {
  let s = text.trim().replace(/^#/, "");
  if (s.length === 3 || s.length === 4) s = [...s].map((c) => c + c).join("");
  if (s.length === 6) s += "ff";
  if (s.length !== 8 || !/^[0-9a-fA-F]{8}$/.test(s)) {
    throw new Error(`${text} is not #rgb, #rgba, #rrggbb or #rrggbbaa`);
  }
  return rgba(
    parseInt(s.slice(0, 2), 16),
    parseInt(s.slice(2, 4), 16),
    parseInt(s.slice(4, 6), 16),
    parseInt(s.slice(6, 8), 16),
  );
}

export function colourToHex(c: Rgba32): string {
  const h = (n: number) => n.toString(16).padStart(2, "0");
  return `#${h(c.r)}${h(c.g)}${h(c.b)}${h(c.a)}`;
}

export const withAlpha = (c: Rgba32, a: number): Rgba32 => rgba(c.r, c.g, c.b, a);

/**
 * A point in 0..1 of the frame.
 *
 * NORMALISED so a spec renders at any size. An overlay placed at pixel 240 is
 * centred on one phone and off the edge of another.
 */
export interface NormVec {
  readonly x: number;
  readonly y: number;
}

export const normVec = (x = 0, y = 0): NormVec => Object.freeze({ x, y });

/** A rectangle in 0..1 of the frame. */
export interface NormRect {
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
}

export const normRect = (x = 0, y = 0, width = 1, height = 1): NormRect =>
  Object.freeze({ x, y, width, height });

export const FULL_RECT: NormRect = normRect();

/**
 * Rounds the EDGES, not the size.
 *
 * Rounding x and width separately lets two rectangles that share an edge end up
 * a pixel apart, which shows as a hairline seam.
 */
export function scaleRect(
  r: NormRect,
  width: number,
  height: number,
): { x: number; y: number; w: number; h: number } {
  const x0 = Math.round(r.x * width);
  const y0 = Math.round(r.y * height);
  const x1 = Math.round((r.x + r.width) * width);
  const y1 = Math.round((r.y + r.height) * height);
  return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
}

/** Output pixel size. */
export interface RenderSize {
  readonly width: number;
  readonly height: number;
}

export const renderSize = (width = 1080, height = 1080): RenderSize => {
  if (width <= 0 || height <= 0) {
    throw new Error("a render size must be positive in both directions");
  }
  return Object.freeze({ width, height });
};

export const SQUARE = renderSize(1080, 1080);
export const STORY = renderSize(1080, 1920);
export const LANDSCAPE = renderSize(1920, 1080);

/** How an image fills a box it does not match. */
export enum ContentFit {
  /**
   * Whole image visible, box may show through. The safe default: nothing is
   * lost, and what a caller did not intend is empty space rather than a cropped
   * face.
   */
  Contain = "contain",
  Cover = "cover",
  Stretch = "stretch",
  None = "none",
}

export enum TextAlign {
  Left = "left",
  Center = "center",
  Right = "right",
}

/** How a motion interpolates. */
export enum EasingKind {
  Linear = "linear",
  EaseIn = "ease-in",
  EaseOut = "ease-out",
  EaseInOut = "ease-in-out",
}

/**
 * CLAMPED, because a caller that computes t from a frame index off by one would
 * otherwise extrapolate - an ease-out past 1.0 overshoots and the layer jumps
 * back.
 */
export function ease(kind: EasingKind, t: number): number {
  const c = t < 0 ? 0 : t > 1 ? 1 : t;
  switch (kind) {
    case EasingKind.EaseIn:
      return c * c;
    case EasingKind.EaseOut:
      return 1 - (1 - c) * (1 - c);
    case EasingKind.EaseInOut:
      return c < 0.5 ? 2 * c * c : 1 - 2 * (1 - c) * (1 - c);
    default:
      return c;
  }
}

/** A layer moving between two rectangles over the clip. */
export interface Motion {
  readonly start: NormRect;
  readonly end: NormRect;
  readonly easing: EasingKind;
}

export const motion = (
  start: NormRect = FULL_RECT,
  end: NormRect = FULL_RECT,
  easing: EasingKind = EasingKind.Linear,
): Motion => Object.freeze({ start, end, easing });

export function motionAt(m: Motion, t: number): NormRect {
  const e = ease(m.easing, t);
  return normRect(
    m.start.x + (m.end.x - m.start.x) * e,
    m.start.y + (m.end.y - m.start.y) * e,
    m.start.width + (m.end.width - m.start.width) * e,
    m.start.height + (m.end.height - m.start.height) * e,
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Pixels

/**
 * RGBA8888, top-down, no padding.
 *
 * A `Uint8ClampedArray` rather than an array of objects: a 1080x1080 frame is
 * 1.1 million pixels, and an object per pixel is orders of magnitude more
 * memory and enough allocation to make a phone stutter mid-render. Clamped
 * because it saturates on overflow instead of wrapping, which is what a pixel
 * should do.
 */
export class PixelBuffer {
  readonly width: number;
  readonly height: number;
  readonly data: Uint8ClampedArray;

  constructor(width: number, height: number, fill?: Rgba32) {
    if (width <= 0 || height <= 0) {
      throw new Error("a pixel buffer must be positive in both directions");
    }
    this.width = width;
    this.height = height;
    this.data = new Uint8ClampedArray(width * height * 4);
    if (fill && fill.a !== 0) {
      for (let i = 0; i < this.data.length; i += 4) {
        this.data[i] = fill.r;
        this.data[i + 1] = fill.g;
        this.data[i + 2] = fill.b;
        this.data[i + 3] = fill.a;
      }
    }
  }

  clone(): PixelBuffer {
    const out = new PixelBuffer(this.width, this.height);
    out.data.set(this.data);
    return out;
  }

  get(x: number, y: number): Rgba32 {
    const i = (y * this.width + x) * 4;
    return rgba(this.data[i], this.data[i + 1], this.data[i + 2], this.data[i + 3]);
  }

  set(x: number, y: number, c: Rgba32): void {
    if (x < 0 || y < 0 || x >= this.width || y >= this.height) return;
    const i = (y * this.width + x) * 4;
    this.data[i] = c.r;
    this.data[i + 1] = c.g;
    this.data[i + 2] = c.b;
    this.data[i + 3] = c.a;
  }

  /**
   * Source-over in STRAIGHT alpha.
   *
   * The divide by the output alpha is the whole point. Without it a
   * half-transparent white drawn onto transparency comes out mid-grey instead
   * of white-at-half-alpha, and every soft edge in the frame gets a dark rim.
   */
  blend(x: number, y: number, c: Rgba32): void {
    if (c.a === 0 || x < 0 || y < 0 || x >= this.width || y >= this.height) return;
    if (c.a === 255) {
      this.set(x, y, c);
      return;
    }
    const i = (y * this.width + x) * 4;
    const sa = c.a / 255;
    const da = this.data[i + 3] / 255;
    const outA = sa + da * (1 - sa);
    if (outA <= 0) {
      this.data[i] = this.data[i + 1] = this.data[i + 2] = this.data[i + 3] = 0;
      return;
    }
    const mix = (s: number, d: number) => (s * sa + d * da * (1 - sa)) / outA;
    this.data[i] = mix(c.r, this.data[i]);
    this.data[i + 1] = mix(c.g, this.data[i + 1]);
    this.data[i + 2] = mix(c.b, this.data[i + 2]);
    this.data[i + 3] = outA * 255;
  }
}

/** Something a layer draws. */
export interface ImageSource {
  decode(decoder?: ImageDecoder): PixelBuffer;
}

/** Pixels already in hand. */
export class RawImageSource implements ImageSource {
  constructor(readonly buffer: PixelBuffer) {}
  decode(): PixelBuffer {
    return this.buffer.clone();
  }
}

/** Bytes of a PNG. */
export class EncodedImageSource implements ImageSource {
  constructor(
    readonly bytes: Uint8Array,
    readonly mediaType = "image/png",
  ) {}
  decode(decoder?: ImageDecoder): PixelBuffer {
    return (decoder ?? new ManagedImageDecoder()).decode(this.bytes);
  }
}

/**
 * A layer rendered by a browser engine, when the host has one.
 *
 * A SOURCE rather than a renderer, so a spec that names one still renders on a
 * host with no browser - the layer is skipped and the rest of the frame is
 * produced. A missing layer is a worse picture; a failed render is no picture.
 */
export class HtmlTemplateSource implements ImageSource {
  constructor(
    readonly html: string,
    readonly css = "",
    readonly provider?: HtmlFrameProvider,
  ) {}
  decode(): PixelBuffer {
    if (!this.provider) throw new Error("no HTML frame provider on this host");
    return this.provider.render(this.html, this.css);
  }
}

/** One image placed in the frame. */
export interface ImageLayer {
  readonly source: ImageSource;
  readonly rect: NormRect;
  readonly fit: ContentFit;
  readonly opacity: number;
  readonly motion?: Motion;
}

export const imageLayer = (
  source: ImageSource,
  rect: NormRect = FULL_RECT,
  fit: ContentFit = ContentFit.Contain,
  opacity = 1,
  m?: Motion,
): ImageLayer => Object.freeze({ source, rect, fit, opacity, motion: m });

export const layerRectAt = (layer: ImageLayer, t: number): NormRect =>
  layer.motion ? motionAt(layer.motion, t) : layer.rect;

/** Text placed in the frame. */
export interface TextOverlay {
  readonly text: string;
  readonly at: NormVec;
  readonly align: TextAlign;
  /**
   * In FRACTIONS OF FRAME HEIGHT, not points. A point size renders the same
   * caption legibly at 1080 and unreadably at 320.
   */
  readonly size: number;
  readonly colour: Rgba32;
  /**
   * A backing plate. Not decoration: white text over an unknown photograph is
   * unreadable about half the time.
   */
  readonly background?: Rgba32;
  readonly padding: number;
}

export const textOverlay = (
  text: string,
  at: NormVec = normVec(0.5, 0.5),
  align: TextAlign = TextAlign.Center,
  size = 0.06,
  colour: Rgba32 = WHITE,
  background?: Rgba32,
  padding = 0.01,
): TextOverlay => Object.freeze({ text, at, align, size, colour, background, padding });

/** A whole frame or clip, declaratively. */
export interface MediaSpec {
  readonly size: RenderSize;
  readonly background: Rgba32;
  readonly layers: readonly ImageLayer[];
  readonly overlays: readonly TextOverlay[];
  readonly durationSeconds: number;
  readonly framesPerSecond: number;
}

export const mediaSpec = (spec: Partial<MediaSpec> = {}): MediaSpec =>
  Object.freeze({
    size: spec.size ?? SQUARE,
    background: spec.background ?? BLACK,
    layers: Object.freeze([...(spec.layers ?? [])]),
    overlays: Object.freeze([...(spec.overlays ?? [])]),
    durationSeconds: spec.durationSeconds ?? 0,
    framesPerSecond: spec.framesPerSecond ?? 30,
  });

export const isStill = (s: MediaSpec): boolean => s.durationSeconds <= 0;

export const frameCount = (s: MediaSpec): number =>
  isStill(s) ? 1 : Math.max(1, Math.round(s.durationSeconds * s.framesPerSecond));

// ─────────────────────────────────────────────────────────────────────────────
// The font

/**
 * A 5x7 bitmap font, one entry per glyph, rows separated by `/`.
 *
 * Hand-drawn and deliberately small: it exists to label a chart axis and stamp
 * a caption, and it is the only way to put a word on a picture without a font
 * file, a shaper and a licence to check.
 */
const GLYPH_ART: Readonly<Record<string, string>> = Object.freeze({
  " ": "...../...../...../...../...../...../.....",
  "0": ".###./#...#/#..##/#.#.#/##..#/#...#/.###.",
  "1": "..#../.##../..#../..#../..#../..#../.###.",
  "2": ".###./#...#/....#/...#./..#../.#.../#####",
  "3": "####./....#/....#/.###./....#/....#/####.",
  "4": "...#./..##./.#.#./#..#./#####/...#./...#.",
  "5": "#####/#..../####./....#/....#/#...#/.###.",
  "6": ".###./#..../#..../####./#...#/#...#/.###.",
  "7": "#####/....#/...#./..#../.#.../.#.../.#...",
  "8": ".###./#...#/#...#/.###./#...#/#...#/.###.",
  "9": ".###./#...#/#...#/.####/....#/....#/.###.",
  A: ".###./#...#/#...#/#####/#...#/#...#/#...#",
  B: "####./#...#/#...#/####./#...#/#...#/####.",
  C: ".###./#...#/#..../#..../#..../#...#/.###.",
  D: "####./#...#/#...#/#...#/#...#/#...#/####.",
  E: "#####/#..../#..../####./#..../#..../#####",
  F: "#####/#..../#..../####./#..../#..../#....",
  G: ".###./#...#/#..../#.###/#...#/#...#/.###.",
  H: "#...#/#...#/#...#/#####/#...#/#...#/#...#",
  I: ".###./..#../..#../..#../..#../..#../.###.",
  J: "....#/....#/....#/....#/#...#/#...#/.###.",
  K: "#...#/#..#./#.#../##.../#.#../#..#./#...#",
  L: "#..../#..../#..../#..../#..../#..../#####",
  M: "#...#/##.##/#.#.#/#.#.#/#...#/#...#/#...#",
  N: "#...#/##..#/#.#.#/#..##/#...#/#...#/#...#",
  O: ".###./#...#/#...#/#...#/#...#/#...#/.###.",
  P: "####./#...#/#...#/####./#..../#..../#....",
  Q: ".###./#...#/#...#/#...#/#.#.#/#..#./.##.#",
  R: "####./#...#/#...#/####./#.#../#..#./#...#",
  S: ".####/#..../#..../.###./....#/....#/####.",
  T: "#####/..#../..#../..#../..#../..#../..#..",
  U: "#...#/#...#/#...#/#...#/#...#/#...#/.###.",
  V: "#...#/#...#/#...#/#...#/#...#/.#.#./..#..",
  W: "#...#/#...#/#...#/#.#.#/#.#.#/##.##/#...#",
  X: "#...#/#...#/.#.#./..#../.#.#./#...#/#...#",
  Y: "#...#/#...#/.#.#./..#../..#../..#../..#..",
  Z: "#####/....#/...#./..#../.#.../#..../#####",
  ".": "...../...../...../...../...../.##../.##..",
  ",": "...../...../...../...../.##../.##../.#...",
  "-": "...../...../...../#####/...../...../.....",
  "+": "...../..#../..#../#####/..#../..#../.....",
  "%": "##..#/##..#/...#./..#../.#.../#..##/#..##",
  ":": "...../.##../.##../...../.##../.##../.....",
  "/": "....#/...#./...#./..#../.#.../.#.../#....",
  "(": "..#../.#.../#..../#..../#..../.#.../..#..",
  ")": "..#../...#./....#/....#/....#/...#./..#..",
  "?": ".###./#...#/....#/...#./..#../...../..#..",
  "!": "..#../..#../..#../..#../..#../...../..#..",
  "'": "..#../..#../...../...../...../...../.....",
});

/**
 * The 5x7 font, scaled by whole pixels.
 *
 * WHOLE PIXELS ONLY. A bitmap glyph resampled to a fractional size grows ragged
 * stems and uneven counters - the artefact that makes a rendered caption look
 * broken rather than small.
 */
export class BitmapFont {
  static readonly GLYPH_WIDTH = 5;
  static readonly GLYPH_HEIGHT = 7;
  /** One blank column between glyphs, at the same scale as the glyph. */
  static readonly TRACKING = 1;

  private readonly rows = new Map<string, number[]>();

  constructor() {
    for (const [ch, art] of Object.entries(GLYPH_ART)) {
      const lines = art.split("/");
      if (
        lines.length !== BitmapFont.GLYPH_HEIGHT ||
        lines.some((l) => l.length !== BitmapFont.GLYPH_WIDTH)
      ) {
        throw new Error(`glyph ${ch} is not 5x7`);
      }
      this.rows.set(
        ch,
        lines.map((l) =>
          [...l].reduce(
            (bits, c, i) => (c === "#" ? bits | (1 << (BitmapFont.GLYPH_WIDTH - 1 - i)) : bits),
            0,
          ),
        ),
      );
    }
  }

  /**
   * Unknown characters fall back to the one for `?`, never to nothing.
   *
   * A missing glyph that draws blank turns a caption in a language this font
   * does not cover into an empty box, and nobody reports an empty box.
   */
  glyph(ch: string): number[] {
    return this.rows.get(ch.toUpperCase()) ?? this.rows.get("?")!;
  }

  measure(text: string, scale: number): { width: number; height: number } {
    if (!text) return { width: 0, height: 0 };
    const advance = (BitmapFont.GLYPH_WIDTH + BitmapFont.TRACKING) * scale;
    return {
      width: advance * text.length - BitmapFont.TRACKING * scale,
      height: BitmapFont.GLYPH_HEIGHT * scale,
    };
  }

  /**
   * At least 1, so text never vanishes at a small size - it goes chunky
   * instead, which is legible and obviously wrong rather than silently absent.
   */
  scaleForHeight(pixels: number): number {
    return Math.max(1, Math.round(pixels / BitmapFont.GLYPH_HEIGHT));
  }

  static readonly DEFAULT = new BitmapFont();
}

// ─────────────────────────────────────────────────────────────────────────────
// The canvas

/** Draws onto a `PixelBuffer`. */
export class RasterCanvas {
  constructor(
    readonly buffer: PixelBuffer,
    readonly font: BitmapFont = BitmapFont.DEFAULT,
  ) {}

  static create(size: RenderSize, background: Rgba32 = TRANSPARENT): RasterCanvas {
    return new RasterCanvas(new PixelBuffer(size.width, size.height, background));
  }

  /**
   * CLIPPED here rather than per pixel.
   *
   * Clipping the loop bounds once instead of testing every pixel is the
   * difference between a full-frame fill costing a million branch
   * mispredictions and costing none.
   */
  fillRect(x: number, y: number, width: number, height: number, colour: Rgba32): void {
    const x0 = Math.max(0, x);
    const y0 = Math.max(0, y);
    const x1 = Math.min(this.buffer.width, x + width);
    const y1 = Math.min(this.buffer.height, y + height);
    if (x1 <= x0 || y1 <= y0) return;
    for (let py = y0; py < y1; py++) {
      for (let px = x0; px < x1; px++) {
        if (colour.a === 255) this.buffer.set(px, py, colour);
        else this.buffer.blend(px, py, colour);
      }
    }
  }

  /**
   * Bresenham. Integer only - no accumulated float error, so a long axis line
   * stays straight to its last pixel.
   */
  drawLine(x0: number, y0: number, x1: number, y1: number, colour: Rgba32): void {
    let x = x0;
    let y = y0;
    const dx = Math.abs(x1 - x0);
    const dy = -Math.abs(y1 - y0);
    const sx = x0 < x1 ? 1 : -1;
    const sy = y0 < y1 ? 1 : -1;
    let err = dx + dy;
    for (;;) {
      this.buffer.blend(x, y, colour);
      if (x === x1 && y === y1) return;
      const e2 = 2 * err;
      if (e2 >= dy) {
        err += dy;
        x += sx;
      }
      if (e2 <= dx) {
        err += dx;
        y += sy;
      }
    }
  }

  /**
   * `x`, `y` is the TOP-LEFT of the run, adjusted for alignment. Returns the
   * measured size so a caller can draw a plate behind it.
   */
  drawText(
    text: string,
    x: number,
    y: number,
    scale: number,
    colour: Rgba32,
    align: TextAlign = TextAlign.Left,
  ): { width: number; height: number } {
    const measured = this.font.measure(text, scale);
    let pen = x;
    if (align === TextAlign.Center) pen -= Math.floor(measured.width / 2);
    else if (align === TextAlign.Right) pen -= measured.width;
    const advance = (BitmapFont.GLYPH_WIDTH + BitmapFont.TRACKING) * scale;
    for (const ch of text) {
      const rows = this.font.glyph(ch);
      for (let ry = 0; ry < rows.length; ry++) {
        const bits = rows[ry];
        if (bits === 0) continue;
        for (let rx = 0; rx < BitmapFont.GLYPH_WIDTH; rx++) {
          if (bits & (1 << (BitmapFont.GLYPH_WIDTH - 1 - rx))) {
            this.fillRect(pen + rx * scale, y + ry * scale, scale, scale, colour);
          }
        }
      }
      pen += advance;
    }
    return measured;
  }

  /**
   * Nearest-neighbour, sampled from the DESTINATION.
   *
   * Destination-driven so every output pixel is written exactly once -
   * source-driven scaling leaves unwritten gaps when scaling up, which show as
   * a grid of holes.
   */
  drawImage(
    source: PixelBuffer,
    rect: { x: number; y: number; w: number; h: number },
    fit: ContentFit = ContentFit.Contain,
    opacity = 1,
  ): void {
    const { x: dx, y: dy, w: dw, h: dh } = rect;
    if (dw <= 0 || dh <= 0 || opacity <= 0) return;

    const sw = source.width;
    const sh = source.height;
    let ox = dx;
    let oy = dy;
    let tw = dw;
    let th = dh;
    if (fit === ContentFit.None) {
      tw = sw;
      th = sh;
    } else if (fit !== ContentFit.Stretch) {
      const s =
        fit === ContentFit.Contain ? Math.min(dw / sw, dh / sh) : Math.max(dw / sw, dh / sh);
      tw = Math.max(1, Math.round(sw * s));
      th = Math.max(1, Math.round(sh * s));
      ox = dx + Math.floor((dw - tw) / 2);
      oy = dy + Math.floor((dh - th) / 2);
    }

    const alpha = opacity < 0 ? 0 : opacity > 1 ? 1 : opacity;
    // Clipped to BOTH the destination rectangle and the buffer: Cover
    // deliberately overflows its box, and without the first clip it would paint
    // over neighbouring layers.
    const xFrom = Math.max(ox, dx, 0);
    const xTo = Math.min(ox + tw, dx + dw, this.buffer.width);
    const yFrom = Math.max(oy, dy, 0);
    const yTo = Math.min(oy + th, dy + dh, this.buffer.height);
    for (let py = yFrom; py < yTo; py++) {
      const syi = Math.min(sh - 1, Math.max(0, Math.floor(((py - oy) * sh) / th)));
      for (let px = xFrom; px < xTo; px++) {
        const sxi = Math.min(sw - 1, Math.max(0, Math.floor(((px - ox) * sw) / tw)));
        const c = source.get(sxi, syi);
        this.buffer.blend(px, py, alpha < 1 ? withAlpha(c, Math.round(c.a * alpha)) : c);
      }
    }
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// PNG

/**
 * The PNG Paeth predictor. `a` left, `b` above, `c` upper-left.
 *
 * THE TIE-BREAK IS ORDERED - left, then above, then upper-left - and it is
 * written as `<=` twice for exactly that reason. Reversing it decodes most
 * images correctly and a few with coloured streaks along diagonal edges, which
 * is a bug that survives a test suite and fails on a photograph.
 */
function paeth(a: number, b: number, c: number): number {
  const p = a + b - c;
  const pa = Math.abs(p - a);
  const pb = Math.abs(p - b);
  const pc = Math.abs(p - c);
  if (pa <= pb && pa <= pc) return a;
  if (pb <= pc) return b;
  return c;
}

/** The CRC-32 table, built once. PNG's polynomial, reflected. */
const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  return table;
})();

function crc32(bytes: Uint8Array): number {
  let c = 0xffffffff;
  for (let i = 0; i < bytes.length; i++) c = CRC_TABLE[(c ^ bytes[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

/**
 * Adler-32, which is what zlib's trailer carries - NOT CRC-32, which is what
 * PNG's chunks carry. Two different checksums in one file, and swapping them
 * produces a stream every decoder rejects with an error about the wrong one.
 */
function adler32(bytes: Uint8Array): number {
  let a = 1;
  let b = 0;
  for (let i = 0; i < bytes.length; i++) {
    a = (a + bytes[i]) % 65521;
    b = (b + a) % 65521;
  }
  return ((b << 16) | a) >>> 0;
}

/**
 * A zlib stream of DEFLATE STORED blocks - valid deflate, no compression.
 *
 * Every PNG decoder accepts it, and it needs no compressor, which is why it is
 * the portable path. LEN and NLEN are LITTLE-endian and NLEN is the ones
 * complement of LEN - a decoder checks that, so getting either wrong is caught
 * immediately rather than producing a subtly wrong image.
 *
 * Blocks cap at 65535 bytes because LEN is 16 bits. A single oversized block is
 * the classic failure here and it truncates the image at 64 KB.
 */
function zlibStored(data: Uint8Array): Uint8Array {
  const MAX = 0xffff;
  const blocks = Math.max(1, Math.ceil(data.length / MAX));
  const out = new Uint8Array(2 + blocks * 5 + data.length + 4);
  let p = 0;
  // 0x78 0x01: deflate, 32K window, no preset dictionary, fastest level. The
  // two bytes must satisfy (CMF<<8 | FLG) % 31 == 0, which this pair does.
  out[p++] = 0x78;
  out[p++] = 0x01;
  for (let i = 0; i < blocks; i++) {
    const start = i * MAX;
    const len = Math.min(MAX, data.length - start);
    out[p++] = i === blocks - 1 ? 1 : 0; // BFINAL on the last, BTYPE 00 stored
    out[p++] = len & 0xff;
    out[p++] = (len >>> 8) & 0xff;
    out[p++] = ~len & 0xff;
    out[p++] = (~len >>> 8) & 0xff;
    out.set(data.subarray(start, start + len), p);
    p += len;
  }
  // Adler-32 is BIG-endian, unlike everything else in the deflate stream.
  const sum = adler32(data);
  out[p++] = (sum >>> 24) & 0xff;
  out[p++] = (sum >>> 16) & 0xff;
  out[p++] = (sum >>> 8) & 0xff;
  out[p++] = sum & 0xff;
  return out.subarray(0, p);
}

/**
 * Inflates a zlib stream of stored blocks.
 *
 * Compressed blocks need a Huffman decoder, which a host's zlib does far better
 * than this would. A file this cannot read is REPORTED, never guessed at - a
 * decoder that returns something plausible for input it did not understand is
 * the worst kind.
 */
function zlibInflateStored(data: Uint8Array): Uint8Array {
  if (data.length < 6) throw new Error("not a zlib stream");
  const out: number[] = [];
  let p = 2; // past CMF/FLG
  for (;;) {
    if (p >= data.length) throw new Error("zlib stream ended inside a block header");
    const header = data[p++];
    const type = (header >>> 1) & 3;
    if (type !== 0) {
      throw new Error(
        "this decoder reads stored deflate blocks only; pass a host inflater for compressed PNGs",
      );
    }
    const len = data[p] | (data[p + 1] << 8);
    const nlen = data[p + 2] | (data[p + 3] << 8);
    p += 4;
    // The complement check is the format's own integrity test. Skipping it is
    // how a misaligned stream reads garbage as a valid block.
    if ((len ^ 0xffff) !== nlen) throw new Error("stored block length is corrupt");
    for (let i = 0; i < len; i++) out.push(data[p + i]);
    p += len;
    if (header & 1) break;
  }
  return new Uint8Array(out);
}

/** PNG in and out. */
export class ImageCodecs {
  static readonly PNG_SIGNATURE = Object.freeze([137, 80, 78, 71, 13, 10, 26, 10]);

  /**
   * Length and CRC are BIG-endian, unlike everything inside the compressed
   * data. Three byte orders in one file, and this is the first two of them.
   */
  static chunk(kind: string, payload: Uint8Array): Uint8Array {
    const out = new Uint8Array(12 + payload.length);
    const view = new DataView(out.buffer);
    view.setUint32(0, payload.length, false);
    for (let i = 0; i < 4; i++) out[4 + i] = kind.charCodeAt(i);
    out.set(payload, 8);
    view.setUint32(8 + payload.length, crc32(out.subarray(4, 8 + payload.length)), false);
    return out;
  }

  /**
   * Every scanline is prefixed with a filter byte - 0 here, meaning stored
   * as-is. Omitting the byte produces a file that is exactly one byte short per
   * row and decodes as a diagonal smear.
   */
  static rawScanlines(buffer: PixelBuffer): Uint8Array {
    const stride = buffer.width * 4;
    const out = new Uint8Array(buffer.height * (stride + 1));
    for (let y = 0; y < buffer.height; y++) {
      out[y * (stride + 1)] = 0;
      out.set(buffer.data.subarray(y * stride, (y + 1) * stride), y * (stride + 1) + 1);
    }
    return out;
  }

  /**
   * Colour type 6 (RGBA), 8 bits, no interlace.
   *
   * `deflate` is optional: given one, the file is smaller; without one it is
   * stored blocks, which every decoder reads.
   */
  static encodePng(buffer: PixelBuffer, deflate?: (raw: Uint8Array) => Uint8Array): Uint8Array {
    const ihdr = new Uint8Array(13);
    const view = new DataView(ihdr.buffer);
    view.setUint32(0, buffer.width, false);
    view.setUint32(4, buffer.height, false);
    ihdr[8] = 8; // bit depth
    ihdr[9] = 6; // colour type: RGBA
    const raw = ImageCodecs.rawScanlines(buffer);
    const parts = [
      new Uint8Array(ImageCodecs.PNG_SIGNATURE),
      ImageCodecs.chunk("IHDR", ihdr),
      ImageCodecs.chunk("IDAT", deflate ? deflate(raw) : zlibStored(raw)),
      ImageCodecs.chunk("IEND", new Uint8Array(0)),
    ];
    const total = parts.reduce((n, p) => n + p.length, 0);
    const out = new Uint8Array(total);
    let at = 0;
    for (const p of parts) {
      out.set(p, at);
      at += p.length;
    }
    return out;
  }

  /**
   * Undoes the five filters and returns RGBA.
   *
   * Grey and palette images are widened to RGBA here so nothing downstream has
   * to know about colour types.
   */
  static decodePng(data: Uint8Array, inflate?: (z: Uint8Array) => Uint8Array): PixelBuffer {
    for (let i = 0; i < 8; i++) {
      if (data[i] !== ImageCodecs.PNG_SIGNATURE[i]) {
        throw new Error("not a PNG: the signature does not match");
      }
    }
    const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
    let p = 8;
    let width = 0;
    let height = 0;
    let colourType = 0;
    // Typed against `ArrayBufferLike`, not the default `ArrayBuffer`: these are
    // assigned SUBARRAYS of the file, which share its backing store, and the
    // narrower default made a correct assignment fail to type-check.
    let palette: Uint8Array<ArrayBufferLike> = new Uint8Array(0);
    let trns: Uint8Array<ArrayBufferLike> = new Uint8Array(0);
    const idat: Uint8Array[] = [];
    while (p + 8 <= data.length) {
      const length = view.getUint32(p, false);
      const kind = String.fromCharCode(data[p + 4], data[p + 5], data[p + 6], data[p + 7]);
      const payload = data.subarray(p + 8, p + 8 + length);
      p += 12 + length;
      if (kind === "IHDR") {
        // Read through a view on the PAYLOAD, not on the whole file at a
        // computed offset. An offset arithmetic slip here reads the wrong four
        // bytes as a dimension, and a PNG with a plausible-but-wrong width
        // decodes as a diagonal smear rather than failing.
        const h = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
        width = h.getUint32(0, false);
        height = h.getUint32(4, false);
        if (payload[8] !== 8) throw new Error(`only 8-bit PNG is supported, not ${payload[8]}-bit`);
        colourType = payload[9];
        if (payload[12] !== 0) throw new Error("interlaced PNG is not supported");
      } else if (kind === "PLTE") palette = payload;
      else if (kind === "tRNS") trns = payload;
      // CONCATENATED before inflating. IDAT may be split at any byte, including
      // mid-symbol, so inflating each chunk on its own fails on exactly the
      // large images that need splitting.
      else if (kind === "IDAT") idat.push(payload);
      else if (kind === "IEND") break;
    }
    if (width === 0 || height === 0) throw new Error("PNG has no IHDR");

    const channels = { 0: 1, 2: 3, 3: 1, 4: 2, 6: 4 }[colourType as 0 | 2 | 3 | 4 | 6];
    if (channels === undefined) throw new Error(`unsupported PNG colour type ${colourType}`);

    const joinedLength = idat.reduce((n, c) => n + c.length, 0);
    const joined = new Uint8Array(joinedLength);
    let at = 0;
    for (const c of idat) {
      joined.set(c, at);
      at += c.length;
    }
    const raw = inflate ? inflate(joined) : zlibInflateStored(joined);

    const stride = width * channels;
    const out = new PixelBuffer(width, height);
    let previous = new Uint8Array(stride);
    let pos = 0;
    for (let y = 0; y < height; y++) {
      const f = raw[pos];
      const line = raw.slice(pos + 1, pos + 1 + stride);
      pos += 1 + stride;
      if (f === 1) {
        for (let i = channels; i < stride; i++) line[i] = (line[i] + line[i - channels]) & 0xff;
      } else if (f === 2) {
        for (let i = 0; i < stride; i++) line[i] = (line[i] + previous[i]) & 0xff;
      } else if (f === 3) {
        for (let i = 0; i < stride; i++) {
          const left = i >= channels ? line[i - channels] : 0;
          // The average is FLOORED before adding - rounding it up drifts a
          // level per row and the image ends visibly lighter at the bottom.
          line[i] = (line[i] + ((left + previous[i]) >> 1)) & 0xff;
        }
      } else if (f === 4) {
        for (let i = 0; i < stride; i++) {
          const left = i >= channels ? line[i - channels] : 0;
          const upperLeft = i >= channels ? previous[i - channels] : 0;
          line[i] = (line[i] + paeth(left, previous[i], upperLeft)) & 0xff;
        }
      } else if (f !== 0) {
        throw new Error(`unknown PNG filter ${f}`);
      }

      const base = y * width * 4;
      for (let x = 0; x < width; x++) {
        const s = x * channels;
        const d = base + x * 4;
        if (colourType === 6) {
          out.data.set(line.subarray(s, s + 4), d);
        } else if (colourType === 2) {
          out.data.set(line.subarray(s, s + 3), d);
          out.data[d + 3] = 255;
        } else if (colourType === 0) {
          const v = line[s];
          out.data[d] = v;
          out.data[d + 1] = v;
          out.data[d + 2] = v;
          out.data[d + 3] = 255;
        } else if (colourType === 4) {
          const v = line[s];
          out.data[d] = v;
          out.data[d + 1] = v;
          out.data[d + 2] = v;
          out.data[d + 3] = line[s + 1];
        } else {
          const idx = line[s];
          out.data[d] = palette[idx * 3] ?? 0;
          out.data[d + 1] = palette[idx * 3 + 1] ?? 0;
          out.data[d + 2] = palette[idx * 3 + 2] ?? 0;
          out.data[d + 3] = idx < trns.length ? trns[idx] : 255;
        }
      }
      previous = line;
    }
    return out;
  }
}

/** Turns encoded bytes into pixels. */
export interface ImageDecoder {
  decode(data: Uint8Array): PixelBuffer;
  canDecode(data: Uint8Array): boolean;
}

/**
 * PNG only.
 *
 * JPEG is deliberately absent rather than half-written: a decoder that produces
 * something for a JPEG but not the right something is worse than one that says
 * it cannot.
 */
export class ManagedImageDecoder implements ImageDecoder {
  constructor(private readonly inflate?: (z: Uint8Array) => Uint8Array) {}

  canDecode(data: Uint8Array): boolean {
    return ImageCodecs.PNG_SIGNATURE.every((b, i) => data[i] === b);
  }

  decode(data: Uint8Array): PixelBuffer {
    if (!this.canDecode(data)) throw new Error("this decoder handles PNG only");
    return ImageCodecs.decodePng(data, this.inflate);
  }
}

/**
 * APNG: a PNG whose first frame is a valid still.
 *
 * That ORDER is the entire trick. A viewer that knows nothing about APNG shows
 * the IDAT and stops, so the file degrades to a still image everywhere rather
 * than failing everywhere - which is why this is the animation format for a
 * host that cannot ship a video encoder.
 */
export class AnimatedPngEncoder {
  private readonly frames: { buffer: PixelBuffer; delayMs: number }[] = [];

  /** Zero means forever, per the spec. Not a missing value. */
  constructor(
    private readonly loops = 0,
    private readonly deflate?: (raw: Uint8Array) => Uint8Array,
  ) {}

  get frameCount(): number {
    return this.frames.length;
  }

  addFrame(buffer: PixelBuffer, delayMs = 100): void {
    const first = this.frames[0]?.buffer;
    if (first && (buffer.width !== first.width || buffer.height !== first.height)) {
      throw new Error("every APNG frame must be the same size");
    }
    this.frames.push({ buffer, delayMs: Math.max(1, delayMs) });
  }

  /**
   * The sequence number spans fcTL AND fdAT and must increase by one across
   * both. Numbering them separately produces a file every decoder rejects, and
   * the error it reports names the chunk rather than the counter.
   */
  encode(): Uint8Array {
    if (this.frames.length === 0) throw new Error("an APNG needs at least one frame");
    const first = this.frames[0].buffer;

    const ihdr = new Uint8Array(13);
    const ihdrView = new DataView(ihdr.buffer);
    ihdrView.setUint32(0, first.width, false);
    ihdrView.setUint32(4, first.height, false);
    ihdr[8] = 8;
    ihdr[9] = 6;

    const actl = new Uint8Array(8);
    const actlView = new DataView(actl.buffer);
    actlView.setUint32(0, this.frames.length, false);
    actlView.setUint32(4, this.loops, false);

    let seq = 0;
    const fctl = (delayMs: number): Uint8Array => {
      const payload = new Uint8Array(26);
      const v = new DataView(payload.buffer);
      v.setUint32(0, seq++, false);
      v.setUint32(4, first.width, false);
      v.setUint32(8, first.height, false);
      // Delay is a RATIONAL, numerator over denominator, not milliseconds.
      v.setUint16(20, delayMs, false);
      v.setUint16(22, 1000, false);
      return ImageCodecs.chunk("fcTL", payload);
    };
    const idatOf = (b: PixelBuffer): Uint8Array => {
      const raw = ImageCodecs.rawScanlines(b);
      return this.deflate ? this.deflate(raw) : zlibStored(raw);
    };

    const parts: Uint8Array[] = [
      new Uint8Array(ImageCodecs.PNG_SIGNATURE),
      ImageCodecs.chunk("IHDR", ihdr),
      ImageCodecs.chunk("acTL", actl),
      fctl(this.frames[0].delayMs),
      ImageCodecs.chunk("IDAT", idatOf(first)),
    ];
    for (const { buffer, delayMs } of this.frames.slice(1)) {
      parts.push(fctl(delayMs));
      const body = idatOf(buffer);
      const fdat = new Uint8Array(4 + body.length);
      new DataView(fdat.buffer).setUint32(0, seq++, false);
      fdat.set(body, 4);
      parts.push(ImageCodecs.chunk("fdAT", fdat));
    }
    parts.push(ImageCodecs.chunk("IEND", new Uint8Array(0)));

    const total = parts.reduce((n, p) => n + p.length, 0);
    const out = new Uint8Array(total);
    let at = 0;
    for (const p of parts) {
      out.set(p, at);
      at += p.length;
    }
    return out;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// The renderer

/** How a clip is encoded. */
export interface ClipEncodeOptions {
  readonly framesPerSecond: number;
  readonly bitrateKbps: number;
  /**
   * The container to try. A host with no encoder ignores it and the renderer
   * falls back to an APNG.
   */
  readonly container: string;
}

export const clipEncodeOptions = (framesPerSecond = 30): ClipEncodeOptions =>
  Object.freeze({ framesPerSecond, bitrateKbps: 2500, container: "mp4" });

/** The result of encoding. */
export interface EncodedClip {
  readonly bytes: Uint8Array;
  readonly mediaType: string;
  readonly width: number;
  readonly height: number;
  readonly frameCount: number;
  /**
   * True when this came out as an animated PNG because no video encoder was
   * available. Carried so a caller can tell a person why the file is large,
   * rather than leaving them to wonder.
   */
  readonly fellBackToApng: boolean;
}

/** Encodes frames into a clip. */
export interface VideoEncoder {
  readonly isAvailable: boolean;
  encode(frames: readonly PixelBuffer[], options: ClipEncodeOptions): EncodedClip;
}

/**
 * Encodes nothing and says so.
 *
 * The default. It reports unavailable rather than throwing, so the renderer
 * takes the APNG path instead of failing.
 */
export class NullVideoEncoder implements VideoEncoder {
  readonly isAvailable = false;
  encode(): EncodedClip {
    throw new Error("no video encoder on this host");
  }
}

/** Renders HTML into pixels, when a host has an engine. */
export interface HtmlFrameProvider {
  readonly isAvailable: boolean;
  render(html: string, css?: string): PixelBuffer;
}

/** Renders nothing. */
export class NullHtmlFrameProvider implements HtmlFrameProvider {
  readonly isAvailable = false;
  render(): PixelBuffer {
    throw new Error("no HTML frame provider on this host");
  }
}

/** Turns a spec into pixels or a clip. */
export interface MediaRenderer {
  renderStill(spec: MediaSpec): PixelBuffer;
  renderClip(spec: MediaSpec, options?: ClipEncodeOptions): EncodedClip;
}

/**
 * Renders a flat background and nothing else.
 *
 * Not a throw: a build with no renderer configured should produce a plain card
 * rather than a stack trace where a picture was expected.
 */
export class NullMediaRenderer implements MediaRenderer {
  renderStill(spec: MediaSpec): PixelBuffer {
    return new PixelBuffer(spec.size.width, spec.size.height, spec.background);
  }
  renderClip(spec: MediaSpec): EncodedClip {
    const buffer = this.renderStill(spec);
    return Object.freeze({
      bytes: ImageCodecs.encodePng(buffer),
      mediaType: "image/png",
      width: buffer.width,
      height: buffer.height,
      frameCount: 1,
      fellBackToApng: true,
    });
  }
}

/** The default renderer: pure TypeScript, no native dependency. */
export class ManagedMediaRenderer implements MediaRenderer {
  constructor(
    private readonly decoder: ImageDecoder = new ManagedImageDecoder(),
    private readonly encoder: VideoEncoder = new NullVideoEncoder(),
    private readonly html: HtmlFrameProvider = new NullHtmlFrameProvider(),
    private readonly font: BitmapFont = BitmapFont.DEFAULT,
    private readonly deflate?: (raw: Uint8Array) => Uint8Array,
  ) {}

  renderFrame(spec: MediaSpec, t = 0): PixelBuffer {
    const canvas = RasterCanvas.create(spec.size, spec.background);
    for (const layer of spec.layers) {
      let source: PixelBuffer;
      try {
        source = layer.source.decode(this.decoder);
      } catch {
        // A layer that cannot be decoded is SKIPPED, not fatal. One broken
        // image should cost one layer, not the whole picture.
        continue;
      }
      canvas.drawImage(
        source,
        scaleRect(layerRectAt(layer, t), spec.size.width, spec.size.height),
        layer.fit,
        layer.opacity,
      );
    }

    for (const overlay of spec.overlays) {
      const scale = this.font.scaleForHeight(overlay.size * spec.size.height);
      const { width, height } = this.font.measure(overlay.text, scale);
      const x = overlay.at.x * spec.size.width;
      const y = overlay.at.y * spec.size.height;
      const top = Math.round(y - height / 2);
      if (overlay.background) {
        const pad = Math.round(overlay.padding * spec.size.height);
        let left = Math.round(x);
        if (overlay.align === TextAlign.Center) left -= Math.floor(width / 2);
        else if (overlay.align === TextAlign.Right) left -= width;
        canvas.fillRect(left - pad, top - pad, width + 2 * pad, height + 2 * pad, overlay.background);
      }
      canvas.drawText(overlay.text, Math.round(x), top, scale, overlay.colour, overlay.align);
    }
    return canvas.buffer;
  }

  renderStill(spec: MediaSpec): PixelBuffer {
    return this.renderFrame(spec, 0);
  }

  renderClip(spec: MediaSpec, options?: ClipEncodeOptions): EncodedClip {
    const opts = options ?? clipEncodeOptions(spec.framesPerSecond);
    const count = frameCount(spec);
    // `count - 1` in the denominator so the last frame lands exactly on t=1.0.
    // Dividing by `count` stops one frame short and a motion never reaches its
    // end rectangle.
    const frames: PixelBuffer[] = [];
    for (let i = 0; i < count; i++) {
      frames.push(this.renderFrame(spec, count > 1 ? i / (count - 1) : 0));
    }
    if (this.encoder.isAvailable) return this.encoder.encode(frames, opts);

    const apng = new AnimatedPngEncoder(0, this.deflate);
    const delay = Math.max(1, Math.round(1000 / Math.max(1, opts.framesPerSecond)));
    for (const frame of frames) apng.addFrame(frame, delay);
    return Object.freeze({
      bytes: apng.encode(),
      mediaType: "image/apng",
      width: spec.size.width,
      height: spec.size.height,
      frameCount: count,
      fellBackToApng: true,
    });
  }
}

/** Ready-made specs for the things people actually ask for. */
export class MediaTemplates {
  /**
   * Wraps by MEASURING, not by character count.
   *
   * A fixed character count wraps a line of capitals off the edge and leaves a
   * line of lowercase half empty, because what fits is ink, not letters.
   */
  static quoteCard(
    text: string,
    size: RenderSize = SQUARE,
    background: Rgba32 = colourFromHex("#2c3e50"),
    ink: Rgba32 = WHITE,
  ): MediaSpec {
    const font = BitmapFont.DEFAULT;
    const scale = font.scaleForHeight(0.07 * size.height);
    const maxWidth = size.width * 0.86;
    const lines: string[] = [];
    let current = "";
    for (const word of text.split(/\s+/).filter(Boolean)) {
      const candidate = current ? `${current} ${word}` : word;
      if (current && font.measure(candidate, scale).width > maxWidth) {
        lines.push(current);
        current = word;
      } else current = candidate;
    }
    if (current) lines.push(current);

    const step = 0.1;
    const top = 0.5 - (step * (lines.length - 1)) / 2;
    return mediaSpec({
      size,
      background,
      overlays: lines.map((line, i) =>
        textOverlay(line, normVec(0.5, top + i * step), TextAlign.Center, 0.07, ink),
      ),
    });
  }

  /**
   * The plate under the title is NOT decoration - white text over an unknown
   * photograph is unreadable about half the time.
   */
  static titleOverImage(image: ImageSource, title: string, size: RenderSize = STORY): MediaSpec {
    return mediaSpec({
      size,
      background: BLACK,
      layers: [imageLayer(image, FULL_RECT, ContentFit.Cover)],
      overlays: [
        textOverlay(title, normVec(0.5, 0.86), TextAlign.Center, 0.055, WHITE, withAlpha(BLACK, 160), 0.02),
      ],
    });
  }

  /**
   * A Ken Burns move. Both rectangles keep the frame COVERED, so the motion
   * never uncovers a background edge partway through.
   */
  static slowZoom(image: ImageSource, seconds = 4, size: RenderSize = LANDSCAPE): MediaSpec {
    return mediaSpec({
      size,
      background: BLACK,
      durationSeconds: seconds,
      layers: [
        imageLayer(
          image,
          FULL_RECT,
          ContentFit.Cover,
          1,
          motion(normRect(0, 0, 1, 1), normRect(-0.06, -0.06, 1.12, 1.12), EasingKind.EaseInOut),
        ),
      ],
    });
  }
}

/** What the companion knows about making pictures here. */
export interface MediaDomainContext {
  readonly canEncodeVideo: boolean;
  readonly canRenderHtml: boolean;
  readonly maxRenderPixels: number;
}

export const mediaDomainContext = (
  partial: Partial<MediaDomainContext> = {},
): MediaDomainContext =>
  Object.freeze({
    canEncodeVideo: partial.canEncodeVideo ?? false,
    canRenderHtml: partial.canRenderHtml ?? false,
    maxRenderPixels: partial.maxRenderPixels ?? 1920 * 1920,
  });

/** Puts the renderer behind a plain request. */
export class MediaCompanionAdapter {
  constructor(
    private readonly renderer: MediaRenderer = new ManagedMediaRenderer(),
    readonly context: MediaDomainContext = mediaDomainContext(),
  ) {}

  describe(): string {
    const parts = ["still images and animated PNG"];
    if (this.context.canEncodeVideo) parts.push("video");
    if (this.context.canRenderHtml) parts.push("HTML layouts");
    return `this host can make ${parts.join(", ")}`;
  }

  makeQuoteCard(text: string): Uint8Array {
    return ImageCodecs.encodePng(this.renderer.renderStill(MediaTemplates.quoteCard(text)));
  }

  makeClip(image: ImageSource, seconds = 4): EncodedClip {
    return this.renderer.renderClip(MediaTemplates.slowZoom(image, seconds));
  }
}

// The C# spellings, kept so the two trees line up.
export type IImageDecoder = ImageDecoder;
export type IMediaRenderer = MediaRenderer;
export type IVideoEncoder = VideoEncoder;
export type IHtmlFrameProvider = HtmlFrameProvider;
