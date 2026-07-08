// inference_bridge.go
//
// Ports CircleAI.Hosting.InferenceBridge:
//   IInferenceBridge, InferenceFragmentKind, InferenceFragment (IInferenceBridge.cs),
//   InferenceRequest (InferenceRequest.cs),
//   InferenceStatus, InferenceResponse (InferenceResponse.cs),
//   ModelDescriptor / ModelFormat + DeviceCapabilities (ModelDescriptor.cs / DeviceCapabilities.cs, minimal),
//   LocalProcessInferenceBridge (LocalProcessInferenceBridge.cs).
//
// This is the cross-OS contract every inference daemon satisfies plus the
// in-process reference bridge that wraps any IChatGenerator. The inference
// server's model registry, lifecycle manager, and endpoints all route through
// IInferenceBridge, so it is ported here alongside the server work unit.

package circleai

import (
	"context"
	"errors"
	"strings"
	"time"

	"github.com/google/uuid"
)

// InferenceFragmentKind classifies a fragment a streaming bridge emits. Ports
// CircleAI.Hosting.InferenceBridge.InferenceFragmentKind.
type InferenceFragmentKind int

const (
	// InferenceFragmentContent — part of the user-facing answer (OpenAI content).
	InferenceFragmentContent InferenceFragmentKind = 0
	// InferenceFragmentReasoning — part of the reasoning trace (OpenAI reasoning_content).
	InferenceFragmentReasoning InferenceFragmentKind = 1
)

// InferenceFragment is one fragment emitted by StreamFragments. Ports
// CircleAI.Hosting.InferenceBridge.InferenceFragment.
type InferenceFragment struct {
	Kind InferenceFragmentKind
	Text string
}

// InferenceStatus is the terminal state of a single inference call. Ports
// CircleAI.Hosting.InferenceBridge.InferenceStatus.
type InferenceStatus int

const (
	// InferenceStatusCompleted — model finished cleanly (end-of-turn token).
	InferenceStatusCompleted InferenceStatus = iota
	// InferenceStatusStoppedByToken — a StopSequence matched.
	InferenceStatusStoppedByToken
	// InferenceStatusStoppedByLength — MaxOutputTokens reached.
	InferenceStatusStoppedByLength
	// InferenceStatusFailed — bridge or model failed (see FailureMessage).
	InferenceStatusFailed
	// InferenceStatusCancelled — caller cancelled before completion.
	InferenceStatusCancelled
)

// InferenceRequest is one completion request submitted to an IInferenceBridge.
// Immutable; create new instances for retries. Ports
// CircleAI.Hosting.InferenceBridge.InferenceRequest.
type InferenceRequest struct {
	ID              uuid.UUID
	ModelID         string
	Prompt          string
	MaxOutputTokens int
	Temperature     float32
	TopP            float32
	StopSequences   []string
	Metadata        map[string]string
	RequestedAt     time.Time
}

// NewInferenceRequest is the convenience factory that stamps a fresh Id +
// RequestedAt and uses sensible defaults. Ports InferenceRequest.Create.
func NewInferenceRequest(modelID, prompt string, maxOutputTokens int, temperature, topP float32) (InferenceRequest, error) {
	if modelID == "" {
		return InferenceRequest{}, errors.New("modelId is required")
	}
	if maxOutputTokens <= 0 {
		maxOutputTokens = 256
	}
	return InferenceRequest{
		ID:              uuid.New(),
		ModelID:         modelID,
		Prompt:          prompt,
		MaxOutputTokens: maxOutputTokens,
		Temperature:     temperature,
		TopP:            topP,
		StopSequences:   []string{},
		Metadata:        map[string]string{},
		RequestedAt:     time.Now().UTC(),
	}, nil
}

