// speech_audio_converter_test.go
//
// Verifies speech_audio_converter.go: the AudioCodec enum ordinals/String, the
// G.711 mu-law / a-law encode-decode round trips, PCM-16 linear resampling, and
// the full Convert() codec+rate pipeline. Byte formats must match the C#
// AudioFormatConverter exactly.

package circleai_test

import (
	"bytes"
	"encoding/binary"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestAudioCodec_Ordinals(t *testing.T) {
	if circleai.AudioCodecPcm16 != 0 || circleai.AudioCodecMuLaw != 1 || circleai.AudioCodecALaw != 2 {
		t.Fatalf("ordinals drifted: %d %d %d", circleai.AudioCodecPcm16, circleai.AudioCodecMuLaw, circleai.AudioCodecALaw)
	}
	if circleai.AudioCodecPcm16.String() != "Pcm16" || circleai.AudioCodecMuLaw.String() != "MuLaw" || circleai.AudioCodecALaw.String() != "ALaw" {
		t.Fatalf("String drifted: %q %q %q", circleai.AudioCodecPcm16, circleai.AudioCodecMuLaw, circleai.AudioCodecALaw)
	}
}

func pcm16(samples ...int16) []byte {
	b := make([]byte, len(samples)*2)
	for i, s := range samples {
		binary.LittleEndian.PutUint16(b[i*2:i*2+2], uint16(s))
	}
	return b
}

func readPcm16(b []byte) []int16 {
	out := make([]int16, len(b)/2)
	for i := range out {
		out[i] = int16(binary.LittleEndian.Uint16(b[i*2 : i*2+2]))
	}
	return out
}

// muLaw of specific PCM values, cross-checked against the ITU-T G.711 reference
// arithmetic the C# port implements.
func TestMuLaw_KnownValues(t *testing.T) {
	// 0 encodes to 0xFF; the encode of 0 is 0xFF (~(sign|exp|mant) with all zero).
	if got := circleai.EncodePcm16ToMuLaw(pcm16(0)); got[0] != 0xFF {
		t.Errorf("mu-law(0) = 0x%02X, want 0xFF", got[0])
	}
	// Full-scale positive clips to 0x80 region; full-scale negative to 0x00 region.
	if got := circleai.EncodePcm16ToMuLaw(pcm16(32767)); got[0] != 0x80 {
		t.Errorf("mu-law(32767) = 0x%02X, want 0x80", got[0])
	}
	if got := circleai.EncodePcm16ToMuLaw(pcm16(-32768)); got[0] != 0x00 {
		t.Errorf("mu-law(-32768) = 0x%02X, want 0x00", got[0])
	}
}

func TestMuLaw_RoundTripMonotone(t *testing.T) {
	// mu-law is lossy, but decode(encode(x)) must be close and sign-preserving.
	for _, s := range []int16{-30000, -8000, -100, 0, 100, 8000, 30000} {
		enc := circleai.EncodePcm16ToMuLaw(pcm16(s))
		dec := readPcm16(circleai.DecodeMuLawToPcm16(enc))[0]
		if (s > 0 && dec < 0) || (s < 0 && dec > 0) {
			t.Errorf("mu-law sign flip: %d -> %d", s, dec)
		}
		diff := int(s) - int(dec)
		if diff < 0 {
			diff = -diff
		}
		// Quantisation error should be within a mu-law step (< ~500 for mid-range).
		if s != 0 && diff > 2100 {
			t.Errorf("mu-law round-trip too lossy: %d -> %d (diff %d)", s, dec, diff)
		}
	}
}

func TestALaw_KnownValues(t *testing.T) {
	// a-law of 0 in this implementation: sign=(0>>8)&0x80=0, v<256 so exp=0,
	// mantissa=0>>4=0, result=(0|0|0)^0x55 = 0x55. (Matches the C# LinearToALaw.)
	if got := circleai.EncodePcm16ToALaw(pcm16(0)); got[0] != 0x55 {
		t.Errorf("a-law(0) = 0x%02X, want 0x55", got[0])
	}
}

func TestALaw_RoundTripSignPreserving(t *testing.T) {
	for _, s := range []int16{-30000, -8000, -100, 0, 100, 8000, 30000} {
		enc := circleai.EncodePcm16ToALaw(pcm16(s))
		dec := readPcm16(circleai.DecodeALawToPcm16(enc))[0]
		if (s > 0 && dec < 0) || (s < 0 && dec > 0) {
			t.Errorf("a-law sign flip: %d -> %d", s, dec)
		}
	}
}

func TestResample_Identity(t *testing.T) {
	in := pcm16(1, 2, 3, 4)
	out := circleai.ResamplePcm16Linear(in, 16000, 16000)
	if !bytes.Equal(in, out) {
		t.Errorf("identity resample changed bytes")
	}
}

func TestResample_Downsample2to1(t *testing.T) {
	// 8 samples @ 16k -> 4 samples @ 8k.
	in := pcm16(0, 1000, 2000, 3000, 4000, 5000, 6000, 7000)
	out := circleai.ResamplePcm16Linear(in, 16000, 8000)
	got := readPcm16(out)
	if len(got) != 4 {
		t.Fatalf("downsample length = %d, want 4", len(got))
	}
	// srcIdx for dst i = i*16000/8000 = 2i -> exact source samples 0,2,4,6.
	want := []int16{0, 2000, 4000, 6000}
	for i := range want {
		if got[i] != want[i] {
			t.Errorf("downsample[%d] = %d, want %d", i, got[i], want[i])
		}
	}
}

func TestResample_UpsampleInterpolates(t *testing.T) {
	// 2 samples @ 8k -> 4 samples @ 16k; linear interpolation at frac 0.5.
	in := pcm16(0, 1000)
	out := circleai.ResamplePcm16Linear(in, 8000, 16000)
	got := readPcm16(out)
	if len(got) != 4 {
		t.Fatalf("upsample length = %d, want 4", len(got))
	}
	// dst0 srcIdx 0 -> 0; dst1 srcIdx 0.5 -> 500; dst2 srcIdx 1 -> 1000; dst3 srcIdx 1.5 -> clamps idx1 to last (1) => 1000.
	want := []int16{0, 500, 1000, 1000}
	for i := range want {
		if got[i] != want[i] {
			t.Errorf("upsample[%d] = %d, want %d", i, got[i], want[i])
		}
	}
}

func TestConvert_MuLaw8kToPcm16k(t *testing.T) {
	// mu-law 8k -> PCM16 16k: decode then 2x upsample -> doubles sample count.
	mulaw := []byte{0xFF, 0x80, 0x00}
	out, err := circleai.ConvertAudio(mulaw, circleai.AudioCodecMuLaw, 8000, circleai.AudioCodecPcm16, 16000)
	if err != nil {
		t.Fatal(err)
	}
	// 3 mu-law bytes -> 3 PCM samples -> upsampled to 6 samples -> 12 bytes.
	if len(out) != 12 {
		t.Errorf("convert output = %d bytes, want 12", len(out))
	}
}

func TestConvert_Pcm16ToMuLawRoundTrip(t *testing.T) {
	in := pcm16(1000, -2000, 3000, -4000)
	mulaw, err := circleai.ConvertAudio(in, circleai.AudioCodecPcm16, 8000, circleai.AudioCodecMuLaw, 8000)
	if err != nil {
		t.Fatal(err)
	}
	if len(mulaw) != 4 {
		t.Fatalf("pcm->mulaw len = %d, want 4", len(mulaw))
	}
	back, err := circleai.ConvertAudio(mulaw, circleai.AudioCodecMuLaw, 8000, circleai.AudioCodecPcm16, 8000)
	if err != nil {
		t.Fatal(err)
	}
	got := readPcm16(back)
	for i, s := range []int16{1000, -2000, 3000, -4000} {
		if (s > 0) != (got[i] >= 0) {
			t.Errorf("round-trip sign flip at %d: %d -> %d", i, s, got[i])
		}
	}
}

func TestConvert_RejectsBadRates(t *testing.T) {
	if _, err := circleai.ConvertAudio(nil, circleai.AudioCodecPcm16, 0, circleai.AudioCodecPcm16, 8000); err == nil {
		t.Error("zero input rate should error")
	}
	if _, err := circleai.ConvertAudio(nil, circleai.AudioCodecPcm16, 8000, circleai.AudioCodecPcm16, -1); err == nil {
		t.Error("negative output rate should error")
	}
}
