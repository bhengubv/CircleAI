// voice_energy_vad.go
//
// Ports CircleAI.Voice.EnergyVadDetector.cs — an energy-based (RMS) stream
// IVoiceActivityDetector. Pure code, no external dependencies.
//
// The detector processes incoming audio in fixed-size frames. When a frame's RMS
// energy exceeds EnergyThreshold it is speech. Speech frames are buffered until
// SilenceFrameCount consecutive below-threshold frames are observed, at which
// point the buffered segment is emitted. A final partial segment is emitted when
// the stream ends mid-speech. This reproduces the C# residual/speech-buffer state
// machine byte-for-byte.

package circleai

import (
	"context"
	"encoding/binary"
	"errors"
	"math"
)

// EnergyVadDetector is an energy-based stream IVoiceActivityDetector using RMS
// energy to distinguish speech from silence. Ports EnergyVadDetector.
type EnergyVadDetector struct {
	// EnergyThreshold is the RMS energy threshold in [0, 1]; frames above it are speech.
	EnergyThreshold float32
	// SilenceFrameCount is how many consecutive below-threshold frames declare end-of-speech.
	SilenceFrameCount int
	// FrameSizeBytes is the analysis frame size in bytes (640 = 20 ms @ 16 kHz mono 16-bit).
	FrameSizeBytes int
}

// NewEnergyVadDetector constructs an energy VAD. Defaults (via
// NewDefaultEnergyVadDetector): energyThreshold=0.02, silenceFrames=15,
// frameSizeBytes=640. Ports the EnergyVadDetector constructor (which validates
// its arguments — this returns an error instead of throwing).
func NewEnergyVadDetector(energyThreshold float32, silenceFrames, frameSizeBytes int) (*EnergyVadDetector, error) {
	if silenceFrames <= 0 {
		return nil, errors.New("silenceFrames must be positive")
	}
	if frameSizeBytes <= 0 {
		return nil, errors.New("frameSizeBytes must be positive")
	}
	if energyThreshold < 0 {
		return nil, errors.New("energyThreshold must be non-negative")
	}
	return &EnergyVadDetector{EnergyThreshold: energyThreshold, SilenceFrameCount: silenceFrames, FrameSizeBytes: frameSizeBytes}, nil
}

// NewDefaultEnergyVadDetector constructs an energy VAD with the C# default
// parameters (0.02, 15, 640).
func NewDefaultEnergyVadDetector() *EnergyVadDetector {
	d, _ := NewEnergyVadDetector(0.02, 15, 640)
	return d
}

// Detect iterates the incoming audio stream frame-by-frame, computes RMS energy,
// and yields complete speech segments when end-of-speech silence is detected. A
// final partial segment is emitted if the stream ends mid-speech. The returned
// channel closes when audioStream completes or ctx cancels.
func (d *EnergyVadDetector) Detect(ctx context.Context, audioStream <-chan []byte) <-chan VadSegment {
	out := make(chan VadSegment)
	go func() {
		defer close(out)

		// Carry-over buffer for bytes that don't fill a complete frame.
		var residual []byte
		// Accumulator for the current speech segment.
		var speechBuffer []byte

		inSpeech := false
		consecutiveSilenceFrames := 0

		emit := func(seg VadSegment) bool {
			select {
			case out <- seg:
				return true
			case <-ctx.Done():
				return false
			}
		}

		for {
			var chunk []byte
			var ok bool
			select {
			case <-ctx.Done():
				return
			case chunk, ok = <-audioStream:
				if !ok {
					// Stream ended — if mid-speech, emit what we have.
					if inSpeech && len(speechBuffer) > 0 {
						emit(VadSegment{Audio: append([]byte(nil), speechBuffer...), IsSpeech: true})
					}
					return
				}
			}

			if len(chunk) == 0 {
				continue
			}

			// Append new data to the residual buffer.
			residual = append(residual, chunk...)

			// Process complete frames from the residual.
			offset := 0
			for len(residual)-offset >= d.FrameSizeBytes {
				frame := residual[offset : offset+d.FrameSizeBytes]
				rms := computeRmsEnergy(frame)
				isSpeechFrame := rms >= d.EnergyThreshold

				if isSpeechFrame {
					if !inSpeech {
						inSpeech = true
						consecutiveSilenceFrames = 0
						speechBuffer = speechBuffer[:0]
					} else {
						consecutiveSilenceFrames = 0
					}
					speechBuffer = append(speechBuffer, frame...)
				} else if inSpeech {
					// Still in speech region; buffer silence frames in case speech
					// resumes (avoids cutting off mid-word).
					speechBuffer = append(speechBuffer, frame...)
					consecutiveSilenceFrames++

					if consecutiveSilenceFrames >= d.SilenceFrameCount {
						// End of speech — emit the buffered segment.
						inSpeech = false
						consecutiveSilenceFrames = 0
						audio := append([]byte(nil), speechBuffer...)
						speechBuffer = speechBuffer[:0]
						if !emit(VadSegment{Audio: audio, IsSpeech: true}) {
							return
						}
					}
				}
				// else: silence while not in speech — discard.

				offset += d.FrameSizeBytes
			}

			// Move unconsumed residual bytes to the start of the buffer.
			remaining := len(residual) - offset
			if remaining > 0 {
				copy(residual, residual[offset:])
			}
			residual = residual[:remaining]
		}
	}()
	return out
}

// computeRmsEnergy computes the RMS energy of a PCM 16-bit frame, normalised to
// [0, 1]. Ports EnergyVadDetector.ComputeRmsEnergy.
func computeRmsEnergy(frameBytes []byte) float32 {
	n := len(frameBytes) / 2
	if n == 0 {
		return 0
	}
	var sumSquares float64
	for i := 0; i < n; i++ {
		s := int16(binary.LittleEndian.Uint16(frameBytes[i*2 : i*2+2]))
		normalised := float64(s) / 32768.0
		sumSquares += normalised * normalised
	}
	return float32(math.Sqrt(sumSquares / float64(n)))
}

// Interface guard.
var _ IVoiceActivityDetector = (*EnergyVadDetector)(nil)
