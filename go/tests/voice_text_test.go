// voice_text_test.go
//
// Asserts the Go SentenceSplitter / LanguageSpanSplitter / GeezRomanizer /
// ToneShaper / NchltPhonemizer ports against the same golden files the C#
// reference generates.
//
// Every case in these fixtures is adversarial. The splitter fixture carries a
// decimal point and a domain name that must NOT split next to a danda and a CJK
// stop that must; the Ge'ez fixture carries the numerals that used to romanise
// as syllables; the tone fixture separates the biquad (bit-reproducible) from
// the coefficient derivation (pow/sin/cos, which no language guarantees to the
// last bit).

package circleai_test

import (
	"encoding/json"
	"math"
	"os"
	"path/filepath"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func readVoiceTextFixture(t *testing.T, name string, into any) {
	t.Helper()
	path := filepath.Join("..", "..", "fixtures", name)
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read %s: %v", name, err)
	}
	if err := json.Unmarshal(data, into); err != nil {
		t.Fatalf("parse %s: %v", name, err)
	}
}

// ── SentenceSplitter ────────────────────────────────────────────────────────

type splitterFixture struct {
	MaxCharsPerSegment int `json:"maxCharsPerSegment"`
	Cases              []struct {
		Name     string `json:"name"`
		Text     string `json:"text"`
		Segments []struct {
			Text            string `json:"text"`
			TrailingPauseMs int    `json:"trailingPauseMs"`
		} `json:"segments"`
	} `json:"cases"`
}

func TestSentenceSplitterMatchesReference(t *testing.T) {
	var f splitterFixture
	readVoiceTextFixture(t, "voice_sentence_splitter.json", &f)

	if circleai.MaxCharsPerSegment != f.MaxCharsPerSegment {
		t.Fatalf("MaxCharsPerSegment = %d, want %d",
			circleai.MaxCharsPerSegment, f.MaxCharsPerSegment)
	}

	for _, c := range f.Cases {
		got := circleai.SplitSentences(c.Text)
		if len(got) != len(c.Segments) {
			t.Errorf("%s: got %d segments, want %d", c.Name, len(got), len(c.Segments))
			continue
		}
		for i, want := range c.Segments {
			if got[i].Text != want.Text {
				t.Errorf("%s segment %d: text %q, want %q", c.Name, i, got[i].Text, want.Text)
			}
			if got[i].TrailingPauseMs != want.TrailingPauseMs {
				t.Errorf("%s segment %d: pause %d, want %d",
					c.Name, i, got[i].TrailingPauseMs, want.TrailingPauseMs)
			}
		}
	}
}

func TestSplitsScriptsThatDoNotPunctuateInLatin(t *testing.T) {
	// A Latin-only terminator list under-splits for about a billion people and
	// fails silently — the paragraph simply runs together.
	var f splitterFixture
	readVoiceTextFixture(t, "voice_sentence_splitter.json", &f)

	want := map[string]bool{
		"devanagari-danda": true, "urdu-full-stop": true,
		"cjk-no-space": true, "khmer-khan": true,
	}
	for _, c := range f.Cases {
		if !want[c.Name] {
			continue
		}
		if n := len(circleai.SplitSentences(c.Text)); n < 2 {
			t.Errorf("%s produced %d segments — it must split", c.Name, n)
		}
	}
}

func TestDoesNotSplitDecimalOrDomain(t *testing.T) {
	var f splitterFixture
	readVoiceTextFixture(t, "voice_sentence_splitter.json", &f)

	want := map[string]bool{"decimal-point": true, "domain-name": true}
	for _, c := range f.Cases {
		if !want[c.Name] {
			continue
		}
		if n := len(circleai.SplitSentences(c.Text)); n != 2 {
			t.Errorf("%s produced %d segments, want 2", c.Name, n)
		}
	}
}

func TestLastSegmentHasNoTrailingPause(t *testing.T) {
	var f splitterFixture
	readVoiceTextFixture(t, "voice_sentence_splitter.json", &f)

	for _, c := range f.Cases {
		got := circleai.SplitSentences(c.Text)
		if len(got) > 0 && got[len(got)-1].TrailingPauseMs != 0 {
			t.Errorf("%s: last segment carries a %d ms pause", c.Name, got[len(got)-1].TrailingPauseMs)
		}
	}
}

// ── LanguageSpanSplitter ────────────────────────────────────────────────────

