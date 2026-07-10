// speech_echo_cancellers.go
//
// Ports CircleAI.Speech.EchoCancellers.cs:
//   - NullEchoCanceller: pass-through DI default.
//   - NlmsEchoCanceller: normalised-LMS adaptive filter (pure, no model).
//   - IEchoCancellerModelRunner: host-supplied AEC model runner seam.
//   - WebRtcEchoCanceller: shell that delegates to the runner, falling back to
//     NLMS when no runner is wired.
//
// The NLMS update reproduces the C# arithmetic exactly (float32 taps, circular
// reference buffer, mu = stepSize / power).

package circleai

import (
	"encoding/binary"
	"fmt"
	"math"
)

// NullEchoCanceller is the pass-through DI default. Ports NullEchoCanceller.
type NullEchoCanceller struct{}

// NullEchoCancellerInstance mirrors NullEchoCanceller.Instance.
var NullEchoCancellerInstance = NullEchoCanceller{}

// BackendID returns "null".
func (NullEchoCanceller) BackendID() string { return "null" }

// Cancel copies the near-end microphone through unchanged.
func (NullEchoCanceller) Cancel(nearEndMicrophone, _ []byte, _ int, destination []byte) int {
	copy(destination, nearEndMicrophone)
	return len(nearEndMicrophone)
}

// Reset is a no-op.
func (NullEchoCanceller) Reset() {}

// NlmsEchoCanceller is a normalised-LMS adaptive-filter AEC. Pure code, no model
// downloads, runs on every device. Filter length defaults to 256 taps (~16 ms @
// 16 kHz). Ports NlmsEchoCanceller. Not safe for concurrent Cancel calls (the
// adaptive filter carries mutable state, matching the C# instance semantics).
type NlmsEchoCanceller struct {
	w            []float32
	stepSize     float32
	epsilon      float32
	filterLength int
	refBuffer    []float32
	refIndex     int
}

// NewNlmsEchoCanceller constructs an NLMS canceller. Defaults: filterLength=256,
// stepSize=0.4, epsilon=1e-6. Ports the NlmsEchoCanceller constructor.
func NewNlmsEchoCanceller(filterLength int, stepSize, epsilon float32) *NlmsEchoCanceller {
	return &NlmsEchoCanceller{
		filterLength: filterLength,
		stepSize:     stepSize,
		epsilon:      epsilon,
		w:            make([]float32, filterLength),
		refBuffer:    make([]float32, filterLength),
	}
}

// NewDefaultNlmsEchoCanceller constructs an NLMS canceller with the C# default
// parameters (filterLength=256, stepSize=0.4, epsilon=1e-6).
func NewDefaultNlmsEchoCanceller() *NlmsEchoCanceller {
	return NewNlmsEchoCanceller(256, 0.4, 1e-6)
}

// BackendID returns "nlms".
func (c *NlmsEchoCanceller) BackendID() string { return "nlms" }