// InferenceResponse is the result of a single completion call. Ports
// CircleAI.Hosting.InferenceBridge.InferenceResponse.
type InferenceResponse struct {
	RequestID        uuid.UUID
	ModelID          string
	OutputText       string
	OutputTokenCount int
	PromptTokenCount int
	Status           InferenceStatus
	InferenceMillis  float64
	FailureMessage   string
	CompletedAt      time.Time
	ReasoningText    string
}

// ModelFormat is the on-disk model file family. Ports
// CircleAI.Hosting.InferenceBridge.ModelFormat with the exact ordinals
// (Gguf=0, Onnx=1, CoreMl=2, Tflite=3, Unknown=4).
type ModelFormat int

const (
	// ModelFormatGguf — llama.cpp GGUF (general GGML universal format).
	ModelFormatGguf ModelFormat = iota
	// ModelFormatOnnx — ONNX Runtime model file.
	ModelFormatOnnx
	// ModelFormatCoreMl — Apple Core ML model package.
	ModelFormatCoreMl
	// ModelFormatTflite — TensorFlow Lite flatbuffer.
	ModelFormatTflite
	// ModelFormatUnknown — format not recognised or not yet classified.
	ModelFormatUnknown
)

// ModelDescriptor is the canonical descriptor for a loaded model. Minimal port
// of CircleAI.Hosting.InferenceBridge.ModelDescriptor — the fields the bridge
// and server surface actually read.
type ModelDescriptor struct {
	ModelID                string
	Version                string
	Format                 ModelFormat
	ContextWindowTokens    int
	VocabSize              int
	ParameterCount         int64
	QuantisationLabel      string
	ApproximateMemoryBytes int64
}

// DeviceCapabilities is the bridge's view of its host hardware. Minimal port of
// CircleAI.Hosting.InferenceBridge.DeviceCapabilities.
type DeviceCapabilities struct {
	OsName                      string
	OsVersion                   string
	PhysicalMemoryBytes         int64
	CpuCoreCount                int
	HasGpu                      bool
	GpuName                     string
	GpuMemoryBytes              int64
	HasNpu                      bool
	NpuName                     string
	HasTransportLayerEncryption bool
}

// IInferenceBridge is the cross-OS contract for an inference daemon. Ports
// CircleAI.Hosting.InferenceBridge.IInferenceBridge.
type IInferenceBridge interface {
	// ListLoadedModels returns a descriptor for every loaded model.
	ListLoadedModels(ctx context.Context) ([]ModelDescriptor, error)
	// IsModelLoaded reports whether modelId is loaded and ready.
	IsModelLoaded(ctx context.Context, modelID string) (bool, error)
	// Complete runs a single completion and returns the full response.
	Complete(ctx context.Context, request InferenceRequest) (InferenceResponse, error)
	// StreamCompletion streams content-only chunks. The channel is closed at end;
	// the error channel receives at most one error.
	StreamCompletion(ctx context.Context, request InferenceRequest) (<-chan string, <-chan error)
	// StreamFragments streams content + reasoning fragments tagged by kind.
	StreamFragments(ctx context.Context, request InferenceRequest) (<-chan InferenceFragment, <-chan error)
	// GetDeviceCapabilities returns the host hardware view.
	GetDeviceCapabilities(ctx context.Context) (DeviceCapabilities, error)
}

// GenerateResponseCapable is the optional extension a generator implements to
// surface a structured response (token counts, finish reason, reasoning). The
// bridge uses it when available; otherwise it falls back to Generate.
type GenerateResponseCapable interface {
	GenerateResponse(ctx context.Context, messages []ChatMessage, opts *GenerationOptions) (ChatResponse, error)
}

// LocalProcessInferenceBridge is the in-process IInferenceBridge that wraps any
// IChatGenerator. Ports CircleAI.Hosting.InferenceBridge.LocalProcessInferenceBridge.
// Transport-layer encryption is reported true — calls never leave the process.
type LocalProcessInferenceBridge struct {
	generator  IChatGenerator
	descriptor ModelDescriptor
	deviceCaps DeviceCapabilities
}

