// vision_face_embedder.go
//
// Ports CircleAI.Vision.OnnxFaceEmbedder (OnnxFaceEmbedder.cs):
//   OnnxFaceEmbedderOptions -> OnnxFaceEmbedderOptions
//   OnnxFaceEmbedder        -> OnnxFaceEmbedder (deterministic, model injected)
//
// The C# original wires an ArcFace ONNX session + ImageSharp crop/resize. Both are
// native/host dependencies, injected here behind the shared ImageDecoder and an
// EmbedderModel callback. The load-bearing algorithm — ClampRegion, the crop-then-
// resize to InputSize, the ArcFace BGR (pixel-127.5)/128 preprocess into a
// [1,3,S,S] tensor, and the output L2Normalise — is a faithful transliteration of
// the C#. A model error degrades to a zero vector (the C# catch).

package circleai

import (
	"context"
	"errors"
)

// OnnxFaceEmbedderOptions mirrors the OnnxFaceEmbedderOptions record.
//
//	InputSize square input dimension (112 = ArcFace default).
//	Dimension output embedding dimension (typically 512).
//
// ModelPath is retained for parity (the native session is injected here).
type OnnxFaceEmbedderOptions struct {
	ModelPath string
	InputSize int
	Dimension int
}

// DefaultOnnxFaceEmbedderOptions returns the C# record defaults (InputSize 112,
// Dimension 512) for the given model path.
func DefaultOnnxFaceEmbedderOptions(modelPath string) OnnxFaceEmbedderOptions {
	return OnnxFaceEmbedderOptions{ModelPath: modelPath, InputSize: 112, Dimension: 512}
}

// EmbedderModel runs the ArcFace model on the preprocessed [1,3,S,S] tensor and
// returns the raw embedding (before re-normalisation). This is the injected native
// ONNX session. An error mirrors the C# catch that degrades to a zero vector.
type EmbedderModel func(ctx context.Context, input DetectorTensor) ([]float32, error)

// OnnxFaceEmbedder is the deterministic port of the C# OnnxFaceEmbedder.
type OnnxFaceEmbedder struct {
	opts    OnnxFaceEmbedderOptions
	decoder ImageDecoder
	model   EmbedderModel
}

// NewOnnxFaceEmbedder constructs an embedder. decoder and model are the injected
// native dependencies and are required. InputSize defaults to 112 and Dimension to
// 512 when 0.
func NewOnnxFaceEmbedder(opts OnnxFaceEmbedderOptions, decoder ImageDecoder, model EmbedderModel) (*OnnxFaceEmbedder, error) {
	if decoder == nil {
		return nil, errors.New("OnnxFaceEmbedder: decoder required")
	}
	if model == nil {
		return nil, errors.New("OnnxFaceEmbedder: model required")
	}
	if opts.InputSize == 0 {
		opts.InputSize = 112
	}
	if opts.Dimension == 0 {
		opts.Dimension = 512
	}
	return &OnnxFaceEmbedder{opts: opts, decoder: decoder, model: model}, nil
}

// Dimension returns the configured embedding dimension.
func (e *OnnxFaceEmbedder) Dimension() int { return e.opts.Dimension }

// EmbedAsync ports OnnxFaceEmbedder.EmbedAsync: decode, clamp+crop+resize the face
// region, ArcFace-preprocess, run the model, L2-normalise. A model error degrades to
// a zero vector at the configured dimension.
func (e *OnnxFaceEmbedder) EmbedAsync(ctx context.Context, imageBytes []byte, face DetectedFace) (FaceEmbedding, error) {
	if err := ctx.Err(); err != nil {
		return FaceEmbedding{}, err
	}

	img, err := e.decoder(ctx, imageBytes)
	if err != nil {
		return FaceEmbedding{}, err
	}

	region := clampRegion(face.Region, img.Width, img.Height)
	crop := cropAndResize(img, region, e.opts.InputSize)

	// ArcFace BGR mean-subtracted + scaled: (pixel - 127.5) / 128.0. Channel 0 = B,
	// channel 1 = G, channel 2 = R.
	s := e.opts.InputSize
	data := make([]float32, 3*s*s)
	for y := 0; y < s; y++ {
		row := crop.Pixels[y]
		for x := 0; x < s; x++ {
			i := x * 3
			r := float32(row[i])
			g := float32(row[i+1])
			b := float32(row[i+2])
			base := y*s + x
			data[0*s*s+base] = (b - 127.5) / 128.0
			data[1*s*s+base] = (g - 127.5) / 128.0
			data[2*s*s+base] = (r - 127.5) / 128.0
		}
	}
	tensor := DetectorTensor{Channels: 3, Height: s, Width: s, Data: data}

	raw, err := e.model(ctx, tensor)
	if err != nil {
		return FaceEmbedding{Vector: make([]float32, e.opts.Dimension), Dimension: e.opts.Dimension}, nil
	}
	out := make([]float32, len(raw))
	copy(out, raw)
	l2Normalise(out)
	return FaceEmbedding{Vector: out, Dimension: len(out)}, nil
}

// clampRegion ports the C# ClampRegion: clamp x,y into [0, dim-1] and w,h into
// [1, dim-x] so the crop rectangle stays inside the image.
func clampRegion(region BoundingBox, imageWidth, imageHeight int) BoundingBox {
	x := clampInt(region.X, 0, imageWidth-1)
	y := clampInt(region.Y, 0, imageHeight-1)
	w := clampInt(region.Width, 1, imageWidth-x)
	h := clampInt(region.Height, 1, imageHeight-y)
	return BoundingBox{X: x, Y: y, Width: w, Height: h}
}

// cropAndResize crops the region out of img then nearest-neighbour resizes it to a
// size×size square — the Go analogue of ImageSharp Crop(...).Resize(size, size).
func cropAndResize(img DecodedImage, region BoundingBox, size int) DecodedImage {
	rows := make([][]byte, size)
	for dy := 0; dy < size; dy++ {
		row := make([]byte, size*3)
		srcY := region.Y + int(float64(dy)*float64(region.Height)/float64(size))
		if srcY >= region.Y+region.Height {
			srcY = region.Y + region.Height - 1
		}
		for dx := 0; dx < size; dx++ {
			srcX := region.X + int(float64(dx)*float64(region.Width)/float64(size))
			if srcX >= region.X+region.Width {
				srcX = region.X + region.Width - 1
			}
			r, g, b := img.At(srcX, srcY)
			di := dx * 3
			row[di], row[di+1], row[di+2] = r, g, b
		}
		rows[dy] = row
	}
	return DecodedImage{Width: size, Height: size, Pixels: rows}
}

// (l2Normalise — L2-normalise a float32 vector in place, no-op under a 1e-9 norm —
// and clampInt are each defined once elsewhere in the flat package and reused here.
// The C# OnnxFaceEmbedder.L2Normalise is byte-identical to voice_speaker_identity's.)

// Interface guard.
var _ IFaceEmbedder = (*OnnxFaceEmbedder)(nil)
