// speech_vad.go
//
// Ports CircleAI.Speech.VoiceActivityDetectors.cs (the per-frame VAD variant):
//   - NullSpeechVoiceActivityDetector: always-speech DI default.
//   - EnergyVoiceActivityDetector: RMS energy + zero-crossing rate + hangover
//     frames. No model, runs on every device.
//   - IVadModelRunner: host-supplied ONNX runner seam.
//   - SileroVoiceActivityDetector: wraps the runner, falling back to energy
//     scoring until a host wires the real model.
//
// NOTE: the CircleAI.Speech IVoiceActivityDetector is a per-FRAME classifier and
// is ported as ISpeechVoiceActivityDetector (see speech_contracts.go). The
// stream-based CircleAI.Voice IVoiceActivityDetector is separate.

package circleai

import (
	"encoding/binary"
	"math"
	"time"
)

// NullSpeechVoiceActivityDetector always reports speech — DI default so nothing
// breaks before a real VAD is wired. Ports NullVoiceActivityDetector
// (CircleAI.Speech).
type NullSpeechVoiceActivityDetector struct{}

// NullSpeechVoiceActivityDetectorInstance mirrors
// NullVoiceActivityDetector.Instance (CircleAI.Speech).
var NullSpeechVoiceActivityDetectorInstance = NullSpeechVoiceActivityDetector{}

// BackendID returns "null".
func (NullSpeechVoiceActivityDetector) BackendID() string { return "null" }

// SpeechThreshold returns 0.5.
func (NullSpeechVoiceActivityDetector) SpeechThreshold() float32 { return 0.5 }

// Classify always reports speech with probability 1.
func (NullSpeechVoiceActivityDetector) Classify(_ []byte, _ int, offset time.Duration) VadFrameResult {
	return VadFrameResult{IsSpeech: true, SpeechProbability: 1, Offset: offset}
}

// Reset is a no-op.
func (NullSpeechVoiceActivityDetector) Reset() {}

// EnergyVoiceActivityDetector is a per-frame VAD using RMS energy +
// zero-crossing rate + hangover-frame smoothing. No ML model required. Ports
// EnergyVoiceActivityDetector. Carries mutable hangover state — not safe for
// concurrent Classify calls (matches the C# instance semantics).
type EnergyVoiceActivityDetector struct {
	speechThreshold   float32
	energyThreshold   float32
	hangoverFrames    int
	hangoverRemaining int
}

// NewEnergyVoiceActivityDetector constructs an energy VAD. Defaults:
// speechThreshold=0.55, energyThreshold=0.012, hangoverFrames=8. Ports the
// EnergyVoiceActivityDetector constructor.
func NewEnergyVoiceActivityDetector(speechThreshold, energyThreshold float32, hangoverFrames int) *EnergyVoiceActivityDetector {
	return &EnergyVoiceActivityDetector{
		speechThreshold: speechThreshold,
		energyThreshold: energyThreshold,
		hangoverFrames:  hangoverFrames,
	}
}

// NewDefaultEnergyVoiceActivityDetector constructs an energy VAD with the C#
// default parameters (speechThreshold=0.55, energyThreshold=0.012, hangoverFrames=8).
func NewDefaultEnergyVoiceActivityDetector() *EnergyVoiceActivityDetector {
	return NewEnergyVoiceActivityDetector(0.55, 0.012, 8)
}

// BackendID returns "energy".
func (d *EnergyVoiceActivityDetector) BackendID() string { return "energy" }

// SpeechThreshold returns the configured speech-probability threshold.
func (d *EnergyVoiceActivityDetector) SpeechThreshold() float32 { return d.speechThreshold }

// Classify classifies one frame of PCM-16 mono audio.
func (d *EnergyVoiceActivityDetector) Classify(audioPcm16Mono []byte, _ int, offset time.Duration) VadFrameResult {
	if len(audioPcm16Mono) < 2 {
		return VadFrameResult{IsSpeech: false, SpeechProbability: 0, Offset: offset}
	}

	n := len(audioPcm16Mono) / 2
	var sumSquares float64
	zeroCrossings := 0
	var previous int16
	for i := 0; i < n; i++ {
		s := int16(binary.LittleEndian.Uint16(audioPcm16Mono[i*2 : i*2+2]))
		sumSquares += float64(int(s) * int(s))
		if i > 0 && sign(int(s)) != sign(int(previous)) && s != 0 && previous != 0 {
			zeroCrossings++
		}
		previous = s
	}
	rms := math.Sqrt(sumSquares/float64(n)) / float64(math.MaxInt16) // 0..1
	zcrRate := float32(zeroCrossings) / float32(n)

	// Speech: high RMS + moderate ZCR (~0.05-0.25 for voiced speech).
	energyGood := rms >= float64(d.energyThreshold)
	zcrGood := zcrRate >= 0.02 && zcrRate <= 0.30
	var rawProb float32
	if energyGood {
		if zcrGood {
			rawProb = 0.85
		} else {
			rawProb = 0.6
		}
	} else {
		rawProb = 0.1
	}

	var isSpeech bool
	if rawProb >= d.speechThreshold {
		isSpeech = true
		d.hangoverRemaining = d.hangoverFrames
	} else if d.hangoverRemaining > 0 {
		isSpeech = true
		d.hangoverRemaining--
		if rawProb < d.speechThreshold {
			rawProb = d.speechThreshold
		}
	} else {
		isSpeech = false
	}

	return VadFrameResult{IsSpeech: isSpeech, SpeechProbability: rawProb, Offset: offset}
}

