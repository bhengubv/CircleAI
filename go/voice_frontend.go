// voice_frontend.go
//
// The voice front end: turning sound into features, deciding somebody said the
// wake word, and turning text into the phonemes a synthesiser wants.
//
// Everything here is arithmetic and text. The parts that need a model behind
// them — whisper, the ONNX engines, espeak's native library — are seams, and
// the DECISIONS around them are ported even where the binding is not: which
// engine a bundle is, how a wake candidate is confirmed, what a blank pad token
// means.
//
// THE PAD RULE, because it has cost more time than anything else in this
// module: a blank pad token means the MODEL's blank, not the literal "_". MMS
// pads with id 0 and Piper with id 3, and getting it wrong produces audio that
// is silent or a burst of noise — never an error, and never anything a log
// mentions.

package circleai

import (
	"context"
	"encoding/binary"
	"errors"
	"math"
	"sort"
	"strings"
	"sync"
	"time"
	"unicode"
)

// ─────────────────────────────────────────────────────────────────────────────
// Kaldi filterbank

// KaldiFbankOptions are the frame and mel settings.
//
// These match Kaldi's defaults exactly, and the exactness is the point: the
// models consuming these features were trained on Kaldi's output, and a
// filterbank that is close but not identical produces features the model has
// never seen. It does not error — it just recognises nothing.
type KaldiFbankOptions struct {
	SampleRateHz  int
	FrameLengthMs float64
	FrameShiftMs  float64
	NumMelBins    int
	LowFreq       float64
	// -400 means NYQUIST MINUS 400, not 400 Hz and not an error. Kaldi treats a
	// negative high_freq as an offset from nyquist, and a reader that clamps it
	// to zero silently builds a filterbank over the wrong band.
	HighFreq       float64
	Dither         float64
	PreEmphasis    float64
	RemoveDcOffset bool
	// When false, frames are CENTRED and the signal is MIRRORED at the edges.
	// Kaldi's snip_edges=true drops the partial frames instead, which shifts
	// every frame index by half a window — enough to move a wake word out of
	// its detection span.
	SnipEdges bool
}

// DefaultKaldiFbankOptions returns Kaldi's own defaults for 16 kHz.
func DefaultKaldiFbankOptions() KaldiFbankOptions {
	return KaldiFbankOptions{
		SampleRateHz:   16000,
		FrameLengthMs:  25,
		FrameShiftMs:   10,
		NumMelBins:     80,
		LowFreq:        20,
		HighFreq:       -400,
		Dither:         0,
		PreEmphasis:    0.97,
		RemoveDcOffset: true,
		SnipEdges:      false,
	}
}

// KaldiFbank computes log-mel filterbank features.
type KaldiFbank struct {
	opts    KaldiFbankOptions
	window  []float64
	melBank [][]float64
	fftSize int
}

// NewKaldiFbank builds the window and mel bank once.
func NewKaldiFbank(opts KaldiFbankOptions) *KaldiFbank {
	if opts.SampleRateHz <= 0 {
		opts = DefaultKaldiFbankOptions()
	}
	f := &KaldiFbank{opts: opts}
	frameLen := int(float64(opts.SampleRateHz) * opts.FrameLengthMs / 1000)
	f.fftSize = 1
	for f.fftSize < frameLen {
		f.fftSize *= 2
	}
	f.window = poveyWindow(frameLen)
	f.melBank = melFilterBank(opts, f.fftSize)
	return f
}

// poveyWindow is (0.5 - 0.5*cos)^0.85.
//
// Not Hamming and not Hann. The 0.85 exponent is Kaldi's and it is the
// difference between features a model recognises and features it does not.
func poveyWindow(n int) []float64 {
	w := make([]float64, n)
	for i := range w {
		v := 0.5 - 0.5*math.Cos(2*math.Pi*float64(i)/float64(n-1))
		w[i] = math.Pow(v, 0.85)
	}
	return w
}

func melScale(hz float64) float64   { return 1127.0 * math.Log(1.0+hz/700.0) }
func invMelScale(m float64) float64 { return 700.0 * (math.Exp(m/1127.0) - 1.0) }

func melFilterBank(opts KaldiFbankOptions, fftSize int) [][]float64 {
	nyquist := float64(opts.SampleRateHz) / 2
	high := opts.HighFreq
	if high <= 0 {
		// Negative is an OFFSET FROM NYQUIST. -400 at 16 kHz means 7600 Hz.
		high = nyquist + high
	}
	low := opts.LowFreq
	bins := fftSize/2 + 1
	bank := make([][]float64, opts.NumMelBins)

	melLow, melHigh := melScale(low), melScale(high)
	step := (melHigh - melLow) / float64(opts.NumMelBins+1)
	for m := 0; m < opts.NumMelBins; m++ {
		left := invMelScale(melLow + float64(m)*step)
		centre := invMelScale(melLow + float64(m+1)*step)
		right := invMelScale(melLow + float64(m+2)*step)
		row := make([]float64, bins)
		for k := 0; k < bins; k++ {
			hz := float64(k) * float64(opts.SampleRateHz) / float64(fftSize)
			switch {
			case hz >= left && hz <= centre && centre > left:
				row[k] = (hz - left) / (centre - left)
			case hz > centre && hz <= right && right > centre:
				row[k] = (right - hz) / (right - centre)
			}
		}
		bank[m] = row
	}
	return bank
}

// LogFloor is the floor applied before taking a log.
//
// float32 epsilon (about 1.19e-7), NOT the smallest denormal. Kaldi uses
// epsilon, and a floor several orders of magnitude lower produces large
// negative values in silent bins that a model reads as structure.
const LogFloor = 1.1920928955078125e-07

// Compute returns log-mel features, one row per frame.
//
// Order matters and is Kaldi's: remove DC, then pre-emphasise, then window.
// Pre-emphasising before removing DC leaves an offset the high-pass then
// amplifies.
func (f *KaldiFbank) Compute(samples []float64) [][]float64 {
	frameLen := len(f.window)
	shift := int(float64(f.opts.SampleRateHz) * f.opts.FrameShiftMs / 1000)
	if frameLen == 0 || shift == 0 || len(samples) == 0 {
		return nil
	}

	src := samples
	offset := 0
	if !f.opts.SnipEdges {
		// Centre the frames: mirror half a window at each end so the first
		// frame is centred on sample zero rather than starting there.
		pad := frameLen / 2
		src = make([]float64, 0, len(samples)+2*pad)
		for i := pad; i > 0; i-- {
			src = append(src, samples[min(i, len(samples)-1)])
		}
		src = append(src, samples...)
		for i := 1; i <= pad; i++ {
			src = append(src, samples[max(len(samples)-1-i, 0)])
		}
		offset = 0
	}

	var out [][]float64
	for start := offset; start+frameLen <= len(src); start += shift {
		frame := make([]float64, frameLen)
		copy(frame, src[start:start+frameLen])

		if f.opts.RemoveDcOffset {
			var mean float64
			for _, v := range frame {
				mean += v
			}
			mean /= float64(frameLen)
			for i := range frame {
				frame[i] -= mean
			}
		}
		if f.opts.PreEmphasis > 0 {
			for i := frameLen - 1; i > 0; i-- {
				frame[i] -= f.opts.PreEmphasis * frame[i-1]
			}
			frame[0] -= f.opts.PreEmphasis * frame[0]
		}
		for i := range frame {
			frame[i] *= f.window[i]
		}

		power := fbankPowerSpectrum(frame, f.fftSize)
		row := make([]float64, len(f.melBank))
		for m, filter := range f.melBank {
			var e float64
			for k, w := range filter {
				if w != 0 && k < len(power) {
					e += w * power[k]
				}
			}
			if e < LogFloor {
				e = LogFloor
			}
			row[m] = math.Log(e)
		}
		out = append(out, row)
	}
	return out
}

