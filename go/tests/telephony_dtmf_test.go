// telephony_dtmf_test.go
//
// Verifies CircleAI.Telephony/DtmfToneGenerator.cs port: exact PCM-16 buffer
// sizing, little-endian sample encoding, the reference sample formula, sequence
// gap insertion, and the error cases. The tone values are recomputed here from
// the same formula so the Go quantisation must match bit-for-bit.

package circleai_test

import (
	"math"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestDtmfGenerate_SizeAndSamples(t *testing.T) {
	// 8000 Hz, 150 ms => 8000*150/1000 = 1200 samples => 2400 bytes.
	buf, err := circleai.DtmfGenerate('5', 8000, 150, 0.5)
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	if len(buf) != 2400 {
		t.Fatalf("len = %d, want 2400", len(buf))
	}

	// Recompute the reference for digit '5' (low 770, high 1336) and compare
	// every sample, little-endian int16.
	low, high := 770.0, 1336.0
	amp := 0.5
	for i := 0; i < 1200; i++ {
		tt := float64(i) / 8000.0
		s := 0.5 * amp * (math.Sin(2*math.Pi*low*tt) + math.Sin(2*math.Pi*high*tt))
		clamped := s
		if clamped < -1 {
			clamped = -1
		} else if clamped > 1 {
			clamped = 1
		}
		want := int16(clamped * float64(math.MaxInt16))
		got := int16(uint16(buf[i*2]) | uint16(buf[i*2+1])<<8)
		if got != want {
			t.Fatalf("sample %d = %d, want %d", i, got, want)
		}
	}
}

func TestDtmfGenerate_AllDigits(t *testing.T) {
	for _, d := range "0123456789*#ABCD" {
		if _, err := circleai.DtmfGenerate(d, 8000, 50, 0.5); err != nil {
			t.Errorf("digit %c: unexpected error %v", d, err)
		}
	}
	// Lower-case letters fold to upper (char.ToUpperInvariant).
	if _, err := circleai.DtmfGenerate('a', 8000, 50, 0.5); err != nil {
		t.Errorf("lowercase 'a' should map to 'A': %v", err)
	}
}

func TestDtmfGenerate_Errors(t *testing.T) {
	if _, err := circleai.DtmfGenerate('5', 0, 150, 0.5); err == nil {
		t.Error("sampleRate 0 should error")
	}
	if _, err := circleai.DtmfGenerate('5', 8000, 0, 0.5); err == nil {
		t.Error("durationMs 0 should error")
	}
	if _, err := circleai.DtmfGenerate('Z', 8000, 150, 0.5); err == nil {
		t.Error("unsupported digit should error")
	}
}

func TestDtmfGenerateSequence_Gaps(t *testing.T) {
	// "12" at 8000 Hz: two 150ms tones (1200 samples each) + one 50ms gap
	// (400 samples). Total = (1200 + 400 + 1200) * 2 bytes = 5600.
	buf, err := circleai.DtmfGenerateSequence("12", 8000, 150, 50, 0.5)
	if err != nil {
		t.Fatalf("sequence: %v", err)
	}
	want := (1200 + 400 + 1200) * 2
	if len(buf) != want {
		t.Fatalf("len = %d, want %d", len(buf), want)
	}

	// The 400-sample gap between the tones must be silence (all zero bytes).
	gapStart := 1200 * 2
	gapEnd := gapStart + 400*2
	for i := gapStart; i < gapEnd; i++ {
		if buf[i] != 0 {
			t.Fatalf("gap byte %d = %d, want 0 (silence)", i, buf[i])
		}
	}

	// Empty string yields an empty buffer.
	empty, _ := circleai.DtmfGenerateSequence("", 8000, 150, 50, 0.5)
	if len(empty) != 0 {
		t.Errorf("empty sequence len = %d, want 0", len(empty))
	}
}
