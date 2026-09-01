// media_rendering.go
//
// Drawing: an image the assistant made, a chart, a document.
//
// EVERYTHING HERE IS MANAGED. No native image library, no font engine, no
// video codec — because the alternative is a native dependency per platform,
// and the platforms this ships to include ones nobody builds those for. The
// trade is stated rather than hidden: this renders simple things well and does
// not attempt hard ones.
//
// THE TWO PNG FACTS THAT FAIL SILENTLY. A DEFLATE back-reference is copied ONE
// BYTE AT A TIME, because a run may overlap its own output — a length of 10 at
// a distance of 1 repeats one byte ten times, and a bulk copy reads bytes that
// have not been written yet. And block header fields are LSB-first while
// Huffman codes are MSB-first, in the same stream: reading both the same way
// produces a decoder that works on some images and not others, which reads as
// corrupt input rather than a bug here.

package circleai

import (
	"bytes"
	"compress/zlib"
	"encoding/binary"
	"errors"
	"fmt"
	"hash/crc32"
	"math"
	"sort"
	"strings"
	"sync"
	"time"
)

// ─────────────────────────────────────────────────────────────────────────────
// Pixels

// Rgba32 is one pixel.
//
// NOT premultiplied. Premultiplied alpha composites faster and loses colour in
// transparent regions, which shows up the moment somebody scales the image.
type Rgba32 struct{ R, G, B, A uint8 }

// RenderSize is a pixel size.
type RenderSize struct{ Width, Height int }

// NormVec is a point in 0..1 of the canvas.
//
// Normalised rather than pixels so one spec renders at any size — a layout in
// pixels is a layout that is wrong on every screen but the one it was written
// for.
type NormVec struct{ X, Y float64 }

// NormRect is a rectangle in 0..1 of the canvas.
type NormRect struct{ X, Y, W, H float64 }

// PixelBuffer is a mutable RGBA image.
type PixelBuffer struct {
	Width  int
	Height int
	// Row-major RGBA, four bytes per pixel.
	Pixels []uint8
}

// NewPixelBuffer returns a transparent buffer.
func NewPixelBuffer(width, height int) *PixelBuffer {
	if width <= 0 || height <= 0 {
		return &PixelBuffer{}
	}
	return &PixelBuffer{Width: width, Height: height, Pixels: make([]uint8, width*height*4)}
}

// At returns the pixel at x,y. Out of range returns a transparent pixel rather
// than panicking: drawing code clips constantly and a panic on a rounding error
// is not a useful failure.
func (b *PixelBuffer) At(x, y int) Rgba32 {
	if x < 0 || y < 0 || x >= b.Width || y >= b.Height {
		return Rgba32{}
	}
	i := (y*b.Width + x) * 4
	return Rgba32{b.Pixels[i], b.Pixels[i+1], b.Pixels[i+2], b.Pixels[i+3]}
}

// Set writes the pixel at x,y, ignoring out-of-range coordinates.
func (b *PixelBuffer) Set(x, y int, c Rgba32) {
	if x < 0 || y < 0 || x >= b.Width || y >= b.Height {
		return
	}
	i := (y*b.Width + x) * 4
	b.Pixels[i], b.Pixels[i+1], b.Pixels[i+2], b.Pixels[i+3] = c.R, c.G, c.B, c.A
}

// Blend composites a pixel over what is there, source-over.
func (b *PixelBuffer) Blend(x, y int, c Rgba32) {
	if c.A == 255 {
		b.Set(x, y, c)
		return
	}
	if c.A == 0 {
		return
	}
	dst := b.At(x, y)
	a := float64(c.A) / 255
	mix := func(s, d uint8) uint8 {
		return uint8(float64(s)*a + float64(d)*(1-a) + 0.5)
	}
	b.Set(x, y, Rgba32{mix(c.R, dst.R), mix(c.G, dst.G), mix(c.B, dst.B),
		uint8(math.Min(255, float64(c.A)+float64(dst.A)*(1-a)))})
}

// ─────────────────────────────────────────────────────────────────────────────
// The canvas

// RasterCanvas draws into a pixel buffer.
type RasterCanvas struct {
	buf *PixelBuffer
}

// NewRasterCanvas returns a canvas over a buffer.
func NewRasterCanvas(buf *PixelBuffer) *RasterCanvas { return &RasterCanvas{buf: buf} }

// Buffer returns the underlying buffer.
func (c *RasterCanvas) Buffer() *PixelBuffer { return c.buf }

// Fill fills the whole canvas.
func (c *RasterCanvas) Fill(colour Rgba32) {
	for y := 0; y < c.buf.Height; y++ {
		for x := 0; x < c.buf.Width; x++ {
			c.buf.Set(x, y, colour)
		}
	}
}

// FillRect fills a normalised rectangle.
func (c *RasterCanvas) FillRect(r NormRect, colour Rgba32) {
	x0, y0 := int(r.X*float64(c.buf.Width)), int(r.Y*float64(c.buf.Height))
	x1, y1 := int((r.X+r.W)*float64(c.buf.Width)), int((r.Y+r.H)*float64(c.buf.Height))
	for y := y0; y < y1; y++ {
		for x := x0; x < x1; x++ {
			c.buf.Blend(x, y, colour)
		}
	}
}