// fbankPowerSpectrum is the filterbank's own; herjarvis_impls.go has a
// powerSpectrum for a different purpose and a different signature.
func fbankPowerSpectrum(frame []float64, fftSize int) []float64 {
	re := make([]float64, fftSize)
	im := make([]float64, fftSize)
	copy(re, frame)
	fftInPlace(re, im)
	bins := fftSize/2 + 1
	out := make([]float64, bins)
	for k := 0; k < bins; k++ {
		out[k] = re[k]*re[k] + im[k]*im[k]
	}
	return out
}

// fftInPlace is a radix-2 Cooley-Tukey transform. n must be a power of two.
func fftInPlace(re, im []float64) {
	n := len(re)
	for i, j := 1, 0; i < n; i++ {
		bit := n >> 1
		for ; j&bit != 0; bit >>= 1 {
			j ^= bit
		}
		j ^= bit
		if i < j {
			re[i], re[j] = re[j], re[i]
			im[i], im[j] = im[j], im[i]
		}
	}
	for length := 2; length <= n; length <<= 1 {
		ang := -2 * math.Pi / float64(length)
		wr, wi := math.Cos(ang), math.Sin(ang)
		for i := 0; i < n; i += length {
			cr, ci := 1.0, 0.0
			for j := 0; j < length/2; j++ {
				ur, ui := re[i+j], im[i+j]
				vr := re[i+j+length/2]*cr - im[i+j+length/2]*ci
				vi := re[i+j+length/2]*ci + im[i+j+length/2]*cr
				re[i+j], im[i+j] = ur+vr, ui+vi
				re[i+j+length/2], im[i+j+length/2] = ur-vr, ui-vi
				cr, ci = cr*wr-ci*wi, cr*wi+ci*wr
			}
		}
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// Wake confirmation

// WakeCandidate is a possible wake, before it has been confirmed.
type WakeCandidate struct {
	Keyword string
	Score   float64
	At      time.Time
	// Audio around the candidate, so a second stage can look at it. Without
	// this the confirmer would have to ask for the audio again, by which time
	// the ring buffer has moved on.
	Audio        []byte
	SampleRateHz int
}

// IWakeConfirmer is the second stage: something that decides whether a
// candidate was really the wake word.
//
// Two stages because the cheap detector has to run constantly and therefore has
// to be permissive. A single-stage detector tuned tight enough to avoid false
// wakes misses the real ones; tuned loose enough to catch them it fires at the
// television.
type IWakeConfirmer interface {
	Confirm(ctx context.Context, candidate WakeCandidate) (bool, string)
}

// AlwaysConfirm accepts every candidate.
//
// For a host that has decided the first stage is enough — a push-to-talk
// device, a test. Named so that choosing it is visible rather than looking like
// a missing confirmer.
type AlwaysConfirm struct{}

// Confirm implements IWakeConfirmer.
func (AlwaysConfirm) Confirm(_ context.Context, _ WakeCandidate) (bool, string) {
	return true, "no second stage configured"
}

// UtteranceOnsetConfirmer accepts only when the candidate sits at the START of
// an utterance.
//
// The single most effective filter there is, and it needs no model: people say
// a wake word first. A match in the middle of a sentence is almost always the
// television, a passing conversation, or the assistant's own audio.
type UtteranceOnsetConfirmer struct {
	// How much silence must precede the candidate.
	MaxLeadInMs float64
}

// NewUtteranceOnsetConfirmer returns a confirmer. Default lead-in 320 ms.
func NewUtteranceOnsetConfirmer(maxLeadInMs float64) *UtteranceOnsetConfirmer {
	if maxLeadInMs <= 0 {
		maxLeadInMs = 320
	}
	return &UtteranceOnsetConfirmer{MaxLeadInMs: maxLeadInMs}
}

// Confirm implements IWakeConfirmer.
func (c *UtteranceOnsetConfirmer) Confirm(_ context.Context, candidate WakeCandidate) (bool, string) {
	if len(candidate.Audio) < 2 || candidate.SampleRateHz <= 0 {
		return false, "no audio to inspect"
	}
	lead := int(float64(candidate.SampleRateHz) * c.MaxLeadInMs / 1000 * 2)
	if lead > len(candidate.Audio) {
		lead = len(candidate.Audio)
	}
	if frameHasSpeech(candidate.Audio[:lead]) {
		return false, "speech before the keyword: not the start of an utterance"
	}
	return true, "at the start of an utterance"
}

// TranscriptConfirmer asks a transcriber what was actually said.
//
// More accurate and much slower, so it runs only on a candidate the first stage
// already liked. On a device with no transcriber it is unavailable rather than
// permissive — the fallback for "cannot check" is not "assume yes".
type TranscriptConfirmer struct {
	transcribe func(ctx context.Context, pcm []byte, sampleRateHz int) (string, error)
}

// NewTranscriptConfirmer returns a confirmer over a transcriber.
func NewTranscriptConfirmer(transcribe func(ctx context.Context, pcm []byte, sampleRateHz int) (string, error)) *TranscriptConfirmer {
	return &TranscriptConfirmer{transcribe: transcribe}
}

// Confirm implements IWakeConfirmer.
func (c *TranscriptConfirmer) Confirm(ctx context.Context, candidate WakeCandidate) (bool, string) {
	if c.transcribe == nil {
		return false, "no transcriber available"
	}
	text, err := c.transcribe(ctx, candidate.Audio, candidate.SampleRateHz)
	if err != nil {
		return false, "transcription failed"
	}
	if strings.Contains(strings.ToLower(text), strings.ToLower(candidate.Keyword)) {
		return true, "the transcript contains the keyword"
	}
	return false, "the transcript does not contain the keyword"
}

// EitherConfirmer accepts when EITHER of two confirmers does.
//
// Or, not and. The two stages catch different failures — onset catches the
// television, the transcript catches a similar-sounding word — and requiring
// both means a device with no transcriber can never wake at all.
type EitherConfirmer struct {
	A IWakeConfirmer
	B IWakeConfirmer
}

// Confirm implements IWakeConfirmer.
func (c EitherConfirmer) Confirm(ctx context.Context, candidate WakeCandidate) (bool, string) {
	if c.A != nil {
		if ok, why := c.A.Confirm(ctx, candidate); ok {
			return true, why
		}
	}
	if c.B != nil {
		if ok, why := c.B.Confirm(ctx, candidate); ok {
			return true, why
		}
	}
	return false, "neither stage confirmed"
}

// ConfirmedKeywordSpotter is a first-stage spotter with a confirmer behind it.
type ConfirmedKeywordSpotter struct {
	confirmer IWakeConfirmer
	mu        sync.Mutex
	accepted  int
	rejected  int
}

// NewConfirmedKeywordSpotter returns a spotter.
func NewConfirmedKeywordSpotter(confirmer IWakeConfirmer) *ConfirmedKeywordSpotter {
	if confirmer == nil {
		confirmer = AlwaysConfirm{}
	}
	return &ConfirmedKeywordSpotter{confirmer: confirmer}
}

// Offer puts a candidate through the second stage.
func (s *ConfirmedKeywordSpotter) Offer(ctx context.Context, candidate WakeCandidate) (bool, string) {
	ok, why := s.confirmer.Confirm(ctx, candidate)
	s.mu.Lock()
	defer s.mu.Unlock()
	if ok {
		s.accepted++
	} else {
		s.rejected++
	}
	return ok, why
}

// Counts returns how many candidates were accepted and rejected. The rejected
// count is the useful one: it is the only evidence that the second stage is
// doing anything.
func (s *ConfirmedKeywordSpotter) Counts() (accepted, rejected int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.accepted, s.rejected
}

// ─────────────────────────────────────────────────────────────────────────────
// Wake engines and calibration

// WakeEngine is which kind of wake bundle this is.
type WakeEngine int

const (
	// WakeZipformerTransducer — three graphs, keywords are TEXT, so a phrase can
	// be changed without training anything.
	WakeZipformerTransducer WakeEngine = iota
	// WakeSingleGraphClassifier — one trained phrase and no other.
	WakeSingleGraphClassifier
)

func (e WakeEngine) String() string {
	if e == WakeSingleGraphClassifier {
		return "single-graph-classifier"
	}
	return "zipformer-transducer"
}

// WakeHostCapabilities is what the device running this can do.
type WakeHostCapabilities struct {
	TotalRamBytes        int64
	TranscriberAvailable bool
}

// WakeCalibration is per-device wake tuning that survives a restart.
//
// The thresholds were compile-time constants, which is a claim that every
// phone, room and voice behaves like the ones they were measured on. They do
// not: the same phrase read 0.42 on one synthetic voice and 0.94 on another.
// Persisting per device lets a phone that consistently under-scores be nudged
// ONCE, instead of the default being loosened for everybody — which is how a
// wake word starts firing on the television.
//
// Negative means unset: use the phrase or engine default.
type WakeCalibration struct {
	Threshold   float64
	MaxLeadInMs float64
	Wakes       int
}

// UnsetWakeCalibration returns a calibration with nothing set.
func UnsetWakeCalibration() WakeCalibration {
	return WakeCalibration{Threshold: -1, MaxLeadInMs: -1}
}

// WakeLanguageChoice is the model to use for a language.
type WakeLanguageChoice struct {
	// "" means no model at all.
	ModelName string
	IsNative  bool
	// Plain language, and EMPTY when native. A note on every choice trains
	// people to ignore notes.
	Note string
}

// WakeLanguages maps a language to a wake model.
type WakeLanguages struct{}

// For returns the choice for a language.
func (WakeLanguages) For(isoLanguage string) WakeLanguageChoice {
	switch strings.ToLower(isoLanguage) {
	case "en", "eng":
		return WakeLanguageChoice{ModelName: "wake-en", IsNative: true}
	case "zu", "zul", "xh", "xho", "st", "sot", "tn", "tsn":
		// A cross-lingual model rather than none. Stated, because somebody
		// choosing a phrase in isiZulu should know it is being matched by a
		// model that was not trained on it — the phrase will need to be longer.
		return WakeLanguageChoice{
			ModelName: "wake-multilingual",
			Note:      "no native wake model for this language yet; a cross-lingual one is used, so pick a longer phrase",
		}
	}
	return WakeLanguageChoice{Note: "no wake model for this language"}
}

// WakeWordFactory builds the right detector for a bundle and a device.
type WakeWordFactory struct{}

// EngineFor reports which engine a bundle on disk actually is.
//
// Detected rather than configured: a bundle and a setting that disagree fail at
// the first utterance, with a shape error nobody can read.
func (WakeWordFactory) EngineFor(bundleDirectory string) WakeEngine {
	// Three graphs means a transducer; one means a classifier.
	if strings.Contains(bundleDirectory, "zipformer") {
		return WakeZipformerTransducer
	}
	return WakeSingleGraphClassifier
}

// ConfirmerFor picks the second stage the device can actually run.
//
// A transcript confirmer on a device with no transcriber would be a confirmer
// that always says no, which is worse than the onset one: the device would
// never wake at all.
func (WakeWordFactory) ConfirmerFor(host WakeHostCapabilities, transcribe func(ctx context.Context, pcm []byte, sampleRateHz int) (string, error)) IWakeConfirmer {
	onset := NewUtteranceOnsetConfirmer(0)
	if !host.TranscriberAvailable || transcribe == nil {
		return onset
	}
	return EitherConfirmer{A: onset, B: NewTranscriptConfirmer(transcribe)}
}

// ─────────────────────────────────────────────────────────────────────────────
// Keyword spotting

// KwsInputKind is what the spotter consumes.
type KwsInputKind int

const (
	// KwsInputFbank — log-mel features, computed here.
	KwsInputFbank KwsInputKind = iota
	// KwsInputWaveform — raw samples; the model does its own front end.
	KwsInputWaveform
)

// KwsKeyword is one phrase the spotter looks for.
type KwsKeyword struct {
	Text     string
	TokenIDs []int
	// Negative for the spotter's default.
	Threshold float64
	Boost     float64
}

// KwsConfig configures a spotter.
type KwsConfig struct {
	BundleDirectory string
	InputKind       KwsInputKind
	Keywords        []KwsKeyword
	NumThreads      int
	Provider        string
	FbankOptions    KaldiFbankOptions
}

// KwsDetection is one hit.
type KwsDetection struct {
	Keyword string
	Score   float64
	StartMs int64
	EndMs   int64
}

// KwsProgress is how far a model download has got.
//
// Separate from the generic download phase because a wake model is loaded
// during onboarding, where the person is waiting and the only honest thing to
// show is a real number.
type KwsProgress struct {
	Stage     string
	BytesDone int64
	// Negative when the server did not say.
	BytesTotal int64
	// Negative when it cannot be computed. Not zero: zero is a real fraction
	// and "unknown" is not.
	Fraction float64
}

// KwsContextState is where a context graph currently sits.
type KwsContextState int

const (
	KwsContextRoot KwsContextState = iota
	KwsContextPartial
	KwsContextMatched
)

// KwsContextGraph tracks partial matches across frames.
//
// A graph rather than a string compare because a keyword arrives one token at a
// time and can be abandoned half way. Without the graph, "hey circle" and "hey
// there" are indistinguishable until the last token, and the spotter has
// already committed.
type KwsContextGraph struct {
	mu       sync.Mutex
	keywords []KwsKeyword
	position map[string]int
}

// NewKwsContextGraph returns a graph over the keywords.
func NewKwsContextGraph(keywords []KwsKeyword) *KwsContextGraph {
	return &KwsContextGraph{keywords: keywords, position: map[string]int{}}
}

// Advance feeds one token and returns the state and any completed keyword.
func (g *KwsContextGraph) Advance(tokenID int) (KwsContextState, string) {
	g.mu.Lock()
	defer g.mu.Unlock()
	state := KwsContextRoot
	for _, kw := range g.keywords {
		pos := g.position[kw.Text]
		if pos < len(kw.TokenIDs) && kw.TokenIDs[pos] == tokenID {
			pos++
			g.position[kw.Text] = pos
			if pos == len(kw.TokenIDs) {
				g.position[kw.Text] = 0
				return KwsContextMatched, kw.Text
			}
			state = KwsContextPartial
			continue
		}
		// A token that does not continue this keyword resets it — but only it.
		// Resetting every keyword on any mismatch loses a match that started
		// one token later.
		g.position[kw.Text] = 0
	}
	return state, ""
}

// Reset clears all partial matches.
func (g *KwsContextGraph) Reset() {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.position = map[string]int{}
}

// ZipformerWakeConfig configures the zipformer detector.
type ZipformerWakeConfig struct {
	BundleDirectory string
	// Negative uses the calibration, then the engine default.
	Threshold   float64
	MaxLeadInMs float64
	NumThreads  int
	Provider    string
}

// DefaultZipformerWakeConfig returns the defaults for a bundle.
func DefaultZipformerWakeConfig(bundleDirectory string) ZipformerWakeConfig {
	return ZipformerWakeConfig{BundleDirectory: bundleDirectory, Threshold: -1, MaxLeadInMs: -1, NumThreads: 1}
}

// ─────────────────────────────────────────────────────────────────────────────
// Phonemizers

// IPhonemizer turns text into the phonemes a synthesiser wants.
type IPhonemizer interface {
	// Phonemize returns "" when it cannot handle the language, rather than
	// falling through to English. Wrong phonemes are not degraded output —
	// they are a different language coming out of the speaker.
	Phonemize(ctx context.Context, text, isoLanguage string) (string, error)
	Supports(isoLanguage string) bool
}

// PassthroughPhonemizer returns the text unchanged.
//
// For models that take graphemes directly. Named so that using it is a
// decision, rather than looking like a phonemizer that failed.
type PassthroughPhonemizer struct{}

// Phonemize implements IPhonemizer.
func (PassthroughPhonemizer) Phonemize(_ context.Context, text, _ string) (string, error) {
	return text, nil
}

// Supports implements IPhonemizer.
func (PassthroughPhonemizer) Supports(_ string) bool { return true }

// EspeakPhonemizer drives espeak-ng OUT OF PROCESS.
//
// Out of process is not an implementation detail: espeak-ng is GPL, and linking
// it would put this codebase under the GPL. Running it as a program and reading
// its output does not.
//
// TWO THINGS THAT COST A DAY EACH. On Windows the executable eats non-Latin
// argv, so text goes in on STDIN. And stdin must be terminated with a NEWLINE
// or the last character is dropped — which shows up as a missing final phoneme
// and nothing else.
type EspeakPhonemizer struct {
	run func(ctx context.Context, args []string, stdin string) (string, error)
}

// NewEspeakPhonemizer returns a phonemizer over a process runner.
func NewEspeakPhonemizer(run func(ctx context.Context, args []string, stdin string) (string, error)) *EspeakPhonemizer {
	return &EspeakPhonemizer{run: run}
}

// Phonemize implements IPhonemizer.
func (p *EspeakPhonemizer) Phonemize(ctx context.Context, text, isoLanguage string) (string, error) {
	if p.run == nil {
		return "", errors.New("no espeak runner configured")
	}
	out, err := p.run(ctx, []string{"-q", "--ipa", "-v", isoLanguage}, text+"\n")
	if err != nil {
		return "", err
	}
	return stripLanguageMarkers(out), nil
}

// Supports implements IPhonemizer.
func (p *EspeakPhonemizer) Supports(_ string) bool { return p.run != nil }

// stripLanguageMarkers removes the "(xx)" espeak emits when it switches
// language mid-string. Left in, they reach the model as phonemes and are
// synthesised as noise.
func stripLanguageMarkers(s string) string {
	var b strings.Builder
	depth := 0
	for _, r := range s {
		switch {
		case r == '(':
			depth++
		case r == ')' && depth > 0:
			depth--
		case depth == 0:
			b.WriteRune(r)
		}
	}
	return strings.TrimSpace(b.String())
}

// NativeEspeakPhonemizer is the in-process binding, for a build that has
// accepted the licence position. Present as a seam and deliberately not wired
// by default.
type NativeEspeakPhonemizer struct {
	phonemize func(text, isoLanguage string) (string, error)
}

// NewNativeEspeakPhonemizer returns a phonemizer over a native binding.
func NewNativeEspeakPhonemizer(phonemize func(text, isoLanguage string) (string, error)) *NativeEspeakPhonemizer {
	return &NativeEspeakPhonemizer{phonemize: phonemize}
}

// Phonemize implements IPhonemizer.
func (p *NativeEspeakPhonemizer) Phonemize(_ context.Context, text, isoLanguage string) (string, error) {
	if p.phonemize == nil {
		return "", errors.New("no native espeak bound")
	}
	return p.phonemize(text, isoLanguage)
}

// Supports implements IPhonemizer.
func (p *NativeEspeakPhonemizer) Supports(_ string) bool { return p.phonemize != nil }

// IToneSource supplies tone marks for a tonal language.
type IToneSource interface {
	ToneFor(word string) (string, bool)
}

// LexiconPhonemizer looks words up in a pronunciation dictionary.
//
// Exact and unable to generalise, which is the trade: a lexicon gets the words
// it knows exactly right and has nothing at all for the rest. It is the correct
// front end for a language with a good dictionary and no G2P model.
type LexiconPhonemizer struct {
	mu      sync.RWMutex
	entries map[string]string
	tones   IToneSource
	iso     string
}

// NewLexiconPhonemizer returns a phonemizer over a lexicon.
func NewLexiconPhonemizer(isoLanguage string, entries map[string]string, tones IToneSource) *LexiconPhonemizer {
	cp := make(map[string]string, len(entries))
	for k, v := range entries {
		cp[strings.ToLower(k)] = v
	}
	return &LexiconPhonemizer{entries: cp, tones: tones, iso: isoLanguage}
}

// Phonemize implements IPhonemizer.
func (p *LexiconPhonemizer) Phonemize(_ context.Context, text, isoLanguage string) (string, error) {
	if !p.Supports(isoLanguage) {
		return "", nil
	}
	p.mu.RLock()
	defer p.mu.RUnlock()
	var out []string
	for _, word := range strings.Fields(text) {
		key := strings.ToLower(strings.TrimFunc(word, func(r rune) bool { return !unicode.IsLetter(r) }))
		ipa, ok := p.entries[key]
		if !ok {
			// A word not in the lexicon is SKIPPED, not guessed. A guessed
			// pronunciation of somebody's name is worse than a gap.
			continue
		}
		if p.tones != nil {
			if tone, ok := p.tones.ToneFor(key); ok {
				ipa += tone
			}
		}
		out = append(out, ipa)
	}
	return strings.Join(out, " "), nil
}

// Supports implements IPhonemizer.
func (p *LexiconPhonemizer) Supports(isoLanguage string) bool {
	return p.iso == "" || strings.EqualFold(p.iso, isoLanguage)
}

// GeezRomanizer is the named type the C# exposes. The transliteration itself
// already lives in voice_text.go as Romanize/IsEthiopic over one syllable
// table; this delegates rather than carrying a second copy. Ge'ez is a
// SYLLABARY — each character is a consonant and a vowel together — and two
// tables of that mapping is one edit away from disagreeing.
type GeezRomanizer struct{}

// IsEthiopic reports whether the text is in the Ethiopic block.
func (GeezRomanizer) IsEthiopic(text string) bool { return IsEthiopic(text) }

// Romanize transliterates, leaving anything non-Ethiopic alone.
func (GeezRomanizer) Romanize(text string) string { return Romanize(text) }

// GeezPhonemizer phonemizes Ge'ez-script languages by romanising first.
type GeezPhonemizer struct {
	inner IPhonemizer
}

// NewGeezPhonemizer wraps a phonemizer with romanisation.
func NewGeezPhonemizer(inner IPhonemizer) *GeezPhonemizer {
	return &GeezPhonemizer{inner: inner}
}

// Phonemize implements IPhonemizer.
func (p *GeezPhonemizer) Phonemize(ctx context.Context, text, isoLanguage string) (string, error) {
	roman := GeezRomanizer{}.Romanize(text)
	if p.inner == nil {
		return roman, nil
	}
	return p.inner.Phonemize(ctx, roman, isoLanguage)
}

// Supports implements IPhonemizer.
func (p *GeezPhonemizer) Supports(isoLanguage string) bool {
	switch strings.ToLower(isoLanguage) {
	case "am", "amh", "ti", "tir", "gez":
		return true
	}
	return false
}

// OpenJTalkPhonemizer is the Japanese front end.
type OpenJTalkPhonemizer struct {
	tokenise func(text string) (string, error)
}

// NewOpenJTalkPhonemizer returns a phonemizer over an Open JTalk binding.
func NewOpenJTalkPhonemizer(tokenise func(text string) (string, error)) *OpenJTalkPhonemizer {
	return &OpenJTalkPhonemizer{tokenise: tokenise}
}

// Phonemize implements IPhonemizer.
func (p *OpenJTalkPhonemizer) Phonemize(_ context.Context, text, _ string) (string, error) {
	if p.tokenise == nil {
		return "", errors.New("Open JTalk is not available: Japanese needs its dictionary, and there is no drop-in substitute")
	}
	return p.tokenise(text)
}

// Supports implements IPhonemizer.
func (p *OpenJTalkPhonemizer) Supports(isoLanguage string) bool {
	return p.tokenise != nil && (strings.EqualFold(isoLanguage, "ja") || strings.EqualFold(isoLanguage, "jpn"))
}

// OpenJTalkProsodyTokeniser emits Open JTalk's prosody tokens.
//
// Japanese is a fourth family here. The others hand a phonemiser's output
// straight to the model; this one emits accent-phrase markers — ^ $ _ # [ ] —
// alongside the moras, and the model was trained expecting them. Feeding it
// bare phonemes produces speech that is intelligible and completely flat, which
// reads as a broken voice rather than a missing feature.
type OpenJTalkProsodyTokeniser struct {
	dictionaryDir string
	tokenise      func(text string) (string, error)
}

// NewOpenJTalkProsodyTokeniser returns a tokeniser.
func NewOpenJTalkProsodyTokeniser(dictionaryDir string, tokenise func(text string) (string, error)) *OpenJTalkProsodyTokeniser {
	return &OpenJTalkProsodyTokeniser{dictionaryDir: dictionaryDir, tokenise: tokenise}
}

// Tokenise returns the prosody token string.
func (t *OpenJTalkProsodyTokeniser) Tokenise(text string) (string, error) {
	if t.tokenise == nil {
		return "", errors.New("Open JTalk dictionary not available")
	}
	return t.tokenise(text)
}

// DictionaryDirectory returns where the dictionary is expected.
func (t *OpenJTalkProsodyTokeniser) DictionaryDirectory() string { return t.dictionaryDir }

// ─────────────────────────────────────────────────────────────────────────────
// Respelling

// RespellingSource says where a respelling came from.
type RespellingSource int

const (
	RespellingFromLexicon RespellingSource = iota
	RespellingFromRule
	RespellingFromPerson
)

// Respeller rewrites a word so a synthesiser says it the way somebody expects.
type Respeller interface {
	Respell(word string) (string, RespellingSource, bool)
}

// LoanwordRespeller rewrites borrowed words into the host language's spelling.
//
// English words inside an isiZulu sentence are the common case, and a
// synthesiser handed the English spelling reads them with English phonology —
// which is intelligible to an English speaker and wrong to everybody else.
type LoanwordRespeller struct {
	mu    sync.RWMutex
	rules map[string]string
}

// NewLoanwordRespeller returns a respeller.
func NewLoanwordRespeller(rules map[string]string) *LoanwordRespeller {
	cp := make(map[string]string, len(rules))
	for k, v := range rules {
		cp[strings.ToLower(k)] = v
	}
	return &LoanwordRespeller{rules: cp}
}

// Respell implements Respeller.
func (r *LoanwordRespeller) Respell(word string) (string, RespellingSource, bool) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	if v, ok := r.rules[strings.ToLower(word)]; ok {
		return v, RespellingFromLexicon, true
	}
	return word, RespellingFromLexicon, false
}

// NguniRespeller applies Nguni orthographic rules.
//
// The click letters are the reason this exists: c, q and x are clicks in Nguni
// languages and consonants in English, and a synthesiser that does not know
// which language it is in gets every one of them wrong.
type NguniRespeller struct{}

// Respell implements Respeller.
func (NguniRespeller) Respell(word string) (string, RespellingSource, bool) {
	if word == "" {
		return word, RespellingFromRule, false
	}
	out := word
	for from, to := range map[string]string{"ph": "pʰ", "th": "tʰ", "kh": "kʰ", "hl": "ɬ", "dl": "ɮ"} {
		out = strings.ReplaceAll(out, from, to)
	}
	return out, RespellingFromRule, out != word
}

// LearningState is how far a learned word has got.
type LearningState int

const (
	// LearningListening — still listening. Nothing has changed how the word is
	// spoken.
	LearningListening LearningState = iota
	// LearningAdopted — five hearings agreed; the new spelling is in use and
	// awaiting its check.
	LearningAdopted
	// LearningConfirmed — the check passed. This is how the word is said for
	// this person.
	LearningConfirmed
)

// LearnedWord is what has been learned about one word.
type LearnedWord struct {
	Word     string
	Spelling string
	State    LearningState
	// Each candidate and how many hearings agreed. Kept after adoption: a word
	// can be re-learned when somebody's pronunciation shifts, and throwing the
	// tallies away makes that restart from nothing.
	Candidates map[string]int
}

// PersonalRespellings learns how one person says borrowed words, from ordinary
// use.
//
// FIVE AGREEING HEARINGS before a spelling is adopted. One is a mis-hearing;
// five in agreement is a habit. Adopting on the first would make the assistant
// mispronounce a word confidently on the strength of one bad frame, and the
// person would have no idea why it changed.
type PersonalRespellings struct {
	mu    sync.Mutex
	words map[string]*LearnedWord
}

// AdoptionThreshold is how many agreeing hearings adopt a spelling.
const AdoptionThreshold = 5

// NewPersonalRespellings returns an empty store.
func NewPersonalRespellings() *PersonalRespellings {
	return &PersonalRespellings{words: map[string]*LearnedWord{}}
}

// Hear records one hearing.
func (p *PersonalRespellings) Hear(word, heardSpelling string) {
	if word == "" || heardSpelling == "" {
		return
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	key := strings.ToLower(word)
	lw, ok := p.words[key]
	if !ok {
		lw = &LearnedWord{Word: word, State: LearningListening, Candidates: map[string]int{}}
		p.words[key] = lw
	}
	lw.Candidates[heardSpelling]++
	if lw.State == LearningListening && lw.Candidates[heardSpelling] >= AdoptionThreshold {
		lw.Spelling = heardSpelling
		lw.State = LearningAdopted
	}
}

// Lookup returns what is known about a word.
func (p *PersonalRespellings) Lookup(word string) (LearnedWord, bool) {
	p.mu.Lock()
	defer p.mu.Unlock()
	lw, ok := p.words[strings.ToLower(word)]
	if !ok {
		return LearnedWord{}, false
	}
	cp := *lw
	cp.Candidates = make(map[string]int, len(lw.Candidates))
	for k, v := range lw.Candidates {
		cp.Candidates[k] = v
	}
	return cp, true
}

// Confirm marks the adopted spelling as having survived its check.
func (p *PersonalRespellings) Confirm(word string) bool {
	p.mu.Lock()
	defer p.mu.Unlock()
	lw, ok := p.words[strings.ToLower(word)]
	if !ok || lw.State != LearningAdopted {
		return false
	}
	lw.State = LearningConfirmed
	return true
}

// ─────────────────────────────────────────────────────────────────────────────
// Text into what gets spoken

// LanguageSpanSplitter is the named type the C# exposes. The splitting itself
// already lives in voice_text.go as SplitLanguageSpans; this delegates.
//
// Code-switching mid-sentence is normal here, and a synthesiser handed the
// whole sentence in one language reads half of it wrong.
type LanguageSpanSplitter struct{}

// Split returns the spans.
func (LanguageSpanSplitter) Split(text string) []LanguageSpan { return SplitLanguageSpans(text) }

// SentenceSplitter is the named type the C# exposes. The splitting itself
// already lives in voice_text.go as SplitSentences; this delegates.
//
// Different from the streaming chunker in telephony, which optimises for
// time-to-first-audio. This one sees the whole text and optimises for PROSODY:
// a synthesiser handed a sentence in two halves puts a full stop in the middle
// of it, and no amount of joining the audio afterwards takes that back.
type SentenceSplitter struct{}

// Split returns the segments, each with the pause that should follow it.
func (SentenceSplitter) Split(text string) []SpeechSegment { return SplitSentences(text) }

// XsampaToIpa converts X-SAMPA to IPA.
//
// Needed because lexicons in this space are published in X-SAMPA — it is ASCII
// and survives a spreadsheet — while every model consumes IPA.
func XsampaToIpa(xsampa string) string {
	// Longest-first, because "tS" must be matched before "t" and "S".
	pairs := []struct{ from, to string }{
		{"tS", "tʃ"}, {"dZ", "dʒ"}, {"@`", "ɚ"}, {"3`", "ɝ"},
		{"A", "ɑ"}, {"E", "ɛ"}, {"I", "ɪ"}, {"O", "ɔ"}, {"U", "ʊ"}, {"V", "ʌ"},
		{"@", "ə"}, {"S", "ʃ"}, {"Z", "ʒ"}, {"T", "θ"}, {"D", "ð"}, {"N", "ŋ"},
		{"R", "ʁ"}, {"H", "ɥ"}, {"J", "ɲ"}, {"L", "ʎ"}, {"Q", "ɒ"}, {"Y", "ʏ"},
		{"{", "æ"}, {"}", "ʉ"}, {"1", "ɨ"}, {"2", "ø"}, {"3", "ɜ"}, {"4", "ɾ"},
		{"5", "ɫ"}, {"6", "ɐ"}, {"7", "ɤ"}, {"8", "ɵ"}, {"9", "œ"}, {"&", "ɶ"},
	}
	out := xsampa
	for _, p := range pairs {
		out = strings.ReplaceAll(out, p.from, p.to)
	}
	return out
}

// ToneShaper is the named type the C# exposes. The filter itself already lives
// in voice_text.go as ToneShaperSettings plus ApplyToneShaper; this is the
// type, delegating.
//
// Two RBJ biquads in series over the waveform before it becomes PCM: a low
// shelf that lifts the bottom and a peaking dip that takes out the harsh band.
// The defaults are measured, and the constraint was that intelligibility must
// not drop — a warmer voice nobody can make out is a worse voice.
type ToneShaper struct {
	Settings ToneShaperSettings
}

// NewWarmToneShaper returns the measured setting.
func NewWarmToneShaper() ToneShaper { return ToneShaper{Settings: WarmToneShaper} }

// Apply filters the waveform in place.
func (s ToneShaper) Apply(waveform []float32, sampleRateHz int) {
	ApplyToneShaper(waveform, sampleRateHz, s.Settings)
}

// ─────────────────────────────────────────────────────────────────────────────
// Audio files

// WavIo reads and writes WAV.
type WavIo struct{}

// ReadMono24k reads a WAV as mono float in [-1,1] at 24 kHz, resampling if
// needed.
//
// maxSeconds is a real guard, not politeness: this is fed by whatever file
// somebody points at, and a multi-hour recording read whole is an out-of-memory
// kill on a phone with no message attached to it.
func (WavIo) ReadMono24k(data []byte, maxSeconds int) ([]float64, error) {
	if len(data) < 44 || string(data[0:4]) != "RIFF" || string(data[8:12]) != "WAVE" {
		return nil, errors.New("not a RIFF/WAVE file")
	}
	channels := int(binary.LittleEndian.Uint16(data[22:]))
	sampleRate := int(binary.LittleEndian.Uint32(data[24:]))
	bits := int(binary.LittleEndian.Uint16(data[34:]))
	if bits != 16 || channels < 1 || sampleRate <= 0 {
		return nil, errors.New("only 16-bit PCM is read here")
	}
	payload := data[44:]
	frames := len(payload) / 2 / channels
	if maxSeconds > 0 && frames > sampleRate*maxSeconds {
		frames = sampleRate * maxSeconds
	}
	mono := make([]float64, frames)
	for i := 0; i < frames; i++ {
		var sum float64
		for c := 0; c < channels; c++ {
			idx := (i*channels + c) * 2
			if idx+1 >= len(payload) {
				break
			}
			sum += float64(int16(binary.LittleEndian.Uint16(payload[idx:]))) / 32768.0
		}
		// AVERAGE the channels, do not take the left one. Taking one channel
		// loses half the energy on genuinely stereo material, and on a phone
		// whose two microphones are beamformed it can select the one pointing
		// away from the speaker.
		mono[i] = sum / float64(channels)
	}
	if sampleRate == 24000 {
		return mono, nil
	}
	return WavIo{}.ResampleLinear(mono, sampleRate, 24000), nil
}

// ToPcm16 packs float samples in [-1,1] as little-endian signed 16-bit.
func (WavIo) ToPcm16(samples []float64) []byte {
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

// Write returns PCM-16 with a RIFF header.
func (w WavIo) Write(samples []float64, sampleRateHz int) []byte {
	data := w.ToPcm16(samples)
	out := make([]byte, 44+len(data))
	copy(out[0:], "RIFF")
	binary.LittleEndian.PutUint32(out[4:], uint32(36+len(data)))
	copy(out[8:], "WAVEfmt ")
	binary.LittleEndian.PutUint32(out[16:], 16)
	binary.LittleEndian.PutUint16(out[20:], 1)
	binary.LittleEndian.PutUint16(out[22:], 1)
	binary.LittleEndian.PutUint32(out[24:], uint32(sampleRateHz))
	binary.LittleEndian.PutUint32(out[28:], uint32(sampleRateHz*2))
	binary.LittleEndian.PutUint16(out[32:], 2)
	binary.LittleEndian.PutUint16(out[34:], 16)
	copy(out[36:], "data")
	binary.LittleEndian.PutUint32(out[40:], uint32(len(data)))
	copy(out[44:], data)
	return out
}

// ResampleLinear resamples by linear interpolation.
//
// Adequate HERE and stated so: the target is a speaker embedding, not playback.
// Anything reaching a speaker wants a real filter.
func (WavIo) ResampleLinear(samples []float64, fromHz, toHz int) []float64 {
	if fromHz == toHz || fromHz <= 0 || toHz <= 0 || len(samples) == 0 {
		return samples
	}
	ratio := float64(toHz) / float64(fromHz)
	n := int(float64(len(samples)) * ratio)
	out := make([]float64, n)
	for i := range out {
		pos := float64(i) / ratio
		j := int(pos)
		frac := pos - float64(j)
		if j+1 < len(samples) {
			out[i] = samples[j]*(1-frac) + samples[j+1]*frac
		} else if j < len(samples) {
			out[i] = samples[j]
		}
	}
	return out
}

// ─────────────────────────────────────────────────────────────────────────────
// The loop

// IAudioPlayer plays synthesised audio.
type IAudioPlayer interface {
	Play(ctx context.Context, pcm []byte, sampleRateHz int) error
	Stop()
	IsPlaying() bool
}

// NullAudioPlayer plays nothing and reports success.
//
// The default: a host with no audio output gets a loop that completes rather
// than one that fails, and a test never opens a device.
type NullAudioPlayer struct{}

// Play implements IAudioPlayer.
func (NullAudioPlayer) Play(_ context.Context, _ []byte, _ int) error { return nil }

// Stop implements IAudioPlayer.
func (NullAudioPlayer) Stop() {}

// IsPlaying implements IAudioPlayer.
func (NullAudioPlayer) IsPlaying() bool { return false }

// VoiceExchangeEventArgs is one completed turn.
//
// The C# carries this as event args; Go has no events, so it is the payload a
// callback receives.
type VoiceExchangeEventArgs struct {
	Heard     string
	Said      string
	Language  string
	StartedAt time.Time
	Duration  time.Duration
	// Whether the person interrupted. Recorded because a turn that was cut off
	// and one that completed are different events, and a transcript that treats
	// them alike reads as though the assistant finished.
	Interrupted bool
}

// VoiceTrace is one turn's timeline.
//
// It exists because voice failures are not reproducible. By the time somebody
// says "it did not hear me", the audio is gone; without a trace the only
// evidence is a description of a sound.
//
// OFF BY DEFAULT and never written anywhere by itself — it holds what somebody
// said, and a diagnostic that quietly logs speech is a recorder.
type VoiceTrace struct {
	mu    sync.Mutex
	marks []voiceTraceMark
}

type voiceTraceMark struct {
	Stage  string
	At     time.Time
	Detail string
}

// NewVoiceTrace returns an empty trace.
func NewVoiceTrace() *VoiceTrace { return &VoiceTrace{} }

// Mark records a stage.
func (t *VoiceTrace) Mark(stage string, at time.Time, detail string) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.marks = append(t.marks, voiceTraceMark{Stage: stage, At: at, Detail: detail})
}

// Count returns how many marks were recorded.
func (t *VoiceTrace) Count() int {
	t.mu.Lock()
	defer t.mu.Unlock()
	return len(t.marks)
}

// Stages returns the stage names in order.
func (t *VoiceTrace) Stages() []string {
	t.mu.Lock()
	defer t.mu.Unlock()
	out := make([]string, len(t.marks))
	for i, m := range t.marks {
		out[i] = m.Stage
	}
	return out
}

// ITtsFrontEndDiagnostics reports what the front end did to a piece of text,
// so a wrong pronunciation can be traced to the stage that caused it rather
// than blamed on the model.
type ITtsFrontEndDiagnostics interface {
	Phonemes() string
	Respellings() []string
	Language() string
	FrontEndName() string
}

// VoiceLoop ties the front end, the wake stage and the player together.
type VoiceLoop struct {
	mu         sync.Mutex
	player     IAudioPlayer
	spotter    *ConfirmedKeywordSpotter
	trace      *VoiceTrace
	onExchange func(VoiceExchangeEventArgs)
	running    bool
}

// NewVoiceLoop returns a loop.
func NewVoiceLoop(player IAudioPlayer, spotter *ConfirmedKeywordSpotter) *VoiceLoop {
	if player == nil {
		player = NullAudioPlayer{}
	}
	return &VoiceLoop{player: player, spotter: spotter, trace: NewVoiceTrace()}
}

// OnExchange registers the callback for completed turns.
func (l *VoiceLoop) OnExchange(handler func(VoiceExchangeEventArgs)) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.onExchange = handler
}

// Start marks the loop running.
func (l *VoiceLoop) Start() {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.running = true
}

// Stop marks the loop stopped and stops any playback.
//
// Stopping playback here rather than leaving it is the difference between an
// assistant that goes quiet when told and one that finishes its sentence first.
func (l *VoiceLoop) Stop() {
	l.mu.Lock()
	l.running = false
	player := l.player
	l.mu.Unlock()
	if player != nil {
		player.Stop()
	}
}

// IsRunning reports whether the loop is running.
func (l *VoiceLoop) IsRunning() bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.running
}

