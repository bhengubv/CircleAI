// speech_audio_converter.go
//
// Ports CircleAI.Speech.AudioFormatConverter.cs and its AudioCodec enum.
//
// Phone carriers feed mu-law / a-law at 8 kHz; cloud STT/TTS speak linear PCM
// at 16/24/44.1 kHz. The converter handles every common path:
//   - mu-law 8 kHz   <-> PCM-16 16 kHz / 24 kHz
//   - a-law  8 kHz   <-> PCM-16 16 kHz / 24 kHz
//   - PCM-16 N kHz   ->  PCM-16 M kHz  (linear interpolation)
//
// The G.711 mu-law / a-law encode/decode and the linear resampler reproduce the
// C# bit arithmetic EXACTLY (byte-for-byte identical output).

package circleai

import (
	"encoding/binary"
	"fmt"
	"math"
)

// AudioCodec enumerates carrier-native audio formats the converter knows how to
// convert. Ordinals are stable and match the C# declaration order. Ports the
// AudioCodec enum.
type AudioCodec int

const (
	// AudioCodecPcm16 — 16-bit signed linear PCM, little-endian, mono.
	AudioCodecPcm16 AudioCodec = iota
	// AudioCodecMuLaw — G.711 mu-law (telephony, North America / Japan).
	AudioCodecMuLaw
	// AudioCodecALaw — G.711 A-law (telephony, Europe).
	AudioCodecALaw
)

// String renders the C# enum member name for an AudioCodec.
func (c AudioCodec) String() string {
	switch c {
	case AudioCodecPcm16:
		return "Pcm16"
	case AudioCodecMuLaw:
		return "MuLaw"
	case AudioCodecALaw:
		return "ALaw"
	default:
		return "Unknown"
	}
}

// ConvertAudio converts audio from one (codec, sample rate) to another. Returns
// the freshly allocated output buffer; the caller does NOT need to size it.
// Ports AudioFormatConverter.Convert.
func ConvertAudio(
	input []byte,
	inputCodec AudioCodec,
	inputSampleRateHz int,
	outputCodec AudioCodec,
	outputSampleRateHz int,
) ([]byte, error) {
	if inputSampleRateHz <= 0 {
		return nil, fmt.Errorf("inputSampleRateHz out of range: %d", inputSampleRateHz)
	}
	if outputSampleRateHz <= 0 {
		return nil, fmt.Errorf("outputSampleRateHz out of range: %d", outputSampleRateHz)
	}

	// 1) Decode source to PCM-16.
	var pcmIn []byte
	switch inputCodec {
	case AudioCodecPcm16:
		pcmIn = append([]byte(nil), input...)
	case AudioCodecMuLaw:
		pcmIn = DecodeMuLawToPcm16(input)
	case AudioCodecALaw:
		pcmIn = DecodeALawToPcm16(input)
	default:
		return nil, fmt.Errorf("unknown input codec %v", inputCodec)
	}

	// 2) Resample if needed.
	pcmResampled := pcmIn
	if inputSampleRateHz != outputSampleRateHz {
		pcmResampled = ResamplePcm16Linear(pcmIn, inputSampleRateHz, outputSampleRateHz)
	}

	// 3) Encode to target codec.
	switch outputCodec {
	case AudioCodecPcm16:
		return pcmResampled, nil
	case AudioCodecMuLaw:
		return EncodePcm16ToMuLaw(pcmResampled), nil
	case AudioCodecALaw:
		return EncodePcm16ToALaw(pcmResampled), nil
	default:
		return nil, fmt.Errorf("unknown output codec %v", outputCodec)
	}
}

// ===== mu-law =====

// DecodeMuLawToPcm16 decodes a mu-law buffer to PCM-16. Ports
// AudioFormatConverter.DecodeMuLawToPcm16.
func DecodeMuLawToPcm16(mulaw []byte) []byte {
	pcm := make([]byte, len(mulaw)*2)
	for i := 0; i < len(mulaw); i++ {
		s := muLawToLinear(mulaw[i])
		binary.LittleEndian.PutUint16(pcm[i*2:i*2+2], uint16(s))
	}
	return pcm
}

// EncodePcm16ToMuLaw encodes a PCM-16 buffer to mu-law. Ports
// AudioFormatConverter.EncodePcm16ToMuLaw.
func EncodePcm16ToMuLaw(pcm []byte) []byte {
	samples := len(pcm) / 2
	mulaw := make([]byte, samples)
	for i := 0; i < samples; i++ {
		s := int16(binary.LittleEndian.Uint16(pcm[i*2 : i*2+2]))
		mulaw[i] = linearToMuLaw(s)
	}
	return mulaw
}