// Reset resets the hangover state.
func (d *EnergyVoiceActivityDetector) Reset() { d.hangoverRemaining = 0 }

// IVadModelRunner is an ONNX model runner contract supplied by the host package.
// Ports IVadModelRunner.
type IVadModelRunner interface {
	// ScoreFrame scores one 30 ms / 16 kHz PCM-16 frame; result is 0..1.
	ScoreFrame(audioPcm16Mono []byte, sampleRateHz int) float32
}

// SileroVoiceActivityDetector delegates the per-frame score to a host
// IVadModelRunner; when no runner is wired it falls back to the energy VAD's
// scoring. Ports SileroVoiceActivityDetector.
type SileroVoiceActivityDetector struct {
	runner            IVadModelRunner
	fallback          *EnergyVoiceActivityDetector
	speechThreshold   float32
	hangoverFrames    int
	hangoverRemaining int
}

// NewSileroVoiceActivityDetector constructs a Silero wrapper. Pass nil runner to
// use the energy fallback. Defaults: speechThreshold=0.5, hangoverFrames=8. Ports
// the SileroVoiceActivityDetector constructor.
func NewSileroVoiceActivityDetector(runner IVadModelRunner, speechThreshold float32, hangoverFrames int) *SileroVoiceActivityDetector {
	return &SileroVoiceActivityDetector{
		runner:          runner,
		fallback:        NewEnergyVoiceActivityDetector(speechThreshold, 0.012, 8),
		speechThreshold: speechThreshold,
		hangoverFrames:  hangoverFrames,
	}
}

// NewDefaultSileroVoiceActivityDetector constructs a Silero wrapper with the C#
// default parameters (runner=nil, speechThreshold=0.5, hangoverFrames=8).
func NewDefaultSileroVoiceActivityDetector(runner IVadModelRunner) *SileroVoiceActivityDetector {
	return NewSileroVoiceActivityDetector(runner, 0.5, 8)
}

// BackendID returns "silero" or "silero (fallback)".
func (d *SileroVoiceActivityDetector) BackendID() string {
	if d.runner == nil {
		return "silero (fallback)"
	}
	return "silero"
}

// SpeechThreshold returns the configured speech-probability threshold.
func (d *SileroVoiceActivityDetector) SpeechThreshold() float32 { return d.speechThreshold }

// Classify classifies one frame — via the runner, or the energy fallback.
func (d *SileroVoiceActivityDetector) Classify(audioPcm16Mono []byte, sampleRateHz int, offset time.Duration) VadFrameResult {
	if d.runner == nil {
		return d.fallback.Classify(audioPcm16Mono, sampleRateHz, offset)
	}

	prob := d.runner.ScoreFrame(audioPcm16Mono, sampleRateHz)
	var isSpeech bool
	if prob >= d.speechThreshold {
		isSpeech = true
		d.hangoverRemaining = d.hangoverFrames
	} else if d.hangoverRemaining > 0 {
		isSpeech = true
		d.hangoverRemaining--
	} else {
		isSpeech = false
	}
	return VadFrameResult{IsSpeech: isSpeech, SpeechProbability: prob, Offset: offset}
}

// Reset resets the hangover state and the fallback.
func (d *SileroVoiceActivityDetector) Reset() {
	d.hangoverRemaining = 0
	d.fallback.Reset()
}

// sign returns -1, 0, or 1 (mirrors System.Math.Sign for int).
func sign(x int) int {
	switch {
	case x > 0:
		return 1
	case x < 0:
		return -1
	default:
		return 0
	}
}

// Interface guards.
var (
	_ ISpeechVoiceActivityDetector = NullSpeechVoiceActivityDetector{}
	_ ISpeechVoiceActivityDetector = (*EnergyVoiceActivityDetector)(nil)
	_ ISpeechVoiceActivityDetector = (*SileroVoiceActivityDetector)(nil)
)
