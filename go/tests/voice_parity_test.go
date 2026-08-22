// voice_parity_test.go
//
// Asserts the Go voice port against the SAME golden files the C# reference
// generates (tools/voice-fixtures). Not "does Go do something sensible" — "does
// Go produce identical answers to every other port".
//
// The fixtures are adversarial on purpose: the SentencePiece vocabulary is built
// so greedy longest-match and Viterbi DISAGREE, and the X-SAMPA cases include a
// multi-character token, the script-g that is U+0261 rather than ASCII 'g', and
// a phone that cannot map and must be REPORTED rather than quietly dropped.

package circleai_test

import (
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"runtime"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func voiceFixturesDir(t *testing.T) string {
	t.Helper()
	_, file, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatal("runtime.Caller failed")
	}
	// tests/ -> go/ -> CircleAI/ -> fixtures/
	return filepath.Join(filepath.Dir(filepath.Dir(filepath.Dir(file))), "fixtures")
}

func readVoiceFixture(t *testing.T, name string, into any) {
	t.Helper()
	data, err := os.ReadFile(filepath.Join(voiceFixturesDir(t), name))
	if err != nil {
		t.Fatalf("failed to read %s: %v", name, err)
	}
	if err := json.Unmarshal(data, into); err != nil {
		t.Fatalf("failed to parse %s: %v", name, err)
	}
}

// ── X-SAMPA → IPA ───────────────────────────────────────────────────────────

type xsampaFixture struct {
	KnownPhones []string `json:"knownPhones"`
	Cases       []struct {
		Xsampa    []string `json:"xsampa"`
		IPA       []string `json:"ipa"`
		Unmapped  []string `json:"unmapped"`
		CanSayAll bool     `json:"canSayAll"`
	} `json:"cases"`
}

func TestVoiceXsampaToIPAMatchesReference(t *testing.T) {
	var fix xsampaFixture
	readVoiceFixture(t, "voice_xsampa_to_ipa.json", &fix)
	if len(fix.Cases) == 0 {
		t.Fatal("fixture has no cases")
	}

	for _, c := range fix.Cases {
		got := circleai.XsampaToIPA(c.Xsampa)
		if !reflect.DeepEqual(got, c.IPA) {
			t.Errorf("ipa for %v: got %v, want %v", c.Xsampa, got, c.IPA)
		}
		if gotUn := circleai.XsampaLastUnmapped(); !reflect.DeepEqual(gotUn, c.Unmapped) {
			t.Errorf("unmapped for %v: got %v, want %v", c.Xsampa, gotUn, c.Unmapped)
		}
		if gotAll := circleai.XsampaCanSayAll(c.Xsampa); gotAll != c.CanSayAll {
			t.Errorf("canSayAll for %v: got %v, want %v", c.Xsampa, gotAll, c.CanSayAll)
		}
	}
}

func TestVoiceXsampaKnownPhonesMatchReference(t *testing.T) {
	var fix xsampaFixture
	readVoiceFixture(t, "voice_xsampa_to_ipa.json", &fix)

	want := map[string]bool{}
	for _, p := range fix.KnownPhones {
		want[p] = true
	}
	got := map[string]bool{}
	for _, p := range circleai.XsampaKnownPhones() {
		got[p] = true
	}
	if !reflect.DeepEqual(got, want) {
		t.Errorf("the phone table itself has drifted from the reference")
	}
}

func TestVoiceScriptGIsU0261NotAsciiG(t *testing.T) {
	// Called out on its own because it is invisible in a diff: the voice's
	// vocabulary carries ɡ (U+0261) and a plain ASCII 'g' silently misses.
	got := circleai.XsampaToIPA([]string{"g"})
	if !reflect.DeepEqual(got, []string{"ɡ"}) {
		t.Errorf("got %q, want U+0261 script g", got)
	}
}

// ── SentencePiece unigram ───────────────────────────────────────────────────

type spFixture struct {
	Vocab  map[string]int     `json:"vocab"`
	Scores map[string]float32 `json:"scores"`
	Cases  []struct {
		Text string `json:"text"`
		IDs  []int  `json:"ids"`
	} `json:"cases"`
}

func loadSpFixture(t *testing.T) (*circleai.SentencePieceUnigram, spFixture) {
	t.Helper()
	var fix spFixture
	readVoiceFixture(t, "voice_sentencepiece_unigram.json", &fix)
	return circleai.NewSentencePieceUnigram(fix.Vocab, fix.Scores), fix
}

func TestVoiceSentencePieceMatchesReference(t *testing.T) {
	sp, fix := loadSpFixture(t)
	if len(fix.Cases) == 0 {
		t.Fatal("fixture has no cases")
	}
	for _, c := range fix.Cases {
		got := sp.Encode(c.Text)
		if !reflect.DeepEqual(got, c.IDs) {
			t.Errorf("ids for %q: got %v, want %v", c.Text, got, c.IDs)
		}
	}
}

func TestVoiceViterbiNotGreedy(t *testing.T) {
	// The fixture vocabulary is built so the two disagree: "▁hello" scores WORSE
	// than "▁hell" + "o". Greedy picks the long piece; Viterbi does not. Without
	// this, a greedy port looks correct.
	sp, fix := loadSpFixture(t)
	want := []int{fix.Vocab["▁hell"], fix.Vocab["o"], fix.Vocab["▁world"]}
	greedy := []int{fix.Vocab["▁hello"], fix.Vocab["▁world"]}

	got := sp.Encode("hello world")
	if !reflect.DeepEqual(got, want) {
		t.Errorf("got %v, want %v", got, want)
	}
	if reflect.DeepEqual(got, greedy) {
		t.Error("this is the greedy answer — the port is not doing Viterbi")
	}
}

func TestVoiceByteFallbackKeepsUtf8Order(t *testing.T) {
	// é is UTF-8 C3 A9. Emitting A9 C3 does not error — both are real pieces with
	// real ids — the model just says a different character, and only outside
	// ASCII, which is exactly the languages this catalogue serves.
	sp, fix := loadSpFixture(t)
	got := sp.Encode("hé")
	if len(got) < 2 {
		t.Fatalf("expected byte fallback pieces, got %v", got)
	}
	tail := got[len(got)-2:]
	want := []int{fix.Vocab["<0xC3>"], fix.Vocab["<0xA9>"]}
	if !reflect.DeepEqual(tail, want) {
		t.Errorf("byte fallback emitted UTF-8 bytes in the wrong order: got %v, want %v", tail, want)
	}
}

func TestVoiceEmptyTextEncodesToNothing(t *testing.T) {
	sp, _ := loadSpFixture(t)
	if got := sp.Encode(""); len(got) != 0 {
		t.Errorf("got %v, want empty", got)
	}
}