func muLawToLinear(mu byte) int16 {
	// G.711 mu-law decode (ITU-T G.711).
	mu = ^mu
	sign := int(mu) & 0x80
	exponent := (int(mu) >> 4) & 0x07
	mantissa := int(mu) & 0x0F
	magnitude := ((mantissa << 3) + 0x84) << exponent
	sample := magnitude - 0x84
	if sign != 0 {
		return int16(-sample)
	}
	return int16(sample)
}

func linearToMuLaw(pcm int16) byte {
	const bias = 0x84
	const clip = 32635
	sign := (int(pcm) >> 8) & 0x80
	v := int(pcm)
	if sign != 0 {
		v = -v
	}
	if v > clip {
		v = clip
	}
	v += bias

	var exponent int
	switch {
	case v >= 0x4000:
		exponent = 7
	case v >= 0x2000:
		exponent = 6
	case v >= 0x1000:
		exponent = 5
	case v >= 0x0800:
		exponent = 4
	case v >= 0x0400:
		exponent = 3
	case v >= 0x0200:
		exponent = 2
	case v >= 0x0100:
		exponent = 1
	default:
		exponent = 0
	}

	mantissa := (v >> (exponent + 3)) & 0x0F
	return byte(^(sign | (exponent << 4) | mantissa))
}

// ===== a-law =====

// DecodeALawToPcm16 decodes an a-law buffer to PCM-16. Ports
// AudioFormatConverter.DecodeALawToPcm16.
func DecodeALawToPcm16(alaw []byte) []byte {
	pcm := make([]byte, len(alaw)*2)
	for i := 0; i < len(alaw); i++ {
		s := aLawToLinear(alaw[i])
		binary.LittleEndian.PutUint16(pcm[i*2:i*2+2], uint16(s))
	}
	return pcm
}

// EncodePcm16ToALaw encodes a PCM-16 buffer to a-law. Ports
// AudioFormatConverter.EncodePcm16ToALaw.
func EncodePcm16ToALaw(pcm []byte) []byte {
	samples := len(pcm) / 2
	alaw := make([]byte, samples)
	for i := 0; i < samples; i++ {
		s := int16(binary.LittleEndian.Uint16(pcm[i*2 : i*2+2]))
		alaw[i] = linearToALaw(s)
	}
	return alaw
}

func aLawToLinear(a byte) int16 {
	a ^= 0x55
	sign := int(a) & 0x80
	exponent := (int(a) >> 4) & 0x07
	mantissa := int(a) & 0x0F
	var magnitude int
	if exponent != 0 {
		magnitude = ((mantissa << 4) + 0x108) << (exponent - 1)
	} else {
		magnitude = (mantissa << 4) + 0x08
	}
	if sign != 0 {
		return int16(-magnitude)
	}
	return int16(magnitude)
}

func linearToALaw(pcm int16) byte {
	sign := (int(pcm) >> 8) & 0x80
	v := int(pcm)
	if sign != 0 {
		v = -v
	}
	if v > 0x7FFF {
		v = 0x7FFF
	}

	var exponent int
	var mantissa int
	if v < 256 {
		exponent = 0
		mantissa = v >> 4
	} else {
		switch {
		case v >= 0x4000:
			exponent = 7
		case v >= 0x2000:
			exponent = 6
		case v >= 0x1000:
			exponent = 5
		case v >= 0x0800:
			exponent = 4
		case v >= 0x0400:
			exponent = 3
		case v >= 0x0200:
			exponent = 2
		default:
			exponent = 1
		}
		mantissa = (v >> (exponent + 3)) & 0x0F
	}
	return byte((sign | (exponent << 4) | mantissa) ^ 0x55)
}

// ===== resample (linear interpolation) =====

// ResamplePcm16Linear resamples a PCM-16 buffer from fromHz to toHz using linear
// interpolation. Ports AudioFormatConverter.ResamplePcm16Linear.
func ResamplePcm16Linear(pcm []byte, fromHz, toHz int) []byte {
	if fromHz == toHz {
		return pcm
	}
	srcSamples := len(pcm) / 2
	dstSamples := int(int64(srcSamples) * int64(toHz) / int64(fromHz))
	dst := make([]byte, dstSamples*2)
	for i := 0; i < dstSamples; i++ {
		srcIdx := float64(i) * float64(fromHz) / float64(toHz)
		idx0 := int(math.Floor(srcIdx))
		idx1 := idx0 + 1
		if idx1 > srcSamples-1 {
			idx1 = srcSamples - 1
		}
		frac := srcIdx - float64(idx0)
		s0 := int16(binary.LittleEndian.Uint16(pcm[idx0*2 : idx0*2+2]))
		s1 := int16(binary.LittleEndian.Uint16(pcm[idx1*2 : idx1*2+2]))
		s := int16(float64(s0) + float64(int(s1)-int(s0))*frac)
		binary.LittleEndian.PutUint16(dst[i*2:i*2+2], uint16(s))
	}
	return dst
}
