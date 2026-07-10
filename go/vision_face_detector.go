// vision_face_detector.go
//
// Ports CircleAI.Vision.OnnxFaceDetector (OnnxFaceDetector.cs):
//   OnnxFaceDetectorOptions -> OnnxFaceDetectorOptions
//   OnnxFaceDetector        -> OnnxFaceDetector (deterministic, model injected)
//
// The C# original wires an ONNX InferenceSession + ImageSharp decode/letterbox.
// Both are native/host dependencies, so this Go port injects them behind two
// callbacks:
//   - a decoder (raw bytes -> width,height,RGB rows) so the letterbox math and the
//     ToTensor NCHW packing are exercised exactly, and
//   - a model runner (input tensor -> [1, channels, boxes] output) so the YOLO
//     postprocess (box decode, back-projection, NMS) is exercised exactly.
// Every numeric step — LetterboxResize, ToTensor, PostprocessYolo, NonMaxSuppression,
// Iou — is a faithful transliteration of the C#. With a decoder+runner injected the
// detector is a pure, deterministic function of its inputs.

package circleai

import (
	"context"
	"errors"
	"math"
	"sort"
)

// OnnxFaceDetectorOptions mirrors the OnnxFaceDetectorOptions record.
//
//	InputSize           square input dimension (640 = YOLOv8 default).
//	ConfidenceThreshold skip detections under this score (0..1).
//	IouThreshold        NMS IoU cutoff (0..1).
//
// ModelPath is retained for parity with the C# record (the native session is
// injected here, so the port does not open a file).
type OnnxFaceDetectorOptions struct {
	ModelPath           string
	InputSize           int
	ConfidenceThreshold float32
	IouThreshold        float32
}

// DefaultOnnxFaceDetectorOptions returns the C# record defaults (InputSize 640,
// ConfidenceThreshold 0.5, IouThreshold 0.45) for the given model path.
func DefaultOnnxFaceDetectorOptions(modelPath string) OnnxFaceDetectorOptions {
	return OnnxFaceDetectorOptions{
		ModelPath:           modelPath,
		InputSize:           640,
		ConfidenceThreshold: 0.5,
		IouThreshold:        0.45,
	}
}

// DecodedImage is the injected-decoder output: pixel dimensions plus row-major RGB.
// Pixels[y] is a row of length Width*3, laid out R,G,B per pixel (0..255). This is
// the Go analogue of an ImageSharp Image<Rgb24>.
type DecodedImage struct {
	Width  int
	Height int
	// Pixels is Height rows, each Width*3 bytes (R,G,B interleaved).
	Pixels [][]byte
}

// At returns the R,G,B bytes at (x,y). Out-of-range yields the letterbox grey the
// C# canvas is filled with (114,114,114) so padded regions read identically.
func (d DecodedImage) At(x, y int) (r, g, b byte) {
	if y < 0 || y >= d.Height || x < 0 || x >= d.Width {
		return 114, 114, 114
	}
	row := d.Pixels[y]
	i := x * 3
	return row[i], row[i+1], row[i+2]
}

// ImageDecoder decodes raw image bytes into a DecodedImage. Injected — the native
// ImageSharp decode lives in the host; in-memory tests supply raw RGB directly.
type ImageDecoder func(ctx context.Context, imageBytes []byte) (DecodedImage, error)

// DetectorTensor is a [1,3,H,W] NCHW float32 tensor (batch fixed at 1). Channels are
// indexed [c] then row-major [y*W+x], matching DenseTensor<float>([1,3,H,W]).
type DetectorTensor struct {
	Channels int
	Height   int
	Width    int
	// Data is Channels*Height*Width, index = c*H*W + y*W + x.
	Data []float32
}

// At returns the value at (channel, y, x).
func (t DetectorTensor) At(c, y, x int) float32 {
	return t.Data[c*t.Height*t.Width+y*t.Width+x]
}

// DetectorOutput is the model output tensor [1, channels, boxes] flattened as the C#
// `output.ToArray()` (index = c*boxes + n). Channels is dims[1], Boxes is dims[2].
type DetectorOutput struct {
	Channels int
	Boxes    int
	// Data is Channels*Boxes, index = c*Boxes + n.
	Data []float32
}

// DetectorModel runs inference on the input tensor and returns the raw output tensor.
// This is the injected native ONNX session (InferenceSession.Run). Returning an error
// mirrors the C# catch that degrades to an empty result.
type DetectorModel func(ctx context.Context, input DetectorTensor) (DetectorOutput, error)