type spansFixture struct {
	Split []struct {
		Text  string `json:"text"`
		Spans []struct {
			Text      string `json:"text"`
			IsForeign bool   `json:"isForeign"`
		} `json:"spans"`
	} `json:"split"`
	ToSpokenForm []struct {
		Input  string `json:"input"`
		Output string `json:"output"`
	} `json:"toSpokenForm"`
	IsForeignWord []struct {
		Word    string `json:"word"`
		Foreign bool   `json:"foreign"`
	} `json:"isForeignWord"`
}

func TestLanguageSpansMatchReference(t *testing.T) {
	var f spansFixture
	readVoiceTextFixture(t, "voice_language_spans.json", &f)

	for _, c := range f.Split {
		got := circleai.SplitLanguageSpans(c.Text)
		if len(got) != len(c.Spans) {
			t.Errorf("%q: got %d spans, want %d", c.Text, len(got), len(c.Spans))
			continue
		}
		for i, want := range c.Spans {
			if got[i].Text != want.Text || got[i].IsForeign != want.IsForeign {
				t.Errorf("%q span %d: {%q %v}, want {%q %v}",
					c.Text, i, got[i].Text, got[i].IsForeign, want.Text, want.IsForeign)
			}
		}
	}

	for _, c := range f.ToSpokenForm {
		if got := circleai.ToSpokenForm(c.Input); got != c.Output {
			t.Errorf("ToSpokenForm(%q) = %q, want %q", c.Input, got, c.Output)
		}
	}

	for _, c := range f.IsForeignWord {
		if got := circleai.IsForeignWord(c.Word); got != c.Foreign {
			t.Errorf("IsForeignWord(%q) = %v, want %v", c.Word, got, c.Foreign)
		}
	}

	// The conservatism is the contract, not an accident: an ordinary lowercase
	// English word must NOT be flagged, because guessing wrong mispronounces a
	// native word to fix a foreign one.
	if circleai.IsForeignWord("hello") || circleai.IsForeignWord("Ngiyabonga") {
		t.Error("an ordinary word was flagged as foreign")
	}
}

// ── GeezRomanizer ───────────────────────────────────────────────────────────

type geezFixture struct {
	IsEthiopic []struct {
		Text     string `json:"text"`
		Ethiopic bool   `json:"ethiopic"`
	} `json:"isEthiopic"`
	Romanize []struct {
		Input  string `json:"input"`
		Output string `json:"output"`
	} `json:"romanize"`
}

func TestGeezRomanizerMatchesReference(t *testing.T) {
	var f geezFixture
	readVoiceTextFixture(t, "voice_geez_romanizer.json", &f)

	for _, c := range f.IsEthiopic {
		if got := circleai.IsEthiopic(c.Text); got != c.Ethiopic {
			t.Errorf("IsEthiopic(%q) = %v, want %v", c.Text, got, c.Ethiopic)
		}
	}
	for _, c := range f.Romanize {
		if got := circleai.Romanize(c.Input); got != c.Output {
			t.Errorf("Romanize(%q) = %q, want %q", c.Input, got, c.Output)
		}
	}
}

func TestNumeralsAreDroppedNotSpoken(t *testing.T) {
	// The eight-per-consonant layout stops at U+1357. Sizing the range check off
	// the consonant table swept seven numerals back into the syllabary, and they
	// came out as sound, so nothing failed.
	if got := circleai.Romanize("፩፪፫"); got != "" {
		t.Errorf("numerals romanised to %q — they have no sound to render", got)
	}
	if got := circleai.Romanize("ፘፙፚ"); got != "ryamyafya" {
		t.Errorf("lone syllables romanised to %q, want %q", got, "ryamyafya")
	}
}

// ── ToneShaper ──────────────────────────────────────────────────────────────

type toneFixture struct {
	WaveformTolerance    float64 `json:"waveformTolerance"`
	CoefficientTolerance float64 `json:"coefficientTolerance"`
	Settings             struct {
		LowShelfHz    float64 `json:"lowShelfHz"`
		LowShelfDb    float64 `json:"lowShelfDb"`
		PresenceHz    float64 `json:"presenceHz"`
		PresenceDb    float64 `json:"presenceDb"`
		PresenceQ     float64 `json:"presenceQ"`
		LowShelfSlope float64 `json:"lowShelfSlope"`
	} `json:"settings"`
	Coefficients []struct {
		SampleRate int `json:"sampleRate"`
		LowShelf   struct {
			B []float64 `json:"b"`
			A []float64 `json:"a"`
		} `json:"lowShelf"`
		Peaking struct {
			B []float64 `json:"b"`
			A []float64 `json:"a"`
		} `json:"peaking"`
	} `json:"coefficients"`
	Waveform struct {
		SampleRate int       `json:"sampleRate"`
		Input      []float64 `json:"input"`
		Output     []float64 `json:"output"`
	} `json:"waveform"`
	SilenceStaysSilent []float64 `json:"silenceStaysSilent"`
}

