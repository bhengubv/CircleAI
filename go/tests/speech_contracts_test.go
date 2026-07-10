// speech_contracts_test.go
//
// Verifies the CircleAI.Speech contract null implementations and the deterministic
// in-memory implementations (KeywordSpeechRecognizer, TemplateSpeechSynthesizer,
// InMemoryWakeWordDetector). The wake-word detector's subscribe-before-start
// fan-out and snapshot-outside-lock firing are exercised.

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestNullSpeechRecognizer(t *testing.T) {
	r := circleai.NullSpeechRecognizerInstance
	if r.BackendID() != "null" {
		t.Errorf("backend %q", r.BackendID())
	}
	res, err := r.Transcribe(context.Background(), le16(1, 2, 3), 16000, "en")
	if err != nil {
		t.Fatal(err)
	}
	if res.Text != "" || res.Language != "en" || len(res.Segments) != 0 || res.TotalDuration != 0 {
		t.Errorf("null asr %+v", res)
	}
}

func TestNullSpeechSynthesizer(t *testing.T) {
	s := circleai.NullSpeechSynthesizerInstance
	res, _ := s.Synthesize(context.Background(), "hi", "", "")
	if s.BackendID() != "null" || len(res.AudioPcm16Mono) != 0 || res.SampleRateHz != 16000 {
		t.Errorf("null tts %q %+v", s.BackendID(), res)
	}
}

func TestNullSpeechWakeWord_NeverFires(t *testing.T) {
	d := circleai.NullSpeechWakeWordDetector{}
	if d.BackendID() != "null" {
		t.Errorf("backend %q", d.BackendID())
	}
	fired := false
	unsub := d.Subscribe(func(circleai.WakeWordEvent) { fired = true })
	_ = d.Start(context.Background())
	_ = d.Stop(context.Background())
	_ = d.Close(context.Background())
	unsub()
	if fired {
		t.Error("null wake word fired")
	}
}

func TestNullOcr(t *testing.T) {
	o := circleai.NullOpticalCharacterRecognizerInstance
	res, _ := o.Recognize(context.Background(), []byte{1, 2}, "auto")
	if o.BackendID() != "null" || res.Text != "" || len(res.Blocks) != 0 {
		t.Errorf("null ocr %q %+v", o.BackendID(), res)
	}
}

func TestKeywordSpeechRecognizer_MatchesPrefix(t *testing.T) {
	r := circleai.NewKeywordSpeechRecognizer("en")
	r.Register(le16(111), "hey b").Register(le16(222), "what time is it")
	if r.BackendID() != "keyword" {
		t.Errorf("backend %q", r.BackendID())
	}
	// Audio starting with sample 111 -> "hey b".
	res, err := r.Transcribe(context.Background(), le16(111, 5, 6), 16000, "")
	if err != nil {
		t.Fatal(err)
	}
	if res.Text != "hey b" || res.Language != "en" {
		t.Errorf("keyword match %+v", res)
	}
	if len(res.Segments) != 1 || res.Segments[0].Text != "hey b" {
		t.Errorf("segments %+v", res.Segments)
	}
	// Duration: 3 samples @ 16k.
	if res.TotalDuration <= 0 {
		t.Errorf("duration not set: %v", res.TotalDuration)
	}
	// No matching prefix -> empty.
	miss, _ := r.Transcribe(context.Background(), le16(999), 16000, "")
	if miss.Text != "" {
		t.Errorf("unexpected match %+v", miss)
	}
}

func TestKeywordSpeechRecognizer_LanguageHintFallback(t *testing.T) {
	r := circleai.NewKeywordSpeechRecognizer("") // no fixed language
	r.Register(le16(1), "hi")
	res, _ := r.Transcribe(context.Background(), le16(1), 16000, "zu")
	if res.Language != "zu" {
		t.Errorf("language hint not used: %q", res.Language)
	}
}

func TestTemplateSynthesizer_Deterministic(t *testing.T) {
	s := circleai.NewDefaultTemplateSpeechSynthesizer()
	if s.BackendID() != "template" {
		t.Errorf("backend %q", s.BackendID())
	}
	a, err := s.Synthesize(context.Background(), "hello", "", "")
	if err != nil {
		t.Fatal(err)
	}
	b, _ := s.Synthesize(context.Background(), "hello", "", "")
	if !bytesEqual(a.AudioPcm16Mono, b.AudioPcm16Mono) {
		t.Error("synthesis not deterministic for identical text")
	}
	// 5 chars * 1600 samples/char * 2 bytes.
	if len(a.AudioPcm16Mono) != 5*1600*2 {
		t.Errorf("audio length = %d, want %d", len(a.AudioPcm16Mono), 5*1600*2)
	}
	if a.SampleRateHz != 16000 || a.Duration <= 0 {
		t.Errorf("meta %+v", a)
	}
	// Different text -> different audio.
	c, _ := s.Synthesize(context.Background(), "world", "", "")
	if bytesEqual(a.AudioPcm16Mono, c.AudioPcm16Mono) {
		t.Error("different text produced identical audio")
	}
	// Empty text -> empty audio.
	e, _ := s.Synthesize(context.Background(), "", "", "")
	if len(e.AudioPcm16Mono) != 0 || e.Duration != 0 {
		t.Errorf("empty text audio %+v", e)
	}
}

