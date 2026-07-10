// vision_plate_recognizer.go
//
// Ports CircleAI.Vision.OnnxPlateRecognizer (OnnxPlateRecognizer.cs):
//   OnnxPlateRecognizerOptions -> OnnxPlateRecognizerOptions
//   OnnxPlateRecognizer        -> OnnxPlateRecognizer (deterministic, model injected)
//
// Same letterbox + YOLO-postprocess + NMS pattern as OnnxFaceDetector, but emits
// PlateRecognitionResult records and leaves PlateText empty (the OCR pass is a
// separate model in the C#). The native ONNX session + ImageSharp decode are
// injected behind the shared DetectorModel + ImageDecoder. The plate postprocess
// differs from the face one in exactly two ways, both preserved verbatim from the
// C#: box width/height are derived from bw/scale, bh/scale (not x2-x1, y2-y1), and
// each result carries the configured CountryHint.

package circleai

import (
	"context"
	"errors"
	"math"
	"sort"
)

// OnnxPlateRecognizerOptions mirrors the OnnxPlateRecognizerOptions record.
//
//	InputSize           square input dimension (640).
//	ConfidenceThreshold skip detections under this score (0..1).
//	IouThreshold        NMS IoU cutoff (0..1).
//	CountryHint         optional country hint stamped onto every result ("" = null).
//
// ModelPath is retained for parity (the native session is injected here).
type OnnxPlateRecognizerOptions struct {
	ModelPath           string
	InputSize           int
	ConfidenceThreshold float32
	IouThreshold        float32
	CountryHint         string
}

// DefaultOnnxPlateRecognizerOptions returns the C# record defaults (InputSize 640,
// ConfidenceThreshold 0.5, IouThreshold 0.45, CountryHint null) for the given path.
func DefaultOnnxPlateRecognizerOptions(modelPath string) OnnxPlateRecognizerOptions {
	return OnnxPlateRecognizerOptions{
		ModelPath:           modelPath,
		InputSize:           640,
		ConfidenceThreshold: 0.5,
		IouThreshold:        0.45,
		CountryHint:         "",
	}
}

// OnnxPlateRecognizer is the deterministic port of the C# OnnxPlateRecognizer.
type OnnxPlateRecognizer struct {
	opts    OnnxPlateRecognizerOptions
	decoder ImageDecoder
	model   DetectorModel
}

// NewOnnxPlateRecognizer constructs a recognizer. decoder and model are the injected
// native dependencies and are required. InputSize defaults to 640 when 0.
func NewOnnxPlateRecognizer(opts OnnxPlateRecognizerOptions, decoder ImageDecoder, model DetectorModel) (*OnnxPlateRecognizer, error) {
	if decoder == nil {
		return nil, errors.New("OnnxPlateRecognizer: decoder required")
	}
	if model == nil {
		return nil, errors.New("OnnxPlateRecognizer: model required")
	}
	if opts.InputSize == 0 {
		opts.InputSize = 640
	}
	return &OnnxPlateRecognizer{opts: opts, decoder: decoder, model: model}, nil
}

// RecognizeAsync ports OnnxPlateRecognizer.RecognizeAsync. Empty input -> empty
// slice. A model error degrades to an empty slice (the C# catch). Cancellation is
// honoured up front.
func (p *OnnxPlateRecognizer) RecognizeAsync(ctx context.Context, imageBytes []byte) ([]PlateRecognitionResult, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if len(imageBytes) == 0 {
		return []PlateRecognitionResult{}, nil
	}

	img, err := p.decoder(ctx, imageBytes)
	if err != nil {
		return nil, err
	}
	origW, origH := img.Width, img.Height

	// The C# computes the letterbox transform inline; letterboxResize produces the
	// identical padded canvas + padX/padY/scale.
	canvas, padX, padY, scale := letterboxResize(img, p.opts.InputSize)
	tensor := detectorToTensor(canvas)

	out, err := p.model(ctx, tensor)
	if err != nil {
		return []PlateRecognitionResult{}, nil
	}
	if out.Channels < 5 {
		return []PlateRecognitionResult{}, nil
	}

	boxes := out.Boxes
	arr := out.Data
	hits := make([]scoredBox, 0, boxes)
	for n := 0; n < boxes; n++ {
		cx := arr[0*boxes+n]
		cy := arr[1*boxes+n]
		bw := arr[2*boxes+n]
		bh := arr[3*boxes+n]
		score := arr[4*boxes+n]
		if score < p.opts.ConfidenceThreshold {
			continue
		}
		x1 := (cx - bw/2 - float32(padX)) / scale
		y1 := (cy - bh/2 - float32(padY)) / scale
		bx := max(0, int(math.Floor(float64(x1))))
		by := max(0, int(math.Floor(float64(y1))))
		// Plate postprocess derives dimensions from bw/scale, bh/scale (verbatim C#).
		bxw := min(origW-bx, int(math.Ceil(float64(bw/scale))))
		bxh := min(origH-by, int(math.Ceil(float64(bh/scale))))
		if bxw <= 0 || bxh <= 0 {
			continue
		}
		hits = append(hits, scoredBox{Score: score, Box: BoundingBox{X: bx, Y: by, Width: bxw, Height: bxh}})
	}

	// Inline NMS, matching the C# (sort desc, greedy keep under IoU threshold).
	sort.SliceStable(hits, func(i, j int) bool { return hits[i].Score > hits[j].Score })
	kept := make([]scoredBox, 0, len(hits))
	for _, c := range hits {
		keep := true
		for _, k := range kept {
			if iou(c.Box, k.Box) > p.opts.IouThreshold {
				keep = false
				break
			}
		}
		if keep {
			kept = append(kept, c)
		}
	}

	results := make([]PlateRecognitionResult, 0, len(kept))
	for _, k := range kept {
		results = append(results, PlateRecognitionResult{
			PlateText:   "", // OCR pass is a separate model — left to a follow-up.
			CountryHint: p.opts.CountryHint,
			Region:      k.Box,
			Confidence:  k.Score,
		})
	}
	return results, nil
}

// Interface guard.
var _ IPlateRecognizer = (*OnnxPlateRecognizer)(nil)
