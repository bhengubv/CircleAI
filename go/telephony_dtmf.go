// telephony_dtmf.go
//
// Ports CircleAI.Telephony/DtmfToneGenerator.cs — the stateless DTMF audio
// generator and the ICallSession send helper. Byte format is load-bearing and
// reproduced exactly: PCM-16 mono, little-endian, samples = sampleRateHz *
// durationMs / 1000, the same dual-tone sum, amplitude scaling, clamp to
// [-1,1], and *short.MaxValue (32767) truncated-to-int16 quantisation.
//
// IDtmfSendable itself is declared in telephony_contracts.go alongside the other
// contracts.

package circleai

import (
	"context"
	"errors"
	"fmt"
	"math"
)

// dtmfFreq is one low/high frequency pair.
type dtmfFreq struct {
	low, high int
}

// dtmfFrequencies is the standard DTMF frequency table (low row × high column),
// matching DtmfToneGenerator.Frequencies exactly.
var dtmfFrequencies = map[rune]dtmfFreq{
	'1': {697, 1209},
	'2': {697, 1336},
	'3': {697, 1477},
	'A': {697, 1633},
	'4': {770, 1209},
	'5': {770, 1336},
	'6': {770, 1477},
	'B': {770, 1633},
	'7': {852, 1209},
	'8': {852, 1336},
	'9': {852, 1477},
	'C': {852, 1633},
	'*': {941, 1209},
	'0': {941, 1336},
	'#': {941, 1477},
	'D': {941, 1633},
}

// DtmfGenerate produces one PCM-16 mono buffer for the digit at the given sample
// rate. Ports DtmfToneGenerator.Generate. durationMs default 150, amplitude
// default 0.5 — use the *Default constants for the C# default-parameter values.
//
// Errors (mirroring the C# exceptions): sampleRateHz <= 0 or durationMs <= 0
// (ArgumentOutOfRangeException), or an unsupported digit (ArgumentException).
func DtmfGenerate(digit rune, sampleRateHz, durationMs int, amplitude float32) ([]byte, error) {
	if sampleRateHz <= 0 {
		return nil, errors.New("sampleRateHz out of range")
	}
	if durationMs <= 0 {
		return nil, errors.New("durationMs out of range")
	}
	key := toUpperInvariant(digit)
	pair, ok := dtmfFrequencies[key]
	if !ok {
		return nil, fmt.Errorf("Unsupported DTMF digit '%c'.", digit)
	}

	samples := sampleRateHz * durationMs / 1000
	buf := make([]byte, samples*2)
	for i := 0; i < samples; i++ {
		t := float64(i) / float64(sampleRateHz)
		s := 0.5 * float64(amplitude) * (math.Sin(2*math.Pi*float64(pair.low)*t) + math.Sin(2*math.Pi*float64(pair.high)*t))
		v := int16(clampFloat(s, -1, 1) * float64(math.MaxInt16))
		// Little-endian int16 (BinaryPrimitives.WriteInt16LittleEndian).
		buf[i*2] = byte(uint16(v))
		buf[i*2+1] = byte(uint16(v) >> 8)
	}
	return buf, nil
}

// DTMF default-parameter values matching the C# signature defaults.
const (
	dtmfDefaultDurationMs      = 150
	dtmfDefaultInterDigitGapMs = 50
	dtmfDefaultAmplitude       = 0.5
)

// DtmfGenerateSequence produces a full string of digits with gap silence between
// them. Ports DtmfToneGenerator.GenerateSequence (toneDurationMs=150,
// interDigitGapMs=50, amplitude=0.5 defaults). An empty string yields an empty
// (non-nil-semantics) buffer.
func DtmfGenerateSequence(digits string, sampleRateHz, toneDurationMs, interDigitGapMs int, amplitude float32) ([]byte, error) {
	if digits == "" {
		return []byte{}, nil
	}
	gapSamples := sampleRateHz * interDigitGapMs / 1000
	gap := make([]byte, gapSamples*2)

	runes := []rune(digits)
	var out []byte
	for i, d := range runes {
		tone, err := DtmfGenerate(d, sampleRateHz, toneDurationMs, amplitude)
		if err != nil {
			return nil, err
		}
		out = append(out, tone...)
		if i < len(runes)-1 {
			out = append(out, gap...)
		}
	}
	return out, nil
}

// DtmfSendThroughSession sends digits over the call via in-band tones. Ports
// DtmfToneGenerator.SendThroughSessionAsync (sampleRateHz=8000,
// toneDurationMs=150, interDigitGapMs=50 defaults). A nil session errors; an
// empty digit string is a no-op. The AudioFrame format is chosen from the sample
// rate exactly as the C# switch does.
func DtmfSendThroughSession(ctx context.Context, session ICallSession, digits string, sampleRateHz, toneDurationMs, interDigitGapMs int) error {
	if session == nil {
		return errors.New("session is required")
	}
	if digits == "" {
		return nil
	}
	pcm, err := DtmfGenerateSequence(digits, sampleRateHz, toneDurationMs, interDigitGapMs, dtmfDefaultAmplitude)
	if err != nil {
		return err
	}
	var format CallMediaFormat
	switch sampleRateHz {
	case 8000:
		format = CallMediaFormatMulaw8000
	case 16000:
		format = CallMediaFormatPcm16000
	case 24000:
		format = CallMediaFormatPcm24000
	default:
		format = CallMediaFormatPcm16000
	}
	return session.SendAudio(ctx, AudioFrame{Pcm: pcm, Format: format, Offset: 0})
}

// (clampFloat is defined in memory_llm_extractor.go and reused here for the
// Math.Clamp(value, -1, 1) quantisation step.)

// toUpperInvariant upper-cases an ASCII letter (char.ToUpperInvariant) — the
// DTMF table only contains ASCII, so a full Unicode fold is unnecessary and
// would diverge from the C# invariant behaviour for the letters A-D.
func toUpperInvariant(r rune) rune {
	if r >= 'a' && r <= 'z' {
		return r - ('a' - 'A')
	}
	return r
}

// dtmfSampleRateForFormat maps a media format to the sample rate the session
// send-fallback uses, mirroring the switch in TwilioCallSession.SendDtmfAsync /
// TelnyxCallSession / PlivoCallSession (Pcm16000→16000, Pcm24000→24000,
// Mulaw8000→8000, default→8000).
func dtmfSampleRateForFormat(f CallMediaFormat) int {
	switch f {
	case CallMediaFormatPcm16000:
		return 16000
	case CallMediaFormatPcm24000:
		return 24000
	case CallMediaFormatMulaw8000:
		return 8000
	default:
		return 8000
	}
}