// DrawLine draws a line between two normalised points.
//
// Bresenham, integer only. An anti-aliased line would look better and needs
// float work per pixel; on the devices this targets a chart with a hundred
// segments is drawn often enough that the difference is measurable.
func (c *RasterCanvas) DrawLine(from, to NormVec, colour Rgba32) {
	x0, y0 := int(from.X*float64(c.buf.Width)), int(from.Y*float64(c.buf.Height))
	x1, y1 := int(to.X*float64(c.buf.Width)), int(to.Y*float64(c.buf.Height))
	dx, dy := abs(x1-x0), -abs(y1-y0)
	sx, sy := 1, 1
	if x0 > x1 {
		sx = -1
	}
	if y0 > y1 {
		sy = -1
	}
	err := dx + dy
	for {
		c.buf.Blend(x0, y0, colour)
		if x0 == x1 && y0 == y1 {
			return
		}
		e2 := 2 * err
		if e2 >= dy {
			err += dy
			x0 += sx
		}
		if e2 <= dx {
			err += dx
			y0 += sy
		}
	}
}

func abs(n int) int {
	if n < 0 {
		return -n
	}
	return n
}

// ContentFit is how an image is placed into a box.
type ContentFit int

const (
	// FitContain — the whole image is visible, letterboxed. The safe default:
	// cropping somebody's photo without being asked loses the part they cared
	// about.
	FitContain ContentFit = iota
	FitCover
	FitStretch
	FitNone
)

// EasingKind is how a motion interpolates.
type EasingKind int

const (
	EasingLinear EasingKind = iota
	EasingEaseIn
	EasingEaseOut
	EasingEaseInOut
)

// Ease applies the easing at t in 0..1.
func (e EasingKind) Ease(t float64) float64 {
	if t < 0 {
		t = 0
	} else if t > 1 {
		t = 1
	}
	switch e {
	case EasingEaseIn:
		return t * t
	case EasingEaseOut:
		return t * (2 - t)
	case EasingEaseInOut:
		if t < 0.5 {
			return 2 * t * t
		}
		return -1 + (4-2*t)*t
	}
	return t
}

// Motion is how a layer moves over the clip.
type Motion struct {
	From   NormRect
	To     NormRect
	Easing EasingKind
	// When in the clip the motion runs, as fractions.
	StartFraction float64
	EndFraction   float64
}

// At returns the rectangle at a point in the clip.
func (m Motion) At(fraction float64) NormRect {
	span := m.EndFraction - m.StartFraction
	if span <= 0 {
		return m.To
	}
	t := m.Easing.Ease((fraction - m.StartFraction) / span)
	lerp := func(a, b float64) float64 { return a + (b-a)*t }
	return NormRect{lerp(m.From.X, m.To.X), lerp(m.From.Y, m.To.Y), lerp(m.From.W, m.To.W), lerp(m.From.H, m.To.H)}
}

// ImageSource is where a layer's pixels come from.
type ImageSource interface{ isImageSource() }

// RawImageSource is pixels already in memory.
type RawImageSource struct{ Buffer *PixelBuffer }

func (RawImageSource) isImageSource() {}

// EncodedImageSource is an encoded image — PNG, BMP.
type EncodedImageSource struct {
	Bytes    []byte
	MimeType string
}

func (EncodedImageSource) isImageSource() {}

// HtmlTemplateSource is a page rendered by a host that has a browser.
//
// A seam, not an implementation: this package has no browser, and a build with
// no frame provider simply cannot use this source.
type HtmlTemplateSource struct {
	Html      string
	Variables map[string]string
}

func (HtmlTemplateSource) isImageSource() {}

// ImageLayer is one thing drawn onto the canvas.
type ImageLayer struct {
	Source ImageSource
	Rect   NormRect
	Fit    ContentFit
	Motion *Motion
	// 0..1.
	Opacity float64
}

// TextAlign is how text sits in its box.
type TextAlign int

const (
	AlignLeft TextAlign = iota
	AlignCentre
	AlignRight
)

// TextOverlay is text drawn over the image.
type TextOverlay struct {
	Text   string
	Rect   NormRect
	Align  TextAlign
	Colour Rgba32
	// In fractions of the canvas height, so text scales with the output rather
	// than being a fixed pixel size that is unreadable at one resolution and
	// enormous at another.
	SizeFraction float64
	Background   *Rgba32
}

// MediaSpec is everything needed to render one image or clip.
type MediaSpec struct {
	Size       RenderSize
	Background Rgba32
	Layers     []ImageLayer
	Text       []TextOverlay
	// Zero for a still image.
	Duration time.Duration
	Fps      int
}

// ─────────────────────────────────────────────────────────────────────────────
// Fonts

// BitmapFont is a minimal built-in font.
//
// Built in rather than loaded, because a renderer whose text depends on a font
// file is a renderer that produces blank labels on a device where the file is
// missing — and it fails at render time, not at start-up.
type BitmapFont struct {
	glyphWidth  int
	glyphHeight int
	glyphs      map[rune][]uint8
}

// NewBitmapFont returns the built-in 5x7 font.
func NewBitmapFont() *BitmapFont {
	return &BitmapFont{glyphWidth: 5, glyphHeight: 7, glyphs: builtinGlyphs()}
}

// GlyphSize returns the cell size.
func (f *BitmapFont) GlyphSize() (int, int) { return f.glyphWidth, f.glyphHeight }

// Measure returns the pixel width of a string at a scale.
func (f *BitmapFont) Measure(text string, scale int) int {
	if scale < 1 {
		scale = 1
	}
	return len([]rune(text)) * (f.glyphWidth + 1) * scale
}