// OnnxFaceDetector is the deterministic port of the C# OnnxFaceDetector. It runs the
// exact letterbox + YOLO-postprocess pipeline over an injected decoder + model.
type OnnxFaceDetector struct {
	opts    OnnxFaceDetectorOptions
	decoder ImageDecoder
	model   DetectorModel
}

// NewOnnxFaceDetector constructs a detector. decoder and model are the injected
// native dependencies and are required (the C# constructor requires a real model
// file + ImageSharp). InputSize defaults to 640 when 0.
func NewOnnxFaceDetector(opts OnnxFaceDetectorOptions, decoder ImageDecoder, model DetectorModel) (*OnnxFaceDetector, error) {
	if decoder == nil {
		return nil, errors.New("OnnxFaceDetector: decoder required")
	}
	if model == nil {
		return nil, errors.New("OnnxFaceDetector: model required")
	}
	if opts.InputSize == 0 {
		opts.InputSize = 640
	}
	return &OnnxFaceDetector{opts: opts, decoder: decoder, model: model}, nil
}

// DetectAsync ports OnnxFaceDetector.DetectAsync. Empty input -> empty slice. A model
// error degrades to an empty slice (the C# catch). Cancellation is honoured up front.
func (d *OnnxFaceDetector) DetectAsync(ctx context.Context, imageBytes []byte) ([]DetectedFace, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if len(imageBytes) == 0 {
		return []DetectedFace{}, nil
	}

	img, err := d.decoder(ctx, imageBytes)
	if err != nil {
		return nil, err
	}
	origW, origH := img.Width, img.Height

	canvas, padX, padY, scale := letterboxResize(img, d.opts.InputSize)
	tensor := detectorToTensor(canvas)

	out, err := d.model(ctx, tensor)
	if err != nil {
		// Mirror the C# catch: inference failure -> empty result.
		return []DetectedFace{}, nil
	}
	return d.postprocessYolo(out, origW, origH, padX, padY, scale), nil
}

// letterboxResize ports the C# LetterboxResize: scale to fit InputSize preserving
// aspect, centre on a 114-grey square canvas. Returns the padded DecodedImage plus
// the pad offsets and scale needed to back-project boxes.
func letterboxResize(img DecodedImage, inputSize int) (DecodedImage, int, int, float32) {
	scale := float32(math.Min(float64(inputSize)/float64(img.Width), float64(inputSize)/float64(img.Height)))
	newW := int(math.Round(float64(img.Width) * float64(scale)))
	newH := int(math.Round(float64(img.Height) * float64(scale)))
	padX := (inputSize - newW) / 2
	padY := (inputSize - newH) / 2

	// Build the 114-grey canvas and draw the nearest-neighbour resized image at
	// (padX, padY). Nearest-neighbour keeps the transform deterministic and is the
	// exact inverse of the (cx - padX)/scale back-projection used downstream.
	rows := make([][]byte, inputSize)
	for y := 0; y < inputSize; y++ {
		row := make([]byte, inputSize*3)
		for x := 0; x < inputSize*3; x++ {
			row[x] = 114
		}
		rows[y] = row
	}
	for dy := 0; dy < newH; dy++ {
		srcY := int(float64(dy) / float64(scale))
		if srcY >= img.Height {
			srcY = img.Height - 1
		}
		cy := padY + dy
		if cy < 0 || cy >= inputSize {
			continue
		}
		dst := rows[cy]
		for dx := 0; dx < newW; dx++ {
			srcX := int(float64(dx) / float64(scale))
			if srcX >= img.Width {
				srcX = img.Width - 1
			}
			cx := padX + dx
			if cx < 0 || cx >= inputSize {
				continue
			}
			r, g, b := img.At(srcX, srcY)
			di := cx * 3
			dst[di], dst[di+1], dst[di+2] = r, g, b
		}
	}
	return DecodedImage{Width: inputSize, Height: inputSize, Pixels: rows}, padX, padY, scale
}

