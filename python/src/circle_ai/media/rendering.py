"""Rendering: a raster canvas, a bitmap font, and PNG in both directions.

WHY THIS IS HAND-WRITTEN. Every image library worth having is either GPL, a
native blob per architecture, or both, and an app that must ship as ONE APK
cannot carry four architectures of a decoder to draw a chart. So the pixels are
ours: composite, filter, deflate, and back again.

THE THREE THINGS THAT ARE ALWAYS GOT WRONG, all of them verified by running
this rather than by reading it:

  * Compositing in STRAIGHT alpha needs the divide. `out = sc*sa + dc*da*(1-sa)`
    without dividing by the output alpha darkens every edge drawn onto
    transparency - the classic black halo.

  * PNG is big-endian in its framing and DEFLATE is little-endian inside it, and
    within DEFLATE the block fields are LSB-first while the Huffman codes are
    MSB-first. Three byte orders in one file.

  * The Paeth predictor's tie-break is ordered: left, then above, then
    upper-left. A different order decodes most images correctly and a few with
    coloured streaks, which is the worst way for a bug to behave.
"""

from __future__ import annotations

import struct
import zlib
from abc import ABC, abstractmethod
from dataclasses import dataclass, field, replace
from enum import Enum
from typing import Sequence


# ─────────────────────────────────────────────────────────────────────────────
# Geometry and colour


@dataclass(frozen=True)
class Rgba32:
    """A colour, STRAIGHT (not premultiplied).

    Straight because it is what an author types and what a PNG stores;
    premultiplying at the edge of the compositor and dividing back out on the
    way is where the rounding lives, and it lives there once.
    """

    r: int = 0
    g: int = 0
    b: int = 0
    a: int = 255

    def __post_init__(self) -> None:
        for name in ("r", "g", "b", "a"):
            v = getattr(self, name)
            if not 0 <= v <= 255:
                raise ValueError(f"{name}={v} is outside 0..255")

    @staticmethod
    def from_hex(text: str) -> "Rgba32":
        """Accepts #rgb, #rgba, #rrggbb and #rrggbbaa.

        Alpha LAST, matching CSS. A reader that assumes #aarrggbb gets a fully
        opaque colour with the wrong red, which looks like a palette mistake
        rather than a parsing one.

        The four-digit shorthand is here because CSS has it, and a palette
        pasted from a stylesheet is exactly how these arrive.
        """
        s = text.lstrip("#").strip()
        if len(s) in (3, 4):
            s = "".join(c * 2 for c in s)
        if len(s) == 6:
            s += "ff"
        if len(s) != 8:
            raise ValueError(
                f"{text!r} is not #rgb, #rgba, #rrggbb or #rrggbbaa")
        return Rgba32(*(int(s[i:i + 2], 16) for i in (0, 2, 4, 6)))

    def to_hex(self) -> str:
        return f"#{self.r:02x}{self.g:02x}{self.b:02x}{self.a:02x}"

    def with_alpha(self, a: int) -> "Rgba32":
        return replace(self, a=max(0, min(255, a)))


Rgba32.TRANSPARENT = Rgba32(0, 0, 0, 0)
Rgba32.BLACK = Rgba32(0, 0, 0, 255)
Rgba32.WHITE = Rgba32(255, 255, 255, 255)


@dataclass(frozen=True)
class NormVec:
    """A point in 0..1 of the frame.

    NORMALISED so a spec renders at any size. An overlay placed at pixel 240 is
    centred on one phone and off the edge of another.
    """

    x: float = 0.0
    y: float = 0.0

    def scaled(self, width: int, height: int) -> tuple[float, float]:
        return self.x * width, self.y * height


@dataclass(frozen=True)
class NormRect:
    """A rectangle in 0..1 of the frame."""

    x: float = 0.0
    y: float = 0.0
    width: float = 1.0
    height: float = 1.0

    @property
    def right(self) -> float:
        return self.x + self.width

    @property
    def bottom(self) -> float:
        return self.y + self.height

    def scaled(self, width: int, height: int) -> tuple[int, int, int, int]:
        """Rounds the EDGES, not the size.

        Rounding x and width separately lets two rectangles that share an edge
        end up a pixel apart, which shows as a hairline seam.
        """
        x0 = int(round(self.x * width))
        y0 = int(round(self.y * height))
        x1 = int(round(self.right * width))
        y1 = int(round(self.bottom * height))
        return x0, y0, x1 - x0, y1 - y0


NormRect.FULL = NormRect()


@dataclass(frozen=True)
class RenderSize:
    """Output pixel size."""

    width: int = 1080
    height: int = 1080

    def __post_init__(self) -> None:
        if self.width <= 0 or self.height <= 0:
            raise ValueError("a render size must be positive in both directions")

    @property
    def aspect(self) -> float:
        return self.width / self.height


RenderSize.SQUARE = RenderSize(1080, 1080)
RenderSize.STORY = RenderSize(1080, 1920)
RenderSize.LANDSCAPE = RenderSize(1920, 1080)