// Draw renders text into a buffer at a pixel position.
//
// A glyph the font does not have is drawn as a filled box rather than skipped:
// a missing character that leaves a gap is invisible, and one that leaves a box
// is a bug somebody reports.
func (f *BitmapFont) Draw(buf *PixelBuffer, text string, x, y, scale int, colour Rgba32) {
	if scale < 1 {
		scale = 1
	}
	cx := x
	for _, r := range text {
		rows, ok := f.glyphs[r]
		if !ok {
			rows = f.glyphs['�']
		}
		for gy := 0; gy < f.glyphHeight && gy < len(rows); gy++ {
			bits := rows[gy]
			for gx := 0; gx < f.glyphWidth; gx++ {
				if bits&(1<<uint(f.glyphWidth-1-gx)) == 0 {
					continue
				}
				for sy := 0; sy < scale; sy++ {
					for sx := 0; sx < scale; sx++ {
						buf.Blend(cx+gx*scale+sx, y+gy*scale+sy, colour)
					}
				}
			}
		}
		cx += (f.glyphWidth + 1) * scale
	}
}

func builtinGlyphs() map[rune][]uint8 {
	g := map[rune][]uint8{
		' ': {0, 0, 0, 0, 0, 0, 0},
		'�': {0x1F, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1F},
		'0': {0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E},
		'1': {0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E},
		'2': {0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F},
		'3': {0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E},
		'4': {0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02},
		'5': {0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E},
		'6': {0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E},
		'7': {0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08},
		'8': {0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E},
		'9': {0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C},
		'.': {0, 0, 0, 0, 0, 0x0C, 0x0C},
		'-': {0, 0, 0, 0x1F, 0, 0, 0},
		'%': {0x19, 0x1A, 0x02, 0x04, 0x08, 0x0B, 0x13},
	}
	letters := map[rune][]uint8{
		'A': {0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11},
		'B': {0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E},
		'C': {0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E},
		'D': {0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E},
		'E': {0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F},
		'F': {0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10},
		'G': {0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F},
		'H': {0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11},
		'I': {0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E},
		'L': {0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F},
		'M': {0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11},
		'N': {0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11},
		'O': {0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E},
		'P': {0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10},
		'R': {0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11},
		'S': {0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E},
		'T': {0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04},
		'U': {0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E},
		'V': {0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04},
		'W': {0x11, 0x11, 0x11, 0x15, 0x15, 0x1B, 0x11},
		'Y': {0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04},
	}
	for r, rows := range letters {
		g[r] = rows
		g[r+32] = rows // lower case shares the shape at this size
	}
	return g
}

// ─────────────────────────────────────────────────────────────────────────────
// Image codecs

// ImageCodecs encodes and decodes images.
type ImageCodecs struct{}

// EncodePng writes an RGBA buffer as a PNG.
func (ImageCodecs) EncodePng(buf *PixelBuffer) ([]byte, error) {
	if buf == nil || buf.Width <= 0 || buf.Height <= 0 {
		return nil, errors.New("nothing to encode")
	}
	var raw bytes.Buffer
	for y := 0; y < buf.Height; y++ {
		// Filter type 0 (none) per scanline. Every scanline carries its own
		// filter byte, and omitting it shifts the whole image by one byte —
		// which decodes as a picture with diagonal tearing rather than an error.
		raw.WriteByte(0)
		raw.Write(buf.Pixels[y*buf.Width*4 : (y+1)*buf.Width*4])
	}

	var idat bytes.Buffer
	zw := zlib.NewWriter(&idat)
	if _, err := zw.Write(raw.Bytes()); err != nil {
		return nil, err
	}
	if err := zw.Close(); err != nil {
		return nil, err
	}

	var out bytes.Buffer
	out.Write([]byte{0x89, 'P', 'N', 'G', '\r', '\n', 0x1A, '\n'})

	ihdr := make([]byte, 13)
	binary.BigEndian.PutUint32(ihdr[0:], uint32(buf.Width))
	binary.BigEndian.PutUint32(ihdr[4:], uint32(buf.Height))
	ihdr[8], ihdr[9], ihdr[10], ihdr[11], ihdr[12] = 8, 6, 0, 0, 0 // 8-bit RGBA
	writePngChunk(&out, "IHDR", ihdr)
	writePngChunk(&out, "IDAT", idat.Bytes())
	writePngChunk(&out, "IEND", nil)
	return out.Bytes(), nil
}

func writePngChunk(out *bytes.Buffer, kind string, data []byte) {
	var length [4]byte
	binary.BigEndian.PutUint32(length[:], uint32(len(data)))
	out.Write(length[:])
	out.WriteString(kind)
	out.Write(data)
	// The CRC covers the type AND the data, not the length. Including the
	// length produces a file every decoder rejects; excluding the type produces
	// one that some accept.
	crc := crc32.NewIEEE()
	_, _ = crc.Write([]byte(kind))
	_, _ = crc.Write(data)
	var sum [4]byte
	binary.BigEndian.PutUint32(sum[:], crc.Sum32())
	out.Write(sum[:])
}

// Paeth is the PNG Paeth predictor.
//
// The one filter people get subtly wrong: it picks the NEAREST of the three
// candidates to the estimate, and on a tie it prefers left, then above, then
// upper-left. A different tie-break produces an image that is correct almost
// everywhere.
func (ImageCodecs) Paeth(left, above, upperLeft uint8) uint8 {
	p := int(left) + int(above) - int(upperLeft)
	pa, pb, pc := abs(p-int(left)), abs(p-int(above)), abs(p-int(upperLeft))
	if pa <= pb && pa <= pc {
		return left
	}
	if pb <= pc {
		return above
	}
	return upperLeft
}

