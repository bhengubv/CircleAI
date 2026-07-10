// speech_noise_reducers.go
//
// Ports CircleAI.Speech.NoiseReducers.cs:
//   - NullNoiseReducer: no-op pass-through with BackendId="null".
//   - SpectralSubtractionNoiseReducer: lightweight no-model floor-noise
//     subtraction in the time domain (envelope-following gate, BackendId="passthrough").
//   - INoiseReducerModelRunner: host-supplied DNN runner seam.
//   - KrispNoiseReducer / DeepFilterNetNoiseReducer: thin shells that delegate
//     to the runner, falling back to spectral subtraction when none is wired.
//
// The gate arithmetic reproduces the C# exactly: samples are read as
// little-endian int16, |s| <= floor are attenuated by multiplying the int sample
// by the float attenuation and truncating to int16; everything else passes
// through unchanged.

package circleai

import (
	"encoding/binary"
	"math"
)

// NullNoiseReducer is the no-op DI default. Ports NullNoiseReducer.
type NullNoiseReducer struct{}

// NullNoiseReducerInstance mirrors NullNoiseReducer.Instance.
var NullNoiseReducerInstance = NullNoiseReducer{}

// BackendID returns "null".
func (NullNoiseReducer) BackendID() string { return "null" }

// IsAvailable returns true.
func (NullNoiseReducer) IsAvailable() bool { return true }

// Reduce copies the input through unchanged.
func (NullNoiseReducer) Reduce(audioPcm16Mono []byte, _ int, destination []byte) int {
	copy(destination, audioPcm16Mono)
	return len(audioPcm16Mono)
}

// SpectralSubtractionNoiseReducer is a lightweight time-domain noise gate: it
// attenuates samples whose magnitude is below a fixed noise floor with a soft
// knee. Zero runtime cost, works on every device. Ports
// SpectralSubtractionNoiseReducer.
type SpectralSubtractionNoiseReducer struct {
	floorEstimate float32
	attenuation   float32
}

// NewSpectralSubtractionNoiseReducer constructs a gate. Defaults:
// floorEstimate=0.008, attenuation=0.25. Ports the constructor.
func NewSpectralSubtractionNoiseReducer(floorEstimate, attenuation float32) *SpectralSubtractionNoiseReducer {
	return &SpectralSubtractionNoiseReducer{floorEstimate: floorEstimate, attenuation: attenuation}
}

// NewDefaultSpectralSubtractionNoiseReducer constructs a gate with the C#
// default parameters (floorEstimate=0.008, attenuation=0.25).
func NewDefaultSpectralSubtractionNoiseReducer() *SpectralSubtractionNoiseReducer {
	return NewSpectralSubtractionNoiseReducer(0.008, 0.25)
}

// BackendID returns "passthrough".
func (*SpectralSubtractionNoiseReducer) BackendID() string { return "passthrough" }

// IsAvailable returns true.
func (*SpectralSubtractionNoiseReducer) IsAvailable() bool { return true }

// Reduce attenuates below-floor samples. Panics (mirroring the C#
// ArgumentException) if the destination is too small.
func (r *SpectralSubtractionNoiseReducer) Reduce(audioPcm16Mono []byte, _ int, destination []byte) int {
	if len(destination) < len(audioPcm16Mono) {
		panic("destination must be at least as long as input")
	}

	n := len(audioPcm16Mono) / 2
	floor := int(r.floorEstimate * float32(math.MaxInt16))
	for i := 0; i < n; i++ {
		s := int(int16(binary.LittleEndian.Uint16(audioPcm16Mono[i*2 : i*2+2])))
		abs := s
		if abs < 0 {
			abs = -abs
		}
		var out int16
		if abs <= floor {
			out = int16(float32(s) * r.attenuation)
		} else {
			out = int16(s)
		}
		binary.LittleEndian.PutUint16(destination[i*2:i*2+2], uint16(out))
	}
	return len(audioPcm16Mono)
}

// INoiseReducerModelRunner is a host-supplied DNN runner for noise reduction.
// Ports INoiseReducerModelRunner.
type INoiseReducerModelRunner interface {
	// Process cleans one frame and writes it into destination, returning the
	// number of bytes written.
	Process(audioPcm16Mono []byte, sampleRateHz int, destination []byte) int
}

// KrispNoiseReducer uses the host's INoiseReducerModelRunner when present,
// falling back to spectral subtraction. Ports KrispNoiseReducer.
type KrispNoiseReducer struct {
	runner   INoiseReducerModelRunner
	fallback *SpectralSubtractionNoiseReducer
}

// NewKrispNoiseReducer constructs a Krisp wrapper. Pass nil runner to use the
// fallback. Ports the KrispNoiseReducer constructor.
func NewKrispNoiseReducer(runner INoiseReducerModelRunner) *KrispNoiseReducer {
	return &KrispNoiseReducer{runner: runner, fallback: NewDefaultSpectralSubtractionNoiseReducer()}
}

// BackendID returns "krisp" or "krisp (fallback)".
func (r *KrispNoiseReducer) BackendID() string {
	if r.runner == nil {
		return "krisp (fallback)"
	}
	return "krisp"
}

// IsAvailable returns true.
func (r *KrispNoiseReducer) IsAvailable() bool { return true }

// Reduce delegates to the runner, or the fallback when none is wired.
func (r *KrispNoiseReducer) Reduce(audioPcm16Mono []byte, sampleRateHz int, destination []byte) int {
	if r.runner == nil {
		return r.fallback.Reduce(audioPcm16Mono, sampleRateHz, destination)
	}
	return r.runner.Process(audioPcm16Mono, sampleRateHz, destination)
}

// DeepFilterNetNoiseReducer uses the host's INoiseReducerModelRunner when
// present, falling back to spectral subtraction. Ports DeepFilterNetNoiseReducer.
type DeepFilterNetNoiseReducer struct {
	runner   INoiseReducerModelRunner
	fallback *SpectralSubtractionNoiseReducer
}

// NewDeepFilterNetNoiseReducer constructs a DeepFilterNet wrapper. Pass nil
// runner to use the fallback. Ports the DeepFilterNetNoiseReducer constructor.
func NewDeepFilterNetNoiseReducer(runner INoiseReducerModelRunner) *DeepFilterNetNoiseReducer {
	return &DeepFilterNetNoiseReducer{runner: runner, fallback: NewDefaultSpectralSubtractionNoiseReducer()}
}

// BackendID returns "deepfilternet" or "deepfilternet (fallback)".
func (r *DeepFilterNetNoiseReducer) BackendID() string {
	if r.runner == nil {
		return "deepfilternet (fallback)"
	}
	return "deepfilternet"
}

// IsAvailable returns true.
func (r *DeepFilterNetNoiseReducer) IsAvailable() bool { return true }

// Reduce delegates to the runner, or the fallback when none is wired.
func (r *DeepFilterNetNoiseReducer) Reduce(audioPcm16Mono []byte, sampleRateHz int, destination []byte) int {
	if r.runner == nil {
		return r.fallback.Reduce(audioPcm16Mono, sampleRateHz, destination)
	}
	return r.runner.Process(audioPcm16Mono, sampleRateHz, destination)
}

// Interface guards.
var (
	_ INoiseReducer = NullNoiseReducer{}
	_ INoiseReducer = (*SpectralSubtractionNoiseReducer)(nil)
	_ INoiseReducer = (*KrispNoiseReducer)(nil)
	_ INoiseReducer = (*DeepFilterNetNoiseReducer)(nil)
)