// NewLocalProcessInferenceBridge wraps generator for the model in descriptor.
// deviceCaps is the (already-probed) host view returned by GetDeviceCapabilities;
// the C# path probes lazily via ICapabilityProbe — here it is injected so the
// bridge holds no probe dependency.
func NewLocalProcessInferenceBridge(generator IChatGenerator, descriptor ModelDescriptor, deviceCaps DeviceCapabilities) (*LocalProcessInferenceBridge, error) {
	if generator == nil {
		return nil, errors.New("chatGenerator is required")
	}
	if strings.TrimSpace(descriptor.ModelID) == "" {
		return nil, errors.New("descriptor.ModelId is required")
	}
	deviceCaps.HasTransportLayerEncryption = true
	return &LocalProcessInferenceBridge{generator: generator, descriptor: descriptor, deviceCaps: deviceCaps}, nil
}

// ListLoadedModels returns the single wrapped descriptor.
func (b *LocalProcessInferenceBridge) ListLoadedModels(context.Context) ([]ModelDescriptor, error) {
	return []ModelDescriptor{b.descriptor}, nil
}

// IsModelLoaded reports whether modelId matches the wrapped descriptor.
func (b *LocalProcessInferenceBridge) IsModelLoaded(_ context.Context, modelID string) (bool, error) {
	if modelID == "" {
		return false, errors.New("modelId is required")
	}
	return b.descriptor.ModelID == modelID, nil
}

// Complete runs a completion via the wrapped generator. Ports CompleteImplAsync.
func (b *LocalProcessInferenceBridge) Complete(ctx context.Context, request InferenceRequest) (InferenceResponse, error) {
	if b.descriptor.ModelID != request.ModelID {
		return InferenceResponse{
			RequestID:      request.ID,
			ModelID:        request.ModelID,
			Status:         InferenceStatusFailed,
			FailureMessage: "Model '" + request.ModelID + "' is not loaded by this bridge (have '" + b.descriptor.ModelID + "').",
			CompletedAt:    time.Now().UTC(),
		}, nil
	}

	messages := []ChatMessage{{Role: "user", Content: request.Prompt}}
	opts := b.optionsFor(request)

	start := time.Now()
	var output, reasoning string
	if grc, ok := b.generator.(GenerateResponseCapable); ok {
		resp, err := grc.GenerateResponse(ctx, messages, opts)
		if err != nil {
			return b.completeError(request, err, start), nil
		}
		output = resp.Text
		reasoning = resp.ReasoningContent
	} else {
		text, err := b.generator.Generate(ctx, messages, opts)
		if err != nil {
			return b.completeError(request, err, start), nil
		}
		output = text
	}
	elapsed := float64(time.Since(start).Nanoseconds()) / 1e6

	status := determineInferenceStatus(output, request)
	return InferenceResponse{
		RequestID:        request.ID,
		ModelID:          request.ModelID,
		OutputText:       output,
		OutputTokenCount: estimateTokenCount(output),
		PromptTokenCount: estimateTokenCount(request.Prompt),
		Status:           status,
		InferenceMillis:  elapsed,
		CompletedAt:      time.Now().UTC(),
		ReasoningText:    reasoning,
	}, nil
}

func (b *LocalProcessInferenceBridge) completeError(request InferenceRequest, err error, start time.Time) InferenceResponse {
	elapsed := float64(time.Since(start).Nanoseconds()) / 1e6
	status := InferenceStatusFailed
	msg := err.Error()
	if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		status = InferenceStatusCancelled
		msg = ""
	}
	return InferenceResponse{
		RequestID:        request.ID,
		ModelID:          request.ModelID,
		OutputText:       "",
		OutputTokenCount: 0,
		PromptTokenCount: estimateTokenCount(request.Prompt),
		Status:           status,
		InferenceMillis:  elapsed,
		FailureMessage:   msg,
		CompletedAt:      time.Now().UTC(),
	}
}