class ContentFit(Enum):
    """How an image fills a box it does not match."""

    #: Whole image visible, box may show through. The safe default: nothing is
    #: lost, and what a caller did not intend is empty space rather than a
    #: cropped face.
    CONTAIN = "contain"
    COVER = "cover"
    STRETCH = "stretch"
    NONE = "none"


class TextAlign(Enum):
    LEFT = "left"
    CENTER = "center"
    RIGHT = "right"


class EasingKind(Enum):
    """How a motion interpolates."""

    LINEAR = "linear"
    EASE_IN = "ease-in"
    EASE_OUT = "ease-out"
    EASE_IN_OUT = "ease-in-out"


def ease(kind: EasingKind, t: float) -> float:
    """CLAMPED, because a caller that computes t from a frame index off by one
    would otherwise extrapolate - an ease-out past 1.0 overshoots and the layer
    jumps back."""
    t = max(0.0, min(1.0, t))
    if kind is EasingKind.EASE_IN:
        return t * t
    if kind is EasingKind.EASE_OUT:
        return 1 - (1 - t) * (1 - t)
    if kind is EasingKind.EASE_IN_OUT:
        return 2 * t * t if t < 0.5 else 1 - 2 * (1 - t) * (1 - t)
    return t


@dataclass(frozen=True)
class Motion:
    """A layer moving between two rectangles over the clip."""

    start: NormRect = NormRect()
    end: NormRect = NormRect()
    easing: EasingKind = EasingKind.LINEAR

    def at(self, t: float) -> NormRect:
        e = ease(self.easing, t)
        return NormRect(
            self.start.x + (self.end.x - self.start.x) * e,
            self.start.y + (self.end.y - self.start.y) * e,
            self.start.width + (self.end.width - self.start.width) * e,
            self.start.height + (self.end.height - self.start.height) * e,
        )


# ─────────────────────────────────────────────────────────────────────────────
# Pixels


class PixelBuffer:
    """RGBA8888, top-down, no padding.

    A `bytearray` rather than a list of tuples: a 1080x1080 frame is 1.1 million
    pixels, and a tuple per pixel is about forty times the memory and enough
    allocation to make a phone stutter mid-render.
    """

    __slots__ = ("width", "height", "data")

    def __init__(self, width: int, height: int, fill: "Rgba32 | None" = None) -> None:
        if width <= 0 or height <= 0:
            raise ValueError("a pixel buffer must be positive in both directions")
        self.width = width
        self.height = height
        if fill is None or (fill.r, fill.g, fill.b, fill.a) == (0, 0, 0, 0):
            self.data = bytearray(width * height * 4)
        else:
            self.data = bytearray(
                bytes((fill.r, fill.g, fill.b, fill.a)) * (width * height))

    def clone(self) -> "PixelBuffer":
        out = PixelBuffer(self.width, self.height)
        out.data[:] = self.data
        return out

    def get(self, x: int, y: int) -> Rgba32:
        i = (y * self.width + x) * 4
        return Rgba32(*self.data[i:i + 4])

    def set(self, x: int, y: int, colour: Rgba32) -> None:
        if not (0 <= x < self.width and 0 <= y < self.height):
            return
        i = (y * self.width + x) * 4
        self.data[i:i + 4] = bytes((colour.r, colour.g, colour.b, colour.a))

    def blend(self, x: int, y: int, colour: Rgba32) -> None:
        """Source-over in STRAIGHT alpha.

        The divide by the output alpha is the whole point. Without it a
        half-transparent white drawn onto transparency comes out mid-grey
        instead of white-at-half-alpha, and every soft edge in the frame gets a
        dark rim.
        """
        if colour.a == 0 or not (0 <= x < self.width and 0 <= y < self.height):
            return
        if colour.a == 255:
            self.set(x, y, colour)
            return
        i = (y * self.width + x) * 4
        dr, dg, db, da = self.data[i:i + 4]
        sa = colour.a / 255.0
        dav = da / 255.0
        out_a = sa + dav * (1 - sa)
        if out_a <= 0:
            self.data[i:i + 4] = b"\x00\x00\x00\x00"
            return

        def mix(s: int, d: int) -> int:
            return max(0, min(255, int(round((s * sa + d * dav * (1 - sa)) / out_a))))

        self.data[i:i + 4] = bytes((
            mix(colour.r, dr), mix(colour.g, dg), mix(colour.b, db),
            int(round(out_a * 255)),
        ))


class ImageSource(ABC):
    """Something a layer draws."""

    @abstractmethod
    def decode(self, decoder: "IImageDecoder | None" = None) -> PixelBuffer: ...


@dataclass(frozen=True)
class RawImageSource(ImageSource):
    """Pixels already in hand."""

    buffer: PixelBuffer

    def decode(self, decoder: "IImageDecoder | None" = None) -> PixelBuffer:
        return self.buffer.clone()


@dataclass(frozen=True)
class EncodedImageSource(ImageSource):
    """Bytes of a PNG."""

    data: bytes
    media_type: str = "image/png"

    def decode(self, decoder: "IImageDecoder | None" = None) -> PixelBuffer:
        return (decoder or ManagedImageDecoder()).decode(self.data)