func assertClose(t *testing.T, got, want, tol float64, what string) {
	t.Helper()
	scale := math.Max(1, math.Abs(want))
	if math.Abs(got-want) > tol*scale {
		t.Errorf("%s: got %v, want %v (tolerance %v)", what, got, want, tol)
	}
}

func TestToneShaperSettings(t *testing.T) {
	var f toneFixture
	readVoiceTextFixture(t, "voice_tone_shaper.json", &f)

	// Field by field, and NOT against the whole fixture object: the shelf slope
	// is a private constant of the filter, not a setting anyone may pass in.
	w := circleai.WarmToneShaper
	if w.LowShelfHz != f.Settings.LowShelfHz || w.LowShelfDb != f.Settings.LowShelfDb ||
		w.PresenceHz != f.Settings.PresenceHz || w.PresenceDb != f.Settings.PresenceDb ||
		w.PresenceQ != f.Settings.PresenceQ {
		t.Errorf("WarmToneShaper = %+v, want %+v", w, f.Settings)
	}
	if f.Settings.LowShelfSlope != 0.9 {
		t.Errorf("shelf slope = %v, want 0.9", f.Settings.LowShelfSlope)
	}
}

func TestToneShaperCoefficients(t *testing.T) {
	// 1e-9 relative, not exact: pow, sin and cos are not bit-identical across
	// languages, and pretending otherwise makes a flaky test rather than a strict
	// one.
	var f toneFixture
	readVoiceTextFixture(t, "voice_tone_shaper.json", &f)

	for _, c := range f.Coefficients {
		ls := circleai.LowShelfCoefficients(circleai.WarmToneShaper, c.SampleRate)
		pk := circleai.PeakingCoefficients(circleai.WarmToneShaper, c.SampleRate)
		for i := 0; i < 3; i++ {
			assertClose(t, ls.B[i], c.LowShelf.B[i], f.CoefficientTolerance, "lowShelf b")
			assertClose(t, ls.A[i], c.LowShelf.A[i], f.CoefficientTolerance, "lowShelf a")
			assertClose(t, pk.B[i], c.Peaking.B[i], f.CoefficientTolerance, "peaking b")
			assertClose(t, pk.A[i], c.Peaking.A[i], f.CoefficientTolerance, "peaking a")
		}
	}
}

func TestToneShaperWaveform(t *testing.T) {
	// The biquad is add and multiply on doubles, so THIS half is expected to
	// agree everywhere. Driving it from the fixture's own coefficients keeps the
	// transcendental functions out of the comparison.
	var f toneFixture
	readVoiceTextFixture(t, "voice_tone_shaper.json", &f)

	var coeffs *struct {
		SampleRate int `json:"sampleRate"`
		LowShelf   struct {
			B []float64 `json:"b"`
			A []float64 `json:"a"`
		} `json:"lowShelf"`
		Peaking struct {
			B []float64 `json:"b"`
			A []float64 `json:"a"`
		} `json:"peaking"`
	}
	for i := range f.Coefficients {
		if f.Coefficients[i].SampleRate == f.Waveform.SampleRate {
			coeffs = &f.Coefficients[i]
			break
		}
	}
	if coeffs == nil {
		t.Fatal("no coefficients for the waveform's sample rate")
	}

	x := make([]float32, len(f.Waveform.Input))
	for i, v := range f.Waveform.Input {
		x[i] = float32(v)
	}

	peak := func(v []float32) float32 {
		var p float32
		for _, s := range v {
			if s < 0 {
				s = -s
			}
			if s > p {
				p = s
			}
		}
		return p
	}

	before := peak(x)
	circleai.Biquad(x, toCoeffs(coeffs.LowShelf.B, coeffs.LowShelf.A))
	circleai.Biquad(x, toCoeffs(coeffs.Peaking.B, coeffs.Peaking.A))
	after := peak(x)
	if after > 0 && after > before {
		g := before / after
		for i := range x {
			x[i] *= g
		}
	}

	for i, want := range f.Waveform.Output {
		assertClose(t, float64(x[i]), want, f.WaveformTolerance, "sample")
	}
}

func toCoeffs(b, a []float64) circleai.BiquadCoefficients {
	var c circleai.BiquadCoefficients
	copy(c.B[:], b)
	copy(c.A[:], a)
	return c
}

