// vision_input.go
//
// Ports CircleAI.Inference.VisionInput (VisionInput.cs).
//
// Raw image data to be embedded by a vision encoder before text generation
// begins. Consumed by a vision-capable IChatGenerator; text-only generators
// ignore it.

package circleai

import "errors"

// VisionInput is raw image data to be embedded by the vision encoder before
// text generation begins. Ports CircleAI.Inference.VisionInput.
type VisionInput struct {
	// ImageBytes is the raw image payload (JPEG, PNG, or any format the encoder
	// accepts). Required — C# marks it `required byte[]`.
	ImageBytes []byte

	// MimeType is an optional MIME hint (e.g. "image/jpeg"). Not passed to the
	// native encoder directly; useful for callers to track format.
	MimeType string
}

// NewVisionInput builds a VisionInput, enforcing the C# `required` contract on
// ImageBytes (non-nil, non-empty).
func NewVisionInput(imageBytes []byte, mimeType string) (VisionInput, error) {
	if len(imageBytes) == 0 {
		return VisionInput{}, errors.New("VisionInput.ImageBytes is required")
	}
	b := make([]byte, len(imageBytes))
	copy(b, imageBytes)
	return VisionInput{ImageBytes: b, MimeType: mimeType}, nil
}
