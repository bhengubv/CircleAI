// speech_inmemory.go
//
// Deterministic in-memory implementations of the CircleAI.Speech ASR / TTS /
// wake-word contracts. The C# reference ships only the "null" defaults plus
// injected cloud backends (Azure/Deepgram/OpenAI/...); this file provides the
// hermetic working implementations the Go port requires — a keyword recogniser,
// a template synthesiser, and a manually-fed wake-word detector — so every
// contract has a real, testable, no-network implementation.
//
// CONCURRENCY (this wave is stream/transport-heavy): the wake-word detector's
// Fire path snapshots the subscriber list UNDER the lock and invokes handlers
// OUTSIDE it, so a handler that (un)subscribes cannot self-deadlock the
// detector. Subscriptions are unbounded (a slice), so a subscriber attached
// before Start still receives every frame injected after Start.

package circleai

import (
	"context"
	"encoding/binary"
	"errors"
	"math"
	"strings"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// KeywordSpeechRecognizer — deterministic ISpeechRecognizer
// ---------------------------------------------------------------------------

// KeywordSpeechRecognizer is a deterministic ISpeechRecognizer that decodes a
// caller-registered mapping of PCM-16 byte prefixes to phrases. It is hermetic
// (no model, no network): the same audio always yields the same transcript. Used
// as the working in-memory recogniser in place of an injected cloud backend.
//
// Matching: the recogniser holds an ordered list of (marker, phrase) rules. The
// first rule whose marker is a prefix of the decoded audio's leading samples
// wins. When nothing matches it returns the empty transcript (like the null
// recogniser) so callers degrade gracefully.
type KeywordSpeechRecognizer struct {
	mu       sync.RWMutex
	rules    []keywordRule
	language string
}

type keywordRule struct {
	marker []byte
	phrase string
}

// NewKeywordSpeechRecognizer constructs an empty recogniser reporting the given
// language on every result (pass "" for none).
func NewKeywordSpeechRecognizer(language string) *KeywordSpeechRecognizer {
	return &KeywordSpeechRecognizer{language: language}
}

// Register adds a rule: when the decoded audio begins with marker, Transcribe
// returns phrase. Rules are tried in registration order. Returns the receiver
// for chaining.
func (r *KeywordSpeechRecognizer) Register(marker []byte, phrase string) *KeywordSpeechRecognizer {
	r.mu.Lock()
	r.rules = append(r.rules, keywordRule{marker: append([]byte(nil), marker...), phrase: phrase})
	r.mu.Unlock()
	return r
}

// BackendID returns "keyword".
func (r *KeywordSpeechRecognizer) BackendID() string { return "keyword" }

// Transcribe decodes audioPcm16Mono, matches it against the registered rules, and
// returns the first hit as a single-segment result spanning the whole clip.
func (r *KeywordSpeechRecognizer) Transcribe(ctx context.Context, audioPcm16Mono []byte, sampleRateHz int, languageHint string) (SpeechTranscriptionResult, error) {
	if err := ctx.Err(); err != nil {
		return SpeechTranscriptionResult{}, err
	}
	lang := r.language
	if lang == "" {
		lang = languageHint
	}

	dur := pcm16Duration(len(audioPcm16Mono), sampleRateHz)

	r.mu.RLock()
	defer r.mu.RUnlock()
	for _, rule := range r.rules {
		if hasBytePrefix(audioPcm16Mono, rule.marker) {
			seg := TranscribedSegment{Text: rule.phrase, Offset: 0, Duration: dur, Language: lang, Confidence: 1}
			return SpeechTranscriptionResult{
				Text:          rule.phrase,
				Language:      lang,
				Segments:      []TranscribedSegment{seg},
				TotalDuration: dur,
			}, nil
		}
	}
	return SpeechTranscriptionResult{Text: "", Language: lang, Segments: []TranscribedSegment{}, TotalDuration: dur}, nil
}

// ---------------------------------------------------------------------------
// TemplateSpeechSynthesizer — deterministic ISpeechSynthesizer
// ---------------------------------------------------------------------------

// TemplateSpeechSynthesizer is a deterministic ISpeechSynthesizer that renders
// text to a reproducible PCM-16 mono tone: it emits a fixed number of samples per
// character at a per-character frequency derived from the rune, so identical text
// always yields byte-identical audio. Hermetic — no model, no network.
type TemplateSpeechSynthesizer struct {
	sampleRateHz    int
	samplesPerChar  int
	amplitude       int16
	baseFrequencyHz float64
}

// NewTemplateSpeechSynthesizer constructs a synthesiser. Defaults (via
// NewDefaultTemplateSpeechSynthesizer): 16 kHz, 1600 samples/char (100 ms),
// amplitude 8000, base frequency 110 Hz.
func NewTemplateSpeechSynthesizer(sampleRateHz, samplesPerChar int, amplitude int16, baseFrequencyHz float64) *TemplateSpeechSynthesizer {
	return &TemplateSpeechSynthesizer{
		sampleRateHz:    sampleRateHz,
		samplesPerChar:  samplesPerChar,
		amplitude:       amplitude,
		baseFrequencyHz: baseFrequencyHz,
	}
}

// NewDefaultTemplateSpeechSynthesizer constructs a synthesiser with default
// parameters (16 kHz, 1600 samples/char, amplitude 8000, base 110 Hz).
func NewDefaultTemplateSpeechSynthesizer() *TemplateSpeechSynthesizer {
	return NewTemplateSpeechSynthesizer(16000, 1600, 8000, 110)
}

// BackendID returns "template".
func (s *TemplateSpeechSynthesizer) BackendID() string { return "template" }

// Synthesize renders text to deterministic PCM-16 mono audio. Empty text yields
// empty audio (zero duration). voiceID/languageHint are accepted for signature
// parity but do not affect the deterministic waveform.
func (s *TemplateSpeechSynthesizer) Synthesize(ctx context.Context, text, voiceID, languageHint string) (SynthesisResult, error) {
	if err := ctx.Err(); err != nil {
		return SynthesisResult{}, err
	}
	runes := []rune(text)
	if len(runes) == 0 {
		return SynthesisResult{AudioPcm16Mono: []byte{}, SampleRateHz: s.sampleRateHz, Duration: 0}, nil
	}

	total := len(runes) * s.samplesPerChar
	pcm := make([]byte, total*2)
	idx := 0
	for _, r := range runes {
		freq := s.baseFrequencyHz + float64(int(r)%64)*10.0
		for i := 0; i < s.samplesPerChar; i++ {
			t := float64(idx) / float64(s.sampleRateHz)
			v := float64(s.amplitude) * math.Sin(2*math.Pi*freq*t)
			binary.LittleEndian.PutUint16(pcm[idx*2:idx*2+2], uint16(int16(v)))
			idx++
		}
	}
	dur := pcm16Duration(len(pcm), s.sampleRateHz)
	return SynthesisResult{AudioPcm16Mono: pcm, SampleRateHz: s.sampleRateHz, Duration: dur}, nil
}

// ---------------------------------------------------------------------------
// InMemoryWakeWordDetector — deterministic ISpeechWakeWordDetector
// ---------------------------------------------------------------------------

// InMemoryWakeWordDetector is a deterministic ISpeechWakeWordDetector driven by
// manually-injected audio frames (in place of a system microphone + hey-snips
// model). It runs a caller-supplied recogniser over each frame while started and
// fires a WakeWordEvent whenever the transcript contains the configured keyword.
// This is the working in-memory wake-word implementation for the Go port.
//
// Concurrency: Fire snapshots subscribers under the lock and invokes handlers
// outside it (no self-deadlock if a handler (un)subscribes). Subscribing before
// Start guarantees delivery of every frame injected after Start — subscriptions
// are retained in an unbounded slice, never dropped.
type InMemoryWakeWordDetector struct {
	keyword    string
	recognizer ISpeechRecognizer
	sampleRate int

	mu        sync.Mutex
	listening bool
	closed    bool
	subs      []*wakeSub
}

type wakeSub struct {
	handler func(WakeWordEvent)
}

// NewInMemoryWakeWordDetector constructs a detector that fires on keyword and
// transcribes injected frames with recognizer (which must not be nil). frames are
// injected at sampleRateHz. keyword matching is case-insensitive substring.
func NewInMemoryWakeWordDetector(keyword string, recognizer ISpeechRecognizer, sampleRateHz int) (*InMemoryWakeWordDetector, error) {
	if strings.TrimSpace(keyword) == "" {
		return nil, errors.New("keyword required")
	}
	if recognizer == nil {
		return nil, errors.New("recognizer required")
	}
	if sampleRateHz <= 0 {
		return nil, errors.New("sampleRateHz must be positive")
	}
	return &InMemoryWakeWordDetector{keyword: strings.TrimSpace(keyword), recognizer: recognizer, sampleRate: sampleRateHz}, nil
}

// BackendID returns "in-memory".
func (d *InMemoryWakeWordDetector) BackendID() string { return "in-memory" }

// Subscribe registers handler and returns an idempotent unsubscribe func. Safe to
// call before or after Start; a pre-Start subscriber sees every post-Start frame.
func (d *InMemoryWakeWordDetector) Subscribe(handler func(WakeWordEvent)) (unsubscribe func()) {
	if handler == nil {
		panic("handler must not be nil")
	}
	sub := &wakeSub{handler: handler}
	d.mu.Lock()
	d.subs = append(d.subs, sub)
	d.mu.Unlock()

	var once sync.Once
	return func() {
		once.Do(func() {
			d.mu.Lock()
			for i, s := range d.subs {
				if s == sub {
					d.subs = append(d.subs[:i], d.subs[i+1:]...)
					break
				}
			}
			d.mu.Unlock()
		})
	}
}

// Start begins listening. Idempotent.
func (d *InMemoryWakeWordDetector) Start(context.Context) error {
	d.mu.Lock()
	defer d.mu.Unlock()
	if d.closed {
		return errors.New("detector closed")
	}
	d.listening = true
	return nil
}

// Stop stops listening. Idempotent.
func (d *InMemoryWakeWordDetector) Stop(context.Context) error {
	d.mu.Lock()
	defer d.mu.Unlock()
	d.listening = false
	return nil
}

// Close disposes the detector (ports IAsyncDisposable.DisposeAsync).
func (d *InMemoryWakeWordDetector) Close(context.Context) error {
	d.mu.Lock()
	defer d.mu.Unlock()
	d.listening = false
	d.closed = true
	d.subs = nil
	return nil
}

// IsListening reports whether the detector is currently started.
func (d *InMemoryWakeWordDetector) IsListening() bool {
	d.mu.Lock()
	defer d.mu.Unlock()
	return d.listening
}

// InjectFrame transcribes one PCM-16 mono frame and, if listening and the
// transcript contains the keyword (case-insensitive), fires a WakeWordEvent to
// all current subscribers. Returns true if the wake word fired. When not
// listening, the frame is ignored and false is returned.
func (d *InMemoryWakeWordDetector) InjectFrame(ctx context.Context, framePcm16Mono []byte) (bool, error) {
	d.mu.Lock()
	if d.closed || !d.listening {
		d.mu.Unlock()
		return false, nil
	}
	d.mu.Unlock()

	res, err := d.recognizer.Transcribe(ctx, framePcm16Mono, d.sampleRate, "")
	if err != nil {
		return false, err
	}
	if res.Text == "" || !strings.Contains(strings.ToLower(res.Text), strings.ToLower(d.keyword)) {
		return false, nil
	}

	// Snapshot subscribers UNDER the lock, fire OUTSIDE it.
	d.mu.Lock()
	snapshot := make([]*wakeSub, len(d.subs))
	copy(snapshot, d.subs)
	d.mu.Unlock()

	evt := WakeWordEvent{Keyword: d.keyword, Confidence: 1, DetectedAtUtc: time.Now().UTC()}
	for _, s := range snapshot {
		s.handler(evt)
	}
	return true, nil
}

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

// pcm16Duration returns the wall-clock duration of a PCM-16 mono byte buffer.
func pcm16Duration(byteLen, sampleRateHz int) time.Duration {
	if sampleRateHz <= 0 {
		return 0
	}
	samples := byteLen / 2
	return time.Duration(int64(samples) * int64(time.Second) / int64(sampleRateHz))
}

// hasBytePrefix reports whether b begins with prefix. An empty prefix matches
// any buffer (mirrors "no marker" always-hit rules).
func hasBytePrefix(b, prefix []byte) bool {
	if len(prefix) == 0 {
		return true
	}
	if len(b) < len(prefix) {
		return false
	}
	for i := range prefix {
		if b[i] != prefix[i] {
			return false
		}
	}
	return true
}

// Interface guards.
var (
	_ ISpeechRecognizer       = (*KeywordSpeechRecognizer)(nil)
	_ ISpeechSynthesizer      = (*TemplateSpeechSynthesizer)(nil)
	_ ISpeechWakeWordDetector = (*InMemoryWakeWordDetector)(nil)
)
