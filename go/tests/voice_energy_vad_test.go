// voice_energy_vad_test.go
//
// Verifies voice_energy_vad.go (stream RMS VAD) and the stream
// NullVoiceActivityDetector: frame accumulation across chunk boundaries, speech
// segment emission after a silence run, mid-speech tail emission at stream end,
// and pass-through of the null detector.

package circleai_test

import (
	"context"
	"encoding/binary"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// frame builds a FrameSizeBytes-sized (default 640) PCM frame at a constant amp.
func frame640(amp int16) []byte {
	b := make([]byte, 640)
	for i := 0; i < 320; i++ {
		binary.LittleEndian.PutUint16(b[i*2:i*2+2], uint16(amp))
	}
	return b
}

func collectSegments(ch <-chan circleai.VadSegment) []circleai.VadSegment {
	var out []circleai.VadSegment
	for s := range ch {
		out = append(out, s)
	}
	return out
}

func TestNullVoiceActivityDetector_Stream_PassThrough(t *testing.T) {
	d := circleai.NullVoiceActivityDetector{}
	ctx := context.Background()
	segs := collectSegments(d.Detect(ctx, feed(le16(1, 2), le16(3, 4))))
	if len(segs) != 2 {
		t.Fatalf("null vad emitted %d segments, want 2", len(segs))
	}
	for _, s := range segs {
		if !s.IsSpeech {
			t.Errorf("null vad segment not speech: %+v", s)
		}
	}
}

func TestEnergyVadDetector_EmitsSpeechAfterSilenceRun(t *testing.T) {
	d, err := circleai.NewEnergyVadDetector(0.02, 3, 640)
	if err != nil {
		t.Fatal(err)
	}
	// Loud amp: 6000/32768 ~= 0.183 RMS (constant) > 0.02 threshold -> speech.
	// Then >= 3 silence frames -> emit the buffered segment.
	loud := frame640(6000)
	silence := frame640(0)
	ctx := context.Background()

	segs := collectSegments(d.Detect(ctx, feed(loud, loud, silence, silence, silence)))
	if len(segs) != 1 {
		t.Fatalf("expected 1 emitted segment, got %d", len(segs))
	}
	if !segs[0].IsSpeech {
		t.Errorf("emitted segment not speech")
	}
	// Segment buffers the 2 speech frames + 3 trailing silence frames = 5*640.
	if len(segs[0].Audio) != 5*640 {
		t.Errorf("segment length = %d, want %d", len(segs[0].Audio), 5*640)
	}
}

func TestEnergyVadDetector_EmitsTailAtStreamEnd(t *testing.T) {
	d, _ := circleai.NewEnergyVadDetector(0.02, 10, 640)
	loud := frame640(6000)
	ctx := context.Background()
	// Stream ends while still in speech (silence run never reached) -> tail emit.
	segs := collectSegments(d.Detect(ctx, feed(loud, loud)))
	if len(segs) != 1 || !segs[0].IsSpeech {
		t.Fatalf("tail segment missing: %+v", segs)
	}
	if len(segs[0].Audio) != 2*640 {
		t.Errorf("tail length = %d, want %d", len(segs[0].Audio), 2*640)
	}
}

func TestEnergyVadDetector_AccumulatesAcrossChunkBoundaries(t *testing.T) {
	d, _ := circleai.NewEnergyVadDetector(0.02, 2, 640)
	loud := frame640(6000)
	silence := frame640(0)
	// Split a single 640-byte frame across two 320-byte chunks to prove the
	// residual buffer reassembles frames.
	half1 := loud[:320]
	half2 := loud[320:]
	ctx := context.Background()
	segs := collectSegments(d.Detect(ctx, feed(half1, half2, silence, silence)))
	if len(segs) != 1 {
		t.Fatalf("cross-boundary reassembly failed: %d segments", len(segs))
	}
}

func TestEnergyVadDetector_AllSilenceEmitsNothing(t *testing.T) {
	d, _ := circleai.NewEnergyVadDetector(0.02, 2, 640)
	silence := frame640(0)
	segs := collectSegments(d.Detect(context.Background(), feed(silence, silence, silence)))
	if len(segs) != 0 {
		t.Errorf("all-silence emitted %d segments", len(segs))
	}
}

func TestEnergyVadDetector_Validation(t *testing.T) {
	if _, err := circleai.NewEnergyVadDetector(0.02, 0, 640); err == nil {
		t.Error("zero silenceFrames should error")
	}
	if _, err := circleai.NewEnergyVadDetector(0.02, 5, 0); err == nil {
		t.Error("zero frameSizeBytes should error")
	}
	if _, err := circleai.NewEnergyVadDetector(-1, 5, 640); err == nil {
		t.Error("negative energyThreshold should error")
	}
}