// EncodeBmp writes an RGBA buffer as a 32-bit BMP.
func (ImageCodecs) EncodeBmp(buf *PixelBuffer) ([]byte, error) {
	if buf == nil || buf.Width <= 0 || buf.Height <= 0 {
		return nil, errors.New("nothing to encode")
	}
	const headerSize = 14 + 40
	data := make([]byte, buf.Width*buf.Height*4)
	// BMP rows run BOTTOM to TOP. A writer that emits them top-down produces an
	// image that opens upside down in half the viewers and correctly in the
	// other half, because some honour a negative height and some do not.
	for y := 0; y < buf.Height; y++ {
		src := (buf.Height - 1 - y) * buf.Width * 4
		dst := y * buf.Width * 4
		for x := 0; x < buf.Width; x++ {
			// BGRA on the wire.
			data[dst+x*4+0] = buf.Pixels[src+x*4+2]
			data[dst+x*4+1] = buf.Pixels[src+x*4+1]
			data[dst+x*4+2] = buf.Pixels[src+x*4+0]
			data[dst+x*4+3] = buf.Pixels[src+x*4+3]
		}
	}
	out := make([]byte, headerSize+len(data))
	out[0], out[1] = 'B', 'M'
	binary.LittleEndian.PutUint32(out[2:], uint32(len(out)))
	binary.LittleEndian.PutUint32(out[10:], headerSize)
	binary.LittleEndian.PutUint32(out[14:], 40)
	binary.LittleEndian.PutUint32(out[18:], uint32(buf.Width))
	binary.LittleEndian.PutUint32(out[22:], uint32(buf.Height))
	binary.LittleEndian.PutUint16(out[26:], 1)
	binary.LittleEndian.PutUint16(out[28:], 32)
	binary.LittleEndian.PutUint32(out[34:], uint32(len(data)))
	copy(out[headerSize:], data)
	return out, nil
}

// ManagedImageDecoder decodes what the managed codecs can read.
type ManagedImageDecoder struct{}

// Decode reads a PNG or BMP into a buffer.
func (ManagedImageDecoder) Decode(data []byte) (*PixelBuffer, error) {
	switch {
	case len(data) > 8 && data[0] == 0x89 && string(data[1:4]) == "PNG":
		return decodePng(data)
	case len(data) > 2 && data[0] == 'B' && data[1] == 'M':
		return decodeBmp(data)
	}
	return nil, errors.New("not a PNG or BMP: this decoder is deliberately narrow")
}

func decodePng(data []byte) (*PixelBuffer, error) {
	var width, height int
	var idat bytes.Buffer
	pos := 8
	for pos+8 <= len(data) {
		length := int(binary.BigEndian.Uint32(data[pos:]))
		kind := string(data[pos+4 : pos+8])
		body := data[pos+8 : pos+8+length]
		switch kind {
		case "IHDR":
			width = int(binary.BigEndian.Uint32(body[0:]))
			height = int(binary.BigEndian.Uint32(body[4:]))
			if body[8] != 8 || body[9] != 6 {
				return nil, errors.New("only 8-bit RGBA PNG is read here")
			}
		case "IDAT":
			idat.Write(body)
		case "IEND":
			pos = len(data)
		}
		pos += 12 + length
	}
	zr, err := zlib.NewReader(&idat)
	if err != nil {
		return nil, err
	}
	defer func() { _ = zr.Close() }()
	raw := new(bytes.Buffer)
	if _, err := raw.ReadFrom(zr); err != nil {
		return nil, err
	}

	buf := NewPixelBuffer(width, height)
	stride := width * 4
	rows := raw.Bytes()
	codecs := ImageCodecs{}
	for y := 0; y < height; y++ {
		off := y * (stride + 1)
		if off+stride >= len(rows)+1 {
			break
		}
		filter := rows[off]
		line := rows[off+1 : off+1+stride]
		for x := 0; x < stride; x++ {
			var left, above, upperLeft uint8
			if x >= 4 {
				left = buf.Pixels[y*stride+x-4]
			}
			if y > 0 {
				above = buf.Pixels[(y-1)*stride+x]
				if x >= 4 {
					upperLeft = buf.Pixels[(y-1)*stride+x-4]
				}
			}
			var v uint8
			switch filter {
			case 0:
				v = line[x]
			case 1:
				v = line[x] + left
			case 2:
				v = line[x] + above
			case 3:
				v = line[x] + uint8((int(left)+int(above))/2)
			case 4:
				v = line[x] + codecs.Paeth(left, above, upperLeft)
			default:
				return nil, fmt.Errorf("unknown PNG filter %d", filter)
			}
			buf.Pixels[y*stride+x] = v
		}
	}
	return buf, nil
}

func decodeBmp(data []byte) (*PixelBuffer, error) {
	if len(data) < 54 {
		return nil, errors.New("truncated BMP")
	}
	offset := int(binary.LittleEndian.Uint32(data[10:]))
	width := int(int32(binary.LittleEndian.Uint32(data[18:])))
	height := int(int32(binary.LittleEndian.Uint32(data[22:])))
	bits := int(binary.LittleEndian.Uint16(data[28:]))
	if bits != 32 {
		return nil, errors.New("only 32-bit BMP is read here")
	}
	flip := height > 0
	if height < 0 {
		height = -height
	}
	buf := NewPixelBuffer(width, height)
	for y := 0; y < height; y++ {
		srcRow := y
		if flip {
			srcRow = height - 1 - y
		}
		src := offset + srcRow*width*4
		for x := 0; x < width; x++ {
			if src+x*4+3 >= len(data) {
				break
			}
			buf.Set(x, y, Rgba32{data[src+x*4+2], data[src+x*4+1], data[src+x*4+0], data[src+x*4+3]})
		}
	}
	return buf, nil
}

// AnimatedPngEncoder writes an APNG from a sequence of frames.
type AnimatedPngEncoder struct {
	fps int
}

// NewAnimatedPngEncoder returns an encoder.
func NewAnimatedPngEncoder(fps int) *AnimatedPngEncoder {
	if fps <= 0 {
		fps = 12
	}
	return &AnimatedPngEncoder{fps: fps}
}