// detectorToTensor ports the C# ToTensor: pack an RGB image into a [1,3,H,W] NCHW
// tensor with channel values pixel/255f.
func detectorToTensor(img DecodedImage) DetectorTensor {
	w, h := img.Width, img.Height
	data := make([]float32, 3*h*w)
	for y := 0; y < h; y++ {
		row := img.Pixels[y]
		for x := 0; x < w; x++ {
			i := x * 3
			base := y*w + x
			data[0*h*w+base] = float32(row[i]) / 255
			data[1*h*w+base] = float32(row[i+1]) / 255
			data[2*h*w+base] = float32(row[i+2]) / 255
		}
	}
	return DetectorTensor{Channels: 3, Height: h, Width: w, Data: data}
}

// scoredBox is a (score, box) candidate used through postprocessing / NMS.
type scoredBox struct {
	Score float32
	Box   BoundingBox
}

// postprocessYolo ports OnnxFaceDetector.PostprocessYolo. YOLOv8 output layout is
// [1, 4+1+K, boxes]; we read the first 5 channels per box (cx, cy, w, h, score),
// back-project from letterbox space to original pixels, threshold, then NMS.
func (d *OnnxFaceDetector) postprocessYolo(output DetectorOutput, origW, origH, padX, padY int, scale float32) []DetectedFace {
	if output.Channels < 5 {
		return []DetectedFace{}
	}
	boxes := output.Boxes
	arr := output.Data
	candidates := make([]scoredBox, 0, boxes)
	for n := 0; n < boxes; n++ {
		cx := arr[0*boxes+n]
		cy := arr[1*boxes+n]
		bw := arr[2*boxes+n]
		bh := arr[3*boxes+n]
		score := arr[4*boxes+n]
		if score < d.opts.ConfidenceThreshold {
			continue
		}
		// Back-project from letterbox space to original pixel space.
		x1 := (cx - bw/2 - float32(padX)) / scale
		y1 := (cy - bh/2 - float32(padY)) / scale
		x2 := (cx + bw/2 - float32(padX)) / scale
		y2 := (cy + bh/2 - float32(padY)) / scale
		bx := max(0, int(math.Floor(float64(x1))))
		by := max(0, int(math.Floor(float64(y1))))
		bxw := min(origW-bx, int(math.Ceil(float64(x2-x1))))
		bxh := min(origH-by, int(math.Ceil(float64(y2-y1))))
		if bxw <= 0 || bxh <= 0 {
			continue
		}
		candidates = append(candidates, scoredBox{Score: score, Box: BoundingBox{X: bx, Y: by, Width: bxw, Height: bxh}})
	}
	kept := nonMaxSuppression(candidates, d.opts.IouThreshold)
	faces := make([]DetectedFace, 0, len(kept))
	for _, c := range kept {
		faces = append(faces, DetectedFace{Region: c.Box, Confidence: c.Score, Landmarks: nil})
	}
	return faces
}

// nonMaxSuppression ports the C# NonMaxSuppression: sort by score desc, greedily keep
// a box unless it overlaps an already-kept box beyond iouThreshold.
func nonMaxSuppression(boxes []scoredBox, iouThreshold float32) []scoredBox {
	// Stable sort by score desc so equal-score ordering is deterministic (the C#
	// List.Sort is not stable, but the box set is deduped by IoU so a stable order
	// only makes the port more predictable — never changes which boxes survive when
	// scores differ).
	sort.SliceStable(boxes, func(i, j int) bool { return boxes[i].Score > boxes[j].Score })
	kept := make([]scoredBox, 0, len(boxes))
	for _, cand := range boxes {
		keep := true
		for _, k := range kept {
			if iou(cand.Box, k.Box) > iouThreshold {
				keep = false
				break
			}
		}
		if keep {
			kept = append(kept, cand)
		}
	}
	return kept
}

// iou ports the C# Iou: intersection-over-union of two axis-aligned boxes.
func iou(a, b BoundingBox) float32 {
	ax2 := a.X + a.Width
	ay2 := a.Y + a.Height
	bx2 := b.X + b.Width
	by2 := b.Y + b.Height
	ix1 := max(a.X, b.X)
	iy1 := max(a.Y, b.Y)
	ix2 := min(ax2, bx2)
	iy2 := min(ay2, by2)
	iw := max(0, ix2-ix1)
	ih := max(0, iy2-iy1)
	inter := iw * ih
	union := a.Width*a.Height + b.Width*b.Height - inter
	if union == 0 {
		return 0
	}
	return float32(inter) / float32(union)
}

// (maxInt / minInt are defined once elsewhere in the flat package and reused here.)

// Interface guard.
var _ IFaceDetector = (*OnnxFaceDetector)(nil)
