// voice_wav_io.go
//
// Port of src/CircleAI.Voice/WavIo.cs — minimal RIFF/WAVE reading and PCM-16
// packing, so a reference recording can become the float samples a voice needs.
//
// Parity is asserted against fixtures/voice_wav_io.json.

package circleai

import (
	"encoding/binary"
	"fmt"
	"math"
	"os"
)

// VoiceTargetRate is Mimi's sample rate — what ReadMono24k resamples to.
const VoiceTargetRate = 24000

// ReadWavMono24k reads a WAV file as mono float samples at 24 kHz.
func ReadWavMono24k(path string, maxSeconds int) ([]float32, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	samples, rate, channels, err := ParseWav(raw, path)
	if err != nil {
		return nil, err
	}

	if channels > 1 {
		mono := make([]float32, len(samples)/channels)
		for i := range mono {
			var sum float32
			for c := 0; c < channels; c++ {
				sum += samples[i*channels+c]
			}
			mono[i] = sum / float32(channels)
		}
		samples = mono
	}

	if rate != VoiceTargetRate {
		samples = resampleLinear(samples, rate, VoiceTargetRate)
	}

	cap := maxSeconds * VoiceTargetRate
	if len(samples) > cap {
		samples = samples[:cap]
	}
	return samples, nil
}

// WavToPcm16 packs float samples in [-1,1] as little-endian signed 16-bit PCM.
func WavToPcm16(samples []float32) []byte {
	out := make([]byte, len(samples)*2)
	for i, s := range samples {
		if s > 1 {
			s = 1
		} else if s < -1 {
			s = -1
		}
		binary.LittleEndian.PutUint16(out[i*2:], uint16(int16(s*32767)))
	}
	return out
}

// ParseWav decodes a RIFF/WAVE buffer into (samples, rate, channels).
func ParseWav(raw []byte, path string) ([]float32, int, int, error) {
	if len(raw) < 12 ||
		binary.BigEndian.Uint32(raw) != 0x52494646 || // "RIFF"
		binary.BigEndian.Uint32(raw[8:]) != 0x57415645 { // "WAVE"
		return nil, 0, 0, fmt.Errorf("%q is not a RIFF/WAVE file", path)
	}

	var format, channels, rate, bits int
	var data []byte
	offset := 12

	// WALK THE CHUNKS. A WAV written by anything other than the simplest encoder
	// carries LIST/fact/cue chunks before the data, and assuming data starts at
	// byte 44 reads metadata as audio — which sounds like a short burst of noise
	// before the real recording.
	for offset+8 <= len(raw) {
		id := binary.BigEndian.Uint32(raw[offset:])
		size := int(int32(binary.LittleEndian.Uint32(raw[offset+4:])))
		body := offset + 8
		if size < 0 || body+size > len(raw) {
			size = len(raw) - body
		}

		switch id {
		case 0x666D7420: // "fmt "
			format = int(binary.LittleEndian.Uint16(raw[body:]))
			channels = int(binary.LittleEndian.Uint16(raw[body+2:]))
			rate = int(int32(binary.LittleEndian.Uint32(raw[body+4:])))
			bits = int(binary.LittleEndian.Uint16(raw[body+14:]))
		case 0x64617461: // "data"
			data = raw[body : body+size]
		}

		offset = body + size + (size & 1) // chunks are word-aligned
	}

	if channels == 0 || rate == 0 || len(data) == 0 {
		return nil, 0, 0, fmt.Errorf("%q has no usable fmt/data chunk", path)
	}

	// 3 is IEEE float; 0xFFFE is WAVE_FORMAT_EXTENSIBLE, whose real format lives
	// in a sub-chunk — treated as PCM here, which is what it is in every file the
	// voice stack has met.
	pcm := format == 1 || format == 0xFFFE
	switch {
	case pcm && bits == 8:
		return mapSamples(data, 1, func(b []byte) float32 {
			return float32(int(b[0])-128) / 128
		}), rate, channels, nil
	case pcm && bits == 16:
		return mapSamples(data, 2, func(b []byte) float32 {
			return float32(int16(binary.LittleEndian.Uint16(b))) / 32768
		}), rate, channels, nil
	case pcm && bits == 24:
		return mapSamples(data, 3, func(b []byte) float32 {
			v := int32(b[0]) | int32(b[1])<<8 | int32(b[2])<<16
			return float32(v<<8>>8) / 8388608
		}), rate, channels, nil
	case pcm && bits == 32:
		return mapSamples(data, 4, func(b []byte) float32 {
			return float32(int32(binary.LittleEndian.Uint32(b))) / 2147483648
		}), rate, channels, nil
	case format == 3 && bits == 32:
		return mapSamples(data, 4, func(b []byte) float32 {
			return math.Float32frombits(binary.LittleEndian.Uint32(b))
		}), rate, channels, nil
	}
	return nil, 0, 0, fmt.Errorf("%q is WAV format %d at %d bits, which this reader does not decode",
		path, format, bits)
}

func mapSamples(data []byte, stride int, convert func([]byte) float32) []float32 {
	count := len(data) / stride
	out := make([]float32, count)
	for i := 0; i < count; i++ {
		out[i] = convert(data[i*stride : (i+1)*stride])
	}
	return out
}

// resampleLinear is adequate here: the target is a speaker embedding, not playback.
func resampleLinear(input []float32, from, to int) []float32 {
	if len(input) == 0 {
		return input
	}
	count := int(math.Round(float64(len(input)) * float64(to) / float64(from)))
	if count < 1 {
		count = 1
	}
	out := make([]float32, count)
	denom := count - 1
	if denom < 1 {
		denom = 1
	}
	step := float64(len(input)-1) / float64(denom)
	for i := range out {
		x := float64(i) * step
		lo := int(x)
		hi := lo + 1
		if hi > len(input)-1 {
			hi = len(input) - 1
		}
		out[i] = float32(float64(input[lo]) + (float64(input[hi])-float64(input[lo]))*(x-float64(lo)))
	}
	return out
}