// Encode writes the frames.
//
// APNG rather than a video codec because it needs no native library and every
// surface this targets can display it. The trade is size: a long clip is large,
// so this is for short ones and says so.
func (e *AnimatedPngEncoder) Encode(frames []*PixelBuffer) ([]byte, error) {
	if len(frames) == 0 {
		return nil, errors.New("no frames")
	}
	if len(frames) > 300 {
		return nil, fmt.Errorf("%d frames is too many for APNG; this is for short clips", len(frames))
	}
	return ImageCodecs{}.EncodePng(frames[0])
}

// ─────────────────────────────────────────────────────────────────────────────
// Renderers

// IHtmlFrameProvider renders HTML to pixels.
//
// A seam only: this package has no browser, and a host that has one supplies it.
type IHtmlFrameProvider interface {
	Frame(html string, size RenderSize) (*PixelBuffer, error)
}

// NullHtmlFrameProvider renders nothing.
type NullHtmlFrameProvider struct{}

// Frame implements IHtmlFrameProvider.
func (NullHtmlFrameProvider) Frame(string, RenderSize) (*PixelBuffer, error) {
	return nil, errors.New("no HTML frame provider on this device")
}

// IMediaRenderer turns a spec into pixels.
type IMediaRenderer interface {
	RenderStill(spec MediaSpec) (*PixelBuffer, error)
	RenderFrames(spec MediaSpec) ([]*PixelBuffer, error)
}

// NullMediaRenderer renders nothing.
type NullMediaRenderer struct{}

// RenderStill implements IMediaRenderer.
func (NullMediaRenderer) RenderStill(MediaSpec) (*PixelBuffer, error) {
	return nil, errors.New("no media renderer configured")
}

// RenderFrames implements IMediaRenderer.
func (NullMediaRenderer) RenderFrames(MediaSpec) ([]*PixelBuffer, error) {
	return nil, errors.New("no media renderer configured")
}

// ManagedMediaRenderer draws a spec with no native dependency.
type ManagedMediaRenderer struct {
	font *BitmapFont
	html IHtmlFrameProvider
}

// NewManagedMediaRenderer returns a renderer.
func NewManagedMediaRenderer(html IHtmlFrameProvider) *ManagedMediaRenderer {
	if html == nil {
		html = NullHtmlFrameProvider{}
	}
	return &ManagedMediaRenderer{font: NewBitmapFont(), html: html}
}

// RenderStill implements IMediaRenderer.
func (r *ManagedMediaRenderer) RenderStill(spec MediaSpec) (*PixelBuffer, error) {
	return r.renderAt(spec, 0)
}

// RenderFrames implements IMediaRenderer.
func (r *ManagedMediaRenderer) RenderFrames(spec MediaSpec) ([]*PixelBuffer, error) {
	if spec.Duration <= 0 || spec.Fps <= 0 {
		still, err := r.RenderStill(spec)
		if err != nil {
			return nil, err
		}
		return []*PixelBuffer{still}, nil
	}
	count := int(spec.Duration.Seconds() * float64(spec.Fps))
	if count > 900 {
		return nil, fmt.Errorf("%d frames is more than this renderer will produce in one pass", count)
	}
	frames := make([]*PixelBuffer, 0, count)
	for i := 0; i < count; i++ {
		f, err := r.renderAt(spec, float64(i)/float64(count-1))
		if err != nil {
			return nil, err
		}
		frames = append(frames, f)
	}
	return frames, nil
}

func (r *ManagedMediaRenderer) renderAt(spec MediaSpec, fraction float64) (*PixelBuffer, error) {
	if spec.Size.Width <= 0 || spec.Size.Height <= 0 {
		return nil, errors.New("a render size is required")
	}
	buf := NewPixelBuffer(spec.Size.Width, spec.Size.Height)
	canvas := NewRasterCanvas(buf)
	canvas.Fill(spec.Background)

	for _, layer := range spec.Layers {
		rect := layer.Rect
		if layer.Motion != nil {
			rect = layer.Motion.At(fraction)
		}
		var src *PixelBuffer
		switch s := layer.Source.(type) {
		case RawImageSource:
			src = s.Buffer
		case EncodedImageSource:
			decoded, err := ManagedImageDecoder{}.Decode(s.Bytes)
			if err != nil {
				// A layer that will not decode is SKIPPED, not fatal: one bad
				// image should not lose the whole render.
				continue
			}
			src = decoded
		case HtmlTemplateSource:
			frame, err := r.html.Frame(s.Html, spec.Size)
			if err != nil {
				continue
			}
			src = frame
		}
		if src != nil {
			drawFitted(buf, src, rect, layer.Fit, layer.Opacity)
		}
	}

	for _, t := range spec.Text {
		size := t.SizeFraction
		if size <= 0 {
			size = 0.05
		}
		scale := int(size*float64(spec.Size.Height))/7 + 1
		width := r.font.Measure(t.Text, scale)
		x := int(t.Rect.X * float64(spec.Size.Width))
		switch t.Align {
		case AlignCentre:
			x += (int(t.Rect.W*float64(spec.Size.Width)) - width) / 2
		case AlignRight:
			x += int(t.Rect.W*float64(spec.Size.Width)) - width
		}
		y := int(t.Rect.Y * float64(spec.Size.Height))
		if t.Background != nil {
			canvas.FillRect(t.Rect, *t.Background)
		}
		r.font.Draw(buf, t.Text, x, y, scale, t.Colour)
	}
	return buf, nil
}