@dataclass(frozen=True)
class HtmlTemplateSource(ImageSource):
    """A layer rendered by a browser engine, when the host has one.

    A SOURCE rather than a renderer, so a spec that names one still renders on a
    device with no browser - the layer is skipped and the rest of the frame is
    produced. A missing layer is a worse picture; a failed render is no picture.
    """

    html: str
    css: str = ""
    provider: "IHtmlFrameProvider | None" = None

    def decode(self, decoder: "IImageDecoder | None" = None) -> PixelBuffer:
        if self.provider is None:
            raise RuntimeError("no HTML frame provider on this device")
        return self.provider.render(self.html, self.css)


@dataclass(frozen=True)
class ImageLayer:
    """One image placed in the frame."""

    source: ImageSource
    rect: NormRect = NormRect()
    fit: ContentFit = ContentFit.CONTAIN
    opacity: float = 1.0
    motion: "Motion | None" = None

    def rect_at(self, t: float) -> NormRect:
        return self.motion.at(t) if self.motion else self.rect


@dataclass(frozen=True)
class TextOverlay:
    """Text placed in the frame."""

    text: str
    at: NormVec = field(default_factory=lambda: NormVec(0.5, 0.5))
    align: TextAlign = TextAlign.CENTER
    #: In FRACTIONS OF FRAME HEIGHT, not points. A point size renders the same
    #: caption legibly at 1080 and unreadably at 320.
    size: float = 0.06
    colour: Rgba32 = Rgba32(255, 255, 255, 255)
    #: A backing plate. Not decoration: white text over an unknown photograph is
    #: unreadable about half the time.
    background: "Rgba32 | None" = None
    padding: float = 0.01


@dataclass(frozen=True)
class MediaSpec:
    """A whole frame or clip, declaratively."""

    size: RenderSize = RenderSize()
    background: Rgba32 = Rgba32(0, 0, 0, 255)
    layers: tuple[ImageLayer, ...] = ()
    overlays: tuple[TextOverlay, ...] = ()
    duration_seconds: float = 0.0
    frames_per_second: int = 30

    @property
    def is_still(self) -> bool:
        return self.duration_seconds <= 0

    @property
    def frame_count(self) -> int:
        if self.is_still:
            return 1
        return max(1, int(round(self.duration_seconds * self.frames_per_second)))


# ─────────────────────────────────────────────────────────────────────────────
# The font

#: A 5x7 bitmap font, one line per glyph, rows separated by `/`.
#:
#: Hand-drawn and deliberately small: it exists to label a chart axis and stamp
#: a caption, and it is the only way to put a word on a picture without a font
#: file, a shaper and a licence to check.
_GLYPH_ART: dict[str, str] = {
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
    "A": ".###./#...#/#...#/#####/#...#/#...#/#...#",
    "B": "####./#...#/#...#/####./#...#/#...#/####.",
    "C": ".###./#...#/#..../#..../#..../#...#/.###.",
    "D": "####./#...#/#...#/#...#/#...#/#...#/####.",
    "E": "#####/#..../#..../####./#..../#..../#####",
    "F": "#####/#..../#..../####./#..../#..../#....",
    "G": ".###./#...#/#..../#.###/#...#/#...#/.###.",
    "H": "#...#/#...#/#...#/#####/#...#/#...#/#...#",
    "I": ".###./..#../..#../..#../..#../..#../.###.",
    "J": "....#/....#/....#/....#/#...#/#...#/.###.",
    "K": "#...#/#..#./#.#../##.../#.#../#..#./#...#",
    "L": "#..../#..../#..../#..../#..../#..../#####",
    "M": "#...#/##.##/#.#.#/#.#.#/#...#/#...#/#...#",
    "N": "#...#/##..#/#.#.#/#..##/#...#/#...#/#...#",
    "O": ".###./#...#/#...#/#...#/#...#/#...#/.###.",
    "P": "####./#...#/#...#/####./#..../#..../#....",
    "Q": ".###./#...#/#...#/#...#/#.#.#/#..#./.##.#",
    "R": "####./#...#/#...#/####./#.#../#..#./#...#",
    "S": ".####/#..../#..../.###./....#/....#/####.",
    "T": "#####/..#../..#../..#../..#../..#../..#..",
    "U": "#...#/#...#/#...#/#...#/#...#/#...#/.###.",
    "V": "#...#/#...#/#...#/#...#/#...#/.#.#./..#..",
    "W": "#...#/#...#/#...#/#.#.#/#.#.#/##.##/#...#",
    "X": "#...#/#...#/.#.#./..#../.#.#./#...#/#...#",
    "Y": "#...#/#...#/.#.#./..#../..#../..#../..#..",
    "Z": "#####/....#/...#./..#../.#.../#..../#####",
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
}