// Cancel cancels far-end echo from the near-end mic via the adaptive filter.
// Panics (mirroring the C# ArgumentException) if lengths mismatch or the
// destination is too small.
func (c *NlmsEchoCanceller) Cancel(nearEndMicrophone, farEndReference []byte, _ int, destination []byte) int {
	if len(nearEndMicrophone) != len(farEndReference) {
		panic(fmt.Sprintf("near-end and far-end must be the same length: %d vs %d", len(nearEndMicrophone), len(farEndReference)))
	}
	if len(destination) < len(nearEndMicrophone) {
		panic("destination must be at least as long as input")
	}

	sampleCount := len(nearEndMicrophone) / 2
	for n := 0; n < sampleCount; n++ {
		micSample := float32(int16(binary.LittleEndian.Uint16(nearEndMicrophone[n*2:n*2+2]))) / float32(math.MaxInt16)
		farSample := float32(int16(binary.LittleEndian.Uint16(farEndReference[n*2:n*2+2]))) / float32(math.MaxInt16)

		// Push far-end into circular reference buffer.
		c.refBuffer[c.refIndex] = farSample

		// Estimated echo: dot(w, ref).
		var echoEstimate float32
		power := c.epsilon
		for k := 0; k < c.filterLength; k++ {
			rIdx := (c.refIndex - k + c.filterLength) % c.filterLength
			x := c.refBuffer[rIdx]
			echoEstimate += c.w[k] * x
			power += x * x
		}

		// Error = mic - echo estimate.
		errSample := micSample - echoEstimate

		// Update filter weights.
		mu := c.stepSize / power
		for k := 0; k < c.filterLength; k++ {
			rIdx := (c.refIndex - k + c.filterLength) % c.filterLength
			c.w[k] += mu * errSample * c.refBuffer[rIdx]
		}

		c.refIndex = (c.refIndex + 1) % c.filterLength

		// Clamp + write.
		outSample := int(clampFloat32(errSample*float32(math.MaxInt16), math.MinInt16, math.MaxInt16))
		binary.LittleEndian.PutUint16(destination[n*2:n*2+2], uint16(int16(outSample)))
	}

	return len(nearEndMicrophone)
}

// Reset clears the filter weights and reference buffer.
func (c *NlmsEchoCanceller) Reset() {
	for i := range c.w {
		c.w[i] = 0
	}
	for i := range c.refBuffer {
		c.refBuffer[i] = 0
	}
	c.refIndex = 0
}

// IEchoCancellerModelRunner is a host-supplied AEC model runner (e.g. WebRTC
// AEC3). Ports IEchoCancellerModelRunner.
type IEchoCancellerModelRunner interface {
	// Process cancels echo of farEnd out of nearEnd into destination and returns
	// the number of bytes written.
	Process(nearEnd, farEnd []byte, sampleRateHz int, destination []byte) int
	// Reset resets the runner's adaptive state.
	Reset()
}

// WebRtcEchoCanceller wraps an IEchoCancellerModelRunner (WebRTC AEC3), falling
// back to NLMS when no runner is wired. Ports WebRtcEchoCanceller.
type WebRtcEchoCanceller struct {
	runner   IEchoCancellerModelRunner
	fallback *NlmsEchoCanceller
}

// NewWebRtcEchoCanceller constructs a WebRTC AEC wrapper. Pass nil runner to use
// the NLMS fallback. Ports the WebRtcEchoCanceller constructor.
func NewWebRtcEchoCanceller(runner IEchoCancellerModelRunner) *WebRtcEchoCanceller {
	return &WebRtcEchoCanceller{runner: runner, fallback: NewDefaultNlmsEchoCanceller()}
}

// BackendID returns "webrtc-aec3" or "webrtc-aec3 (fallback)".
func (c *WebRtcEchoCanceller) BackendID() string {
	if c.runner == nil {
		return "webrtc-aec3 (fallback)"
	}
	return "webrtc-aec3"
}

// Cancel delegates to the runner, or the NLMS fallback when no runner is wired.
func (c *WebRtcEchoCanceller) Cancel(nearEndMicrophone, farEndReference []byte, sampleRateHz int, destination []byte) int {
	if c.runner == nil {
		return c.fallback.Cancel(nearEndMicrophone, farEndReference, sampleRateHz, destination)
	}
	return c.runner.Process(nearEndMicrophone, farEndReference, sampleRateHz, destination)
}

// Reset resets both the fallback and the runner (if present).
func (c *WebRtcEchoCanceller) Reset() {
	c.fallback.Reset()
	if c.runner != nil {
		c.runner.Reset()
	}
}

// clampFloat32 clamps v to [lo, hi] (mirrors Math.Clamp).
func clampFloat32(v, lo, hi float32) float32 {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

// Interface guards.
var (
	_ IEchoCanceller = NullEchoCanceller{}
	_ IEchoCanceller = (*NlmsEchoCanceller)(nil)
	_ IEchoCanceller = (*WebRtcEchoCanceller)(nil)
)