// StreamCompletion streams content-only chunks, falling back to a single full
// completion when the generator streams nothing. Ports StreamCompletionImplAsync.
func (b *LocalProcessInferenceBridge) StreamCompletion(ctx context.Context, request InferenceRequest) (<-chan string, <-chan error) {
	out := make(chan string)
	errc := make(chan error, 1)
	if b.descriptor.ModelID != request.ModelID {
		close(out)
		close(errc)
		return out, errc
	}
	messages := []ChatMessage{{Role: "user", Content: request.Prompt}}
	opts := b.optionsFor(request)

	go func() {
		defer close(out)
		defer close(errc)
		chunks, cerrs := b.generator.Stream(ctx, messages, opts)
		hasYielded := false
		for c := range chunks {
			hasYielded = true
			select {
			case out <- c:
			case <-ctx.Done():
				errc <- ctx.Err()
				return
			}
		}
		if err := <-cerrs; err != nil {
			errc <- err
			return
		}
		if !hasYielded {
			full, err := b.generator.Generate(ctx, messages, opts)
			if err != nil {
				errc <- err
				return
			}
			select {
			case out <- full:
			case <-ctx.Done():
				errc <- ctx.Err()
			}
		}
	}()
	return out, errc
}

// StreamFragments streams content + reasoning fragments. Ports StreamFragmentsImplAsync.
func (b *LocalProcessInferenceBridge) StreamFragments(ctx context.Context, request InferenceRequest) (<-chan InferenceFragment, <-chan error) {
	out := make(chan InferenceFragment)
	errc := make(chan error, 1)
	if b.descriptor.ModelID != request.ModelID {
		close(out)
		close(errc)
		return out, errc
	}
	messages := []ChatMessage{{Role: "user", Content: request.Prompt}}
	opts := b.optionsFor(request)

	go func() {
		defer close(out)
		defer close(errc)
		frags, ferrs := StreamFragments(ctx, b.generator, messages, opts)
		for f := range frags {
			kind := InferenceFragmentContent
			if f.Kind == ChatFragmentReasoning {
				kind = InferenceFragmentReasoning
			}
			select {
			case out <- InferenceFragment{Kind: kind, Text: f.Text}:
			case <-ctx.Done():
				errc <- ctx.Err()
				return
			}
		}
		if err := <-ferrs; err != nil {
			errc <- err
		}
	}()
	return out, errc
}

// GetDeviceCapabilities returns the injected host view.
func (b *LocalProcessInferenceBridge) GetDeviceCapabilities(context.Context) (DeviceCapabilities, error) {
	return b.deviceCaps, nil
}

func (b *LocalProcessInferenceBridge) optionsFor(request InferenceRequest) *GenerationOptions {
	o := DefaultGenerationOptions()
	o.MaxTokens = request.MaxOutputTokens
	o.Temperature = request.Temperature
	o.TopP = request.TopP
	if len(request.StopSequences) > 0 {
		o.StopSequences = request.StopSequences
	}
	return &o
}

// determineInferenceStatus classifies terminal state. Ports DetermineStatus.
func determineInferenceStatus(output string, request InferenceRequest) InferenceStatus {
	for _, s := range request.StopSequences {
		if s != "" && strings.Contains(output, s) {
			return InferenceStatusStoppedByToken
		}
	}
	if estimateTokenCount(output) >= request.MaxOutputTokens {
		return InferenceStatusStoppedByLength
	}
	return InferenceStatusCompleted
}

// estimateTokenCount is the ~4-chars-per-token heuristic, min 1. Ports EstimateTokenCount.
func estimateTokenCount(text string) int {
	if text == "" {
		return 0
	}
	n := len(text) / 4
	if n < 1 {
		return 1
	}
	return n
}

var _ IInferenceBridge = (*LocalProcessInferenceBridge)(nil)
