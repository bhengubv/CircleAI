// hosting_inference_bridge_test.go
//
// Verifies the CircleAI.Hosting.InferenceBridge.ModelFormat enum is at parity
// with the C# spec (Gguf=0, Onnx=1, CoreMl=2, Tflite=3, Unknown=4) after the
// alignment fix, and that a LocalProcessInferenceBridge built with a spec
// ModelFormat still routes a completion through the wrapped generator.

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestModelFormat_Ordinals(t *testing.T) {
	pairs := []struct {
		got  circleai.ModelFormat
		want int
	}{
		{circleai.ModelFormatGguf, 0},
		{circleai.ModelFormatOnnx, 1},
		{circleai.ModelFormatCoreMl, 2},
		{circleai.ModelFormatTflite, 3},
		{circleai.ModelFormatUnknown, 4},
	}
	for _, p := range pairs {
		if int(p.got) != p.want {
			t.Errorf("ModelFormat ordinal: got %d, want %d", int(p.got), p.want)
		}
	}
}

func TestLocalProcessInferenceBridge_WithSpecFormat(t *testing.T) {
	ctx := context.Background()
	gen := &scriptGenerator{fn: func(m []circleai.ChatMessage) string { return "bridge-out" }}
	descriptor := circleai.ModelDescriptor{
		ModelID:             "onnx-model",
		Version:             "1.0.0",
		Format:              circleai.ModelFormatOnnx,
		ContextWindowTokens: 4096,
	}
	bridge, err := circleai.NewLocalProcessInferenceBridge(gen, descriptor, circleai.DeviceCapabilities{})
	if err != nil {
		t.Fatalf("bridge ctor: %v", err)
	}

	req, err := circleai.NewInferenceRequest("onnx-model", "hi", 256, 0.7, 0.95)
	if err != nil {
		t.Fatalf("request: %v", err)
	}
	resp, err := bridge.Complete(ctx, req)
	if err != nil {
		t.Fatalf("complete: %v", err)
	}
	if resp.OutputText != "bridge-out" {
		t.Errorf("output = %q, want bridge-out", resp.OutputText)
	}
	if resp.Status != circleai.InferenceStatusCompleted {
		t.Errorf("status = %d, want Completed", resp.Status)
	}

	models, _ := bridge.ListLoadedModels(ctx)
	if len(models) != 1 || models[0].Format != circleai.ModelFormatOnnx {
		t.Errorf("loaded models = %+v", models)
	}
}