class BitmapFont:
    """The 5x7 font, scaled by whole pixels.

    WHOLE PIXELS ONLY. A bitmap glyph resampled to a fractional size grows
    ragged stems and uneven counters - the artefact that makes a rendered
    caption look broken rather than small.
    """

    GLYPH_WIDTH = 5
    GLYPH_HEIGHT = 7
    #: One blank column between glyphs, at the same scale as the glyph.
    TRACKING = 1

    def __init__(self) -> None:
        self._rows: dict[str, tuple[int, ...]] = {}
        for ch, art in _GLYPH_ART.items():
            rows = art.split("/")
            if len(rows) != self.GLYPH_HEIGHT or any(
                len(r) != self.GLYPH_WIDTH for r in rows
            ):
                raise ValueError(
                    f"glyph {ch!r} is not {self.GLYPH_WIDTH}x{self.GLYPH_HEIGHT}")
            self._rows[ch] = tuple(
                sum(1 << (self.GLYPH_WIDTH - 1 - i)
                    for i, c in enumerate(r) if c == "#")
                for r in rows
            )

    def glyph(self, ch: str) -> tuple[int, ...]:
        """Unknown characters fall back to the one for `?`, never to nothing.

        A missing glyph that draws blank turns a caption in a language this font
        does not cover into an empty box, and nobody reports an empty box.
        """
        return self._rows.get(ch.upper()) or self._rows["?"]

    def measure(self, text: str, scale: int) -> tuple[int, int]:
        if not text:
            return 0, 0
        advance = (self.GLYPH_WIDTH + self.TRACKING) * scale
        return advance * len(text) - self.TRACKING * scale, self.GLYPH_HEIGHT * scale

    def scale_for_height(self, pixels: float) -> int:
        """At least 1, so text never vanishes at a small size - it goes chunky
        instead, which is legible and obviously wrong rather than silently
        absent."""
        return max(1, int(round(pixels / self.GLYPH_HEIGHT)))


BitmapFont.DEFAULT = BitmapFont()


# ─────────────────────────────────────────────────────────────────────────────
# The canvas