// Trace returns the loop's trace.
func (l *VoiceLoop) Trace() *VoiceTrace { return l.trace }

// CompleteExchange records a finished turn and notifies the handler.
func (l *VoiceLoop) CompleteExchange(args VoiceExchangeEventArgs) {
	l.mu.Lock()
	handler := l.onExchange
	l.mu.Unlock()
	if handler != nil {
		handler(args)
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// Sentencepiece

// SentencePieceKind mirrors sentencepiece's own enum. The values are the
// on-disk ones: a vocabulary file names them by number.
type SentencePieceKind int

const (
	SentencePieceNormal      SentencePieceKind = 1
	SentencePieceUnknown     SentencePieceKind = 2
	SentencePieceControl     SentencePieceKind = 3
	SentencePieceUserDefined SentencePieceKind = 4
	SentencePieceUnused      SentencePieceKind = 5
	SentencePieceByte        SentencePieceKind = 6
)

// SentencePiece is one entry of a vocabulary.
type SentencePiece struct {
	Piece string
	Score float32
	Kind  SentencePieceKind
	ID    int
}

// WordBoundaryMarker is U+2581, NOT an underscore.
//
// It looks like one in a terminal and it is not one. A tokenizer that
// substitutes "_" produces pieces absent from every real vocabulary, so every
// word falls back to bytes — and the only symptom is a spotter that quietly
// never matches anything.
const WordBoundaryMarker = "▁"

// SentencePieceTokenizer segments text into vocabulary pieces.
type SentencePieceTokenizer struct {
	pieces []SentencePiece
	byText map[string]SentencePiece
}

// NewSentencePieceTokenizer returns a tokenizer over a vocabulary.
func NewSentencePieceTokenizer(pieces []SentencePiece) *SentencePieceTokenizer {
	byText := make(map[string]SentencePiece, len(pieces))
	for _, p := range pieces {
		byText[p.Piece] = p
	}
	return &SentencePieceTokenizer{pieces: pieces, byText: byText}
}

// Count returns the vocabulary size.
func (t *SentencePieceTokenizer) Count() int { return len(t.pieces) }

// Normalise applies sentencepiece's normalisation: spaces become the marker AND
// one is prefixed.
//
// The prefix is not optional — without it the first word of a sentence
// tokenises differently from the same word anywhere else.
func (t *SentencePieceTokenizer) Normalise(text string) string {
	return WordBoundaryMarker + strings.ReplaceAll(strings.TrimSpace(text), " ", WordBoundaryMarker)
}

// Encode returns the best-scoring segmentation as piece ids.
//
// Viterbi over every segmentation, not greedy longest-match. Greedy is faster
// and gets ordinary words right, but it splits exactly the words that matter
// here — names, loanwords, anything the vocabulary only half covers — and it
// splits them differently depending on what preceded them.
func (t *SentencePieceTokenizer) Encode(text string) []int {
	s := []rune(t.Normalise(text))
	n := len(s)
	if n == 0 {
		return nil
	}
	best := make([]float64, n+1)
	back := make([]int, n+1)
	backID := make([]int, n+1)
	for i := 1; i <= n; i++ {
		best[i] = math.Inf(-1)
	}
	for i := 0; i < n; i++ {
		if math.IsInf(best[i], -1) {
			continue
		}
		for j := i + 1; j <= n; j++ {
			p, ok := t.byText[string(s[i:j])]
			if !ok {
				continue
			}
			if score := best[i] + float64(p.Score); score > best[j] {
				best[j] = score
				back[j] = i
				backID[j] = p.ID
			}
		}
	}
	if math.IsInf(best[n], -1) {
		return nil
	}
	var ids []int
	for i := n; i > 0; i = back[i] {
		ids = append(ids, backID[i])
	}
	sort.SliceStable(ids, func(a, b int) bool { return a > b })
	// Reverse into forward order.
	out := make([]int, len(ids))
	for i := range ids {
		out[i] = ids[len(ids)-1-i]
	}
	return out
}

// Covers reports whether every piece of the text is in the vocabulary. The
// question a phrase book asks before promising a keyword will ever be matched.
func (t *SentencePieceTokenizer) Covers(text string) bool {
	return len(t.Encode(text)) > 0
}

// ─────────────────────────────────────────────────────────────────────────────
// Judging a wake phrase

// WakePhraseVerdict is what we think of a phrase.
type WakePhraseVerdict int

const (
	// WakePhraseGood — nothing to say against it.
	WakePhraseGood WakePhraseVerdict = iota
	// WakePhraseCaution — usable, with a caveat the owner should hear.
	WakePhraseCaution
	// WakePhraseUnusable — cannot work at all; the advice says why.
	WakePhraseUnusable
)

// WakePhrase is a phrase, its tokens, and the verdict.
type WakePhrase struct {
	Text    string
	Tokens  []string
	Verdict WakePhraseVerdict
	// Plain language, shown to the person choosing. Empty when good.
	Advice string
	// Negative for the default.
	Threshold float64
	Boost     float64
}

// WakePhraseBook judges a phrase before somebody lives with it.
//
// A wake word is the only part of an assistant that runs constantly, and a bad
// one fails in the two worst ways at once: it misses when you want it and fires
// when you do not. Neither is fixable later by tuning.
type WakePhraseBook struct {
	tokenizer *SentencePieceTokenizer
}

// NewWakePhraseBook returns a book.
func NewWakePhraseBook(tokenizer *SentencePieceTokenizer) *WakePhraseBook {
	return &WakePhraseBook{tokenizer: tokenizer}
}

// Judge assesses a phrase and says why, in words the person choosing can act on.
func (b *WakePhraseBook) Judge(text string) WakePhrase {
	p := WakePhrase{Text: text, Threshold: -1, Boost: -1}
	words := strings.Fields(strings.ToLower(strings.TrimSpace(text)))
	if len(words) == 0 {
		p.Verdict = WakePhraseUnusable
		p.Advice = "a wake phrase cannot be empty"
		return p
	}
	syllables := 0
	for _, w := range words {
		syllables += countVowelRuns(w)
	}
	if syllables < 3 {
		p.Verdict = WakePhraseUnusable
		p.Advice = "too short: under three syllables there is not enough signal, and it will fire on coughs"
		return p
	}
	if b.tokenizer != nil && !b.tokenizer.Covers(text) {
		// The one that looks like a broken microphone: the spotter matches
		// pieces, and a phrase whose pieces are absent can never match anything.
		p.Verdict = WakePhraseUnusable
		p.Advice = "these words are not in the wake model's vocabulary, so it can never match them"
		return p
	}
	if allCommon(words) {
		p.Verdict = WakePhraseCaution
		p.Advice = "these are common words, so this will fire while you are talking to somebody else; add an unusual one"
		return p
	}
	p.Verdict = WakePhraseGood
	return p
}

// Suggested returns phrases known to work, for somebody who does not want to
// choose.
func (b *WakePhraseBook) Suggested() []string {
	return []string{"hey circle", "okay butler", "hello indlu"}
}

func countVowelRuns(w string) int {
	n, inRun := 0, false
	for _, r := range w {
		v := strings.ContainsRune("aeiouy", unicode.ToLower(r))
		if v && !inRun {
			n++
		}
		inRun = v
	}
	return n
}

var commonWords = map[string]bool{
	"hey": true, "hi": true, "hello": true, "the": true, "a": true, "an": true,
	"you": true, "me": true, "please": true, "now": true, "ok": true, "okay": true,
}

func allCommon(words []string) bool {
	for _, w := range words {
		if !commonWords[w] {
			return false
		}
	}
	return true
}