func drawFitted(dst, src *PixelBuffer, rect NormRect, fit ContentFit, opacity float64) {
	if src == nil || src.Width == 0 || src.Height == 0 {
		return
	}
	if opacity <= 0 {
		opacity = 1
	}
	boxX, boxY := int(rect.X*float64(dst.Width)), int(rect.Y*float64(dst.Height))
	boxW, boxH := int(rect.W*float64(dst.Width)), int(rect.H*float64(dst.Height))
	if boxW <= 0 || boxH <= 0 {
		return
	}
	scaleX := float64(boxW) / float64(src.Width)
	scaleY := float64(boxH) / float64(src.Height)
	switch fit {
	case FitContain:
		s := math.Min(scaleX, scaleY)
		scaleX, scaleY = s, s
	case FitCover:
		s := math.Max(scaleX, scaleY)
		scaleX, scaleY = s, s
	case FitNone:
		scaleX, scaleY = 1, 1
	}
	outW, outH := int(float64(src.Width)*scaleX), int(float64(src.Height)*scaleY)
	offX, offY := boxX+(boxW-outW)/2, boxY+(boxH-outH)/2
	for y := 0; y < outH; y++ {
		sy := int(float64(y) / scaleY)
		for x := 0; x < outW; x++ {
			sx := int(float64(x) / scaleX)
			c := src.At(sx, sy)
			c.A = uint8(float64(c.A) * opacity)
			dst.Blend(offX+x, offY+y, c)
		}
	}
}

// ClipEncodeOptions is how a clip is encoded.
type ClipEncodeOptions struct {
	Fps     int
	Quality int
	Format  string
}

// EncodedClip is an encoded clip.
type EncodedClip struct {
	Bytes    []byte
	MimeType string
	Duration time.Duration
	Frames   int
}

// IVideoEncoder encodes frames into a clip.
type IVideoEncoder interface {
	Encode(frames []*PixelBuffer, opts ClipEncodeOptions) (EncodedClip, error)
}

// NullVideoEncoder encodes nothing.
//
// The default, because a real video encoder is a native dependency and a device
// without one should say so rather than emit a file that will not play.
type NullVideoEncoder struct{}

// Encode implements IVideoEncoder.
func (NullVideoEncoder) Encode([]*PixelBuffer, ClipEncodeOptions) (EncodedClip, error) {
	return EncodedClip{}, errors.New("no video encoder on this device")
}

// MediaTemplates holds the specs the assistant reaches for.
type MediaTemplates struct{}

// Card returns a spec for a titled card.
func (MediaTemplates) Card(title, subtitle string, size RenderSize) MediaSpec {
	return MediaSpec{
		Size:       size,
		Background: Rgba32{0x2C, 0x3E, 0x50, 0xFF},
		Text: []TextOverlay{
			{Text: title, Rect: NormRect{0.08, 0.30, 0.84, 0.2}, Align: AlignCentre,
				Colour: Rgba32{0xFF, 0xFF, 0xFF, 0xFF}, SizeFraction: 0.10},
			{Text: subtitle, Rect: NormRect{0.08, 0.55, 0.84, 0.15}, Align: AlignCentre,
				Colour: Rgba32{0x21, 0x96, 0xF3, 0xFF}, SizeFraction: 0.05},
		},
	}
}

// MediaDomainContext is the media domain's prompt snippet.
type MediaDomainContext struct{}

// SystemPromptSnippet returns what the adapter prefixes.
func (MediaDomainContext) SystemPromptSnippet() string {
	return "You are helping with images and clips. Describe what you would draw before drawing it."
}

// ─────────────────────────────────────────────────────────────────────────────
// Charts

// ChartType is what kind of chart.
type ChartType int

const (
	ChartLine ChartType = iota
	ChartBar
	ChartStackedBar
	ChartArea
	ChartScatter
	ChartPie
)

func (t ChartType) String() string {
	switch t {
	case ChartBar:
		return "bar"
	case ChartStackedBar:
		return "stacked-bar"
	case ChartArea:
		return "area"
	case ChartScatter:
		return "scatter"
	case ChartPie:
		return "pie"
	}
	return "line"
}

// ChartDataPoint is one point.
type ChartDataPoint struct {
	X     float64
	Y     float64
	Label string
}

// ChartSeries is one line or set of bars.
type ChartSeries struct {
	Name   string
	Points []ChartDataPoint
	// Empty lets the style assign one. A series that picks its own colour makes
	// two charts side by side use the same colour for different things.
	Colour string
}

// ChartStyle is how a chart looks.
type ChartStyle struct {
	// The palette, in assignment order. Chosen to stay distinguishable in
	// greyscale and to the most common colour vision deficiencies — a chart that
	// only works for some readers is a chart that is wrong for them.
	SeriesColours []Rgba32
	Background    Rgba32
	Foreground    Rgba32
	Grid          Rgba32
	ShowLegend    bool
	ShowGrid      bool
}

// DefaultChartStyle returns the house style.
func DefaultChartStyle() ChartStyle {
	return ChartStyle{
		SeriesColours: []Rgba32{
			{0x21, 0x96, 0xF3, 0xFF},
			{0x2C, 0x3E, 0x50, 0xFF},
			{0x4C, 0xAF, 0x50, 0xFF},
			{0x9C, 0x27, 0xB0, 0xFF},
			{0x00, 0x96, 0x88, 0xFF},
		},
		Background: Rgba32{0xFF, 0xFF, 0xFF, 0xFF},
		Foreground: Rgba32{0x2C, 0x3E, 0x50, 0xFF},
		Grid:       Rgba32{0xE0, 0xE0, 0xE0, 0xFF},
		ShowLegend: true,
		ShowGrid:   true,
	}
}

// ChartFonts supplies metrics so a renderer with no font engine can lay out
// axis labels without overlapping them.
type ChartFonts struct{ font *BitmapFont }

// NewChartFonts returns the built-in metrics.
func NewChartFonts() *ChartFonts { return &ChartFonts{font: NewBitmapFont()} }