class RasterCanvas:
    """Draws onto a `PixelBuffer`."""

    def __init__(self, buffer: PixelBuffer, font: "BitmapFont | None" = None) -> None:
        self.buffer = buffer
        self.font = font or BitmapFont.DEFAULT

    @classmethod
    def create(
        cls, size: RenderSize, background: Rgba32 = Rgba32(0, 0, 0, 0)
    ) -> "RasterCanvas":
        return cls(PixelBuffer(size.width, size.height, background))

    def fill_rect(
        self, x: int, y: int, width: int, height: int, colour: Rgba32
    ) -> None:
        """CLIPPED here rather than per pixel.

        Clipping the loop bounds once instead of testing every pixel is the
        difference between a full-frame fill costing a million branch
        mispredictions and costing none.
        """
        x0, y0 = max(0, x), max(0, y)
        x1 = min(self.buffer.width, x + width)
        y1 = min(self.buffer.height, y + height)
        if x1 <= x0 or y1 <= y0:
            return
        if colour.a == 255:
            row = bytes((colour.r, colour.g, colour.b, 255)) * (x1 - x0)
            for py in range(y0, y1):
                i = (py * self.buffer.width + x0) * 4
                self.buffer.data[i:i + len(row)] = row
            return
        for py in range(y0, y1):
            for px in range(x0, x1):
                self.buffer.blend(px, py, colour)

    def draw_line(self, x0: int, y0: int, x1: int, y1: int, colour: Rgba32) -> None:
        """Bresenham. Integer only - no accumulated float error, so a long axis
        line stays straight to its last pixel."""
        dx, dy = abs(x1 - x0), -abs(y1 - y0)
        sx = 1 if x0 < x1 else -1
        sy = 1 if y0 < y1 else -1
        err = dx + dy
        while True:
            self.buffer.blend(x0, y0, colour)
            if x0 == x1 and y0 == y1:
                return
            e2 = 2 * err
            if e2 >= dy:
                err += dy
                x0 += sx
            if e2 <= dx:
                err += dx
                y0 += sy

    def draw_text(
        self, text: str, x: int, y: int, scale: int,
        colour: Rgba32, align: TextAlign = TextAlign.LEFT,
    ) -> tuple[int, int]:
        """`x`, `y` is the TOP-LEFT of the run, adjusted for alignment. Returns
        the measured size so a caller can draw a plate behind it."""
        width, height = self.font.measure(text, scale)
        if align is TextAlign.CENTER:
            x -= width // 2
        elif align is TextAlign.RIGHT:
            x -= width
        pen = x
        advance = (BitmapFont.GLYPH_WIDTH + BitmapFont.TRACKING) * scale
        for ch in text:
            for ry, bits in enumerate(self.font.glyph(ch)):
                if bits == 0:
                    continue
                for rx in range(BitmapFont.GLYPH_WIDTH):
                    if bits & (1 << (BitmapFont.GLYPH_WIDTH - 1 - rx)):
                        self.fill_rect(
                            pen + rx * scale, y + ry * scale, scale, scale, colour)
            pen += advance
        return width, height

    def draw_image(
        self, source: PixelBuffer, rect: tuple[int, int, int, int],
        fit: ContentFit = ContentFit.CONTAIN, opacity: float = 1.0,
    ) -> None:
        """Nearest-neighbour, sampled from the DESTINATION.

        Destination-driven so every output pixel is written exactly once -
        source-driven scaling leaves unwritten gaps when scaling up, which show
        as a grid of holes.
        """
        dx, dy, dw, dh = rect
        if dw <= 0 or dh <= 0 or opacity <= 0:
            return

        sw, sh = source.width, source.height
        if fit is ContentFit.STRETCH:
            ox, oy, tw, th = dx, dy, dw, dh
        elif fit is ContentFit.NONE:
            ox, oy, tw, th = dx, dy, sw, sh
        else:
            s = (min(dw / sw, dh / sh) if fit is ContentFit.CONTAIN
                 else max(dw / sw, dh / sh))
            tw, th = max(1, int(round(sw * s))), max(1, int(round(sh * s)))
            ox, oy = dx + (dw - tw) // 2, dy + (dh - th) // 2

        alpha = max(0.0, min(1.0, opacity))
        # Clipped to BOTH the destination rectangle and the buffer: COVER
        # deliberately overflows its box, and without the first clip it would
        # paint over neighbouring layers.
        x_from, x_to = max(ox, dx, 0), min(ox + tw, dx + dw, self.buffer.width)
        y_from, y_to = max(oy, dy, 0), min(oy + th, dy + dh, self.buffer.height)
        for py in range(y_from, y_to):
            syi = min(sh - 1, max(0, (py - oy) * sh // th))
            for px in range(x_from, x_to):
                sxi = min(sw - 1, max(0, (px - ox) * sw // tw))
                c = source.get(sxi, syi)
                if alpha < 1.0:
                    c = c.with_alpha(int(round(c.a * alpha)))
                self.buffer.blend(px, py, c)


# ─────────────────────────────────────────────────────────────────────────────
# PNG


def _paeth(a: int, b: int, c: int) -> int:
    """The PNG Paeth predictor. `a` left, `b` above, `c` upper-left.

    THE TIE-BREAK IS ORDERED - left, then above, then upper-left - and it is
    written as `<=` twice for exactly that reason. Reversing it decodes most
    images correctly and a few with coloured streaks along diagonal edges, which
    is a bug that survives a test suite and fails on a photograph.
    """
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    if pb <= pc:
        return b
    return c


class ImageCodecs:
    """PNG in and out.

    Static because it holds no state and needs none: encoding is a pure function
    of the pixels.
    """

    PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"

    @staticmethod
    def chunk(kind: bytes, payload: bytes) -> bytes:
        """Length and CRC are BIG-endian, unlike everything inside the
        compressed data. Three byte orders in one file, and this is the first
        two of them."""
        return (
            struct.pack(">I", len(payload)) + kind + payload
            + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)
        )

    @staticmethod
    def raw_scanlines(buffer: PixelBuffer) -> bytes:
        """Every scanline is prefixed with a filter byte - 0 here, meaning
        stored as-is.

        Omitting the byte produces a file that is exactly one byte short per row
        and decodes as a diagonal smear.
        """
        raw = bytearray()
        stride = buffer.width * 4
        for y in range(buffer.height):
            raw.append(0)
            raw += buffer.data[y * stride:(y + 1) * stride]
        return bytes(raw)

    @staticmethod
    def encode_png(buffer: PixelBuffer, compression: int = 6) -> bytes:
        """Colour type 6 (RGBA), 8 bits, no interlace."""
        return (
            ImageCodecs.PNG_SIGNATURE
            + ImageCodecs.chunk(b"IHDR", struct.pack(
                ">IIBBBBB", buffer.width, buffer.height, 8, 6, 0, 0, 0))
            + ImageCodecs.chunk(
                b"IDAT", zlib.compress(ImageCodecs.raw_scanlines(buffer), compression))
            + ImageCodecs.chunk(b"IEND", b"")
        )

    @staticmethod
    def decode_png(data: bytes) -> PixelBuffer:
        """Undoes the five filters and returns RGBA.

        Grey and palette images are widened to RGBA here so nothing downstream
        has to know about colour types.
        """
        if not data.startswith(ImageCodecs.PNG_SIGNATURE):
            raise ValueError("not a PNG: the signature does not match")

        pos = len(ImageCodecs.PNG_SIGNATURE)
        width = height = colour_type = 0
        idat = bytearray()
        palette = b""
        trns = b""
        while pos + 8 <= len(data):
            length = struct.unpack(">I", data[pos:pos + 4])[0]
            kind = data[pos + 4:pos + 8]
            payload = data[pos + 8:pos + 8 + length]
            pos += 12 + length
            if kind == b"IHDR":
                width, height, depth, colour_type, _, _, interlace = struct.unpack(
                    ">IIBBBBB", payload)
                if interlace:
                    raise ValueError("interlaced PNG is not supported")
                if depth != 8:
                    raise ValueError(f"only 8-bit PNG is supported, not {depth}-bit")
            elif kind == b"PLTE":
                palette = payload
            elif kind == b"tRNS":
                trns = payload
            elif kind == b"IDAT":
                # CONCATENATED before inflating. IDAT may be split at any byte,
                # including mid-symbol, so inflating each chunk on its own fails
                # on exactly the large images that need splitting.
                idat += payload
            elif kind == b"IEND":
                break

        if width == 0 or height == 0:
            raise ValueError("PNG has no IHDR")

        channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}.get(colour_type)
        if channels is None:
            raise ValueError(f"unsupported PNG colour type {colour_type}")

        raw = zlib.decompress(bytes(idat))
        stride = width * channels
        out = PixelBuffer(width, height)
        previous = bytearray(stride)
        pos = 0
        for y in range(height):
            f = raw[pos]
            line = bytearray(raw[pos + 1:pos + 1 + stride])
            pos += 1 + stride
            if f == 1:
                for i in range(channels, stride):
                    line[i] = (line[i] + line[i - channels]) & 0xFF
            elif f == 2:
                for i in range(stride):
                    line[i] = (line[i] + previous[i]) & 0xFF
            elif f == 3:
                for i in range(stride):
                    left = line[i - channels] if i >= channels else 0
                    # The average is FLOORED before adding - rounding it up
                    # drifts a level per row and the image ends visibly lighter
                    # at the bottom than the top.
                    line[i] = (line[i] + ((left + previous[i]) >> 1)) & 0xFF
            elif f == 4:
                for i in range(stride):
                    left = line[i - channels] if i >= channels else 0
                    upper_left = previous[i - channels] if i >= channels else 0
                    line[i] = (line[i] + _paeth(left, previous[i], upper_left)) & 0xFF
            elif f != 0:
                raise ValueError(f"unknown PNG filter {f}")

            base = y * width * 4
            for x in range(width):
                s = x * channels
                d = base + x * 4
                if colour_type == 6:
                    out.data[d:d + 4] = line[s:s + 4]
                elif colour_type == 2:
                    out.data[d:d + 3] = line[s:s + 3]
                    out.data[d + 3] = 255
                elif colour_type == 0:
                    v = line[s]
                    out.data[d:d + 4] = bytes((v, v, v, 255))
                elif colour_type == 4:
                    v = line[s]
                    out.data[d:d + 4] = bytes((v, v, v, line[s + 1]))
                else:
                    idx = line[s]
                    p = palette[idx * 3:idx * 3 + 3] or b"\x00\x00\x00"
                    out.data[d:d + 4] = bytes(p) + bytes(
                        (trns[idx] if idx < len(trns) else 255,))
            previous = line
        return out


class IImageDecoder(ABC):
    """Turns encoded bytes into pixels."""

    @abstractmethod
    def decode(self, data: bytes) -> PixelBuffer: ...

    @abstractmethod
    def can_decode(self, data: bytes) -> bool: ...


class ManagedImageDecoder(IImageDecoder):
    """PNG only, in pure Python.

    JPEG is deliberately absent rather than half-written: a decoder that
    produces something for a JPEG but not the right something is worse than one
    that says it cannot.
    """

    def can_decode(self, data: bytes) -> bool:
        return data.startswith(ImageCodecs.PNG_SIGNATURE)

    def decode(self, data: bytes) -> PixelBuffer:
        if not self.can_decode(data):
            raise ValueError("this decoder handles PNG only")
        return ImageCodecs.decode_png(data)


class AnimatedPngEncoder:
    """APNG: a PNG whose first frame is a valid still.

    That ORDER is the entire trick. A viewer that knows nothing about APNG shows
    the IDAT and stops, so the file degrades to a still image everywhere rather
    than failing everywhere - which is why this is the animation format for a
    device that cannot ship a video encoder.
    """

    def __init__(self, loops: int = 0) -> None:
        #: Zero means forever, per the spec. Not a missing value.
        self._loops = max(0, loops)
        self._frames: list[tuple[PixelBuffer, int]] = []

    def add_frame(self, buffer: PixelBuffer, delay_ms: int = 100) -> None:
        if self._frames and (
            buffer.width != self._frames[0][0].width
            or buffer.height != self._frames[0][0].height
        ):
            raise ValueError("every APNG frame must be the same size")
        self._frames.append((buffer, max(1, delay_ms)))

    @property
    def frame_count(self) -> int:
        return len(self._frames)

    def encode(self) -> bytes:
        """The sequence number spans fcTL AND fdAT and must increase by one
        across both.

        Numbering them separately produces a file every decoder rejects, and the
        error it reports names the chunk rather than the counter.
        """
        if not self._frames:
            raise ValueError("an APNG needs at least one frame")

        first, first_delay = self._frames[0]
        out = bytearray(ImageCodecs.PNG_SIGNATURE)
        out += ImageCodecs.chunk(b"IHDR", struct.pack(
            ">IIBBBBB", first.width, first.height, 8, 6, 0, 0, 0))
        out += ImageCodecs.chunk(b"acTL", struct.pack(
            ">II", len(self._frames), self._loops))

        seq = 0

        def fctl(delay_ms: int) -> bytes:
            nonlocal seq
            # Delay is a RATIONAL, numerator over denominator, not milliseconds.
            payload = struct.pack(
                ">IIIIIHHBB", seq, first.width, first.height, 0, 0,
                delay_ms, 1000, 0, 0)
            seq += 1
            return ImageCodecs.chunk(b"fcTL", payload)

        out += fctl(first_delay)
        out += ImageCodecs.chunk(
            b"IDAT", zlib.compress(ImageCodecs.raw_scanlines(first), 6))
        for buffer, delay in self._frames[1:]:
            out += fctl(delay)
            out += ImageCodecs.chunk(
                b"fdAT",
                struct.pack(">I", seq)
                + zlib.compress(ImageCodecs.raw_scanlines(buffer), 6))
            seq += 1
        out += ImageCodecs.chunk(b"IEND", b"")
        return bytes(out)


# ─────────────────────────────────────────────────────────────────────────────
# The renderer


@dataclass(frozen=True)
class ClipEncodeOptions:
    """How a clip is encoded."""

    frames_per_second: int = 30
    bitrate_kbps: int = 2500
    #: The container to try. A host with no encoder ignores it and the renderer
    #: falls back to an APNG.
    container: str = "mp4"


@dataclass(frozen=True)
class EncodedClip:
    """The result of encoding."""

    data: bytes
    media_type: str
    width: int
    height: int
    frame_count: int
    #: True when this came out as an animated PNG because no video encoder was
    #: available. Carried so a caller can tell a person why the file is large,
    #: rather than leaving them to wonder.
    fell_back_to_apng: bool = False


class IVideoEncoder(ABC):
    """Encodes frames into a clip."""

    @property
    @abstractmethod
    def is_available(self) -> bool: ...

    @abstractmethod
    def encode(
        self, frames: Sequence[PixelBuffer], options: ClipEncodeOptions
    ) -> EncodedClip: ...


class NullVideoEncoder(IVideoEncoder):
    """Encodes nothing and says so.

    The default. It reports unavailable rather than raising, so the renderer
    takes the APNG path instead of failing.
    """

    @property
    def is_available(self) -> bool:
        return False

    def encode(
        self, frames: Sequence[PixelBuffer], options: ClipEncodeOptions
    ) -> EncodedClip:
        raise RuntimeError("no video encoder on this device")


class IHtmlFrameProvider(ABC):
    """Renders HTML into pixels, when a host has an engine."""

    @property
    @abstractmethod
    def is_available(self) -> bool: ...

    @abstractmethod
    def render(self, html: str, css: str = "") -> PixelBuffer: ...


class NullHtmlFrameProvider(IHtmlFrameProvider):
    """Renders nothing."""

    @property
    def is_available(self) -> bool:
        return False

    def render(self, html: str, css: str = "") -> PixelBuffer:
        raise RuntimeError("no HTML frame provider on this device")


class IMediaRenderer(ABC):
    """Turns a spec into pixels or a clip."""

    @abstractmethod
    def render_still(self, spec: MediaSpec) -> PixelBuffer: ...

    @abstractmethod
    def render_clip(
        self, spec: MediaSpec, options: "ClipEncodeOptions | None" = None
    ) -> EncodedClip: ...


class NullMediaRenderer(IMediaRenderer):
    """Renders a flat background and nothing else.

    Not a raise: a build with no renderer configured should produce a plain card
    rather than a stack trace where a picture was expected.
    """

    def render_still(self, spec: MediaSpec) -> PixelBuffer:
        return PixelBuffer(spec.size.width, spec.size.height, spec.background)

    def render_clip(
        self, spec: MediaSpec, options: "ClipEncodeOptions | None" = None
    ) -> EncodedClip:
        buffer = self.render_still(spec)
        return EncodedClip(
            ImageCodecs.encode_png(buffer), "image/png",
            buffer.width, buffer.height, 1, True)


class ManagedMediaRenderer(IMediaRenderer):
    """The default renderer: pure Python, no native dependency."""

    def __init__(
        self,
        decoder: "IImageDecoder | None" = None,
        encoder: "IVideoEncoder | None" = None,
        html: "IHtmlFrameProvider | None" = None,
        font: "BitmapFont | None" = None,
    ) -> None:
        self._decoder = decoder or ManagedImageDecoder()
        self._encoder = encoder or NullVideoEncoder()
        self._html = html or NullHtmlFrameProvider()
        self._font = font or BitmapFont.DEFAULT

    def render_frame(self, spec: MediaSpec, t: float = 0.0) -> PixelBuffer:
        canvas = RasterCanvas.create(spec.size, spec.background)
        for layer in spec.layers:
            try:
                source = layer.source.decode(self._decoder)
            except Exception:
                # A layer that cannot be decoded is SKIPPED, not fatal. One
                # broken image should cost one layer, not the whole picture.
                continue
            canvas.draw_image(
                source, layer.rect_at(t).scaled(spec.size.width, spec.size.height),
                layer.fit, layer.opacity)

        for overlay in spec.overlays:
            scale = self._font.scale_for_height(overlay.size * spec.size.height)
            width, height = self._font.measure(overlay.text, scale)
            x, y = overlay.at.scaled(spec.size.width, spec.size.height)
            top = int(round(y - height / 2))
            if overlay.background is not None:
                pad = int(round(overlay.padding * spec.size.height))
                left = int(round(x))
                if overlay.align is TextAlign.CENTER:
                    left -= width // 2
                elif overlay.align is TextAlign.RIGHT:
                    left -= width
                canvas.fill_rect(
                    left - pad, top - pad, width + 2 * pad, height + 2 * pad,
                    overlay.background)
            canvas.draw_text(
                overlay.text, int(round(x)), top, scale, overlay.colour, overlay.align)
        return canvas.buffer

    def render_still(self, spec: MediaSpec) -> PixelBuffer:
        return self.render_frame(spec, 0.0)

    def render_clip(
        self, spec: MediaSpec, options: "ClipEncodeOptions | None" = None
    ) -> EncodedClip:
        opts = options or ClipEncodeOptions(spec.frames_per_second)
        count = spec.frame_count
        # `count - 1` in the denominator so the last frame lands exactly on
        # t=1.0. Dividing by `count` stops one frame short and a motion never
        # reaches its end rectangle.
        frames = [
            self.render_frame(spec, i / (count - 1) if count > 1 else 0.0)
            for i in range(count)
        ]
        if self._encoder.is_available:
            return self._encoder.encode(frames, opts)

        apng = AnimatedPngEncoder()
        delay = max(1, int(round(1000 / max(1, opts.frames_per_second))))
        for frame in frames:
            apng.add_frame(frame, delay)
        return EncodedClip(
            apng.encode(), "image/apng",
            spec.size.width, spec.size.height, count, True)


class MediaTemplates:
    """Ready-made specs for the things people actually ask for."""

    @staticmethod
    def quote_card(
        text: str, size: RenderSize = RenderSize(1080, 1080),
        background: Rgba32 = Rgba32(44, 62, 80, 255),
        ink: Rgba32 = Rgba32(255, 255, 255, 255),
    ) -> MediaSpec:
        """Wraps by MEASURING, not by character count.

        A fixed character count wraps a line of capitals off the edge and leaves
        a line of lowercase half empty, because what fits is ink, not letters.
        """
        font = BitmapFont.DEFAULT
        scale = font.scale_for_height(0.07 * size.height)
        max_width = int(size.width * 0.86)
        lines: list[str] = []
        current = ""
        for word in text.split():
            candidate = f"{current} {word}".strip()
            if current and font.measure(candidate, scale)[0] > max_width:
                lines.append(current)
                current = word
            else:
                current = candidate
        if current:
            lines.append(current)

        step = 0.10
        top = 0.5 - step * (len(lines) - 1) / 2
        return MediaSpec(
            size=size, background=background,
            overlays=tuple(
                TextOverlay(line, NormVec(0.5, top + i * step), TextAlign.CENTER,
                            0.07, ink)
                for i, line in enumerate(lines)
            ),
        )

    @staticmethod
    def title_over_image(
        image: ImageSource, title: str, size: RenderSize = RenderSize(1080, 1920),
    ) -> MediaSpec:
        """The plate under the title is NOT decoration - white text over an
        unknown photograph is unreadable about half the time."""
        return MediaSpec(
            size=size, background=Rgba32(0, 0, 0, 255),
            layers=(ImageLayer(image, NormRect(), ContentFit.COVER),),
            overlays=(TextOverlay(
                title, NormVec(0.5, 0.86), TextAlign.CENTER, 0.055,
                Rgba32(255, 255, 255, 255), Rgba32(0, 0, 0, 160), 0.02),),
        )

    @staticmethod
    def slow_zoom(
        image: ImageSource, seconds: float = 4.0,
        size: RenderSize = RenderSize(1920, 1080),
    ) -> MediaSpec:
        """A Ken Burns move. Both rectangles keep the frame COVERED, so the
        motion never uncovers a background edge partway through."""
        return MediaSpec(
            size=size, background=Rgba32(0, 0, 0, 255), duration_seconds=seconds,
            layers=(ImageLayer(
                image, NormRect(), ContentFit.COVER, 1.0,
                Motion(NormRect(0, 0, 1, 1), NormRect(-0.06, -0.06, 1.12, 1.12),
                       EasingKind.EASE_IN_OUT)),),
        )


@dataclass(frozen=True)
class MediaDomainContext:
    """What the companion knows about making pictures here."""

    can_encode_video: bool = False
    can_render_html: bool = False
    max_render_pixels: int = 1920 * 1920

    def describe(self) -> str:
        parts = ["still images and animated PNG"]
        if self.can_encode_video:
            parts.append("video")
        if self.can_render_html:
            parts.append("HTML layouts")
        return "this device can make " + ", ".join(parts)


class MediaCompanionAdapter:
    """Puts the renderer behind a plain request."""

    def __init__(
        self, renderer: "IMediaRenderer | None" = None,
        context: "MediaDomainContext | None" = None,
    ) -> None:
        self._renderer = renderer or ManagedMediaRenderer()
        self._context = context or MediaDomainContext()

    @property
    def context(self) -> MediaDomainContext:
        return self._context

    def make_quote_card(self, text: str) -> bytes:
        return ImageCodecs.encode_png(
            self._renderer.render_still(MediaTemplates.quote_card(text)))

    def make_clip(self, image: ImageSource, seconds: float = 4.0) -> EncodedClip:
        return self._renderer.render_clip(MediaTemplates.slow_zoom(image, seconds))