func TestSilenceStaysSilent(t *testing.T) {
	var f toneFixture
	readVoiceTextFixture(t, "voice_tone_shaper.json", &f)

	silence := make([]float32, len(f.SilenceStaysSilent))
	circleai.ApplyToneShaper(silence, f.Waveform.SampleRate, circleai.WarmToneShaper)
	for i, v := range silence {
		if float64(v) != f.SilenceStaysSilent[i] {
			t.Errorf("silence %d became %v", i, v)
		}
	}
}

func TestBothFiltersAreApplied(t *testing.T) {
	// A port that dropped the presence dip would still change the waveform, so
	// "it moved" proves nothing — the two stages must differ from each other.
	var f toneFixture
	readVoiceTextFixture(t, "voice_tone_shaper.json", &f)

	x := make([]float32, len(f.Waveform.Input))
	onlyShelf := make([]float32, len(f.Waveform.Input))
	for i, v := range f.Waveform.Input {
		x[i] = float32(v)
		onlyShelf[i] = float32(v)
	}

	circleai.ApplyToneShaper(x, f.Waveform.SampleRate, circleai.WarmToneShaper)
	circleai.Biquad(onlyShelf,
		circleai.LowShelfCoefficients(circleai.WarmToneShaper, f.Waveform.SampleRate))

	differs := false
	for i := range x {
		if math.Abs(float64(x[i]-onlyShelf[i])) > 1e-4 {
			differs = true
			break
		}
	}
	if !differs {
		t.Error("the presence dip made no difference — it was not applied")
	}
}

// ── NchltPhonemizer ─────────────────────────────────────────────────────────

type nchltFixture struct {
	Dict     string `json:"dict"`
	Rules    string `json:"rules"`
	PhoneMap string `json:"phoneMap"`
	GraphMap string `json:"graphMap"`
	Gnulls   string `json:"gnulls"`
	Cases    []struct {
		Name               string   `json:"name"`
		Text               string   `json:"text"`
		Phones             []string `json:"phones"`
		RulePredictedWords int      `json:"rulePredictedWords"`
		UnknownGraphemes   []string `json:"unknownGraphemes"`
	} `json:"cases"`
	PredictWord []struct {
		Word   string   `json:"word"`
		Phones []string `json:"phones"`
	} `json:"predictWord"`
}

func (f nchltFixture) make() *circleai.NchltPhonemizer {
	return circleai.NewNchltPhonemizerFromText(f.Dict, f.Rules, f.PhoneMap, f.GraphMap, f.Gnulls)
}

func equalPhoneList(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

func TestNchltMatchesReference(t *testing.T) {
	var f nchltFixture
	readVoiceTextFixture(t, "voice_nchlt_phonemizer.json", &f)

	for _, c := range f.Cases {
		p := f.make()
		got := p.Phonemize(c.Text)
		if !equalPhoneList(got, c.Phones) {
			t.Errorf("%s: phones %v, want %v", c.Name, got, c.Phones)
		}
		if p.LastRulePredictedWords != c.RulePredictedWords {
			t.Errorf("%s: ruleWords %d, want %d", c.Name, p.LastRulePredictedWords, c.RulePredictedWords)
		}
		if !equalPhoneList(p.LastUnknownGraphemes, c.UnknownGraphemes) {
			t.Errorf("%s: unknown %v, want %v", c.Name, p.LastUnknownGraphemes, c.UnknownGraphemes)
		}
	}

	for _, c := range f.PredictWord {
		if got := f.make().PredictWord(c.Word); !equalPhoneList(got, c.Phones) {
			t.Errorf("PredictWord(%q) = %v, want %v", c.Word, got, c.Phones)
		}
	}
}

func TestDictionaryBeatsTheRules(t *testing.T) {
	// Both paths can pronounce this word. The dictionary must win, and the rule
	// counter must show it did — the counter is the only evidence of which path
	// ran, and a port that always predicted would still return sensible phones.
	var f nchltFixture
	readVoiceTextFixture(t, "voice_nchlt_phonemizer.json", &f)

	p := f.make()
	p.Phonemize("sawubona")
	if p.LastRulePredictedWords != 0 {
		t.Error("a catalogued word was predicted rather than looked up")
	}
}

func TestUnknownGraphemeIsReported(t *testing.T) {
	var f nchltFixture
	readVoiceTextFixture(t, "voice_nchlt_phonemizer.json", &f)

	p := f.make()
	p.Phonemize("azb")
	if !equalPhoneList(p.LastUnknownGraphemes, []string{"z"}) {
		t.Errorf("unknown graphemes = %v, want [z]", p.LastUnknownGraphemes)
	}
}