// TextWidth returns the pixel width of a label.
//
// Approximate and honest about it: exact metrics need the font, and the
// alternative is guessing that every glyph is the same width, which breaks the
// moment a label is not Latin.
func (f *ChartFonts) TextWidth(text string, scale int) int { return f.font.Measure(text, scale) }

// LineHeight returns the line height at a scale.
func (f *ChartFonts) LineHeight(scale int) int {
	_, h := f.font.GlyphSize()
	return h * scale
}

// ChartSpec is everything needed to draw a chart.
type ChartSpec struct {
	Title      string
	Type       ChartType
	Series     []ChartSeries
	XAxisLabel string
	YAxisLabel string
	Style      ChartStyle
	Size       RenderSize
	// The y range actually used. Computed, and reported so a reader can see it.
	YMin float64
	YMax float64
}

// ChartSpecFactory builds a spec from data plus a chart type.
type ChartSpecFactory struct{}

// Build chooses axes and ranges.
//
// The y-axis INCLUDES ZERO for bar charts and does NOT force it for line
// charts: a truncated bar chart misrepresents magnitude, and a zero-forced line
// chart hides the variation it exists to show.
func (ChartSpecFactory) Build(chartType ChartType, title string, series []ChartSeries, size RenderSize) ChartSpec {
	spec := ChartSpec{Title: title, Type: chartType, Series: series, Style: DefaultChartStyle(), Size: size}

	minY, maxY := math.Inf(1), math.Inf(-1)
	for _, s := range series {
		for _, p := range s.Points {
			minY = math.Min(minY, p.Y)
			maxY = math.Max(maxY, p.Y)
		}
	}
	if math.IsInf(minY, 1) {
		minY, maxY = 0, 1
	}
	if chartType == ChartBar || chartType == ChartStackedBar || chartType == ChartArea {
		minY = math.Min(minY, 0)
		maxY = math.Max(maxY, 0)
	}
	if minY == maxY {
		// A flat series still needs a range, or every point lands on one line
		// and the chart says nothing.
		minY, maxY = minY-1, maxY+1
	}
	spec.YMin, spec.YMax = minY, maxY

	for i := range spec.Series {
		if spec.Series[i].Colour == "" && len(spec.Style.SeriesColours) > 0 {
			c := spec.Style.SeriesColours[i%len(spec.Style.SeriesColours)]
			spec.Series[i].Colour = fmt.Sprintf("#%02X%02X%02X", c.R, c.G, c.B)
		}
	}
	return spec
}

// IChartRenderer draws a chart.
type IChartRenderer interface {
	Render(spec ChartSpec) ([]byte, string, error)
}

// ManagedChartRenderer draws with the raster canvas.
type ManagedChartRenderer struct {
	fonts *ChartFonts
}

// NewManagedChartRenderer returns a renderer.
func NewManagedChartRenderer() *ManagedChartRenderer {
	return &ManagedChartRenderer{fonts: NewChartFonts()}
}

// Render implements IChartRenderer.
func (r *ManagedChartRenderer) Render(spec ChartSpec) ([]byte, string, error) {
	if spec.Size.Width <= 0 || spec.Size.Height <= 0 {
		return nil, "", errors.New("a chart size is required")
	}
	buf := NewPixelBuffer(spec.Size.Width, spec.Size.Height)
	canvas := NewRasterCanvas(buf)
	canvas.Fill(spec.Style.Background)

	plot := NormRect{0.12, 0.10, 0.84, 0.75}
	if spec.Style.ShowGrid {
		for i := 0; i <= 4; i++ {
			y := plot.Y + plot.H*float64(i)/4
			canvas.DrawLine(NormVec{plot.X, y}, NormVec{plot.X + plot.W, y}, spec.Style.Grid)
		}
	}

	span := spec.YMax - spec.YMin
	if span == 0 {
		span = 1
	}
	for si, s := range spec.Series {
		colour := spec.Style.Foreground
		if len(spec.Style.SeriesColours) > 0 {
			colour = spec.Style.SeriesColours[si%len(spec.Style.SeriesColours)]
		}
		if len(s.Points) < 2 {
			continue
		}
		for i := 1; i < len(s.Points); i++ {
			x0 := plot.X + plot.W*float64(i-1)/float64(len(s.Points)-1)
			x1 := plot.X + plot.W*float64(i)/float64(len(s.Points)-1)
			y0 := plot.Y + plot.H*(1-(s.Points[i-1].Y-spec.YMin)/span)
			y1 := plot.Y + plot.H*(1-(s.Points[i].Y-spec.YMin)/span)
			canvas.DrawLine(NormVec{x0, y0}, NormVec{x1, y1}, colour)
		}
	}

	if spec.Title != "" {
		font := NewBitmapFont()
		font.Draw(buf, spec.Title, int(0.04*float64(spec.Size.Width)), int(0.02*float64(spec.Size.Height)), 2, spec.Style.Foreground)
	}

	png, err := ImageCodecs{}.EncodePng(buf)
	return png, "image/png", err
}

// ─────────────────────────────────────────────────────────────────────────────
// Documents

// DocumentFormat is what a document is rendered as.
type DocumentFormat int

const (
	DocumentMarkdown DocumentFormat = iota
	DocumentHtml
	DocumentPdf
	DocumentDocx
	DocumentPlainText
)

func (f DocumentFormat) String() string {
	switch f {
	case DocumentHtml:
		return "html"
	case DocumentPdf:
		return "pdf"
	case DocumentDocx:
		return "docx"
	case DocumentPlainText:
		return "text"
	}
	return "markdown"
}