func TestInMemoryWakeWord_FiresOnKeyword(t *testing.T) {
	rec := circleai.NewKeywordSpeechRecognizer("en")
	rec.Register(le16(111), "hey b please")
	d, err := circleai.NewInMemoryWakeWordDetector("hey b", rec, 16000)
	if err != nil {
		t.Fatal(err)
	}
	if d.BackendID() != "in-memory" {
		t.Errorf("backend %q", d.BackendID())
	}

	// Subscribe BEFORE start — must receive the fire produced after start.
	var events []circleai.WakeWordEvent
	unsub := d.Subscribe(func(e circleai.WakeWordEvent) { events = append(events, e) })
	defer unsub()

	// Not listening yet: injecting a matching frame must not fire.
	fired, _ := d.InjectFrame(context.Background(), le16(111, 1, 1))
	if fired || len(events) != 0 {
		t.Fatalf("fired before start: fired=%v events=%d", fired, len(events))
	}

	if err := d.Start(context.Background()); err != nil {
		t.Fatal(err)
	}
	if !d.IsListening() {
		t.Fatal("not listening after start")
	}

	// Matching frame -> fire.
	fired, err = d.InjectFrame(context.Background(), le16(111, 1, 1))
	if err != nil {
		t.Fatal(err)
	}
	if !fired || len(events) != 1 || events[0].Keyword != "hey b" {
		t.Fatalf("keyword did not fire: fired=%v events=%+v", fired, events)
	}

	// Non-matching frame -> no fire.
	fired, _ = d.InjectFrame(context.Background(), le16(999))
	if fired || len(events) != 1 {
		t.Errorf("non-matching frame fired: %d events", len(events))
	}

	// After stop, matching frame must not fire.
	_ = d.Stop(context.Background())
	fired, _ = d.InjectFrame(context.Background(), le16(111, 1, 1))
	if fired || len(events) != 1 {
		t.Errorf("fired after stop: %d events", len(events))
	}
}

func TestInMemoryWakeWord_UnsubscribeStopsDelivery(t *testing.T) {
	rec := circleai.NewKeywordSpeechRecognizer("en").Register(le16(7), "hey b")
	d, _ := circleai.NewInMemoryWakeWordDetector("hey b", rec, 16000)
	count := 0
	unsub := d.Subscribe(func(circleai.WakeWordEvent) { count++ })
	_ = d.Start(context.Background())
	_, _ = d.InjectFrame(context.Background(), le16(7))
	unsub()
	unsub() // idempotent
	_, _ = d.InjectFrame(context.Background(), le16(7))
	if count != 1 {
		t.Errorf("delivery after unsubscribe: count=%d want 1", count)
	}
}

func TestInMemoryWakeWord_HandlerCanUnsubscribeWithoutDeadlock(t *testing.T) {
	// A handler that unsubscribes itself must not deadlock (fire snapshots outside
	// the lock). If this test hangs, the snapshot-outside-lock rule is broken.
	rec := circleai.NewKeywordSpeechRecognizer("en").Register(le16(3), "hey b")
	d, _ := circleai.NewInMemoryWakeWordDetector("hey b", rec, 16000)
	var unsub func()
	unsub = d.Subscribe(func(circleai.WakeWordEvent) { unsub() })
	_ = d.Start(context.Background())
	done := make(chan struct{})
	go func() {
		_, _ = d.InjectFrame(context.Background(), le16(3))
		close(done)
	}()
	select {
	case <-done:
	case <-timeoutC(2):
		t.Fatal("deadlock: handler unsubscribe blocked the fire path")
	}
}

func TestInMemoryWakeWord_Validation(t *testing.T) {
	rec := circleai.NewKeywordSpeechRecognizer("en")
	if _, err := circleai.NewInMemoryWakeWordDetector("  ", rec, 16000); err == nil {
		t.Error("blank keyword should error")
	}
	if _, err := circleai.NewInMemoryWakeWordDetector("hey", nil, 16000); err == nil {
		t.Error("nil recognizer should error")
	}
	if _, err := circleai.NewInMemoryWakeWordDetector("hey", rec, 0); err == nil {
		t.Error("zero sample rate should error")
	}
}
