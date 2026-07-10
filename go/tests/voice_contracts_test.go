// voice_contracts_test.go
//
// Verifies the CircleAI.Voice contract vocabulary and null/deterministic
// implementations that are not stream-heavy: AudioFormat, NullVoiceTranscriber,
// KeywordVoiceTranscriber, NullWakeWordDetector, NullTtsEngine, and the
// SpeakerEmbedderInputKind enum.

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func drain(ch <-chan []byte) [][]byte {
	var out [][]byte
	for c := range ch {
		out = append(out, c)
	}
	return out
}

func feed(chunks ...[]byte) <-chan []byte {
	ch := make(chan []byte)
	go func() {
		defer close(ch)
		for _, c := range chunks {
			ch <- c
		}
	}()
	return ch
}

func TestAudioFormat_Canonical(t *testing.T) {
	f := circleai.AudioFormatPcm16Mono16k
	if f.SampleRate != 16000 || f.Channels != 1 || f.BitsPerSample != 16 {
		t.Errorf("canonical format %+v", f)
	}
}

func TestSpeakerEmbedderInputKind_Ordinals(t *testing.T) {
	if circleai.SpeakerEmbedderInputKindLogMel != 0 || circleai.SpeakerEmbedderInputKindRawWaveform != 1 {
		t.Fatalf("ordinals drifted")
	}
	if circleai.SpeakerEmbedderInputKindLogMel.String() != "LogMel" || circleai.SpeakerEmbedderInputKindRawWaveform.String() != "RawWaveform" {
		t.Errorf("String drifted")
	}
}

func TestNullVoiceTranscriber(t *testing.T) {
	tr := &circleai.NullVoiceTranscriber{}
	res, err := tr.Transcribe(context.Background(), le16(1, 2, 3))
	if err != nil {
		t.Fatal(err)
	}
	if res.Text != "" || res.Confidence != 0 || res.LanguageCode != "und" {
		t.Errorf("null transcriber %+v", res)
	}

	// StreamTranscribe drains input and yields nothing.
	ctx := context.Background()
	partials := tr.StreamTranscribe(ctx, feed(le16(1), le16(2)))
	got := 0
	for range partials {
		got++
	}
	if got != 0 {
		t.Errorf("null stream yielded %d items", got)
	}

	_ = tr.Close(ctx)
	if _, err := tr.Transcribe(ctx, nil); err == nil {
		t.Error("transcribe after close should error")
	}
}

func TestKeywordVoiceTranscriber_SingleShot(t *testing.T) {
	tr := circleai.NewKeywordVoiceTranscriber("en")
	tr.Register(le16(42), "hey b what time is it")
	res, err := tr.Transcribe(context.Background(), le16(42, 0, 0))
	if err != nil {
		t.Fatal(err)
	}
	if res.Text != "hey b what time is it" || res.Confidence != 1 || res.LanguageCode != "en" {
		t.Errorf("keyword transcribe %+v", res)
	}
	miss, _ := tr.Transcribe(context.Background(), le16(1))
	if miss.Text != "" {
		t.Errorf("unexpected match %+v", miss)
	}
}

func TestKeywordVoiceTranscriber_DefaultLanguageUnd(t *testing.T) {
	tr := circleai.NewKeywordVoiceTranscriber("")
	res, _ := tr.Transcribe(context.Background(), le16(9))
	if res.LanguageCode != "und" {
		t.Errorf("default language = %q, want und", res.LanguageCode)
	}
}

func TestKeywordVoiceTranscriber_StreamConcatenates(t *testing.T) {
	tr := circleai.NewKeywordVoiceTranscriber("en")
	// Marker spans two samples; feed them split across chunks.
	tr.Register(le16(10, 20), "hello world")
	ctx := context.Background()
	partials := tr.StreamTranscribe(ctx, feed(le16(10), le16(20), le16(30)))
	var last circleai.PartialTranscription
	got := 0
	for p := range partials {
		last = p
		got++
	}
	if got != 1 || !last.IsFinal || last.Text != "hello world" {
		t.Errorf("stream concat result got=%d last=%+v", got, last)
	}
}

func TestKeywordVoiceTranscriber_StreamNoMatchYieldsNothing(t *testing.T) {
	tr := circleai.NewKeywordVoiceTranscriber("en")
	tr.Register(le16(99), "nope")
	partials := tr.StreamTranscribe(context.Background(), feed(le16(1), le16(2)))
	for range partials {
		t.Error("no-match stream should yield nothing")
	}
}

func TestNullWakeWordDetector_Voice(t *testing.T) {
	d := circleai.NewNullWakeWordDetector()
	if d.WakeWord() != "Hey B" {
		t.Errorf("default wake word %q", d.WakeWord())
	}
	ctx := context.Background()
	if d.IsListening() {
		t.Error("should not listen before start")
	}
	fired := false
	d.Subscribe(func(circleai.WakeWordDetectedEventArgs) { fired = true })
	_ = d.Start(ctx)
	if !d.IsListening() {
		t.Error("should listen after start")
	}
	_ = d.Stop(ctx)
	if d.IsListening() {
		t.Error("should not listen after stop")
	}
	_ = d.Close(ctx)
	if fired {
		t.Error("null wake word fired")
	}
	if _, err := circleai.NewNullWakeWordDetectorWith("  "); err == nil {
		t.Error("blank wake word should error")
	}
	custom, err := circleai.NewNullWakeWordDetectorWith("Yo B")
	if err != nil || custom.WakeWord() != "Yo B" {
		t.Errorf("custom wake word %v %q", err, custom.WakeWord())
	}
}

func TestNullTtsEngine(t *testing.T) {
	e := circleai.NullTtsEngine{}
	res, err := e.Synthesise(context.Background(), "hello")
	if err != nil {
		t.Fatal(err)
	}
	if len(res.AudioData) != 0 || res.SampleRate != 24000 || res.Channels != 1 || res.BitsPerSample != 16 {
		t.Errorf("null tts %+v", res)
	}
	if circleai.NullTtsEngineEmptyResult.SampleRate != 24000 {
		t.Error("empty-result constant drifted")
	}
	chunks := drain(e.StreamSynthesise(context.Background(), "hello"))
	if len(chunks) != 0 {
		t.Errorf("null tts stream yielded %d chunks", len(chunks))
	}
}