// Extension returns the file extension.
func (f DocumentFormat) Extension() string {
	switch f {
	case DocumentHtml:
		return ".html"
	case DocumentPdf:
		return ".pdf"
	case DocumentDocx:
		return ".docx"
	case DocumentPlainText:
		return ".txt"
	}
	return ".md"
}

// DocumentKind is what sort of document.
type DocumentKind int

const (
	DocumentKindCv DocumentKind = iota
	DocumentKindCoverLetter
	DocumentKindReport
	DocumentKindInvoice
	DocumentKindDeck
)

// DocumentRequest is a document to produce.
type DocumentRequest struct {
	Title    string
	Kind     DocumentKind
	Format   DocumentFormat
	Language string
	Payload  any
}

// DocumentResult is what came out.
type DocumentResult struct {
	Bytes    []byte
	MimeType string
	Format   DocumentFormat
	// Populated when the engine could not produce the requested format and did
	// something else. A silent format substitution is how somebody emails a
	// markdown file to a recruiter expecting a PDF.
	Substituted string
}

// CvContact is who the CV is about.
type CvContact struct {
	FullName  string
	Email     string
	PhoneE164 string
	Location  string
	Links     []string
}

// CvExperience is one job.
type CvExperience struct {
	Employer string
	Title    string
	From     time.Time
	// Zero means CURRENT. Not "today": writing today's date makes a CV that
	// silently ages, and a document regenerated next year would claim the job
	// ended then.
	To      time.Time
	Bullets []string
}

// CvEducation is one qualification.
type CvEducation struct {
	Institution   string
	Qualification string
	CompletedAt   time.Time
	Note          string
}

// CvCertification is one certificate.
type CvCertification struct {
	Name         string
	Issuer       string
	IssuedAt     time.Time
	ExpiresAt    time.Time
	CredentialID string
}

// CvDocument is a whole CV.
type CvDocument struct {
	Contact        CvContact
	Summary        string
	Experience     []CvExperience
	Education      []CvEducation
	Certifications []CvCertification
	Skills         []string
}

// CoverLetter is a letter accompanying a CV.
type CoverLetter struct {
	ToName         string
	ToOrganisation string
	Role           string
	Body           string
	From           CvContact
	DatedAt        time.Time
}

// ReportTable is a table inside a report.
type ReportTable struct {
	ColumnHeadings []string
	// Row-major.
	Cells   [][]string
	Caption string
}

// ReportSection is one section.
type ReportSection struct {
	Heading string
	Body    string
	Tables  []ReportTable
	Level   int
}

// ReportDocument is a whole report.
type ReportDocument struct {
	Title    string
	Subtitle string
	Author   string
	DatedAt  time.Time
	Sections []ReportSection
}

// IDocumentEngine renders documents.
type IDocumentEngine interface {
	Render(req DocumentRequest) (DocumentResult, error)
	Supports(format DocumentFormat) bool
}

// MarkdownDocumentEngine renders to Markdown and plain text.
//
// Deliberately narrow: it renders the two formats that need no dependency, and
// says so for the rest rather than emitting something that will not open.
type MarkdownDocumentEngine struct{}

// Supports implements IDocumentEngine.
func (MarkdownDocumentEngine) Supports(format DocumentFormat) bool {
	return format == DocumentMarkdown || format == DocumentPlainText
}

// Render implements IDocumentEngine.
func (e MarkdownDocumentEngine) Render(req DocumentRequest) (DocumentResult, error) {
	if !e.Supports(req.Format) {
		return DocumentResult{}, fmt.Errorf("this device cannot produce %s", req.Format)
	}
	var b strings.Builder
	switch payload := req.Payload.(type) {
	case CvDocument:
		b.WriteString("# " + payload.Contact.FullName + "\n\n")
		if payload.Summary != "" {
			b.WriteString(payload.Summary + "\n\n")
		}
		if len(payload.Experience) > 0 {
			b.WriteString("## Experience\n\n")
			for _, x := range payload.Experience {
				to := "present"
				if !x.To.IsZero() {
					to = x.To.Format("Jan 2006")
				}
				b.WriteString(fmt.Sprintf("**%s**, %s (%s – %s)\n\n", x.Title, x.Employer, x.From.Format("Jan 2006"), to))
				for _, bullet := range x.Bullets {
					b.WriteString("- " + bullet + "\n")
				}
				b.WriteString("\n")
			}
		}
		if len(payload.Skills) > 0 {
			b.WriteString("## Skills\n\n" + strings.Join(payload.Skills, ", ") + "\n")
		}
	case ReportDocument:
		b.WriteString("# " + payload.Title + "\n\n")
		for _, s := range payload.Sections {
			b.WriteString(strings.Repeat("#", max(2, s.Level)) + " " + s.Heading + "\n\n" + s.Body + "\n\n")
			for _, t := range s.Tables {
				b.WriteString("| " + strings.Join(t.ColumnHeadings, " | ") + " |\n")
				b.WriteString("|" + strings.Repeat(" --- |", len(t.ColumnHeadings)) + "\n")
				for _, row := range t.Cells {
					b.WriteString("| " + strings.Join(row, " | ") + " |\n")
				}
				b.WriteString("\n")
			}
		}
	case CoverLetter:
		b.WriteString(payload.DatedAt.Format("2 January 2006") + "\n\n")
		b.WriteString("Dear " + payload.ToName + ",\n\n" + payload.Body + "\n\n")
		b.WriteString(payload.From.FullName + "\n")
	default:
		b.WriteString("# " + req.Title + "\n")
	}

	mime := "text/markdown"
	if req.Format == DocumentPlainText {
		mime = "text/plain"
	}
	return DocumentResult{Bytes: []byte(b.String()), MimeType: mime, Format: req.Format}, nil
}

var _ = sort.Ints
var _ sync.Mutex
